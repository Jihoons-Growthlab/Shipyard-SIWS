"""Yard operations planning: crane work assignments, berths and quay shifting, outfitting work plan.

Crane plan   crane_assignments (who lifts what, where, when) checked against equipment_specs capacity and
             against the crane's other jobs that day; crane_load sums booked hours per crane and ISO week.
Quay plan    berths (slots along a quay or pier), berth_assignments (which hull lies at which berth from/to),
             quay_shifts (planned and actual moves between docks, berths and the sea); berth_occupancy checks fit
             and timing against the hull's launch milestone.
Outfitting   outfit_works by hull, zone, trade and phase with man-hours and progress, outfit_links for precedence;
             outfit_progress rolls up earned and planned man-hours per hull and zone.

Every register action with an `id` updates the row when the id exists, so plans can be revised and actuals
(progress, actual shift day) recorded on the same document.
"""
from datetime import date, timedelta

import geometry
import math
import planning

CRANE_WORK = ['블록 탑재', 'PE 리프트', '블록 턴오버', '의장품 인양', '안벽 의장 인양', '크레인 정비']
BERTH_SIDES = ['피어 남동측', '피어 북서측', '안벽 전면']
BERTH_PURPOSES = ['안벽 의장', '시운전 준비', '인도 대기', '수리']
SHIFT_PURPOSES = ['진수 후 이동', '선석 이동', '시운전 출항', '시운전 귀항', '인도 출항']
OUTFIT_ZONES = ['선수부', '화물창', '기관실', '거주구', '선미부', '상갑판', '화물 시스템']
OUTFIT_TRADES = ['배관', '전장', '기장', '선장', '철의장', '도장', '보온', '계장', '통풍·공조']
RETURN_PHASE = '귀항 후 보완·인도 준비'
WORK_PHASES = ['선행 의장', '탑재 후 의장', '안벽 의장', '시운전 준비', RETURN_PHASE]
SHORE_PHASES = {'안벽 의장', '시운전 준비', RETURN_PHASE}
WORK_STATUSES = ['계획', '진행', '완료', '보류']
LIFTING = {'골리앗 크레인', '갠트리 크레인', '지브 크레인'}
# Shift end points that are not facilities: the sea-trial area and departure on delivery.
SEA_POINTS = {'SEA-TRIAL': '시운전 해역', 'DEPARTED': '인도 출항(해상)'}
SHIFT_HOURS = 16  # two shifts a working day, used for crane utilisation

CHOICES = {'point_source': ['현장 계획', '추정'], 'crane_work': CRANE_WORK, 'berth_side': BERTH_SIDES, 'berth_purpose': BERTH_PURPOSES,
           'shift_purpose': SHIFT_PURPOSES, 'outfit_zone': OUTFIT_ZONES, 'outfit_trade': OUTFIT_TRADES,
           'work_phase': WORK_PHASES, 'work_status': WORK_STATUSES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS crane_assignments(id TEXT PRIMARY KEY, equipment TEXT NOT NULL REFERENCES equipment(id),
 hull TEXT NOT NULL REFERENCES hulls(id), block TEXT REFERENCES blocks(id), crane_work TEXT NOT NULL,
 work_area TEXT NOT NULL REFERENCES locations(id), work_day TEXT NOT NULL, start_hour INTEGER NOT NULL,
 hours INTEGER NOT NULL, lift_t REAL NOT NULL, note TEXT);
CREATE TABLE IF NOT EXISTS berths(id TEXT PRIMARY KEY, name TEXT NOT NULL, quay TEXT NOT NULL REFERENCES locations(id),
 berth_side TEXT NOT NULL, start_m INTEGER NOT NULL, berth_length_m REAL NOT NULL, water_depth_m REAL NOT NULL);
CREATE TABLE IF NOT EXISTS shift_point_positions(id TEXT PRIMARY KEY,
 x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, heading_deg REAL NOT NULL);
CREATE TABLE IF NOT EXISTS berth_assignments(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 berth TEXT NOT NULL REFERENCES berths(id), start_day TEXT NOT NULL, end_day TEXT NOT NULL,
 berth_purpose TEXT NOT NULL, note TEXT);
CREATE TABLE IF NOT EXISTS quay_shifts(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 from_point TEXT NOT NULL, to_point TEXT NOT NULL, plan_day TEXT NOT NULL, actual_day TEXT, tugs INTEGER NOT NULL,
 shift_purpose TEXT NOT NULL, note TEXT);
CREATE TABLE IF NOT EXISTS outfit_works(id TEXT PRIMARY KEY, hull TEXT NOT NULL REFERENCES hulls(id),
 block TEXT REFERENCES blocks(id), outfit_zone TEXT NOT NULL, outfit_trade TEXT NOT NULL, work_phase TEXT NOT NULL,
 work_area TEXT REFERENCES locations(id), start_day TEXT NOT NULL, end_day TEXT NOT NULL, planned_mh INTEGER NOT NULL,
 crew INTEGER NOT NULL, progress_pct INTEGER NOT NULL, work_status TEXT NOT NULL, note TEXT);
CREATE TABLE IF NOT EXISTS crane_lifts(assignment TEXT PRIMARY KEY REFERENCES crane_assignments(id),
 pick_x REAL NOT NULL, pick_y REAL NOT NULL, pick_z REAL NOT NULL, drop_x REAL NOT NULL, drop_y REAL NOT NULL,
 drop_z REAL NOT NULL, point_source TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS outfit_links(id TEXT PRIMARY KEY, work TEXT NOT NULL REFERENCES outfit_works(id),
 predecessor_work TEXT NOT NULL REFERENCES outfit_works(id), UNIQUE(work, predecessor_work));
'''
FIELDS = {
    'crane_assignments': ('id', 'equipment', 'hull', 'block', 'crane_work', 'work_area', 'work_day', 'start_hour',
                          'hours', 'lift_t', 'note'),
    'berths': ('id', 'name', 'quay', 'berth_side', 'start_m', 'berth_length_m', 'water_depth_m'),
    'shift_point_positions': ('id', 'x', 'y', 'z', 'heading_deg'),
    'berth_assignments': ('id', 'hull', 'berth', 'start_day', 'end_day', 'berth_purpose', 'note'),
    'quay_shifts': ('id', 'hull', 'from_point', 'to_point', 'plan_day', 'actual_day', 'tugs', 'shift_purpose', 'note'),
    'outfit_works': ('id', 'hull', 'block', 'outfit_zone', 'outfit_trade', 'work_phase', 'work_area', 'start_day',
                     'end_day', 'planned_mh', 'crew', 'progress_pct', 'work_status', 'note'),
    'outfit_links': ('id', 'work', 'predecessor_work'),
    # Where a rail crane's lift is picked up and set down (yard m; z = hook height above the quay surface, m).
    # point_source: 현장 계획 (entered from the work plan) or 추정 (derived from the hull and crane geometry).
    'crane_lifts': ('assignment', 'pick_x', 'pick_y', 'pick_z', 'drop_x', 'drop_y', 'drop_z', 'point_source'),
}
LIFT_SOURCES = ['현장 계획', '추정']
LIFT_POINTS = {'pick_x', 'pick_y', 'pick_z', 'drop_x', 'drop_y', 'drop_z'}
PREFIXES = {'crane_assignments': 'CRA', 'berth_assignments': 'BTA', 'quay_shifts': 'QSH', 'outfit_works': 'OFW',
            'outfit_links': 'OFL'}
OPTIONAL = {'crane_assignments': {'block', 'note'}, 'berth_assignments': {'note'},
            'quay_shifts': {'actual_day', 'note'}, 'outfit_works': {'block', 'work_area', 'note'}}
DATES = {'work_day', 'start_day', 'end_day', 'plan_day', 'actual_day'}
NUMBERS = LIFT_POINTS | {'start_hour', 'hours', 'lift_t', 'start_m', 'berth_length_m', 'water_depth_m', 'tugs', 'planned_mh', 'crew',
           'progress_pct'}
MEASURES = {'lift_t', 'berth_length_m', 'water_depth_m'} | LIFT_POINTS
SIGNED = set(LIFT_POINTS)
FLOATS = {'x', 'y', 'z', 'heading_deg'}
planning.NONNEGATIVE.update({'start_hour', 'start_m', 'tugs', 'progress_pct', 'lift_t'})
READS = set(FIELDS) | {'crane_load', 'berth_occupancy', 'shift_points', 'outfit_progress', 'hull_overview'}


def upsert(db, table, v):
    columns = ','.join(v)
    updates = ','.join(f'{column}=excluded.{column}' for column in v if column != 'id')
    db.execute(f'INSERT INTO {table} ({columns}) VALUES ({",".join("?" for _ in v)}) '
               f'ON CONFLICT(id) DO UPDATE SET {updates}', tuple(v.values()))
    return v['id']


def check_chart(db, equipment, lift_t, rail, pick, drop, require):
    """The lift weight within the crane's load chart (crane_load_charts) at the radius the lift needs; a crane without a
    chart is checked against its rating only (crane_assignments)."""
    points = geometry.chart_points(db, equipment)
    if not points or not lift_t:
        return
    radius = geometry.lift_radius(rail, pick, drop)
    rated = geometry.chart_capacity(points, radius)
    require(lift_t <= rated, f'{equipment} 작업 반경 {radius:.1f} m의 정격하중은 {rated:g}t입니다. '
                             f'인양 중량 {lift_t:g}t을 줄이거나 인양·착지 지점을 크레인 쪽으로 옮기세요.')


def rail_distance(rail, x, y):
    """Horizontal distance (m) from a point to a crane's rail centre line segment."""
    ax, ay, bx, by = rail['rail_x0'], rail['rail_y0'], rail['rail_x1'], rail['rail_y1']
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy or 1.0)))
    return math.hypot(x - ax - t * dx, y - ay - t * dy)


def overlaps(a0, a1, b0, b1):
    return a0 <= b1 and b0 <= a1


def shift_points(db, include_positions=False):
    points = [{'id': row['id'], 'name': row['name'], 'kind': '도크'}
              for row in db.execute("SELECT id, name FROM locations WHERE kind='도크' ORDER BY id")]
    points += [{'id': row['id'], 'name': row['name'], 'kind': '선석'} for row in db.execute('SELECT id, name FROM berths ORDER BY id')]
    points += [{'id': key, 'name': name, 'kind': '해상'} for key, name in SEA_POINTS.items()]
    if include_positions:
        positions = {row['id']: {k: row[k] for k in ('x', 'y', 'z', 'heading_deg')}
                     for row in db.execute('SELECT * FROM shift_point_positions')}
        for point in points:
            point['position'] = positions.get(point['id'])
            point['position_known'] = point['position'] is not None
    return points


def vessel_length(db, hull):
    row = db.execute('SELECT length_m FROM vessel_details WHERE hull=?', (hull,)).fetchone()
    return row['length_m'] if row else None


def write(db, action, v, api):
    require, lookup = api.require, api.lookup
    if action == 'shift_point_positions':
        require(v['id'] in {p['id'] for p in shift_points(db)}, '등록된 이동 지점을 선택하세요.')
        require(0 <= v['heading_deg'] < 360, '선수 방향은 0 이상 360 미만이어야 합니다.')
        return upsert(db, action, v)
    if action == 'crane_assignments':
        spec = db.execute('SELECT * FROM equipment_specs WHERE equipment=?', (v['equipment'],)).fetchone()
        require(spec and spec['equipment_type'] in LIFTING, '장비 능력에 크레인으로 등록된 장비를 선택하세요.')
        require(v['crane_work'] in CRANE_WORK, '크레인 작업 유형을 선택하세요.')
        lookup(db, 'hulls', v['hull'])
        lookup(db, 'locations', v['work_area'])
        require(v['start_hour'] <= 23 and v['start_hour'] + v['hours'] <= 24, '작업은 하루(0~24시) 안에서 끝나야 합니다.')
        require(v['lift_t'] <= spec['capacity_t'],
                f'인양 중량 {v["lift_t"]}t이 {v["equipment"]} 정격하중 {spec["capacity_t"]}t을 초과합니다.')
        if v['block']:
            block = lookup(db, 'blocks', v['block'])
            require(block['hull'] == v['hull'], '호선과 블록이 일치해야 합니다.')
        lift = db.execute('SELECT * FROM crane_lifts WHERE assignment=?', (v['id'],)).fetchone() if v['id'] else None
        rail = db.execute('SELECT * FROM crane_rails WHERE equipment=?', (v['equipment'],)).fetchone()
        if lift and rail:
            check_chart(db, v['equipment'], v['lift_t'], rail, (lift['pick_x'], lift['pick_y']), (lift['drop_x'], lift['drop_y']), require)
        end = v['start_hour'] + v['hours']
        for row in db.execute('SELECT id, start_hour, hours FROM crane_assignments WHERE equipment=? AND work_day=? AND id<>?',
                              (v['equipment'], v['work_day'], v['id'])):
            require(not (v['start_hour'] < row['start_hour'] + row['hours'] and row['start_hour'] < end),
                    f'{row["id"]} 작업과 같은 크레인의 작업 시간이 겹칩니다.')
        return upsert(db, action, v)
    if action == 'crane_lifts':
        require(v['point_source'] in LIFT_SOURCES, '인양 지점 출처를 목록에서 선택하세요.')
        job = lookup(db, 'crane_assignments', v['assignment'])
        rail = db.execute('SELECT * FROM crane_rails WHERE equipment=?', (job['equipment'],)).fetchone()
        kinematics = db.execute('SELECT * FROM crane_kinematics WHERE equipment=?', (job['equipment'],)).fetchone()
        require(rail and kinematics, '레일과 붐 제원(crane_kinematics)이 등록된 크레인의 배정만 인양 지점을 지정할 수 있습니다.')
        reach = kinematics['boom_m'] * math.cos(math.radians(kinematics['luff_min_deg']))
        for point in ('pick', 'drop'):
            distance = rail_distance(rail, v[f'{point}_x'], v[f'{point}_y'])
            require(distance <= reach, f'{"인양" if point == "pick" else "착지"} 지점이 레일에서 {distance:.1f} m로 최대 작업 반경 {reach:.1f} m를 넘습니다.')
            require(-20 <= v[f'{point}_z'] <= kinematics['pivot_height_m'] + kinematics['boom_m'],
                    '훅 높이는 안벽 기준 -20 m 이상, 붐 끝 높이 이하로 입력하세요.')
        check_chart(db, job['equipment'], job['lift_t'], rail, (v['pick_x'], v['pick_y']), (v['drop_x'], v['drop_y']), require)
        db.execute(f'INSERT INTO crane_lifts ({",".join(v)}) VALUES ({",".join("?" for _ in v)}) ON CONFLICT(assignment) DO UPDATE SET '
                   + ','.join(f'{k}=excluded.{k}' for k in v if k != 'assignment'), tuple(v.values()))
        return v['assignment']
    if action == 'berths':
        require(lookup(db, 'locations', v['quay'])['kind'] == '안벽', '안벽 유형의 시설을 선택하세요.')
        require(v['berth_side'] in BERTH_SIDES, '선석 위치를 선택하세요.')
        require(not db.execute('SELECT 1 FROM locations WHERE id=?', (v['id'],)).fetchone(), '시설 ID와 다른 선석 ID를 쓰세요.')
        require(v['id'] not in SEA_POINTS, '예약된 ID입니다.')
        return upsert(db, action, v)
    if action == 'berth_assignments':
        lookup(db, 'hulls', v['hull'])
        berth = lookup(db, 'berths', v['berth'])
        require(v['berth_purpose'] in BERTH_PURPOSES, '배정 목적을 선택하세요.')
        require(v['start_day'] <= v['end_day'], '배정 종료일은 시작일 이후여야 합니다.')
        length = vessel_length(db, v['hull'])
        require(length is None or length <= berth['berth_length_m'],
                f'선박 길이 {length}m가 선석 길이 {berth["berth_length_m"]}m보다 깁니다.')
        clash = db.execute('SELECT id, hull FROM berth_assignments WHERE berth=? AND id<>? AND start_day<=? AND end_day>=?',
                           (v['berth'], v['id'], v['end_day'], v['start_day'])).fetchone()
        require(not clash, f'{clash["id"] if clash else ""} ({clash["hull"] if clash else ""}) 배정과 선석 점유 기간이 겹칩니다.')
        twice = db.execute('SELECT id, berth FROM berth_assignments WHERE hull=? AND id<>? AND start_day<=? AND end_day>=?',
                           (v['hull'], v['id'], v['end_day'], v['start_day'])).fetchone()
        require(not twice, f'같은 기간에 {twice["berth"] if twice else ""} 선석에도 배정되어 있습니다 ({twice["id"] if twice else ""}).')
        return upsert(db, action, v)
    if action == 'quay_shifts':
        lookup(db, 'hulls', v['hull'])
        points = {row['id'] for row in shift_points(db)}
        require(v['from_point'] in points and v['to_point'] in points, '도크, 선석 또는 해상 지점을 선택하세요.')
        require(v['from_point'] != v['to_point'], '출발지와 도착지가 같습니다.')
        require(v['shift_purpose'] in SHIFT_PURPOSES, '이동 목적을 선택하세요.')
        require(v['tugs'] <= 10, '예인선 수를 확인하세요 (0~10척).')
        if v['actual_day']:
            require(api.day(v['actual_day']) <= date.today(), '실제 이동일은 오늘 이후일 수 없습니다.')
            milestone = {'진수 후 이동': '진수', '시운전 출항': '시운전',
                         '시운전 귀항': '시운전', '인도 출항': '인도'}.get(v['shift_purpose'])
            if milestone:
                record = db.execute('SELECT actual FROM hull_milestones WHERE hull=? AND milestone=?',
                                    (v['hull'], milestone)).fetchone()
                if record:
                    require(record['actual'] and record['actual'] <= v['actual_day'],
                            f'{milestone} 실적일을 먼저 확인하세요.')
        return upsert(db, action, v)
    if action == 'outfit_works':
        lookup(db, 'hulls', v['hull'])
        issued = db.execute('SELECT 1 FROM outfit_orders WHERE work=?', (v['id'],)).fetchone()
        linked = db.execute('''SELECT id FROM outfit_conditions WHERE id=?
            UNION ALL SELECT id FROM outfit_materials WHERE work=? LIMIT 1''', (v['id'], v['id'])).fetchone()
        if linked:
            current = lookup(db, 'outfit_works', v['id'])
            require(all(v[key] == current[key] for key in ('hull', 'block', 'work_phase')),
                    '착수 조건 또는 자재가 연결된 작업의 호선·블록·단계는 바꿀 수 없습니다.')
        if issued:
            current = lookup(db, 'outfit_works', v['id'])
            require(all(v[key] == current[key] for key in
                        ('hull', 'block', 'outfit_zone', 'outfit_trade', 'work_phase', 'progress_pct', 'work_status')),
                    '발행 후에는 작업 대상이나 실적 진척률을 계획 화면에서 변경할 수 없습니다.')
        for key, values in (('outfit_zone', OUTFIT_ZONES), ('outfit_trade', OUTFIT_TRADES), ('work_phase', WORK_PHASES),
                            ('work_status', WORK_STATUSES)):
            require(v[key] in values, f'{key} 값을 목록에서 선택하세요.')
        if v['block']:
            require(lookup(db, 'blocks', v['block'])['hull'] == v['hull'], '호선과 블록이 일치해야 합니다.')
        if v['work_area']:
            lookup(db, 'locations', v['work_area'])
        require(v['start_day'] <= v['end_day'], '작업 종료일은 시작일 이후여야 합니다.')
        require(v['progress_pct'] <= 100, '진척률은 0~100% 사이여야 합니다.')
        require((v['progress_pct'] == 100) == (v['work_status'] == '완료'), '진척률 100%와 완료 상태를 함께 입력하세요.')
        return upsert(db, action, v)
    if action == 'outfit_links':
        work, prior = lookup(db, 'outfit_works', v['work']), lookup(db, 'outfit_works', v['predecessor_work'])
        require(v['work'] != v['predecessor_work'], '같은 작업을 선행으로 지정할 수 없습니다.')
        require(work['hull'] == prior['hull'], '같은 호선의 작업을 연결하세요.')
        edges = {}
        for row in db.execute('SELECT work, predecessor_work FROM outfit_links'):
            edges.setdefault(row['work'], []).append(row['predecessor_work'])
        pending, seen = [v['predecessor_work']], set()
        while pending:
            node = pending.pop()
            require(node != v['work'], '작업 선후행이 순환합니다.')
            if node not in seen:
                seen.add(node)
                pending.extend(edges.get(node, ()))
        return api.insert(db, action, **v)
    raise ValueError('지원하지 않는 작업입니다.')


def crane_load(db):
    """Booked hours per crane and ISO week against two-shift working days; peak lift against capacity."""
    specs = {row['equipment']: dict(row) for row in db.execute('SELECT * FROM equipment_specs')}
    weeks = {}
    for row in db.execute('SELECT * FROM crane_assignments'):
        day = date.fromisoformat(row['work_day'])
        monday = day - timedelta(days=day.weekday())
        item = weeks.setdefault((row['equipment'], monday), {'hours': 0, 'jobs': 0, 'days': set(), 'peak': 0.0, 'hulls': set()})
        item['hours'] += row['hours']
        item['jobs'] += 1
        item['days'].add(day)
        item['peak'] = max(item['peak'], row['lift_t'])
        item['hulls'].add(row['hull'])
    rows = []
    for (equipment, monday), item in sorted(weeks.items(), key=lambda pair: (pair[0][1], pair[0][0])):
        spec = specs.get(equipment, {})
        available = 5 * SHIFT_HOURS
        utilization = round(100 * item['hours'] / available)
        status = '과부하' if utilization > 100 else '높음' if utilization >= 85 else '보통' if utilization >= 40 else '여유'
        rows.append({'id': f'{equipment}:{monday.isoformat()}', 'equipment': equipment,
                     'equipment_type': spec.get('equipment_type'), 'station': spec.get('station'),
                     'week': monday.isoformat(), 'jobs': item['jobs'], 'work_days': len(item['days']),
                     'booked_hours': item['hours'], 'available_hours': available, 'utilization_pct': utilization,
                     'peak_lift_t': item['peak'], 'capacity_t': spec.get('capacity_t'),
                     'hulls_served': ', '.join(sorted(item['hulls'])), 'load_status': status})
    return rows


def launch_days(db):
    return {row['hull']: row['forecast'] for row in db.execute("SELECT hull, forecast FROM hull_milestones WHERE milestone='진수'")}


def berth_occupancy(db):
    berths = {row['id']: dict(row) for row in db.execute('SELECT * FROM berths')}
    launches, today, milestones = launch_days(db), date.today().isoformat(), milestone_days(db)
    rows = []
    for row in db.execute('SELECT * FROM berth_assignments ORDER BY berth, start_day'):
        berth, length = berths[row['berth']], vessel_length(db, row['hull'])
        draft = db.execute('SELECT draft_m FROM hull_geometry WHERE hull=?', (row['hull'],)).fetchone()
        notes = []
        m = milestones.get(row['hull'], {})
        if row['hull'] in launches and row['start_day'] < launches[row['hull']]:
            notes.append('진수 예정일 전 배정')
        if row['berth_purpose'] in ('안벽 의장', '시운전 준비') and '시운전' in m and row['end_day'] > m['시운전']:
            notes.append(f'시운전({m["시운전"]}) 이후까지 의장 배정')
        if '인도' in m and row['end_day'] > m['인도']:
            notes.append(f'인도일({m["인도"]}) 이후 점유')
        if draft and draft['draft_m'] > berth['water_depth_m']:
            notes.append(f'설계 흘수 {draft["draft_m"]}m > 수심 {berth["water_depth_m"]}m (경하 흘수 확인)')
        days = (date.fromisoformat(row['end_day']) - date.fromisoformat(row['start_day'])).days + 1
        state = '종료' if row['end_day'] < today else '점유 중' if row['start_day'] <= today else '예정'
        rows.append({'id': row['id'], 'hull': row['hull'], 'berth': row['berth'], 'berth_name': berth['name'],
                     'quay': berth['quay'], 'start_day': row['start_day'], 'end_day': row['end_day'], 'days': days,
                     'berth_purpose': row['berth_purpose'], 'vessel_length_m': length,
                     'berth_length_m': berth['berth_length_m'], 'spare_m': round(berth['berth_length_m'] - length, 1) if length else None,
                     'launch_day': launches.get(row['hull']), 'occupancy_status': state, 'check': ' · '.join(notes) or '정상'})
    return rows


def milestone_days(db):
    """{hull: {milestone: actual or forecast}} from the 대일정."""
    out = {}
    for row in db.execute('SELECT hull, milestone, forecast, actual FROM hull_milestones'):
        out.setdefault(row['hull'], {})[row['milestone']] = row['actual'] or row['forecast']
    for row in db.execute("SELECT hull, plan_day, actual_day FROM quay_shifts WHERE shift_purpose='시운전 귀항'"):
        phases = out.setdefault(row['hull'], {})
        day = row['actual_day'] or row['plan_day']
        phases['귀항'] = max(phases.get('귀항', day), day)
    return out


def phase_window(work, m):
    """(start bound, end bound, label) the 대일정 sets for an outfitting phase, or None when milestones are missing."""
    plus = lambda day, n: (date.fromisoformat(day) + timedelta(days=n)).isoformat()
    phase = work['work_phase']
    if phase == '선행 의장' and '기공' in m:
        return None, plus(m['기공'], 14), '기공 2주 후까지'
    if phase == '탑재 후 의장' and '기공' in m and '진수' in m:
        return m['기공'], m['진수'], f'기공~진수 {m["기공"]}~{m["진수"]}'
    if phase == '안벽 의장' and '진수' in m and '시운전' in m:
        return m['진수'], plus(m['시운전'], -1), '진수일부터 시운전 전날까지'
    if phase == '시운전 준비' and '시운전' in m:
        return m.get('진수'), plus(m['시운전'], -1), '시운전 전날까지'
    if phase == RETURN_PHASE and '귀항' in m and '인도' in m:
        return m['귀항'], plus(m['인도'], -1), '귀항일부터 인도 전날까지'
    return None


def outfit_works(db):
    """Works with their predecessors and schedule flags as of today, checked against the 대일정 milestones."""
    today = date.today().isoformat()
    milestones = milestone_days(db)
    works = {row['id']: dict(row) for row in db.execute('SELECT * FROM outfit_works')}
    berths = {}
    for row in db.execute('''SELECT a.hull, b.quay, a.start_day, a.end_day
            FROM berth_assignments a JOIN berths b ON b.id=a.berth ORDER BY a.start_day'''):
        berths.setdefault((row['hull'], row['quay']), []).append(row)
    prior = {}
    for row in db.execute('SELECT work, predecessor_work FROM outfit_links'):
        prior.setdefault(row['work'], []).append(row['predecessor_work'])
    rows = []
    for key in sorted(works, key=lambda k: (works[k]['hull'], works[k]['start_day'], k)):
        item = works[key]
        before = prior.get(key, [])
        flags = []
        if item['work_status'] != '완료' and item['end_day'] < today:
            flags.append('종료일 경과')
        if item['work_status'] in ('진행', '완료') and any(works[p]['work_status'] != '완료' for p in before):
            flags.append('선행 미완료 착수')
        if item['work_status'] == '계획' and item['start_day'] < today:
            flags.append('착수 지연')
        window = phase_window(item, milestones.get(item['hull'], {}))
        if window and ((window[0] and item['start_day'] < window[0]) or item['end_day'] > window[1]):
            flags.append(f'대일정 범위 밖 ({window[2]})')
        if item['work_phase'] == RETURN_PHASE and not window:
            flags.append('귀항·인도 일정 미등록')
        if item['work_phase'] in SHORE_PHASES and item['work_area']:
            remaining = item['start_day']
            for stay in berths.get((item['hull'], item['work_area']), ()):
                if stay['start_day'] <= remaining <= stay['end_day']:
                    remaining = (date.fromisoformat(stay['end_day']) + timedelta(days=1)).isoformat()
            if remaining <= item['end_day']:
                flags.append('작업 기간의 선석 배정 확인')
        item['predecessor_works'] = ', '.join(before) or None
        item['earned_mh'] = round(item['planned_mh'] * item['progress_pct'] / 100)
        item['schedule_check'] = ' · '.join(flags) or '정상'
        rows.append(item)
    return rows


def planned_share(start, end, today):
    if today < start:
        return 0.0
    if today >= end:
        return 1.0
    return ((today - start).days + 1) / ((end - start).days + 1)


def outfit_progress(db):
    """Planned (linear over each work's dates) against earned man-hours per hull and zone, as of today."""
    today = date.today()
    groups = {}
    for row in db.execute('SELECT * FROM outfit_works'):
        item = groups.setdefault((row['hull'], row['outfit_zone']), {'works': 0, 'done': 0, 'mh': 0, 'earned': 0.0,
                                                                      'planned': 0.0, 'held': 0, 'start': row['start_day'],
                                                                      'end': row['end_day']})
        item['works'] += 1
        item['done'] += row['work_status'] == '완료'
        item['held'] += row['work_status'] == '보류'
        item['mh'] += row['planned_mh']
        item['earned'] += row['planned_mh'] * row['progress_pct'] / 100
        item['planned'] += row['planned_mh'] * planned_share(date.fromisoformat(row['start_day']), date.fromisoformat(row['end_day']), today)
        item['start'], item['end'] = min(item['start'], row['start_day']), max(item['end'], row['end_day'])
    rows = []
    for (hull, zone), item in sorted(groups.items(), key=lambda pair: (pair[0][0], OUTFIT_ZONES.index(pair[0][1]))):
        planned, earned = item['planned'], item['earned']
        spi = round(earned / planned, 2) if planned else None
        status = '착수 전' if not planned and not earned else '완료' if item['done'] == item['works'] else \
            '지연' if spi is not None and spi < 0.9 else '선행' if spi is not None and spi > 1.05 else '정상'
        rows.append({'id': f'{hull}:{zone}', 'hull': hull, 'outfit_zone': zone, 'works': item['works'],
                     'works_done': item['done'], 'works_held': item['held'], 'planned_mh': item['mh'],
                     'planned_to_date_mh': round(planned), 'earned_mh': round(earned),
                     'progress_pct': round(100 * earned / item['mh']) if item['mh'] else 0,
                     'spi': spi, 'start_day': item['start'], 'end_day': item['end'], 'progress_status': status})
    return rows


def crane_assignments(db):
    """Crane jobs with the erection-plan forecast of the block they lift (소일정 against 중일정)."""
    forecast = {row['block']: row['forecast'] for row in db.execute('SELECT block, forecast FROM erection_plans')}
    rows = []
    for row in db.execute('SELECT * FROM crane_assignments ORDER BY work_day, equipment, start_hour'):
        item = dict(row)
        plan = forecast.get(item['block']) if item['crane_work'] == '블록 탑재' else None
        item['erection_forecast'] = plan
        late = (date.fromisoformat(item['work_day']) - date.fromisoformat(plan)).days if plan else 0
        item['crane_check'] = f'탑재 예정보다 {late}일 늦음' if late > 0 else '정상'
        rows.append(item)
    return rows


def hull_overview(db):
    """대일정 per hull: dock and berth allocation against the milestones, with risk rolled up from the
    중일정/소일정 (outfitting SPI and overdue works, crane lifts later than the erection plan)."""
    today = date.today()
    milestones = milestone_days(db)
    milestone_records = {}
    for row in db.execute('SELECT * FROM hull_milestones ORDER BY id'):
        milestone_records.setdefault(row['hull'], {})[row['milestone']] = row
    counts = {}
    for row in db.execute('''SELECT b.hull, p.block, (
          SELECT e.inspection FROM erection_records e WHERE e.block=p.block
          ORDER BY e.recorded_at DESC, e.id DESC LIMIT 1) AS inspection
          FROM (SELECT DISTINCT block FROM erection_plans) p JOIN blocks b ON b.id=p.block'''):
        planned, accepted = counts.get(row['hull'], (0, 0))
        counts[row['hull']] = (planned + 1, accepted + (row['inspection'] == '합격'))
    actual_moves = {}
    for row in db.execute("SELECT * FROM quay_shifts WHERE actual_day IS NOT NULL AND actual_day<=? ORDER BY actual_day,id",
                          (today.isoformat(),)):
        actual_moves[row['hull']] = row
    vessels = {row['hull']: dict(row) for row in db.execute('SELECT * FROM vessel_details')}
    bookings, stays = {}, {}
    for row in db.execute('SELECT * FROM dock_bookings ORDER BY start_day'):
        bookings.setdefault(row['hull'], []).append(row)
    for row in db.execute('SELECT * FROM berth_assignments ORDER BY start_day'):
        stays.setdefault(row['hull'], []).append(row)
    works = {}
    for row in outfit_works(db):
        item = works.setdefault(row['hull'], {'planned': 0.0, 'earned': 0.0, 'overdue': 0, 'outside': 0})
        item['planned'] += row['planned_mh'] * planned_share(date.fromisoformat(row['start_day']), date.fromisoformat(row['end_day']), today)
        item['earned'] += row['earned_mh']
        item['overdue'] += '종료일 경과' in row['schedule_check'] or '착수 지연' in row['schedule_check']
        item['outside'] += '대일정 범위 밖' in row['schedule_check']
    crane_late = {}
    for row in crane_assignments(db):
        if row['crane_check'] != '정상':
            crane_late[row['hull']] = crane_late.get(row['hull'], 0) + 1
    hulls = sorted(set(milestones) | set(vessels) | set(counts), key=lambda h: (milestones.get(h, {}).get('기공', '9999'), h))
    rows = []
    for hull in hulls:
        m, notes = milestones.get(hull, {}), []
        dock = bookings.get(hull, [])
        if '기공' in m and '진수' in m:
            if not dock:
                notes.append('도크 예약 없음')
            elif not any(b['start_day'] <= m['기공'] and b['end_day'] >= m['진수'] for b in dock):
                notes.append('도크 예약이 기공~진수를 덮지 않음')
        berth = stays.get(hull, [])
        if '진수' in m:
            launch = date.fromisoformat(m['진수'])
            if not berth and launch <= today + timedelta(days=90):
                notes.append('진수 후 선석 미배정')
            elif berth and berth[0]['start_day'] < m['진수']:
                notes.append('진수 전 선석 배정')
            elif berth and (date.fromisoformat(berth[0]['start_day']) - launch).days > 3:
                notes.append(f'진수 후 선석 대기 {(date.fromisoformat(berth[0]["start_day"]) - launch).days}일')
        trial = m.get('시운전')
        for a, b in zip(berth, berth[1:]):
            gap = (date.fromisoformat(b['start_day']) - date.fromisoformat(a['end_day'])).days
            if gap > 1 and not (trial and a['end_day'] < trial <= b['start_day']):
                notes.append(f'선석 공백 {gap - 1}일 ({a["end_day"]}~{b["start_day"]})')
        if berth and '인도' in m and berth[-1]['end_day'] > m['인도']:
            notes.append('인도일 이후 선석 점유')
        w = works.get(hull, {'planned': 0, 'earned': 0, 'overdue': 0, 'outside': 0})
        spi = round(w['earned'] / w['planned'], 2) if w['planned'] else None
        schedule = milestone_records.get(hull, {})
        pending = sorted((r['forecast'], key) for key, r in schedule.items() if not r['actual'])
        delivery = schedule.get('인도')
        delivered = bool(delivery and delivery['actual'] and delivery['actual'] <= today.isoformat())
        planned, accepted = counts.get(hull, (0, 0))
        actual_move = actual_moves.get(hull)
        actual_date = lambda key: schedule[key]['actual'] if key in schedule else None
        forecast_date = lambda key: schedule[key]['forecast'] if key in schedule else None
        if delivered:
            lifecycle_status = '인도 완료'
        elif pending and pending[0][0] < today.isoformat():
            lifecycle_status = pending[0][1] + ' 예정일 경과 · 실적 미등록'
        elif pending:
            lifecycle_status = pending[0][1] + ' 예정'
        else:
            lifecycle_status = '대일정 확인'
        late = crane_late.get(hull, 0)
        if delivered:
            risk = '인도 완료'
        elif (pending and pending[0][0] < today.isoformat()) or notes or (spi is not None and spi < 0.85) or w['overdue'] >= 5:
            risk = '위험'
        elif (spi is not None and spi < 0.95) or w['overdue'] or w['outside'] or late:
            risk = '주의'
        else:
            risk = '정상'
        rows.append({'id': hull, 'hull': hull, 'vessel_type': vessels.get(hull, {}).get('vessel_type'),
                     'keel_day': m.get('기공'), 'launch_day': forecast_date('진수'), 'trial_day': forecast_date('시운전'), 'delivery_day': forecast_date('인도'),
                     'launch_actual': actual_date('진수'), 'trial_actual': actual_date('시운전'), 'delivery_actual': actual_date('인도'),
                     'actual_shift_day': actual_move['actual_day'] if actual_move else None,
                     'actual_shift_point': actual_move['to_point'] if actual_move else None,
                     'erection_planned_count': planned, 'erection_accepted_count': accepted, 'lifecycle_status': lifecycle_status,
                     'docks': ', '.join(sorted({b['dock'] for b in dock})) or None,
                     'berth_sequence': ' → '.join(s['berth'] for s in berth) or None,
                     'next_milestone': f'{pending[0][1]} {pending[0][0]}' if pending and not delivered else None,
                     'days_to_next': (date.fromisoformat(pending[0][0]) - today).days if pending and not delivered else None,
                     'outfit_spi': spi, 'overdue_works': w['overdue'], 'works_outside_window': w['outside'],
                     'late_crane_lifts': late, 'allocation_check': ' · '.join(notes) or '정상', 'risk': risk})
    return rows


def read(db, table):
    if table == 'crane_load':
        return crane_load(db)
    if table == 'berth_occupancy':
        return berth_occupancy(db)
    if table == 'shift_points':
        return shift_points(db, include_positions=True)
    if table == 'outfit_progress':
        return outfit_progress(db)
    if table == 'outfit_works':
        return outfit_works(db)
    if table == 'crane_assignments':
        return crane_assignments(db)
    if table == 'hull_overview':
        return hull_overview(db)
    if table == 'crane_lifts':
        return [dict(row) for row in db.execute('''SELECT l.*, a.equipment, a.hull, a.work_day, a.start_hour, a.hours, a.crane_work
            FROM crane_lifts l JOIN crane_assignments a ON a.id=l.assignment ORDER BY a.work_day, a.start_hour, l.assignment''')]
    order = {'shift_point_positions': 'id', 'berths': 'id', 'berth_assignments': 'start_day, berth',
             'quay_shifts': 'plan_day, id', 'outfit_links': 'id'}
    if table in order:
        return [dict(row) for row in db.execute(f'SELECT * FROM {table} ORDER BY {order[table]}')]
    return None
