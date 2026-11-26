"""Production reporting lot, inspection attempts, and trace evidence for work orders."""
from collections import defaultdict
from datetime import date, datetime, timedelta, timezone
import re

import traceability

SCHEMA = '''
CREATE TABLE IF NOT EXISTS production_lots(
 id TEXT PRIMARY KEY, work_order TEXT NOT NULL UNIQUE REFERENCES orders(id));
CREATE INDEX IF NOT EXISTS results_by_order ON production_results(work_order, actual, id);
CREATE TABLE IF NOT EXISTS production_result_details(
 result_id TEXT PRIMARY KEY REFERENCES production_results(id),
 recorded_by TEXT, actual_start_at TEXT, actual_end_at TEXT, shift_code TEXT,
 work_area TEXT REFERENCES locations(id), equipment TEXT REFERENCES equipment(id),
 output_qty REAL, output_unit TEXT, labor_mh REAL, inspection_evidence TEXT);
'''
DETAIL_FIELDS = ('actual_start_at', 'actual_end_at', 'shift_code', 'work_area', 'equipment',
                 'output_qty', 'output_unit', 'labor_mh', 'inspection_evidence')
SHIFTS = ('주간', '야간', '교대 외')
UNITS = ('개', '장', '세트', 'm', 'm²', 'kg', 't')
BUSINESS_TZ = timezone(timedelta(hours=9))


def work_timestamp(value, require):
    if value is None:
        return None
    require(re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}', value),
            '작업 시각을 YYYY-MM-DDTHH:mm 형식으로 입력하세요.')
    return datetime.fromisoformat(value).replace(tzinfo=BUSINESS_TZ)


def validate(db, values, api):
    require = api.require
    details = {key: values[key] for key in DETAIL_FIELDS}
    require(api.day(values['actual']) <= datetime.now(BUSINESS_TZ).date(), '실적일은 오늘 이후일 수 없습니다.')
    start, end = (work_timestamp(details[key], require) for key in DETAIL_FIELDS[:2])
    require(not start or start.date() <= date.fromisoformat(values['actual']), '작업 시작일이 실적일보다 늦습니다.')
    require(not end or end.date().isoformat() == values['actual'], '작업 종료일과 실적일이 같아야 합니다.')
    require(not (start and end) or start <= end, '작업 종료 시각은 시작 시각 이후여야 합니다.')
    require(all(value is None or value <= datetime.now(BUSINESS_TZ) for value in (start, end)),
            '작업 시각은 현재 이후일 수 없습니다.')
    details['actual_start_at'] = start.isoformat() if start else None
    details['actual_end_at'] = end.isoformat() if end else None
    require(details['shift_code'] is None or details['shift_code'] in SHIFTS, '작업 조를 목록에서 선택하세요.')
    qty, unit = details['output_qty'], details['output_unit']
    require((qty is None and unit is None) or (qty is not None and qty >= 0 and unit in UNITS),
            '실적 수량은 0 이상으로 입력하고 단위를 선택하세요.')
    require(details['labor_mh'] is None or details['labor_mh'] >= 0, '실적 공수는 0 이상이어야 합니다.')
    for field, table in (('work_area', 'locations'), ('equipment', 'equipment')):
        if details[field]:
            api.lookup(db, table, details[field])
    return details


def ensure_lot(db, insert, work_order):
    # All writes, including the upgrade, run under the existing database write lock.
    existing = db.execute('SELECT id FROM production_lots WHERE work_order=?', (work_order,)).fetchone()
    if existing:
        return existing['id']
    return insert(db, 'production_lots', id=traceability.document_number(db, 'production_lots'),
                  work_order=work_order)


def upgrade(db, insert):
    """Preserve result IDs, reconstruct missing final rows from completed orders, and assign stable lots."""
    for row in db.execute('''SELECT o.id, o.actual, o.result, o.inspection, a.actual_reason
        FROM orders o JOIN activities a ON a.id=o.activity
        WHERE o.actual IS NOT NULL AND o.result IS NOT NULL AND o.inspection IN ('합격','불합격')
        AND NOT EXISTS (SELECT 1 FROM production_results r WHERE r.work_order=o.id) ORDER BY o.id'''):
        insert(db, 'production_results', id=traceability.document_number(db, 'production_results'),
               work_order=row['id'], actual=row['actual'], result=row['result'],
               inspection=row['inspection'], reason=row['actual_reason'] or '', recorded_at=None)
    for row in db.execute('''SELECT DISTINCT r.work_order FROM production_results r
        WHERE NOT EXISTS (SELECT 1 FROM production_lots l WHERE l.work_order=r.work_order)
        ORDER BY r.work_order'''):
        ensure_lot(db, insert, row['work_order'])


def read(db):
    rows = [dict(row) for row in db.execute('''SELECT r.id, l.id AS lot_no, r.work_order, a.hull, a.block,
        a.stage, d.assembly AS block_assembly, r.actual, r.inspection, o.cadence,
        r.result, r.reason, x.output_qty, x.output_unit, x.labor_mh, x.shift_code,
        x.actual_start_at, x.actual_end_at, x.work_area, x.equipment, x.inspection_evidence,
        op.center, cl.location AS planned_work_area, o.day AS planned_order_day, o.parent AS parent_order,
        o.activity, p.plan_id,
        (SELECT b.mbom FROM order_boms b WHERE b.work_order=o.id
            ORDER BY b.linked_at DESC,b.id DESC LIMIT 1) AS mbom,
        d.zone, d.drawing, d.weight_t, r.recorded_at,
        COALESCE(x.recorded_by,(SELECT NULLIF(au.user_id,'') FROM audit_log au
            WHERE au.action='results' AND au.document=r.id ORDER BY au.recorded_at,au.id LIMIT 1)) AS recorded_by
        FROM production_results r JOIN orders o ON o.id=r.work_order JOIN activities a ON a.id=o.activity
        LEFT JOIN production_lots l ON l.work_order=o.id
        LEFT JOIN production_result_details x ON x.result_id=r.id
        LEFT JOIN block_details d ON d.block=a.block LEFT JOIN operations op ON op.activity=a.id
        LEFT JOIN center_locations cl ON cl.center=op.center LEFT JOIN order_plans p ON p.work_order=o.id
        ORDER BY r.id''')]
    issues = defaultdict(list)
    for record in db.execute('''SELECT i.work_order, i.id AS issue, i.day, i.qty, r.material,
        m.unit, il.lot, il.qty AS lot_qty, rl.heat_no, rl.cert_no, rl.receipt
        FROM issues i JOIN requirements r ON r.id=i.requirement JOIN materials m ON m.id=r.material
        LEFT JOIN issue_lots il ON il.issue=i.id LEFT JOIN receipt_lots rl ON rl.id=il.lot
        ORDER BY i.work_order,i.day,i.id,il.lot'''):
        issues[record['work_order']].append(dict(record))
    attempts = defaultdict(int)
    # Historical imports may have no recording timestamp. Retain their actual date and do not infer an attempt number.
    for row in sorted(rows, key=lambda r: (r['recorded_at'] or '', r['id'])):
        order = row['work_order']
        attempts[order] += 1
        row['inspection_sequence'] = attempts[order] if row['recorded_at'] else None
        issue_order = row['parent_order'] or order
        evidence = [item for item in issues[issue_order] if item['day'] <= row['actual']]
        quantities = {item['issue']: item['qty'] for item in evidence}
        assigned = defaultdict(int)
        for item in evidence:
            assigned[item['issue']] += item['lot_qty'] or 0
        row['issued_lot_status'] = ('출고 기록 없음' if not evidence else
                                   '로트 미배정' if not any(assigned.values()) else
                                   '일부 배정' if any(assigned[key] < qty for key, qty in quantities.items())
                                   else '배정 확인')
        row['issued_lot_ids'] = ', '.join(sorted({item['lot'] for item in evidence if item['lot']}))
        row['issued_heat_nos'] = ', '.join(sorted({item['heat_no'] for item in evidence if item['heat_no']}))
        row['issued_lots'] = [{key: item[key] for key in ('issue', 'day', 'material', 'unit', 'lot',
                              'lot_qty', 'heat_no', 'cert_no', 'receipt')} for item in evidence if item['lot']]
    return rows
