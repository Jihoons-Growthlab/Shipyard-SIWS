import argparse
import json
import math
import os
import sqlite3
import threading
from contextlib import closing, contextmanager
from datetime import date, datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

import auth
import bom
import cache
import geometry
import lots
import menus
import openapi
import outfitting
import planning
import procurement
import production
import quality
import resources
import results
import search
import storage
import traceability
import yardsim
import shipbuilding
import sys
import yardplan
import stockplan
from urllib.parse import parse_qs

ROOT = Path(__file__).resolve().parent
DATABASE = ROOT / 'shipyard.sqlite3'
DATABASE_URL = None
STAGES = ['설계·구매', '가공·조립', '선행의장·도장', '선탑재·탑재', '진수·안벽의장', '시운전·인도']
SCHEMA = '''
CREATE TABLE IF NOT EXISTS hulls(id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS locations(id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS blocks(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 location TEXT REFERENCES locations(id));
CREATE TABLE IF NOT EXISTS equipment(id TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS activities(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), stage TEXT NOT NULL, baseline TEXT NOT NULL,
 forecast TEXT NOT NULL, actual TEXT, reason TEXT NOT NULL, actual_reason TEXT, inspection TEXT);
CREATE TABLE IF NOT EXISTS plans(id INTEGER PRIMARY KEY, start TEXT NOT NULL,
 end_exclusive TEXT NOT NULL, issued_at TEXT NOT NULL, snapshot TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS orders(id TEXT PRIMARY KEY, activity TEXT NOT NULL REFERENCES activities(id),
 cadence TEXT NOT NULL, day TEXT NOT NULL, instruction TEXT NOT NULL,
 parent TEXT REFERENCES orders(id), actual TEXT, result TEXT, inspection TEXT);
CREATE TABLE IF NOT EXISTS moves(id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id),
 equipment TEXT NOT NULL REFERENCES equipment(id), origin TEXT REFERENCES locations(id),
 destination TEXT NOT NULL REFERENCES locations(id), started_at TEXT NOT NULL, confirmed_at TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS one_active_block ON moves(block) WHERE confirmed_at IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS one_active_equipment ON moves(equipment) WHERE confirmed_at IS NULL;
CREATE TABLE IF NOT EXISTS telemetry(id INTEGER PRIMARY KEY, move TEXT NOT NULL REFERENCES moves(id),
 recorded_at TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, load REAL NOT NULL, status TEXT NOT NULL);
'''
FIELDS = {
    'hulls': ('id', 'name'),
    'locations': ('id', 'name', 'kind'),
    'blocks': ('id', 'hull', 'location'),
    'equipment': ('id', 'name'),
    'activities': ('id', 'hull', 'block', 'stage', 'baseline', 'forecast', 'reason'),
    'forecast': ('id', 'forecast', 'reason'),
    'plans': ('start',),
    'orders': ('id', 'activity', 'cadence', 'day', 'instruction', 'parent', 'plan_id'),
    'results': ('id', 'actual', 'result', 'inspection', 'reason') + results.DETAIL_FIELDS,
    'moves': ('id', 'block', 'equipment', 'destination'),
    'confirm': ('id',),
    'telemetry': ('move', 'x', 'y', 'load', 'status'),
}
FIELDS.update(shipbuilding.FIELDS)
traceability.PREFIXES.update(shipbuilding.PREFIXES)
FIELDS.update(planning.FIELDS)
FIELDS.update(procurement.FIELDS)
SCHEMA += shipbuilding.SCHEMA + planning.SCHEMA + procurement.SCHEMA + traceability.SCHEMA + results.SCHEMA
READS = shipbuilding.READS | {'inventory_ledger', 'production_results'} | set(procurement.FIELDS) | set(planning.FIELDS) | {'mrp', 'aps'} | {'hulls', 'locations', 'blocks', 'equipment', 'activities', 'plans', 'orders', 'moves', 'telemetry'}
# Shipyard extensions: assembly stages/man-hours, crane & dock constraints, steel lots, ITP/NCR/punch, roles & audit.
EXTENSIONS = (production, resources, lots, quality, geometry, yardplan, outfitting, stockplan, bom, auth)
OPTIONAL = {'block', 'location', 'parent', 'reason', 'predecessor', 'plan_id'}
ACTION_OPTIONAL = {'results': {'reason', *results.DETAIL_FIELDS}}
DATE_KEYS = {'baseline', 'forecast', 'start', 'day', 'actual', 'delivery', 'receipt_day', 'need_day', 'earliest',
             'promised_day'}
CHOICES = {'shift_code': results.SHIFTS, 'output_unit': results.UNITS}
# Free-precision finite numbers (telemetry metres/tonnes, latitude/longitude): no fixed decimal places.
FLOAT_KEYS = {'x', 'y', 'load', 'output_qty', 'labor_mh'}
for module in EXTENSIONS:
    SCHEMA += module.SCHEMA
    FIELDS.update(module.FIELDS)
    traceability.PREFIXES.update(module.PREFIXES)
    READS |= module.READS
    ACTION_OPTIONAL.update(module.OPTIONAL)
    DATE_KEYS |= module.DATES
    planning.NUMBERS.update(module.NUMBERS)
    planning.MEASURES.update(getattr(module, 'MEASURES', set()))
    planning.SIGNED.update(getattr(module, 'SIGNED', set()))
    CHOICES.update(module.CHOICES)
    FLOAT_KEYS |= getattr(module, 'FLOATS', set())
shipbuilding.EXTRA_BLOCKERS[:] = [production.blockers, resources.blockers, quality.blockers, outfitting.erection_blockers]
production.STAGE_CHECKS[:] = [bom.stage_gate]
MENU_ROUTES, MENU_ACTIONS = menus.build(FIELDS)
LOCAL_NO_LOGIN = False
LOCAL_USER = {'id': 'local-workspace', 'name': '로컬 작업자', 'group_id': 'LOCAL'}
SESSION_ACTIONS = {'login', 'logout', 'change_password'}
SPECIAL_POSTS = {'login', 'logout', 'change_password', 'group_permissions_bulk'}
NO_CACHE = {'login_events'}
STORE = cache.MemoryStore()


def connect():
    if DATABASE_URL:
        return storage.Postgres(DATABASE_URL)
    db = sqlite3.connect(DATABASE)
    db.row_factory = sqlite3.Row
    db.execute('PRAGMA foreign_keys = ON')
    return db


def now():
    return datetime.now(timezone.utc).isoformat()


def day(value):
    parsed = date.fromisoformat(value)
    if parsed.isoformat() != value:
        raise ValueError('날짜는 YYYY-MM-DD 형식이어야 합니다.')
    return parsed


def require(value, message):
    if not value:
        raise ValueError(message)


def lookup(db, table, key):
    row = db.execute(f'SELECT * FROM {table} WHERE id = ?', (key,)).fetchone()
    require(row, f'{table}에서 ID {key}을(를) 찾을 수 없습니다.')
    return row


def insert(db, table, **values):
    params = ','.join('?' for _ in values)
    query = f'INSERT INTO {table} ({",".join(values)}) VALUES ({params})'
    if table in {'plans', 'telemetry'} and 'id' not in values:
        return db.execute(query + ' RETURNING id', tuple(values.values())).fetchone()['id']
    db.execute(query, tuple(values.values()))
    key = {'operations': 'activity', 'center_locations': 'center', 'holidays': 'holiday'}.get(table, 'id')
    return values.get(key)


def activity_view(row):
    item = dict(row)
    baseline = day(item['baseline'])
    item['forecast_variance_days'] = (day(item['forecast']) - baseline).days
    item['actual_variance_days'] = (day(item['actual']) - baseline).days if item['actual'] else None
    return item


def read(db, table):
    if table == "production_results":
        return results.read(db)
    if table == "block_readiness":
        return shipbuilding.readiness(db)
    for module in EXTENSIONS:
        if table in module.READS:
            rows = module.read(db, table)
            if rows is not None:
                return rows
    queries = {
        'orders': '''SELECT o.*, p.plan_id, (SELECT b.mbom FROM order_boms b WHERE b.work_order=o.id
            ORDER BY b.linked_at DESC, b.id DESC LIMIT 1) AS mbom FROM orders o LEFT JOIN order_plans p ON p.work_order=o.id ORDER BY o.id''',
        'goods_receipts': 'SELECT g.*, l.location FROM goods_receipts g LEFT JOIN receipt_locations l ON l.receipt=g.id ORDER BY g.id',
        'issues': 'SELECT i.*, l.source, l.destination FROM issues i LEFT JOIN issue_locations l ON l.issue=i.id ORDER BY i.id',
        'center_locations': 'SELECT * FROM center_locations ORDER BY center',
    }
    if table in queries:
        return [dict(row) for row in db.execute(queries[table])]
    if table == 'purchase_orders':
        return [dict(row) for row in db.execute(procurement.OPEN_ORDERS + ' ORDER BY p.id')]
    if table in ('mrp', 'aps'):
        return getattr(planning, table)(db)
    if table in ('vessel_details', 'block_details', 'operations'):
        key = {'vessel_details': 'hull', 'block_details': 'block', 'operations': 'activity'}[table]
        return [dict(row) for row in db.execute(f'SELECT * FROM {table} ORDER BY {key}')]
    if table == 'blocks':
        query = '''SELECT b.*, m.id AS active_move, CASE WHEN m.id IS NOT NULL THEN 'in_transit'
            WHEN b.location IS NOT NULL THEN 'confirmed' ELSE 'unknown' END AS position_status
            FROM blocks b LEFT JOIN moves m ON m.block = b.id AND m.confirmed_at IS NULL ORDER BY b.id'''
    else:
        query = f'SELECT * FROM {table} ORDER BY id'
    rows = [dict(row) for row in db.execute(query)]
    if table == 'activities':
        return [activity_view(row) for row in rows]
    if table == 'plans':
        for row in rows:
            row['snapshot'] = json.loads(row['snapshot'])
            row['document_no'] = f"PLAN-{row['id']:06d}" 
    return rows


def require_predecessors(db, activity, actual):
    """An activity is completed only after its constraint predecessors were completed and passed inspection."""
    for prior in sorted(resources.predecessors(db).get(activity, ())):
        row = lookup(db, 'activities', prior)
        require(row['actual'] and row['actual'] <= actual and row['inspection'] == '합격',
                f'선행 공정 {prior}의 완료·검사 합격 후에 실적을 등록하세요.')


def write(db, action, data, actor=None):
    fields = FIELDS[action]
    require(isinstance(data, dict) and not (data.keys() - set(fields)), '지원하지 않는 입력 항목입니다.')
    optional = OPTIONAL | ACTION_OPTIONAL.get(action, set())
    values = {}
    for key in fields:
        value = data.get(key)
        if key == 'actual' and action == 'milestone_update' and not value:
            values[key] = None
            continue
        if value in (None, '') and key in optional and (key in planning.NUMBERS or key in FLOAT_KEYS):
            values[key] = None  # optional number left empty; the module decides the default
            continue
        if key == 'id' and not value and action in traceability.PREFIXES:
            value = traceability.document_number(db, action)
        if key in planning.NUMBERS:
            require(type(value) in (str, int, float), f'{key} 수치가 필요합니다.')
        elif key in FLOAT_KEYS:
            require(type(value) in (str, int, float), f'{key} 수치가 필요합니다.')
            value = float(value)
            require(math.isfinite(value), f'{key}에는 유한한 수치를 입력하세요.')
        else:
            require(value is None or isinstance(value, str), f'{key}에는 문자열을 입력하세요.')
            value = (value or '') if key in auth.SECRET_KEYS else (value or '').strip()
            require(len(value) <= 1000 and '\x00' not in value, f'{key} 입력이 유효하지 않습니다.')
            require(value or key in optional, f'{key} 값을 입력하세요.')
            if key in DATE_KEYS and value:
                day(value)
            if not value and key != 'reason':
                value = None
        values[key] = value
    v = values
    if action == 'milestone_update' and v['actual']:
        milestone = lookup(db, 'hull_milestones', v['id'])
        if milestone['actual'] != v['actual']:
            require(v['actual'] <= date.today().isoformat(), '실적일은 오늘 이후일 수 없습니다.')
            shipbuilding.check_milestone_order(db, milestone, v['actual'], require)
            production.milestone_gate(db, milestone['hull'], milestone['milestone'], v['actual'], require)
            outfitting.milestone_gate(db, milestone['hull'], milestone['milestone'], v['actual'], require)
            quality.milestone_gate(db, milestone['hull'], milestone['milestone'], require)
    for module in EXTENSIONS:
        if action in module.FIELDS:
            planning.validate(db, action, v)
            if module is auth:
                return auth.write(db, action, v, sys.modules[__name__], actor)
            return module.write(db, action, v, sys.modules[__name__])
    if action in shipbuilding.FIELDS:
        return shipbuilding.write(db, action, v, sys.modules[__name__])
    if action in procurement.FIELDS:
        planning.validate(db, action, v)
        if action in {'goods_receipts', 'issues'}:
            require(day(v['day']) <= date.today(), '실제 자재 거래일은 오늘 이후일 수 없습니다.')
        if action in {'goods_receipts', 'warehouse_stock', 'center_locations'}:
            require(v['location'], '시설을 선택하세요.')
        procurement.apply(db, action, v, lookup, require)
        link = procurement.transfer(db, action, v, lookup, require)
        key = insert(db, action, **v)
        if link:
            table, values = link
            insert(db, table, **values)
        if action == 'warehouse_stock':
            traceability.ledger(db, insert, now(), v['material'], v['location'], date.today().isoformat(),
                                '기초 배정', key, v['qty'])
        if action == 'goods_receipts' and v['inspection'] == '합격':
            order = db.execute(f'SELECT material FROM ({procurement.OPEN_ORDERS}) WHERE id=?',
                               (v['purchase_order'],)).fetchone()
            traceability.ledger(db, insert, now(), order['material'], link[1]['location'], v['day'],
                                '입고', key, v['qty'])
        if action == 'issues':
            material = lookup(db, 'requirements', v['requirement'])['material']
            traceability.ledger(db, insert, now(), material, link[1]['source'], v['day'], '출고', key, -v['qty'])
        return key
    if action in planning.FIELDS:
        planning.validate(db, action, v)
        key = {'vessel_details': 'hull', 'block_details': 'block'}.get(action)
        if key:
            columns = ','.join(v)
            placeholders = ','.join('?' for _ in v)
            updates = ','.join(f'{column}=excluded.{column}' for column in v if column != key)
            db.execute(f'INSERT INTO {action} ({columns}) VALUES ({placeholders}) '
                       f'ON CONFLICT({key}) DO UPDATE SET {updates}', tuple(v.values()))
            return v[key]
        if action == 'requirements':
            # A finished activity keeps its material history; new needs go on a new activity or work order.
            require(not lookup(db, 'activities', v['activity'])['actual'], '완료한 공정에는 자재 소요를 추가할 수 없습니다.')
    if action == 'locations':
        require(v['kind'] in {'야드', '작업장', '도크', '안벽', '창고'}, '시설 유형을 선택하세요.')
    if action == 'activities':
        require(v['stage'] in STAGES, '공정을 선택하세요.')
        if v['block']:
            require(lookup(db, 'blocks', v['block'])['hull'] == v['hull'], '호선과 블록이 일치해야 합니다.')
        require(v['forecast'] <= v['baseline'] or v['reason'], '지연 원인을 입력하세요.')
    if action == 'forecast':
        activity = lookup(db, 'activities', v['id'])
        require(not activity['actual'], '완료한 공정의 예정일은 변경할 수 없습니다.')
        require(v['forecast'] <= activity['baseline'] or v['reason'], '지연 원인을 입력하세요.')
        db.execute('UPDATE activities SET forecast=?, reason=? WHERE id=?', (v['forecast'], v['reason'], v['id']))
        return v['id']
    if action == 'plans':
        start = day(v['start'])
        require(start.weekday() == 0, '계획 시작일은 월요일이어야 합니다.')
        end = (start + timedelta(weeks=12)).isoformat()
        rows = db.execute('''SELECT * FROM activities
            WHERE (baseline >= ? AND baseline < ?) OR (forecast >= ? AND forecast < ?)
            ORDER BY forecast, id''', (v['start'], end, v['start'], end))
        plan_id = insert(db, 'plans', start=v['start'], end_exclusive=end, issued_at=now(),
                      snapshot=json.dumps([activity_view(row) for row in rows], ensure_ascii=False))
        return f'PLAN-{plan_id:06d}'
    if action == 'orders':
        plan_value = v.pop('plan_id')
        plan_id = None
        if plan_value:
            require(plan_value.isascii() and plan_value.isdigit() and len(plan_value) <= 18, '발행 계획 번호를 선택하세요.')
            plan_id = int(plan_value)
            plan = lookup(db, 'plans', plan_id)
            require(plan['start'] <= v['day'] < plan['end_exclusive'] and
                    any(row['id'] == v['activity'] for row in json.loads(plan['snapshot'])),
                    '선택한 발행 계획에 작업일과 공정이 포함되어야 합니다.')
        activity = lookup(db, 'activities', v['activity'])
        require(not activity['actual'], '완료한 공정에 작업지시를 추가할 수 없습니다.')
        require(v['cadence'] in {'주간', '일일'}, '주간 또는 일일을 선택하세요.')
        start = day(v['day'])
        if v['cadence'] == '주간':
            require(start.weekday() == 0 and not v['parent'], '주간 지시는 월요일로 지정하고 상위 지시를 비워 주세요.')
        else:
            parent = lookup(db, 'orders', v['parent'])
            offset = (start - day(parent['day'])).days
            require(parent['cadence'] == '주간' and not parent['actual'] and
                    parent['activity'] == v['activity'] and 0 <= offset < 7,
                    '같은 공정의 미완료 주간 지시와 해당 주의 날짜가 필요합니다.')
            parent_plan = db.execute('SELECT plan_id FROM order_plans WHERE work_order=?',
                                     (parent['id'],)).fetchone()
            if parent_plan:
                require(plan_id is None or plan_id == parent_plan['plan_id'], '상위 작업지시의 발행 계획과 일치해야 합니다.')
                plan_id = parent_plan['plan_id']
        key = insert(db, 'orders', **v)
        if plan_id is not None:
            insert(db, 'order_plans', work_order=key, plan_id=plan_id)
        bom.link_new_order(db, sys.modules[__name__], key)
        return key
    if action == 'results':
        details = results.validate(db, v, sys.modules[__name__])
        order = lookup(db, 'orders', v['id'])
        require(order['inspection'] != '합격', '이미 합격한 작업지시입니다.')
        require(v['inspection'] in {'합격', '불합격'}, '검사 결과를 선택하세요.')
        require(not order['actual'] or order['actual'] <= v['actual'], '재검사일은 이전 실적일 이후여야 합니다.')
        children = list(db.execute('SELECT actual, inspection FROM orders WHERE parent=?', (v['id'],)))
        if v['inspection'] == '합격':
            procurement.require_issued(db, order, v['actual'], require)
            if order['cadence'] == '주간':
                require_predecessors(db, order['activity'], v['actual'])
            require(all(row['actual'] and row['inspection'] == '합격' and row['actual'] <= v['actual']
                        for row in children), '하위 작업의 합격 실적과 실제일을 확인하세요.')
        db.execute('UPDATE orders SET actual=?, result=?, inspection=? WHERE id=?',
                   (v['actual'], v['result'], v['inspection'], v['id']))
        if order['cadence'] == '주간':
            activity = lookup(db, 'activities', order['activity'])
            reason = v['reason'] or activity['actual_reason'] or activity['reason']
            require(v['actual'] <= activity['baseline'] or reason, '실제일 지연 원인을 입력하세요.')
            if reason:
                db.execute('UPDATE activities SET actual_reason=? WHERE id=?', (reason, order['activity']))
            remaining = db.execute('''SELECT COUNT(*) FROM orders WHERE activity=? AND cadence='주간'
                AND (inspection IS NULL OR inspection <> '합격')''', (order['activity'],)).fetchone()[0]
            db.execute("""UPDATE activities SET actual=(SELECT MAX(actual) FROM orders
                WHERE activity=? AND cadence='주간'), inspection='합격' WHERE id=? AND ?=0""",
                       (order['activity'], order['activity'], remaining))
            if v['inspection'] == '불합격':
                db.execute("UPDATE activities SET inspection='불합격' WHERE id=?", (order['activity'],))
        number = traceability.document_number(db, 'production_results')
        results.ensure_lot(db, insert, order['id'])
        insert(db, 'production_results', id=number, work_order=order['id'], actual=v['actual'],
               result=v['result'], inspection=v['inspection'], reason=v['reason'], recorded_at=now())
        insert(db, 'production_result_details', result_id=number, recorded_by=actor, **details)
        return number
    if action == 'moves':
        block = lookup(db, 'blocks', v['block'])
        require(block['location'] != v['destination'], '출발지와 목적지가 같을 수 없습니다.')
        resources.check_move(db, v['block'], v['equipment'], require)
        v.update(origin=block['location'], started_at=now())
    if action == 'confirm':
        move = lookup(db, 'moves', v['id'])
        require(not move['confirmed_at'], '이미 확정된 이동입니다.')
        db.execute('UPDATE moves SET confirmed_at=? WHERE id=?', (now(), v['id']))
        db.execute('UPDATE blocks SET location=? WHERE id=?', (move['destination'], move['block']))
        return v['id']
    if action == 'telemetry':
        lookup(db, 'moves', v['move'])
        require(v['load'] >= 0, '하중은 0 이상이어야 합니다.')
        v['recorded_at'] = now()
    return insert(db, action, **v)


def seed(db):
    if db.execute("SELECT 1 FROM hulls").fetchone():
        return
    def add(action, **values):
        return write(db, action, values)
    add('hulls', id='H101', name='상선 신조 101호선')
    add('locations', id='SHOP', name='블록 조립장', kind='작업장')
    add('locations', id='STORE', name='후판 자재창고', kind='창고')
    add('locations', id='YARD', name='블록 적치 야드', kind='야드')
    add('locations', id='DOCK', name='1도크', kind='도크')
    add('locations', id='QUAY', name='1안벽', kind='안벽')
    add('blocks', id='B101', hull='H101', location='SHOP')
    add('blocks', id='B102', hull='H101')
    add('equipment', id='CR1', name='갠트리 크레인 1호')
    start = date.today() - timedelta(days=date.today().weekday())
    for index, stage in enumerate(STAGES):
        baseline = start + timedelta(weeks=index * 2)
        delayed = index == 2
        add('activities', id=f'A{index + 1}', hull='H101', block='B101' if 0 < index < 4 else '',
            stage=stage, baseline=baseline.isoformat(), forecast=(baseline + timedelta(days=3 * delayed)).isoformat(),
            reason='도장 자재 입고 지연' if delayed else '')
    add('vessel_details', hull='H101', vessel_type='LNG 운반선', vessel_name='모의 101호',
        customer='모의 선주', length_m=299, beam_m=46, delivery=(start + timedelta(weeks=11)).isoformat())
    add('block_details', block='B101', zone='화물창 구역', assembly='대조립', weight_t=120, drawing='D-B101-R01')
    add('suppliers', id='SUP-STEEL', name='모의 후판 공급사')
    add('materials', id='PLATE', name='선체용 강판', unit='장', stock=20, lead_days=14)
    add('requirements', id='REQ1', activity='A2', material='PLATE', qty=25,
        need_day=(start + timedelta(weeks=2)).isoformat())
    add('warehouse_stock', id='OPEN-PLATE', location='STORE', material='PLATE', qty=20)
    add('centers', id='C1', name='블록 조립장', hours=8)
    add('center_locations', center='C1', location='SHOP')
    add('operations', activity='A2', center='C1', duration=16, earliest=(start + timedelta(weeks=2)).isoformat())
    add('operations', activity='A3', center='C1', duration=12, predecessor='A2',
        earliest=(start + timedelta(weeks=4)).isoformat())
    add('plans', start=start.isoformat())
    add('orders', id='W1', activity='A1', cadence='주간', day=start.isoformat(), instruction='설계 승인 및 자재 명세 확인')
    add('orders', id='D1', activity='A1', cadence='일일', day=start.isoformat(), instruction='자재 명세 확인', parent='W1')
    add('results', id='D1', actual=start.isoformat(), result='자재 명세 확인 완료', inspection='합격')
    add('moves', id='M1', block='B101', equipment='CR1', destination='DOCK')
    add('telemetry', move='M1', x=24, y=18, load=120, status='이송 중')


@contextmanager
def read_db():
    with closing(connect()) as db, db:
        storage.begin_read(db)
        yield db


def cached_rows(table):
    """Full rows of a route through the versioned cache, as (json_text, hit)."""
    def compute():
        with read_db() as db:
            return read(db, table)
    if table in NO_CACHE:
        return json.dumps(compute(), ensure_ascii=False), False
    return cache.cached(STORE, 'route:' + table, compute)


def meta():
    return {'login_required': not LOCAL_NO_LOGIN, 'fields': FIELDS, 'stages': STAGES, 'numbered': list(traceability.PREFIXES),
            'gates': shipbuilding.GATES, 'milestones': shipbuilding.MILESTONES,
            'optional': {key: sorted(value) for key, value in ACTION_OPTIONAL.items()},
            'dates': sorted(DATE_KEYS), 'measures': sorted(planning.MEASURES), 'signed': sorted(planning.SIGNED),
            'integers': sorted(planning.NUMBERS - planning.MEASURES), 'choices': CHOICES,
            'modules': menus.MODULES, 'views': menus.VIEWS, 'refs': menus.REFS, 'id_sources': menus.ID_SOURCES,
            'derived': menus.DERIVED, 'menu_routes': MENU_ROUTES, 'menu_actions': MENU_ACTIONS,
            'common': menus.COMMON, 'home_routes': menus.HOME_ROUTES, 'trace_routes': menus.TRACE_ROUTES,
            'server_paged': menus.SERVER_PAGED, 'admin_menu': menus.ADMIN_MENU, 'admin_group': auth.ADMIN_GROUP}


_OPENAPI = []


def openapi_document():
    if not _OPENAPI:
        _OPENAPI.append(json.dumps(openapi.build(sys.modules[__name__]), ensure_ascii=False))
    return _OPENAPI[0]


def user_access(user):
    if LOCAL_NO_LOGIN:
        business_menus = [menu for menu in menus.MENUS if menu != menus.ADMIN_MENU]
        return {
            'menus': {menu: {'read': True, 'write': True} for menu in business_menus},
            'routes': sorted(set(menus.COMMON).union(*(MENU_ROUTES[menu] for menu in business_menus))),
            'actions': sorted(set().union(*(MENU_ACTIONS[menu] for menu in business_menus))),
        }
    return auth.access(STORE, read_db, user['group_id'], MENU_ROUTES, MENU_ACTIONS)


def invalidate():
    try:
        cache.bump(STORE)
    except cache.STORE_ERRORS:
        pass


class Handler(BaseHTTPRequestHandler):
    def send(self, status, payload, content_type='application/json', headers=None):
        body = payload if isinstance(payload, bytes) else payload.encode() if isinstance(payload, str) \
            else json.dumps(payload, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type + '; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        for key, value in (headers or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)

    def user(self):
        if LOCAL_NO_LOGIN:
            return LOCAL_USER
        return auth.authenticate(STORE, read_db, self.headers.get('Authorization', ''))

    def do_GET(self):
        parts = urlsplit(self.path)
        path = parts.path
        if path == '/':
            self.send(200, (ROOT / 'index.html').read_bytes(), 'text/html')
            return
        if path in ('/guide', '/guide/'):
            self.send(200, (ROOT / 'docs' / 'ERP_WORKFLOW.html').read_bytes(), 'text/html')
            return
        brand_assets = {'/brand/hoon-icon.png': ('hoon-icon.png', 'image/png'),
                        '/brand/yard-hero.jpg': ('yard-hero.jpg', 'image/jpeg')}
        if path in brand_assets:
            name, content_type = brand_assets[path]
            self.send(200, (ROOT / 'brand' / name).read_bytes(), content_type)
            return
        if path in ('/docs', '/docs/'):
            self.send(200, openapi.DOCS_HTML, 'text/html')
            return
        if path == '/api/openapi.json':
            self.send(200, openapi_document())
            return
        if not path.startswith('/api/'):
            self.send(404, {'error': '주소를 찾을 수 없습니다.'})
            return
        table = path[5:]
        if table == 'meta':
            self.send(200, meta())
            return
        try:
            user = self.user()
            granted = user_access(user)
            if table == 'me':
                self.send(200, {'id': user['id'], 'name': user['name'], 'group_id': user['group_id'], **granted})
                return
            if table == 'crane_activity':
                # Rail jib cranes now (or ?at=ISO time) from the ERP crane plan: the twin and the equipment monitor read it.
                require({'crane_rails', 'crane_assignments'}.issubset(granted['routes']), '크레인 레일과 크레인 배정을 조회할 권한이 필요합니다.')
                at = parse_qs(parts.query).get('at', [None])[0]
                moment = datetime.fromisoformat(at) if at else datetime.now(yardsim.KST)
                moment = moment if moment.tzinfo else moment.replace(tzinfo=yardsim.KST)
                rows = {route: json.loads(cached_rows(route)[0]) if route in granted['routes'] else []
                        for route in ('equipment_specs', 'equipment', 'crane_rails', 'crane_assignments', 'crane_kinematics', 'crane_lifts',
                                      'crane_load_charts')}
                self.send(200, yardsim.crane_activity(rows, moment))
                return
            if table == 'yard_sim':
                yard_sim_access(granted)
                self.send(200, yardsim.current() or {'scenario': None})
                return
            if table not in READS:
                self.send(404, {'error': '조회 항목을 찾을 수 없습니다.'})
                return
            if table not in granted['routes']:
                raise PermissionError('이 항목을 조회할 권한이 없습니다.')
            raw = parse_qs(parts.query, keep_blank_values=True)
            if not raw:
                body, hit = cached_rows(table)
            else:
                params = search.parse(raw)

                def compute():
                    rows = json.loads(cached_rows(table)[0])
                    maps = None
                    if params.get('hull'):
                        maps = search.hull_maps({name: json.loads(cached_rows(name)[0]) for name in search.HULL_SOURCES})
                    names = {row['id']: row['name'] for row in json.loads(cached_rows('materials')[0])}
                    return search.query(rows, params, maps, names)
                if table in NO_CACHE:
                    body, hit = json.dumps(compute(), ensure_ascii=False), False
                else:
                    body, hit = cache.cached(STORE, f'search:{table}:{cache.digest(params)}', compute)
            self.send(200, body, headers={'X-Cache': 'hit' if hit else 'miss'})
        except auth.AuthError as exc:
            self.send(401, {'error': str(exc)})
        except PermissionError as exc:
            self.send(403, {'error': str(exc)})
        except ValueError as exc:
            self.send(400, {'error': str(exc)})
        except cache.STORE_ERRORS + storage.DATABASE_ERRORS as exc:
            self.log_error('%s', exc)
            self.send(503, {'error': '저장소를 사용할 수 없습니다. 잠시 후 다시 시도하세요.'})

    def body(self):
        require(self.headers.get_content_type() == 'application/json', 'JSON 요청이 필요합니다.')
        length = int(self.headers.get('Content-Length', '0'))
        require(0 < length <= 65536, '요청 크기를 확인하세요.')
        data = json.loads(self.rfile.read(length))
        require(isinstance(data, dict), 'JSON 객체가 필요합니다.')
        return data

    def yard_sim_post(self):
        """POST /api/yard_sim: {"action": "open", "hull", "start"?, "end"?, "days_per_second"?} or play / pause /
        speed (value) / seek (value: ISO time) / restart / close. Changes only the in-memory replay session."""
        try:
            user = self.user()
            granted = user_access(user)
            yard_sim_access(granted)
            data = self.body()
            action = data.get('action')
            if action == 'open':
                hull = data.get('hull')
                require(isinstance(hull, str) and hull, '재생할 호선을 지정하세요.')
                days = data.get('days_per_second')
                require(days is None or (isinstance(days, (int, float)) and 0 < days <= 60), '초당 일수를 확인하세요.')
                state = yardsim.open_session(hull, yard_sim_rows(granted), data.get('start'), data.get('end'), days,
                                             keep=not data.get('reset'))
            elif action == 'close':
                yardsim.close()
                state = {'scenario': None}
            else:
                state = yardsim.control(action, data.get('value'))
            self.send(200, state)
        except auth.AuthError as exc:
            self.send(401, {'error': str(exc)})
        except PermissionError as exc:
            self.send(403, {'error': str(exc)})
        except (ValueError, TypeError) as exc:
            self.send(400, {'error': str(exc)})
        except cache.STORE_ERRORS + storage.DATABASE_ERRORS as exc:
            self.log_error('%s', exc)
            self.send(503, {'error': '저장소를 사용할 수 없습니다. 잠시 후 다시 시도하세요.'})

    def do_POST(self):
        action = urlsplit(self.path).path[5:]
        if action == 'yard_sim' and self.path.startswith('/api/'):
            self.yard_sim_post()
            return
        if not self.path.startswith('/api/') or (action not in FIELDS and action not in SPECIAL_POSTS):
            self.send(404, {'error': '입력 항목을 찾을 수 없습니다.'})
            return
        if LOCAL_NO_LOGIN and action in SESSION_ACTIONS:
            self.send(404, {'error': '로컬 모드에서는 로그인 기능을 사용하지 않습니다.'})
            return
        api = sys.modules[__name__]
        try:
            data = self.body()
            if action == 'login':
                with closing(connect()) as db, db:
                    storage.begin_write(db)
                    status, payload = auth.login(db, STORE, api, data, self.client_address[0])
                self.send(status, payload)
                return
            user = self.user()
            if action == 'logout':
                STORE.delete(user['session'])
                STORE.srem(f"user_sessions:{user['id']}", user['session'])
                self.send(200, {'id': user['id']})
                return
            if action == 'change_password':
                with closing(connect()) as db, db:
                    storage.begin_write(db)
                    auth.change_password(db, STORE, api, user, data)
                    auth.audit(db, api, user['id'], action, user['id'], data)
                auth.revoke(STORE, user['id'], keep=user['session'])
                self.send(201, {'id': user['id']})
                return
            if action not in user_access(user)['actions']:
                raise PermissionError('이 작업을 저장할 권한이 없습니다.')
            with closing(connect()) as db, db:
                storage.begin_write(db)
                if action == 'group_permissions_bulk':
                    key = auth.save_permissions(db, api, data)
                else:
                    key = write(db, action, data, user['id'])
                auth.audit(db, api, user['id'], action, key, data)
            auth.after_commit(STORE, action, data, key)
            invalidate()
        except auth.AuthError as exc:
            self.send(401, {'error': str(exc)})
        except PermissionError as exc:
            self.send(403, {'error': str(exc)})
        except (ValueError, OverflowError) as exc:
            self.send(400, {'error': str(exc)})
        except storage.INTEGRITY_ERRORS:
            self.send(409, {'error': 'ID 중복, 진행 중인 이동 또는 기준정보 참조를 확인하세요.'})
        except cache.STORE_ERRORS + storage.DATABASE_ERRORS as exc:
            self.log_error('%s', exc)
            self.send(503, {'error': '저장소를 사용할 수 없습니다. 잠시 후 다시 시도하세요.'})
        else:
            self.send(201, {'id': key})


def yard_sim_access(granted):
    """The shared replay reads the yard plan: whoever may read the erection, stocking and crane plans may watch and
    steer it (it writes nothing)."""
    if not set(yardsim.REQUIRED).issubset(granted['routes']):
        raise PermissionError('탑재·적치·크레인 계획을 조회할 권한이 있어야 야드 재생을 볼 수 있습니다.')


def yard_sim_rows(granted):
    """ERP rows the replay is built from (cached route reads); routes this user cannot read are empty."""
    return {route: json.loads(cached_rows(route)[0]) if route in granted['routes'] else [] for route in yardsim.ROUTES}


def connect_store():
    """Use Redis when SHIPYARD_REDIS_URL is set; PostgreSQL mode requires it."""
    url = os.environ.get('SHIPYARD_REDIS_URL')
    if DATABASE_URL and not url:
        raise SystemExit('PostgreSQL 모드에서는 SHIPYARD_REDIS_URL(Redis)이 필요합니다.')
    store = cache.connect(url)
    store.ping()
    return store


def bump_external_cache():
    """For scripts that change the database outside the server (seeds, migration, admin creation)."""
    url = os.environ.get('SHIPYARD_REDIS_URL')
    if url:
        cache.bump(cache.connect(url))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=8001)
    parser.add_argument('--db', type=Path, help='명시하면 SQLite 파일로 실행')
    parser.add_argument('--empty', action='store_true')
    parser.add_argument('--no-login', action='store_true', help='로컬 ERP 업무 화면을 로그인 없이 실행')
    args = parser.parse_args()
    LOCAL_NO_LOGIN = args.no_login
    if args.db:
        DATABASE = args.db
    else:
        DATABASE_URL = os.environ.get('SHIPYARD_DATABASE_URL')
    STORE = connect_store()
    with closing(connect()) as db, db:
        storage.init_schema(db, SCHEMA)
        if not DATABASE_URL:
            storage.begin_write(db)
        geometry.upgrade_location_footprints(db)
        geometry.upgrade_crane_kinematics(db)
        traceability.upgrade(db, insert, now())
        results.upgrade(db, insert)
        auth.ensure_groups(db, insert)
        if not args.empty:
            seed(db)
    invalidate()
    if not DATABASE_URL and not LOCAL_NO_LOGIN:
        with closing(connect()) as db:
            if not db.execute('SELECT 1 FROM accounts LIMIT 1').fetchone():
                print('관리자 계정이 없습니다: python3 create_admin.py --db <파일> 로 먼저 만드세요.', flush=True)
    bind_hosts = [h.strip() for h in os.environ.get('SHIPYARD_BIND_HOSTS', '127.0.0.1').split(',') if h.strip()]
    servers = [ThreadingHTTPServer((host, args.port), Handler) for host in bind_hosts]
    try:
        for server in servers:
            print(f'http://{server.server_address[0]}:{server.server_port}', flush=True)
        for server in servers[1:]:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        servers[0].serve_forever()
    finally:
        for server in servers:
            server.server_close()
