"""Crane capacity, dock occupancy, working calendar and erection scheduling constraints."""
import math
from datetime import date, timedelta

import production

EQUIPMENT_TYPES = ['골리앗 크레인', '갠트리 크레인', '지브 크레인', '트랜스포터', '지게차']
LIFTING = {'골리앗 크레인', '갠트리 크레인', '지브 크레인'}
CHOICES = {'equipment_type': EQUIPMENT_TYPES}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS equipment_specs(equipment TEXT PRIMARY KEY REFERENCES equipment(id),
 equipment_type TEXT NOT NULL, capacity_t REAL NOT NULL, station TEXT REFERENCES locations(id));
CREATE TABLE IF NOT EXISTS dock_bookings(id TEXT PRIMARY KEY, dock TEXT NOT NULL REFERENCES locations(id),
 hull TEXT NOT NULL REFERENCES hulls(id), start_day TEXT NOT NULL, end_day TEXT NOT NULL, reason TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS holidays(holiday TEXT PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS operation_links(id TEXT PRIMARY KEY, activity TEXT NOT NULL REFERENCES operations(activity),
 predecessor TEXT NOT NULL REFERENCES operations(activity), UNIQUE(activity, predecessor));
'''
FIELDS = {
    'equipment_specs': ('equipment', 'equipment_type', 'capacity_t', 'station'),
    'dock_bookings': ('id', 'dock', 'hull', 'start_day', 'end_day', 'reason'),
    'holidays': ('holiday', 'name'),
    'operation_links': ('id', 'activity', 'predecessor'),
}
PREFIXES = {'dock_bookings': 'DKB', 'operation_links': 'OPL'}
OPTIONAL = {'equipment_specs': {'station'}}
DATES = {'start_day', 'end_day', 'holiday'}
NUMBERS = {'capacity_t'}
MEASURES = {'capacity_t'}
READS = set(FIELDS) | {'erection_schedule'}


def holidays(db):
    return {row['holiday'] for row in db.execute('SELECT holiday FROM holidays')}


def workday(day, closed):
    while day.weekday() >= 5 or day.isoformat() in closed:
        day += timedelta(days=1)
    return day


def check_move(db, block, equipment, require):
    spec = db.execute('SELECT * FROM equipment_specs WHERE equipment=?', (equipment,)).fetchone()
    weight = db.execute('SELECT weight_t FROM block_details WHERE block=?', (block,)).fetchone()
    if spec and weight:
        require(weight['weight_t'] <= spec['capacity_t'],
                f'블록 중량 {weight["weight_t"]}t이 {equipment} 정격하중 {spec["capacity_t"]}t을 초과합니다.')


def write(db, action, v, api):
    require, lookup, insert = api.require, api.lookup, api.insert
    if action == 'equipment_specs':
        require(v['equipment_type'] in EQUIPMENT_TYPES, '장비 유형을 선택하세요.')
        lookup(db, 'equipment', v['equipment'])
        if v['station']:
            require(lookup(db, 'locations', v['station'])['kind'] != '창고', '생산 시설을 선택하세요.')
        db.execute('''INSERT INTO equipment_specs(equipment,equipment_type,capacity_t,station) VALUES(?,?,?,?)
            ON CONFLICT(equipment) DO UPDATE SET equipment_type=excluded.equipment_type,
            capacity_t=excluded.capacity_t, station=excluded.station''',
                   (v['equipment'], v['equipment_type'], v['capacity_t'], v['station']))
        return v['equipment']
    if action == 'dock_bookings':
        require(lookup(db, 'locations', v['dock'])['kind'] == '도크', '도크 유형의 시설을 선택하세요.')
        require(v['start_day'] <= v['end_day'], '점유 종료일은 시작일 이후여야 합니다.')
        clash = db.execute('SELECT id FROM dock_bookings WHERE dock=? AND start_day<=? AND end_day>=?',
                           (v['dock'], v['end_day'], v['start_day'])).fetchone()
        require(not clash, f'{clash["id"] if clash else ""} 예약과 도크 점유 기간이 겹칩니다.')
    if action == 'operation_links':
        require(v['predecessor'], '선행 공정을 선택하세요.')
        require(v['activity'] != v['predecessor'], '같은 공정을 선행으로 지정할 수 없습니다.')
        hulls = {row['activity']: row['hull'] for row in db.execute(
            'SELECT o.activity, a.hull FROM operations o JOIN activities a ON a.id=o.activity')}
        require(v['activity'] in hulls and v['predecessor'] in hulls, '제약 작업으로 등록된 공정을 선택하세요.')
        require(hulls[v['activity']] == hulls[v['predecessor']], '같은 호선의 공정을 연결하세요.')
        edges = predecessors(db)
        pending, seen = [v['predecessor']], set()
        while pending:
            node = pending.pop()
            require(node != v['activity'], '공정 선후행이 순환합니다.')
            if node not in seen:
                seen.add(node)
                pending.extend(edges.get(node, ()))
    return insert(db, action, **v)


def predecessors(db):
    edges = {}
    for row in db.execute('SELECT activity, predecessor FROM operations WHERE predecessor IS NOT NULL'):
        edges.setdefault(row['activity'], set()).add(row['predecessor'])
    for row in db.execute('SELECT activity, predecessor FROM operation_links'):
        edges.setdefault(row['activity'], set()).add(row['predecessor'])
    return edges


def cranes(db):
    by_station = {}
    for row in db.execute('SELECT * FROM equipment_specs WHERE station IS NOT NULL ORDER BY capacity_t, equipment'):
        if row['equipment_type'] in LIFTING:
            by_station.setdefault(row['station'], []).append((row['capacity_t'], row['equipment']))
    # A goliath whose registered rails straddle several docks (crane_rails, e.g. FLEET-GC over docks 4 and 5) also
    # serves every other dock whose centre lies between its rails.
    spans = db.execute('''SELECT r.*, s.capacity_t, s.station FROM crane_rails r JOIN equipment_specs s ON s.equipment=r.equipment
        WHERE s.equipment_type IN ('골리앗 크레인', '갠트리 크레인')''').fetchall()
    if spans:
        docks = db.execute('''SELECT g.location, g.x, g.y FROM location_geometry g JOIN locations l ON l.id=g.location
            WHERE l.kind='도크' ''').fetchall()
        for rail in spans:
            dx, dy = rail['rail_x1'] - rail['rail_x0'], rail['rail_y1'] - rail['rail_y0']
            length = math.hypot(dx, dy)
            if length <= 0:
                continue
            ux, uy = dx / length, dy / length
            for dock in docks:
                px, py = dock['x'] - rail['rail_x0'], dock['y'] - rail['rail_y0']
                along, across = px * ux + py * uy, -px * uy + py * ux
                serves = 0 <= along <= length and abs(across) < rail['gauge_m'] / 2
                listed = by_station.setdefault(dock['location'], [])
                if serves and dock['location'] != rail['station'] and all(e != rail['equipment'] for _, e in listed):
                    listed.append((rail['capacity_t'], rail['equipment']))
                    listed.sort()
    return by_station


def booked(db, dock, hull, day):
    return db.execute('SELECT 1 FROM dock_bookings WHERE dock=? AND hull=? AND start_day<=? AND end_day>=?',
                      (dock, hull, day, day)).fetchone()


def blockers(db, block, plan):
    if not plan:
        return []
    out = []
    dock = db.execute('SELECT kind FROM locations WHERE id=?', (plan['destination'],)).fetchone()
    if dock and dock['kind'] == '도크' and not booked(db, plan['destination'], block['hull'], plan['forecast']):
        out.append('도크 점유 예약 없음')
    available = cranes(db).get(plan['destination'], [])
    weight = db.execute('SELECT weight_t FROM block_details WHERE block=?', (block['id'],)).fetchone()
    if not available:
        out.append('탑재 크레인 미배정')
    elif weight and available[-1][0] < weight['weight_t']:
        out.append(f'크레인 용량 부족 ({weight["weight_t"]}t > {available[-1][0]}t)')
    return out


def erection_schedule(db):
    closed = holidays(db)
    plans = {row['block']: dict(row) for row in db.execute('''SELECT p.*, b.hull, d.weight_t, l.kind
        FROM erection_plans p JOIN blocks b ON b.id=p.block JOIN locations l ON l.id=p.destination
        LEFT JOIN block_details d ON d.block=p.block''')}
    links = {}
    for row in db.execute('SELECT * FROM erection_links'):
        links.setdefault(row['successor_block'], set()).add(row['predecessor_block'])
    station_cranes = cranes(db)
    # Blocks with an erection job in the crane plan (yardplan.crane_assignments) take that day and crane.
    assigned = {row['block']: (row['equipment'], row['work_day']) for row in db.execute(
        "SELECT block, equipment, work_day FROM crane_assignments WHERE block IS NOT NULL AND crane_work='블록 탑재' "
        'ORDER BY work_day DESC')}
    busy, scheduled, rows = {(crane, date.fromisoformat(day)) for crane, day in assigned.values()}, {}, []
    pending = sorted(plans, key=lambda key: (plans[key]['forecast'], key))
    while pending:
        # Erection links are validated acyclic, so some block always has resolved predecessors.
        key = next(item for item in pending if all(p in scheduled or p not in plans for p in links.get(item, ())))
        pending.remove(key)
        plan, prior = plans[key], sorted(links.get(key, ()))
        crane = day = None
        done = production.erected(db, key)
        options = [item for item in station_cranes.get(plan['destination'], [])
                   if plan['weight_t'] is not None and item[0] >= plan['weight_t']]
        if done:
            day, status = date.fromisoformat(done), '탑재 완료'
        elif any(p not in plans for p in prior):
            status = '선행 블록 계획 없음'
        elif any(scheduled[p] is None for p in prior):
            status = '선행 블록 보류'
        elif key in assigned:
            crane, planned_day = assigned[key]
            day = date.fromisoformat(planned_day)
            status = '크레인 배정'
            if any(scheduled[p] > day for p in prior):
                status = '선행 블록보다 먼저 배정'
            elif plan['kind'] == '도크' and not booked(db, plan['destination'], plan['hull'], planned_day):
                status = '도크 예약 범위 밖'
        elif plan['weight_t'] is None:
            status = '블록 중량 미등록'
        elif not station_cranes.get(plan['destination']):
            status = '탑재 크레인 미배정'
        elif not options:
            status = '크레인 용량 부족'
        else:
            day = date.fromisoformat(plan['forecast'])
            for p in prior:
                day = max(day, scheduled[p] + timedelta(days=1))
            day = workday(day, closed)
            while all((item[1], day) in busy for item in options):
                day = workday(day + timedelta(days=1), closed)
            crane = next(item[1] for item in options if (item[1], day) not in busy)
            busy.add((crane, day))
            status = '일정 제안'
            if plan['kind'] == '도크' and not booked(db, plan['destination'], plan['hull'], day.isoformat()):
                status = '도크 예약 범위 밖'
        scheduled[key] = day
        rows.append({'block': key, 'hull': plan['hull'], 'destination': plan['destination'],
                     'weight_t': plan['weight_t'], 'forecast': plan['forecast'],
                     'predecessor_blocks': ', '.join(prior) or None, 'crane': crane,
                     'scheduled_day': day.isoformat() if day else None,
                     'delay_days': (day - date.fromisoformat(plan['forecast'])).days if day else None,
                     'planning_status': status})
    return rows


def read(db, table):
    if table == 'erection_schedule':
        return erection_schedule(db)
    if table == 'equipment_specs':
        return [dict(row) for row in db.execute('SELECT * FROM equipment_specs ORDER BY equipment')]
    if table == 'holidays':
        return [dict(row) for row in db.execute('SELECT * FROM holidays ORDER BY holiday')]
    return None
