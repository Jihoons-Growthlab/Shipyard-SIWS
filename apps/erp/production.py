"""Block assembly stages, man-hour progress, outfitting items, drawing revisions and erection records."""
from datetime import date

ASSEMBLY_STAGES = ['절단·가공', '소조립', '중조립', '대조립', '선행탑재(PE)', '탑재']
PRE_ERECTION = ASSEMBLY_STAGES[:-1]
JOINING_FROM = ASSEMBLY_STAGES.index('중조립')
OUTFIT_EVENTS = ['입고', '설치', '검사 완료']
# Checks registered by other modules before a block assembly stage is completed: fn(db, stage_row, day, require).
STAGE_CHECKS = []
CHOICES = {
    'assembly_stage': ASSEMBLY_STAGES,
    'item_type': ['배관 스풀', '기자재', '전장품', '철의장품', '기타'],
    'outfit_stage': ['선행의장', '후행의장'],
    'event': OUTFIT_EVENTS,
    'change_type': ['최초 출도', '설계 변경'],
}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS contractors(id TEXT PRIMARY KEY, name TEXT NOT NULL, trade TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS block_stages(id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id),
 assembly_stage TEXT NOT NULL, center TEXT NOT NULL REFERENCES centers(id), planned_mh INTEGER NOT NULL,
 baseline TEXT NOT NULL, forecast TEXT NOT NULL, actual TEXT, UNIQUE(block, assembly_stage));
CREATE TABLE IF NOT EXISTS work_logs(id TEXT PRIMARY KEY, block_stage TEXT NOT NULL REFERENCES block_stages(id),
 work_day TEXT NOT NULL, manhours INTEGER NOT NULL, progress_pct INTEGER NOT NULL,
 contractor TEXT REFERENCES contractors(id), note TEXT, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outfitting_items(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), item_type TEXT NOT NULL, name TEXT NOT NULL, outfit_stage TEXT NOT NULL,
 required_day TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outfitting_events(id TEXT PRIMARY KEY, item TEXT NOT NULL REFERENCES outfitting_items(id),
 event TEXT NOT NULL, event_day TEXT NOT NULL, evidence TEXT NOT NULL, recorded_at TEXT NOT NULL, UNIQUE(item, event));
CREATE TABLE IF NOT EXISTS drawing_revisions(id TEXT PRIMARY KEY, drawing_no TEXT NOT NULL,
 block TEXT NOT NULL REFERENCES blocks(id), revision TEXT NOT NULL, issued_day TEXT NOT NULL,
 change_type TEXT NOT NULL, description TEXT NOT NULL, recorded_at TEXT NOT NULL, UNIQUE(drawing_no, revision));
CREATE TABLE IF NOT EXISTS erection_records(id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id),
 dock TEXT NOT NULL REFERENCES locations(id), erect_day TEXT NOT NULL, inspection TEXT NOT NULL,
 evidence TEXT NOT NULL, recorded_at TEXT NOT NULL);
'''
FIELDS = {
    'contractors': ('id', 'name', 'trade'),
    'block_stages': ('id', 'block', 'assembly_stage', 'center', 'planned_mh', 'baseline', 'forecast'),
    'work_logs': ('id', 'block_stage', 'work_day', 'manhours', 'progress_pct', 'contractor', 'note'),
    'outfitting_items': ('id', 'hull', 'block', 'item_type', 'name', 'outfit_stage', 'required_day'),
    'outfitting_events': ('id', 'item', 'event', 'event_day', 'evidence'),
    'drawing_revisions': ('id', 'drawing_no', 'block', 'revision', 'issued_day', 'change_type', 'description'),
    'erection_records': ('id', 'block', 'dock', 'erect_day', 'inspection', 'evidence'),
}
PREFIXES = {'block_stages': 'BS', 'work_logs': 'MH', 'outfitting_items': 'OF', 'outfitting_events': 'OFE',
            'drawing_revisions': 'REV', 'erection_records': 'ER'}
OPTIONAL = {'work_logs': {'contractor', 'note'}, 'outfitting_items': {'block'}}
DATES = {'work_day', 'event_day', 'required_day', 'issued_day', 'erect_day'}
NUMBERS = {'planned_mh', 'manhours', 'progress_pct'}
READS = set(FIELDS) | {'block_progress', 'hull_progress', 'contractor_summary'}


def stage_rows(db, block):
    return {row['assembly_stage']: dict(row) for row in
            db.execute('SELECT * FROM block_stages WHERE block=?', (block,))}


def children(db, block):
    return [row['child_block'] for row in
            db.execute('SELECT child_block FROM block_structure WHERE parent_block=?', (block,))]


def erected(db, block, as_of=None):
    """Day of the latest passing erection record, or None."""
    row = db.execute('''SELECT inspection, erect_day FROM erection_records WHERE block=? AND erect_day<=?
        ORDER BY recorded_at DESC, id DESC LIMIT 1''', (block, as_of or '9999-12-31')).fetchone()
    return row['erect_day'] if row and row['inspection'] == '합격' else None


def complete_stage(db, stage, day, require):
    stages = stage_rows(db, stage['block'])
    index = ASSEMBLY_STAGES.index(stage['assembly_stage'])
    for earlier in ASSEMBLY_STAGES[:index]:
        row = stages.get(earlier)
        require(not row or (row['actual'] and row['actual'] <= day), f'앞 조립 단계 {earlier}을(를) 먼저 완료하세요.')
    if index >= JOINING_FROM:
        for child in children(db, stage['block']):
            for row in stage_rows(db, child).values():
                require(row['assembly_stage'] == '탑재' or (row['actual'] and row['actual'] <= day),
                        f'구성 블록 {child}의 {row["assembly_stage"]} 단계를 먼저 완료하세요.')
    if stage['assembly_stage'] == '탑재':
        require(erected(db, stage['block']), '탑재 검사 합격 기록을 먼저 등록하세요.')
    for check in STAGE_CHECKS:
        check(db, stage, day, require)
    db.execute('UPDATE block_stages SET actual=? WHERE id=?', (day, stage['id']))


def same_hull(db, lookup, require, hull, block):
    if block:
        require(lookup(db, 'blocks', block)['hull'] == hull, '호선과 블록이 일치해야 합니다.')


def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    today = date.today().isoformat()
    for key, values in CHOICES.items():
        if key in v:
            require(v[key] in values, f'{key} 값을 목록에서 선택하세요.')
    if action == 'block_stages':
        require(v['block'], '블록을 선택하세요.')
        lookup(db, 'blocks', v['block'])
    if action == 'work_logs':
        stage = lookup(db, 'block_stages', v['block_stage'])
        require(not stage['actual'], '이미 완료된 조립 단계입니다.')
        require(v['work_day'] <= today, '실적일은 오늘 이후일 수 없습니다.')
        require(v['progress_pct'] <= 100, '누적 진척률은 1~100 사이로 입력하세요.')
        prior = db.execute('SELECT MAX(progress_pct) AS pct, MAX(work_day) AS last_day FROM work_logs WHERE block_stage=?',
                           (stage['id'],)).fetchone()
        require(v['progress_pct'] >= (prior['pct'] or 0), '누적 진척률은 이전 실적보다 작을 수 없습니다.')
        require(v['work_day'] >= (prior['last_day'] or v['work_day']), '이전 실적일 이후 날짜를 입력하세요.')
        if v['progress_pct'] == 100:
            complete_stage(db, stage, v['work_day'], require)
        v['recorded_at'] = api.now()
    if action == 'outfitting_items':
        require(v['outfit_stage'] != '선행의장' or v['block'], '선행의장품은 설치할 블록을 지정하세요.')
        same_hull(db, lookup, require, v['hull'], v['block'])
    if action == 'outfitting_events':
        lookup(db, 'outfitting_items', v['item'])
        require(not db.execute('''SELECT 1 FROM outfit_item_links l JOIN outfit_orders o ON o.work=l.work
                                  WHERE l.id=?''', (v['item'],)).fetchone(),
                '연결된 의장 작업지시에서 설치·검사를 등록하세요.')
        done = list(db.execute('SELECT * FROM outfitting_events WHERE item=? ORDER BY event_day, id', (v['item'],)))
        require(len(done) < len(OUTFIT_EVENTS), '모든 단계를 마친 의장품입니다.')
        expected = OUTFIT_EVENTS[len(done)]
        require(v['event'] == expected, f'다음 단계는 {expected}입니다.')
        require(v['event_day'] <= today, '진행일은 오늘 이후일 수 없습니다.')
        require(not done or v['event_day'] >= done[-1]['event_day'], '이전 단계 이후 날짜를 입력하세요.')
        v['recorded_at'] = api.now()
    if action == 'drawing_revisions':
        require(v['block'], '블록을 선택하세요.')
        lookup(db, 'blocks', v['block'])
        exists = db.execute('SELECT 1 FROM drawing_revisions WHERE drawing_no=?', (v['drawing_no'],)).fetchone()
        if v['change_type'] == '최초 출도':
            require(not exists, '이미 출도된 도면입니다. 설계 변경으로 등록하세요.')
        else:
            require(exists, '최초 출도 기록이 있는 도면만 설계 변경할 수 있습니다.')
        v['recorded_at'] = api.now()
    if action == 'erection_records':
        require(v['block'], '블록을 선택하세요.')
        block = lookup(db, 'blocks', v['block'])
        plan = db.execute('SELECT * FROM erection_plans WHERE block=?', (v['block'],)).fetchone()
        require(plan, '탑재 계획을 먼저 등록하세요.')
        require(plan['destination'] == v['dock'], '탑재 계획의 목적 시설과 같아야 합니다.')
        require(v['inspection'] in {'합격', '불합격'}, '검사 결과를 선택하세요.')
        require(v['erect_day'] <= today, '탑재일은 오늘 이후일 수 없습니다.')
        require(not erected(db, v['block']), '이미 탑재 검사에 합격한 블록입니다.')
        if v['inspection'] == '합격':
            moving = db.execute('SELECT 1 FROM moves WHERE block=? AND confirmed_at IS NULL', (v['block'],)).fetchone()
            require(block['location'] == v['dock'] and not moving, '블록을 탑재 시설로 이동 확정한 뒤 기록하세요.')
            state = next(row for row in api.shipbuilding.readiness(db) if row['id'] == v['block'])
            require(state['readiness'] == '준비 확인', '탑재 준비가 끝나지 않았습니다: ' + state['blockers'])
            for prior in db.execute('SELECT predecessor_block FROM erection_links WHERE successor_block=?', (v['block'],)):
                require((erected(db, prior['predecessor_block']) or '9999-12-31') <= v['erect_day'],
                        '선행 블록 탑재일 이후로 입력하세요.')
        v['recorded_at'] = api.now()
    return insert(db, action, **v)


def blockers(db, block, plan):
    out = []
    stages = stage_rows(db, block['id'])
    pending = [name for name in PRE_ERECTION if name in stages and not stages[name]['actual']]
    if pending:
        out.append('조립 단계 미완료 ' + ', '.join(pending))
    items = db.execute('''SELECT COUNT(*) FROM outfitting_items i WHERE i.block=? AND i.outfit_stage='선행의장'
        AND NOT EXISTS (SELECT 1 FROM outfitting_events e WHERE e.item=i.id AND e.event IN ('설치','검사 완료'))''',
                       (block['id'],)).fetchone()[0]
    if items:
        out.append(f'선행의장품 미설치 {items}건')
    revision = db.execute('SELECT * FROM drawing_revisions WHERE block=? ORDER BY recorded_at DESC, id DESC LIMIT 1',
                          (block['id'],)).fetchone()
    if revision:
        check = db.execute('''SELECT recorded_at FROM block_gate_checks WHERE block=? AND gate='도면 출도'
            AND verdict='확인' ORDER BY recorded_at DESC LIMIT 1''', (block['id'],)).fetchone()
        if not check or check['recorded_at'] < revision['recorded_at']:
            out.append(f'최신 도면 {revision["drawing_no"]} {revision["revision"]} 출도 확인 필요')
    return out


def milestone_gate(db, hull, milestone, as_of, require):
    planned = [row['block'] for row in db.execute('''SELECT DISTINCT p.block FROM erection_plans p
        JOIN blocks b ON b.id=p.block WHERE b.hull=?''', (hull,))]
    done = [block for block in planned if erected(db, block, as_of)]
    if milestone == '기공':
        require(done, '기공 실적에는 첫 블록의 탑재 검사 합격 기록이 필요합니다.')
    if milestone == '진수':
        require(planned, '진수 실적을 등록하려면 탑재 계획이 필요합니다.')
        require(len(done) == len(planned), f'진수 전 탑재 미완료 블록 {len(planned) - len(done)}개가 있습니다.')
    if milestone in {'시운전', '인도'}:
        open_items = db.execute('''SELECT COUNT(*) FROM outfitting_items i WHERE i.hull=? AND NOT EXISTS
            (SELECT 1 FROM outfit_item_links l JOIN outfit_orders o ON o.work=l.work WHERE l.id=i.id) AND NOT EXISTS
            (SELECT 1 FROM outfitting_events e WHERE e.item=i.id AND e.event='검사 완료' AND e.event_day<=?)''',
                                (hull, as_of)).fetchone()[0]
        require(not open_items, f'검사 완료되지 않은 의장품 {open_items}건이 있습니다.')


def progress_rows(db):
    weights = {row['block']: row['weight_t'] for row in db.execute('SELECT block, weight_t FROM block_details')}
    logs = {}
    for row in db.execute('SELECT * FROM work_logs ORDER BY work_day, id'):
        item = logs.setdefault(row['block_stage'], {'mh': 0, 'pct': 0})
        item['mh'] += row['manhours']
        item['pct'] = max(item['pct'], row['progress_pct'])
    blocks = {}
    for stage in db.execute('SELECT s.*, b.hull FROM block_stages s JOIN blocks b ON b.id=s.block ORDER BY s.block, s.id'):
        log = logs.get(stage['id'], {'mh': 0, 'pct': 0})
        pct = 100 if stage['actual'] else log['pct']
        row = blocks.setdefault(stage['block'], {'block': stage['block'], 'hull': stage['hull'], 'stages': 0,
                                                 'completed_stages': 0, 'planned_mh': 0, 'actual_mh': 0, 'earned_mh': 0.0,
                                                 'current_stage': None})
        row['stages'] += 1
        row['completed_stages'] += bool(stage['actual'])
        row['planned_mh'] += stage['planned_mh']
        row['actual_mh'] += log['mh']
        row['earned_mh'] += stage['planned_mh'] * pct / 100
        if not stage['actual'] and row['current_stage'] is None:
            row['current_stage'] = stage['assembly_stage']
    output = []
    for row in blocks.values():
        ratio = row['earned_mh'] / row['planned_mh'] if row['planned_mh'] else 0
        weight = weights.get(row['block'])
        output.append({**row, 'earned_mh': round(row['earned_mh'], 1), 'progress_pct': round(ratio * 100, 1),
                       'weight_t': weight, 'earned_t': round(weight * ratio, 1) if weight else None,
                       'productivity': round(row['earned_mh'] / row['actual_mh'], 2) if row['actual_mh'] else None})
    order = {name: index for index, name in enumerate(ASSEMBLY_STAGES)}
    output.sort(key=lambda item: (item['hull'], order.get(item['current_stage'], 99), item['block']))
    return output


def read(db, table):
    if table == 'block_progress':
        return progress_rows(db)
    if table == 'hull_progress':
        totals = {}
        for row in progress_rows(db):
            item = totals.setdefault(row['hull'], {'hull': row['hull'], 'blocks': 0, 'weight_t': 0.0, 'earned_t': 0.0,
                                                   'planned_mh': 0, 'actual_mh': 0, 'earned_mh': 0.0})
            item['blocks'] += 1
            item['weight_t'] += row['weight_t'] or 0
            item['earned_t'] += row['earned_t'] or 0
            for key in ('planned_mh', 'actual_mh', 'earned_mh'):
                item[key] += row[key]
        return [{**item, 'weight_t': round(item['weight_t'], 1), 'earned_t': round(item['earned_t'], 1),
                 'earned_mh': round(item['earned_mh'], 1),
                 'weight_progress_pct': round(item['earned_t'] / item['weight_t'] * 100, 1) if item['weight_t'] else None,
                 'mh_progress_pct': round(item['earned_mh'] / item['planned_mh'] * 100, 1) if item['planned_mh'] else None,
                 'productivity': round(item['earned_mh'] / item['actual_mh'], 2) if item['actual_mh'] else None}
                for item in totals.values()]
    if table == 'contractor_summary':
        rows = db.execute('''SELECT COALESCE(w.contractor, '직영') AS contractor, b.hull, COUNT(*) AS logs,
            SUM(w.manhours) AS actual_mh FROM work_logs w JOIN block_stages s ON s.id=w.block_stage
            JOIN blocks b ON b.id=s.block GROUP BY COALESCE(w.contractor, '직영'), b.hull ORDER BY 1, 2''')
        names = {row['id']: row for row in db.execute('SELECT * FROM contractors')}
        return [{**dict(row), 'name': names[row['contractor']]['name'] if row['contractor'] in names else '직영',
                 'trade': names[row['contractor']]['trade'] if row['contractor'] in names else ''} for row in rows]
    if table == 'block_stages':
        order = {name: index for index, name in enumerate(ASSEMBLY_STAGES)}
        logs = {}
        for row in db.execute('SELECT * FROM work_logs ORDER BY work_day, id'):
            item = logs.setdefault(row['block_stage'], [0, 0])
            item[0] += row['manhours']
            item[1] = max(item[1], row['progress_pct'])
        rows = [dict(row) for row in db.execute('SELECT * FROM block_stages')]
        for row in rows:
            row['actual_mh'], row['progress_pct'] = logs.get(row['id'], [0, 0])
            if row['actual']:
                row['progress_pct'] = 100
        return sorted(rows, key=lambda row: (row['block'], order[row['assembly_stage']]))
    if table == 'outfitting_items':
        latest = {}
        for row in db.execute('SELECT * FROM outfitting_events ORDER BY event_day, recorded_at'):
            latest[row['item']] = row['event']
        linked = {row['id']: row for row in db.execute('''SELECT l.id,l.work,w.work_status,o.id AS outfit_order
            FROM outfit_item_links l JOIN outfit_works w ON w.id=l.work LEFT JOIN outfit_orders o ON o.work=w.id''')}
        status = {'완료': '검사 완료', '진행': '설치·검사 진행', '계획': '작업지시 발행', '보류': '보류'}
        return [{**dict(row), 'work': linked[row['id']]['work'] if row['id'] in linked else None,
                 'outfit_order': linked[row['id']]['outfit_order'] if row['id'] in linked else None,
                 'status': status[linked[row['id']]['work_status']]
                 if row['id'] in linked and linked[row['id']]['outfit_order'] else latest.get(row['id'], '미입고')}
                for row in db.execute('SELECT * FROM outfitting_items ORDER BY id')]
    return None
