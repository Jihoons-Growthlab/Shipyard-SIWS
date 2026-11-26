"""Ship milestones, block dependencies and evidence-based erection readiness."""
import production

# Extra readiness checks registered by other modules: fn(db, block_row, erection_plan_row_or_None) -> [str]
EXTRA_BLOCKERS = []

MILESTONES = ['착공', '기공', '진수', '시운전', '인도']
GATES = ['도면 출도', '자재 준비', '조립 완료', '의장 완료', '도장 완료', '검사 승인', '시설 확보', '이동 준비']
SCHEMA = '''
CREATE TABLE IF NOT EXISTS hull_milestones(
 id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id), milestone TEXT NOT NULL,
 baseline TEXT NOT NULL, forecast TEXT NOT NULL, actual TEXT, reason TEXT NOT NULL,
 UNIQUE(hull,milestone));
CREATE TABLE IF NOT EXISTS milestone_history(
 id TEXT PRIMARY KEY, milestone_id TEXT NOT NULL REFERENCES hull_milestones(id),
 forecast TEXT NOT NULL, actual TEXT, reason TEXT NOT NULL, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS block_structure(
 id TEXT PRIMARY KEY, parent_block TEXT NOT NULL REFERENCES blocks(id),
 child_block TEXT NOT NULL UNIQUE REFERENCES blocks(id), UNIQUE(parent_block,child_block));
CREATE TABLE IF NOT EXISTS erection_links(
 id TEXT PRIMARY KEY, predecessor_block TEXT NOT NULL REFERENCES blocks(id),
 successor_block TEXT NOT NULL REFERENCES blocks(id), UNIQUE(predecessor_block,successor_block));
CREATE TABLE IF NOT EXISTS erection_plans(
 id TEXT PRIMARY KEY, block TEXT NOT NULL UNIQUE REFERENCES blocks(id),
 destination TEXT NOT NULL REFERENCES locations(id), baseline TEXT NOT NULL, forecast TEXT NOT NULL, reason TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS block_gate_checks(
 id TEXT PRIMARY KEY, block TEXT NOT NULL REFERENCES blocks(id), gate TEXT NOT NULL,
 verdict TEXT NOT NULL, evidence TEXT NOT NULL, recorded_at TEXT NOT NULL);
'''
FIELDS = {
 'hull_milestones': ('id','hull','milestone','baseline','forecast','reason'),
 'milestone_update': ('id','forecast','actual','reason'),
 'block_structure': ('id','parent_block','child_block'),
 'erection_links': ('id','predecessor_block','successor_block'),
 'erection_plans': ('id','block','destination','baseline','forecast','reason'),
 'block_gate_checks': ('id','block','gate','verdict','evidence'),
}
PREFIXES = {'hull_milestones':'MS','milestone_history':'MSH','block_structure':'BST',
            'erection_links':'EL','erection_plans':'EP','block_gate_checks':'GATE'}
READS = set(FIELDS) - {'milestone_update'} | {'milestone_history','block_readiness'}


def validate_edge(db, table, source, target, source_column, target_column, lookup, require):
    require(source != target, '동일 블록을 연결할 수 없습니다.')
    require(lookup(db,'blocks',source)['hull'] == lookup(db,'blocks',target)['hull'], '같은 호선의 블록을 연결하세요.')
    edges = {}
    for row in db.execute(f'SELECT * FROM {table}'):
        edges.setdefault(row[source_column], []).append(row[target_column])
    pending, seen = [target], set()
    while pending:
        node = pending.pop()
        require(node != source, '블록 관계가 순환합니다.')
        if node not in seen:
            seen.add(node)
            pending.extend(edges.get(node, []))


def check_milestone_order(db, milestone, actual, require):
    order = ('기공', '진수', '시운전', '인도')
    if milestone['milestone'] not in order:
        return
    position = order.index(milestone['milestone'])
    for row in db.execute('SELECT milestone, actual FROM hull_milestones WHERE hull=? AND id<>?',
                          (milestone['hull'], milestone['id'])):
        if row['milestone'] not in order:
            continue
        index = order.index(row['milestone'])
        if index < position:
            require(row['actual'] and row['actual'] <= actual, f'{row["milestone"]} 실적을 먼저 확인하세요.')
        elif index > position and row['actual']:
            require(actual <= row['actual'], f'{row["milestone"]} 실적일보다 늦을 수 없습니다.')


def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    if action == 'milestone_update':
        original = lookup(db,'hull_milestones',v['id'])
        require(v['forecast'] == original['forecast'] and v['actual'] == original['actual'] or v['reason'],
                '일정 변경 사유를 입력하세요.')
        db.execute('UPDATE hull_milestones SET forecast=?,actual=?,reason=? WHERE id=?',
                   (v['forecast'],v['actual'],v['reason'],v['id']))
        return insert(db,'milestone_history',id=api.traceability.document_number(db,'milestone_history'),
                      milestone_id=v['id'], forecast=v['forecast'],actual=v['actual'],reason=v['reason'],recorded_at=api.now())
    if action == 'hull_milestones':
        require(v['milestone'] in MILESTONES, '주요 일정을 선택하세요.')
    if action in {'hull_milestones','erection_plans'}:
        require(v['forecast'] <= v['baseline'] or v['reason'], '예정 지연 사유를 입력하세요.')
    if action == 'block_structure':
        validate_edge(db,action,v['parent_block'],v['child_block'],'parent_block','child_block',lookup,require)
    if action == 'erection_links':
        validate_edge(db,action,v['predecessor_block'],v['successor_block'],'predecessor_block','successor_block',lookup,require)
    if action == 'erection_plans':
        require(lookup(db,'locations',v['destination'])['kind'] in {'도크','야드'}, '탑재 시설은 도크 또는 야드로 지정하세요.')
    if action == 'block_gate_checks':
        require(v['gate'] in GATES, '준비 항목을 선택하세요.')
        require(v['verdict'] in {'확인','보류','해당 없음'}, '판정을 선택하세요.')
        v['recorded_at'] = api.now()
    return insert(db,action,**v)


def readiness(db):
    checks = {}
    for row in db.execute('SELECT * FROM block_gate_checks ORDER BY recorded_at,id'):
        checks[(row['block'],row['gate'])] = dict(row)
    predecessors = {}
    for row in db.execute('SELECT * FROM erection_links'):
        predecessors.setdefault(row['successor_block'],[]).append(row['predecessor_block'])
    children = {}
    for row in db.execute('SELECT * FROM block_structure'):
        children.setdefault(row['parent_block'],[]).append(row['child_block'])
    plans = {row['block']:dict(row) for row in db.execute('SELECT * FROM erection_plans')}
    activities = {}
    for row in db.execute("SELECT * FROM activities WHERE block IS NOT NULL AND stage IN ('설계·구매','가공·조립','선행의장·도장')"):
        activities.setdefault(row['block'],[]).append(dict(row))
    output = []
    for block in db.execute('SELECT * FROM blocks ORDER BY id'):
        key = block['id']
        blockers = []
        evidence = []
        for gate in GATES:
            check = checks.get((key,gate))
            verdict = check['verdict'] if check else '미확인'
            evidence.append({'gate':gate,'verdict':verdict,'document':check['id'] if check else None,
                             'evidence':check['evidence'] if check else ''})
            if verdict in {'미확인','보류'}:
                blockers.append(gate + ' ' + verdict)
        missing = db.execute('''SELECT COUNT(*) FROM requirements r JOIN activities a ON a.id=r.activity
            WHERE a.block=? AND r.qty > COALESCE((SELECT SUM(i.qty) FROM issues i WHERE i.requirement=r.id),0)''',
            (key,)).fetchone()[0]
        if missing:
            blockers.append(f'미출고 자재 소요 {missing}건')
        for activity in activities.get(key,[]):
            if not activity['actual']:
                blockers.append(activity['id'] + ' 공정 미완료')
        for child in children.get(key,[]):
            child_activities = activities.get(child,[])
            assembly = checks.get((child,'조립 완료'))
            if not assembly or assembly['verdict'] != '확인' or any(not item['actual'] for item in child_activities):
                blockers.append('구성 블록 조립 미확인 ' + child)
        # A predecessor counts as erected only with a passing erection inspection record.
        unmet = [prior for prior in sorted(set(predecessors.get(key,[]))) if not production.erected(db, prior)]
        if unmet:
            blockers.append('선행 블록 탑재 미확인 ' + ', '.join(unmet))
        plan = plans.get(key)
        if not plan:
            blockers.append('탑재 계획 미등록')
        moving = db.execute('SELECT id FROM moves WHERE block=? AND confirmed_at IS NULL',(key,)).fetchone()
        if moving:
            blockers.append('이동 중 ' + moving['id'])
        for check in EXTRA_BLOCKERS:
            blockers.extend(check(db, block, plan))
        erected_day = production.erected(db, key)
        output.append({'id':key,'hull':block['hull'],'location':block['location'],
                       'erection_plan':plan['id'] if plan else None,
                       'forecast':plan['forecast'] if plan else None,
                       'destination':plan['destination'] if plan else None,
                       'readiness':'탑재 완료' if erected_day else '준비 확인' if not blockers else '준비 필요',
                       'erected_day':erected_day,
                       'blockers':' / '.join(blockers),'checks':evidence})
    return output
