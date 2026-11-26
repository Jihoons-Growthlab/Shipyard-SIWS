"""Execution documents for the ERP's existing outfitting work plans."""
from datetime import date
from yardplan import RETURN_PHASE, SHORE_PHASES

SCHEMA = '''
CREATE TABLE IF NOT EXISTS outfit_conditions(
 id TEXT PRIMARY KEY REFERENCES outfit_works(id), block_stage TEXT REFERENCES block_stages(id),
 erection_required TEXT NOT NULL CHECK(erection_required IN ('필수','해당 없음')));
CREATE TABLE IF NOT EXISTS outfit_materials(
 id TEXT PRIMARY KEY, work TEXT NOT NULL REFERENCES outfit_works(id),
 requirement TEXT NOT NULL UNIQUE REFERENCES requirements(id));
CREATE INDEX IF NOT EXISTS outfit_materials_by_work ON outfit_materials(work);
CREATE INDEX IF NOT EXISTS outfit_works_by_block ON outfit_works(block);
CREATE TABLE IF NOT EXISTS outfit_item_links(
 id TEXT PRIMARY KEY REFERENCES outfitting_items(id), work TEXT NOT NULL REFERENCES outfit_works(id));
CREATE TABLE IF NOT EXISTS outfit_orders(
 id TEXT PRIMARY KEY, work TEXT NOT NULL UNIQUE REFERENCES outfit_works(id),
 system_code TEXT NOT NULL, planned_qty REAL NOT NULL, outfit_unit TEXT NOT NULL,
 drawing_revision TEXT REFERENCES drawing_revisions(id), issued_day TEXT NOT NULL,
 instruction TEXT NOT NULL, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outfit_installations(
 id TEXT PRIMARY KEY, outfit_order TEXT NOT NULL REFERENCES outfit_orders(id),
 work_day TEXT NOT NULL, installed_qty REAL NOT NULL, manhours INTEGER NOT NULL,
 note TEXT, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outfit_inspections(
 id TEXT PRIMARY KEY, outfit_installation TEXT NOT NULL REFERENCES outfit_installations(id),
 inspect_day TEXT NOT NULL, inspection TEXT NOT NULL, inspector TEXT NOT NULL,
 evidence TEXT NOT NULL, recorded_at TEXT NOT NULL);
'''
FIELDS = {
    'outfit_conditions': ('id', 'block_stage', 'erection_required'),
    'outfit_materials': ('id', 'work', 'requirement'),
    'outfit_item_links': ('id', 'work'),
    'outfit_orders': ('id', 'work', 'system_code', 'planned_qty', 'outfit_unit', 'drawing_revision',
                      'issued_day', 'instruction'),
    'outfit_installations': ('id', 'outfit_order', 'work_day', 'installed_qty', 'manhours', 'note'),
    'outfit_inspections': ('id', 'outfit_installation', 'inspect_day', 'inspection', 'inspector', 'evidence'),
}
PREFIXES = {'outfit_materials': 'OFM', 'outfit_orders': 'OFWO', 'outfit_installations': 'OFRES', 'outfit_inspections': 'OFIN'}
OPTIONAL = {'outfit_conditions': {'block_stage'}, 'outfit_orders': {'drawing_revision'}, 'outfit_installations': {'note'}}
DATES = {'issued_day', 'work_day', 'inspect_day'}
MEASURES = {'planned_qty', 'installed_qty'}
NUMBERS = MEASURES | {'manhours'}
CHOICES = {'erection_required': ['필수', '해당 없음'], 'outfit_unit': ['개', '세트', 'm', 'm²', 'kg']}
READS = set(FIELDS)

INSTALLATIONS = '''
SELECT i.*, o.work, o.outfit_unit, o.system_code, w.hull, w.block, w.work_phase,
 (SELECT q.inspection FROM outfit_inspections q WHERE q.outfit_installation=i.id
     ORDER BY q.recorded_at DESC,q.id DESC LIMIT 1) AS inspection
FROM outfit_installations i JOIN outfit_orders o ON o.id=i.outfit_order
JOIN outfit_works w ON w.id=o.work
'''


def quantities(db, order):
    row = db.execute(f'''SELECT COALESCE(SUM(installed_qty),0) AS installed,
        COALESCE(SUM(CASE WHEN inspection='합격' THEN installed_qty ELSE 0 END),0) AS accepted,
        COALESCE(SUM(manhours),0) AS actual_mh
        FROM ({INSTALLATIONS}) AS progress WHERE outfit_order=?''', (order,)).fetchone()
    return round(row['installed'], 3), round(row['accepted'], 3), row['actual_mh']


def project_progress(db, order):
    installed, accepted, _ = quantities(db, order['id'])
    complete = accepted >= order['planned_qty']
    pct = 100 if complete else min(99, int(100 * accepted / order['planned_qty']))
    db.execute('UPDATE outfit_works SET progress_pct=?,work_status=? WHERE id=?',
               (pct, '완료' if complete else '진행' if installed else '계획', order['work']))


def installation_blockers(db, work, as_of):
    condition = db.execute('SELECT * FROM outfit_conditions WHERE id=?', (work,)).fetchone()
    messages = []
    target = db.execute('SELECT * FROM outfit_works WHERE id=?', (work,)).fetchone()
    if target and target['work_phase'] in SHORE_PHASES:
        launch = db.execute("SELECT 1 FROM hull_milestones WHERE hull=? AND milestone='진수' AND actual<=?",
                            (target['hull'], as_of)).fetchone()
        move = db.execute('''SELECT q.shift_purpose, b.quay FROM quay_shifts q LEFT JOIN berths b ON b.id=q.to_point
            WHERE q.hull=? AND q.actual_day<=? ORDER BY q.actual_day DESC,q.id DESC LIMIT 1''',
                          (target['hull'], as_of)).fetchone()
        if not launch:
            messages.append('진수 실적 확인 필요')
        if not move or not move['quay'] or (target['work_area'] and move['quay'] != target['work_area']):
            messages.append('작업 안벽 접안 실적 확인 필요')
        if target['work_phase'] == RETURN_PHASE and not db.execute('''SELECT 1 FROM quay_shifts
                WHERE hull=? AND shift_purpose='시운전 귀항' AND actual_day<=?''',
                (target['hull'], as_of)).fetchone():
            messages.append('시운전 귀항 실적 확인 필요')
    if condition and condition['block_stage']:
        stage = db.execute('SELECT * FROM block_stages WHERE id=?', (condition['block_stage'],)).fetchone()
        if not stage['actual'] or stage['actual'] > as_of:
            messages.append('조립 단계 미완료 · ' + stage['assembly_stage'])
    for row in db.execute('''SELECT r.id,r.material,r.qty,m.unit,
        COALESCE((SELECT SUM(i.qty) FROM issues i WHERE i.requirement=r.id AND i.day<=?),0) AS issued_qty
        FROM outfit_materials l JOIN requirements r ON r.id=l.requirement JOIN materials m ON m.id=r.material
        WHERE l.work=? ORDER BY r.id''', (as_of, work)):
        if row['issued_qty'] < row['qty']:
            messages.append(f"자재 미출고 · {row['material']} {row['qty'] - row['issued_qty']}{row['unit']} ({row['id']})")
    return messages


def milestone_gate(db, hull, milestone, as_of, require):
    phases = {'시운전': ('안벽 의장', '시운전 준비'), '인도': (RETURN_PHASE,)}.get(milestone)
    if not phases:
        return
    if milestone == '인도' and db.execute("SELECT 1 FROM quay_shifts WHERE hull=? AND shift_purpose='시운전 귀항'",
                                          (hull,)).fetchone():
        require(db.execute("SELECT 1 FROM quay_shifts WHERE hull=? AND shift_purpose='시운전 귀항' AND actual_day<=?",
                           (hull, as_of)).fetchone(), '인도 전 시운전 귀항 실적을 확인하세요.')
    placeholders = ','.join('?' for _ in phases)
    for row in db.execute(f'''SELECT w.id, o.id AS order_id, o.planned_qty FROM outfit_works w
            LEFT JOIN outfit_orders o ON o.work=w.id
            WHERE w.hull=? AND w.work_phase IN ({placeholders}) AND (o.id IS NOT NULL OR w.work_phase=?)''',
            (hull, *phases, RETURN_PHASE)):
        require(row['order_id'], f'{milestone} 전 의장 작업지시 발행 필요 · {row["id"]}')
        require(accepted_as_of(db, row['order_id'], as_of) >= row['planned_qty'],
                f'{milestone} 전 의장 검사 미완료 · {row["id"]}')
    for row in db.execute('''SELECT DISTINCT o.id,o.planned_qty FROM outfitting_items i
        JOIN outfit_item_links l ON l.id=i.id JOIN outfit_orders o ON o.work=l.work
        JOIN outfit_works w ON w.id=o.work WHERE i.hull=? AND (w.work_phase<>? OR ?='인도')''',
                          (hull, RETURN_PHASE, milestone)):
        require(accepted_as_of(db, row['id'], as_of) >= row['planned_qty'],
                f'{milestone} 전 연결 의장품 검사 미완료 · {row["id"]}')


def accepted_as_of(db, order, as_of):
    accepted = db.execute('''SELECT COALESCE(SUM(i.installed_qty),0) AS qty FROM outfit_installations i
        WHERE i.outfit_order=? AND (SELECT q.inspection FROM outfit_inspections q
           WHERE q.outfit_installation=i.id AND q.inspect_day<=?
           ORDER BY q.recorded_at DESC,q.id DESC LIMIT 1)='합격' ''', (order, as_of)).fetchone()['qty']
    return round(accepted, 3)


def erection_blockers(db, block, plan):
    return [f"탑재 전 필수 의장 검사 미완료 · {row['id']} {row['outfit_trade']}"
            for row in db.execute('''SELECT w.id,w.outfit_trade FROM outfit_conditions c JOIN outfit_works w ON w.id=c.id
                LEFT JOIN outfit_orders o ON o.work=w.id WHERE w.block=? AND c.erection_required='필수'
                AND (o.id IS NULL OR w.work_status<>'완료') ORDER BY w.id''', (block['id'],))]


def read_materials(db):
    rows = []
    for row in db.execute('''SELECT l.*,w.hull,w.block,r.material,m.name AS material_name,m.unit,r.qty,r.need_day
            FROM outfit_materials l JOIN outfit_works w ON w.id=l.work
            JOIN requirements r ON r.id=l.requirement JOIN materials m ON m.id=r.material ORDER BY l.id'''):
        ref = row['requirement']
        purchases = [dict(doc) for doc in db.execute('''SELECT q.id AS request,p.id AS purchase_order,p.promised_day,
            q.qty AS requested_qty FROM purchase_requests q LEFT JOIN purchase_orders p ON p.request=q.id
            WHERE q.requirement=? ORDER BY q.id''', (ref,))]
        receipts = [dict(doc) for doc in db.execute('''SELECT g.id,g.qty,g.inspection,g.day FROM goods_receipts g
            JOIN purchase_orders p ON p.id=g.purchase_order JOIN purchase_requests q ON q.id=p.request
            WHERE q.requirement=? ORDER BY g.day,g.id''', (ref,))]
        issues = [dict(doc) for doc in db.execute(
            'SELECT id,qty,day FROM issues WHERE requirement=? ORDER BY day,id', (ref,))]
        received = sum(doc['qty'] for doc in receipts if doc['inspection'] == '합격')
        issued = sum(doc['qty'] for doc in issues)
        rows.append({**dict(row), 'received_qty': received, 'issued_qty': issued,
                     'remaining_qty': max(0, row['qty'] - issued), 'purchases': purchases,
                     'receipt_documents': receipts, 'issue_documents': issues})
    return rows


def write(db, action, values, api):
    lookup, require = api.lookup, api.require
    today = date.today().isoformat()
    if action == 'outfit_conditions':
        work = lookup(db, 'outfit_works', values['id'])
        require(values['erection_required'] in CHOICES['erection_required'], '탑재 필수 여부를 선택하세요.')
        if values['erection_required'] == '필수':
            require(work['block'] and work['work_phase'] == '선행 의장',
                    '블록에 연결된 선행 의장만 탑재 필수로 지정할 수 있습니다.')
        if values['block_stage']:
            stage = lookup(db, 'block_stages', values['block_stage'])
            require(stage['block'] == work['block'] and stage['assembly_stage'] != '탑재',
                    '같은 블록의 탑재 이전 조립 단계를 지정하세요.')
        db.execute('''INSERT INTO outfit_conditions(id,block_stage,erection_required) VALUES(?,?,?)
            ON CONFLICT(id) DO UPDATE SET block_stage=excluded.block_stage,erection_required=excluded.erection_required''',
                   (values['id'],values['block_stage'],values['erection_required']))
        return values['id']
    if action == 'outfit_materials':
        work = lookup(db, 'outfit_works', values['work'])
        req = lookup(db, 'requirements', values['requirement'])
        activity = lookup(db, 'activities', req['activity'])
        require(activity['hull'] == work['hull'] and activity['block'] == work['block'],
                '작업계획과 호선·블록이 같은 자재 소요를 연결하세요.')
        require(not db.execute('SELECT 1 FROM outfit_materials WHERE requirement=?',
                               (req['id'],)).fetchone(), '이미 의장 작업에 연결된 자재 소요입니다.')
        return api.insert(db, action, **values)
    if action == 'outfit_item_links':
        item = lookup(db, 'outfitting_items', values['id'])
        work = lookup(db, 'outfit_works', values['work'])
        require(item['hull'] == work['hull'] and item['block'] == work['block'],
                '같은 호선·블록의 의장품과 작업 패키지를 선택하세요.')
        return api.insert(db, action, **values)
    if action == 'outfit_orders':
        work = lookup(db, 'outfit_works', values['work'])
        require(work['work_status'] == '계획' and work['progress_pct'] == 0,
                '실적이 없는 계획 상태의 의장 작업만 발행할 수 있습니다.')
        require(work['work_phase'] != '선행 의장' or work['block'], '선행 의장 작업에는 블록이 필요합니다.')
        require(values['outfit_unit'] in CHOICES['outfit_unit'], '계획 수량 단위를 선택하세요.')
        require(values['issued_day'] <= today, '발행일은 오늘 이후일 수 없습니다.')
        if values['drawing_revision']:
            drawing = lookup(db, 'drawing_revisions', values['drawing_revision'])
            require(work['block'] and drawing['block'] == work['block'], '작업 블록의 도면 리비전을 선택하세요.')
        require(not db.execute('SELECT 1 FROM outfit_orders WHERE work=?', (work['id'],)).fetchone(),
                '이미 발행한 작업지시가 있습니다.')
    elif action == 'outfit_installations':
        order = lookup(db, 'outfit_orders', values['outfit_order'])
        require(order['issued_day'] <= values['work_day'] <= today, '작업일은 발행일 이후이고 오늘 이하여야 합니다.')
        blocked = db.execute('''SELECT 1 FROM outfit_links l JOIN outfit_works p ON p.id=l.predecessor_work
            WHERE l.work=? AND p.work_status<>'완료' LIMIT 1''', (order['work'],)).fetchone()
        require(not blocked, '선행 의장 작업을 먼저 완료하세요.')
        gates = installation_blockers(db, order['work'], values['work_day'])
        require(not gates, '의장 설치 조건 미충족: ' + ' / '.join(gates))
        installed, _, _ = quantities(db, order['id'])
        require(round(installed + values['installed_qty'], 3) <= order['planned_qty'], '누적 설치량이 계획 수량을 초과합니다.')
    else:
        installation = lookup(db, 'outfit_installations', values['outfit_installation'])
        order = lookup(db, 'outfit_orders', installation['outfit_order'])
        prior = db.execute('''SELECT * FROM outfit_inspections WHERE outfit_installation=?
            ORDER BY recorded_at DESC,id DESC LIMIT 1''', (installation['id'],)).fetchone()
        require(values['inspection'] in ('합격', '불합격'), '검사 결과를 선택하세요.')
        require(not prior or prior['inspection'] != '합격', '이미 검사에 합격한 실적입니다.')
        earliest = prior['inspect_day'] if prior else installation['work_day']
        require(earliest <= values['inspect_day'] <= today, '검사일은 작업일 또는 이전 검사일 이후이고 오늘 이하여야 합니다.')
    key = api.insert(db, action, **values, recorded_at=api.now())
    if action != 'outfit_orders':
        project_progress(db, order)
    return key


def read(db, route):
    if route == 'outfit_item_links':
        return [dict(row) for row in db.execute('''SELECT l.*,i.hull,i.name FROM outfit_item_links l
            JOIN outfitting_items i ON i.id=l.id ORDER BY l.id''')]
    if route == 'outfit_materials':
        return read_materials(db)
    if route == 'outfit_conditions':
        return [{**dict(row), 'installation_blockers': installation_blockers(db, row['id'], date.today().isoformat())}
                for row in db.execute('''SELECT c.*,w.hull,w.block,s.assembly_stage,s.actual AS assembly_completed_day
                    FROM outfit_conditions c JOIN outfit_works w ON w.id=c.id
                    LEFT JOIN block_stages s ON s.id=c.block_stage ORDER BY c.id''')]
    if route == 'outfit_installations':
        return [dict(row) for row in db.execute(INSTALLATIONS + ' ORDER BY i.work_day,i.id')]
    if route == 'outfit_inspections':
        return [dict(row) for row in db.execute('''SELECT q.*, i.outfit_order, w.hull, w.block, o.work
            FROM outfit_inspections q JOIN outfit_installations i ON i.id=q.outfit_installation
            JOIN outfit_orders o ON o.id=i.outfit_order JOIN outfit_works w ON w.id=o.work
            ORDER BY q.inspect_day,q.id''')]
    rows = []
    for row in db.execute('''SELECT o.*, w.hull,w.block,w.outfit_zone,w.outfit_trade,w.work_phase,w.work_area,
        w.start_day,w.end_day,w.planned_mh,w.progress_pct,w.work_status
        FROM outfit_orders o JOIN outfit_works w ON w.id=o.work ORDER BY o.id'''):
        installed, accepted, mh = quantities(db, row['id'])
        rows.append({**dict(row), 'installed_qty': installed, 'accepted_qty': accepted, 'actual_mh': mh})
    return rows
