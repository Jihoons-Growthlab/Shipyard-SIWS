"""Shared yard plan replay (GET/POST /api/yard_sim).

One playback session per ERP process, read by every screen (Portal equipment map, Unity twin): a scenario built from
the ERP plan of one hull, a simulation clock and the state of blocks, cranes and vehicles at that clock. The state is
a pure function of (scenario, time), so two screens asking at the same moment get the same positions, and the loop
back to the start of the range resets everything at once. Nothing here writes to the database: plans, actual moves,
inventory and confirmed block locations are only read.

Sources of each movement (field ``source`` of every block and equipment item):

- ``plan``: an ERP plan position (block_stock_plans slot, the landed position on the hull from hull_placements and
  block geometry) or a parked/idle position.
- ``plan_sim``: timed from an ERP plan (crane_assignments start hour and hours); the motion inside the window
  (travel, hoist, carry, lower) is a simulation of that plan.
- ``estimated``: no ERP time or route exists: transports between stocking stays (straight-line route, estimated start
  and duration), cranes without registered rails (dock axis), lifts without a crane assignment.
- ``actual`` is never produced by the replay: confirmed moves are only used as evidence of which vehicle carried a block,
  and are counted separately under ``actual``.

Coordinates are ERP yard metres (x, y; heading from yard +X towards +Y), heights are yard z in the Unity frame
(the quay surface is at 12 m, ``QUAY_LEVEL``); block positions are its centre of gravity.
"""

import bisect
import geometry
import math
import threading
import time
from datetime import date, datetime, time as clock, timedelta, timezone

KST = timezone(timedelta(hours=9))
REQUIRED = ('hull_placements', 'location_geo', 'erection_timeline', 'block_stock_plans', 'crane_assignments')
ROUTES = ('hull_placements', 'location_geo', 'erection_timeline', 'block_stock_plans', 'crane_assignments',
          'crane_rails', 'equipment_specs', 'equipment', 'moves', 'telemetry', 'crane_kinematics', 'crane_lifts',
          'crane_load_charts')

QUAY_LEVEL = 12.0           # Unity YardLayout.QuayLevel (quay surface, yard z)
DUNNAGE = 0.3               # supports under a stocked block (Unity BlockMotion.Dunnage)
WALL = 6.0                  # dock wall thickness (Unity YardLayout.WallThickness)
HEAD_RAIL = 100.0           # goliath rails run this far inland of the dock head (Unity GantryCraneView)
RAIL_OFFSET = 8.0           # rails this far outside the dock walls
CARRY_CLEARANCE = 30.0      # carried this high above the higher of start and landing (Unity BlockMotion)
TROLLEY_MARGIN = 9.0        # the trolley stops this far inside the rails
HOOK_PARKED = QUAY_LEVEL + 70.0

TRANSPORT_START_HOUR = 8    # estimated: transports start at 08:00 KST unless the lift needs the block earlier
EARLY_START_HOUR = 5
EMPTY_SPEED = 2.0           # m/s, estimated transporter speed without load
LOADED_SPEED = 1.0          # m/s, with a block
LOAD_MINUTES = 10           # jacking up / setting down
DEFAULT_LIFT_HOUR, DEFAULT_LIFT_HOURS = 8, 2

DAYS_PER_SECOND = 4.0       # ShipyardTwinSettings.DaysPerSecond: idle days per second at ×1
WORK_MINUTES_PER_SECOND = 20.0   # while a block is transported or lifted: minutes per second at ×1
MIN_SPEED, MAX_SPEED = 0.05, 32
CRANE_MINUTES_PER_SECOND = 30.0  # while a rail jib crane works (outfitting lifts): minutes per second at ×1

# Rail jib cranes (crane_kinematics): boom from a slewing pivot on the rail; hook radius = boom · cos(boom angle).
# They move only for their ERP crane_assignments (lifts of outfitting materials: pipes, cable drums, small equipment,
# scaffolding, welding machines; hull blocks go to the goliath). Without a job they wait parked, boom up, hook raised,
# nothing on the hook; during a 크레인 정비 job they stand still.
JIB_TYPES = {'지브 크레인'}
DEFAULT_KINEMATICS = {'boom_m': 50.0, 'pivot_height_m': 30.0, 'luff_min_deg': 15.0, 'luff_max_deg': 80.0,
                      'rest_luff_deg': 70.0, 'staging_offset_m': 15.0, 'danger_margin_m': 5.0}
REST_HOOK = 28.0             # hook height above the quay while parked or travelling (m), at most 4 m under the boom tip
PICK_HOOK = 2.0              # hook height above the quay at a quay pick-up (m)
LIFT_CYCLE_MINUTES = 12      # one lift (pick on the quay, set down on board) inside a job window
TRAVEL_SHARE = RETURN_SHARE = 0.06   # of the job window: rail travel at the start, slew back to rest at the end
# One lift cycle: (end share, state, motion).
LIFT_PHASES = ((0.18, '선회', 'to_pick'), (0.32, '권하', 'lower_pick'), (0.46, '권상', 'hoist'), (0.66, '선회', 'to_drop'),
               (0.80, '권하', 'lower_drop'), (0.92, '권상', 'release'), (1.0, '작업 대기', 'hold'))

SOURCE_LABELS = {'plan': 'ERP 계획 위치', 'plan_sim': '계획 기반 모의', 'estimated': '추정 경로·시각', 'actual': '이동 실적'}
CRANE_TYPES = {'골리앗 크레인', '갠트리 크레인', '지브 크레인'}
VEHICLE_TYPES = {'트랜스포터'}


def _day(value):
    return date.fromisoformat(str(value)[:10]) if value else None


def _at(day, hours=0.0):
    return datetime.combine(day, clock(0), KST) + timedelta(hours=hours)


def _iso(moment):
    return moment.astimezone(KST).isoformat(timespec='seconds')


def _smooth(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)


def _lerp(a, b, t):
    return a + (b - a) * t


def _lerp_angle(a, b, t):
    d = (b - a + 540.0) % 360.0 - 180.0
    return (a + d * t) % 360.0


def _zone(location):
    """'DEMO-A-YARD' → 'DEMO-A'; main-yard IDs share 'MAIN' (GY, DT, DTW)."""
    prefix = location.rsplit('-', 1)[0] if '-' in location else location
    return 'MAIN' if prefix.split('-')[0] in ('GY', 'DT', 'DTW') else prefix


def luff_for(k, radius):
    """Boom angle (deg) that puts the hook at a radius, within the boom's angle range."""
    low = math.cos(math.radians(k['luff_max_deg']))
    high = math.cos(math.radians(k['luff_min_deg']))
    return math.degrees(math.acos(max(low, min(high, radius / k['boom_m']))))


def rest_hook(k):
    """Hook height (above the quay) while parked: REST_HOOK, at most 4 m under the parked boom tip."""
    return min(REST_HOOK, k['pivot_height_m'] + k['boom_m'] * math.sin(math.radians(k['rest_luff_deg'])) - 4.0)


def jib_pose(rail, k, param, slew, luff, hook):
    """Pivot, boom tip and hook of a jib crane (yard m; heights absolute in the Unity frame, quay at 12 m)."""
    px, py = rail.point(param)
    radius = k['boom_m'] * math.cos(math.radians(luff))
    a = math.radians(slew)
    tip_z = QUAY_LEVEL + k['pivot_height_m'] + k['boom_m'] * math.sin(math.radians(luff))
    hx, hy = px + radius * math.cos(a), py + radius * math.sin(a)
    return {'x': px, 'y': py, 'slew_deg': slew % 360.0, 'luff_deg': luff, 'radius_m': radius, 'tip_z': tip_z,
            'hook': {'x': hx, 'y': hy, 'z': min(QUAY_LEVEL + hook, tip_z - 3.0)}}


def home_position(equipment_id):
    """Parked rail position (0..1) of a crane before its first job: the middle of its travel zone, so cranes sharing a
    rail wait apart."""
    return 0.5


class Rail:
    """A crane's travel line: points at parameter 0..1 and the signed offset across it."""

    def __init__(self, x0, y0, x1, y1, gauge, source, basis):
        self.x0, self.y0, self.x1, self.y1, self.gauge = x0, y0, x1, y1, gauge
        self.length = max(1e-6, math.hypot(x1 - x0, y1 - y0))
        self.ux, self.uy = (x1 - x0) / self.length, (y1 - y0) / self.length
        self.source, self.basis = source, basis

    @property
    def heading(self):
        return math.degrees(math.atan2(self.uy, self.ux)) % 360.0

    def point(self, param, lateral=0.0):
        return (self.x0 + self.ux * self.length * param - self.uy * lateral,
                self.y0 + self.uy * self.length * param + self.ux * lateral)

    def project(self, x, y):
        dx, dy = x - self.x0, y - self.y0
        return (dx * self.ux + dy * self.uy) / self.length, -dx * self.uy + dy * self.ux

    def reaches(self, x, y):
        param, lateral = self.project(x, y)
        return -1e-6 <= param <= 1 + 1e-6 and abs(lateral) <= self.gauge / 2 - TROLLEY_MARGIN + 1e-6

    def as_dict(self):
        return {'x0': round(self.x0, 2), 'y0': round(self.y0, 2), 'x1': round(self.x1, 2), 'y1': round(self.y1, 2),
                'gauge_m': round(self.gauge, 2), 'source': self.source, 'basis': self.basis}


def dock_rail(facility):
    """Goliath rails along a dock (as Unity GantryCraneView): from 100 m inland of the head to 10 m short of the gate."""
    a = math.radians(facility['heading_deg'])
    pit = facility['size_x_m'] / 2 - WALL
    cx, cy = facility['x'], facility['y']
    start, end = -pit - HEAD_RAIL, pit - 10
    return Rail(cx + math.cos(a) * start, cy + math.sin(a) * start, cx + math.cos(a) * end, cy + math.sin(a) * end,
                facility['size_y_m'] + 2 * RAIL_OFFSET, 'estimated', '레일 미등록 · 도크 축 추정')


def landed_pose(placement, block):
    """Centre of gravity of an erected block in yard coordinates (hull origin + heading-rotated ship cog)."""
    a = math.radians(placement['heading_deg'])
    cx, cy = block['cog_x'], block['cog_y']
    return (placement['origin_x'] + cx * math.cos(a) - cy * math.sin(a),
            placement['origin_y'] + cx * math.sin(a) + cy * math.cos(a),
            placement['origin_z'] + block['cog_z'], placement['heading_deg'] % 360.0)


def ground_z(block):
    """Centre of gravity height of a block resting on supports on the quay."""
    return QUAY_LEVEL + DUNNAGE + (block['cog_z'] - block['z_min'])


def build(hull, rows, range_start=None, range_end=None):
    """Scenario of one hull from ERP route rows (dict route → list of rows). Raises ValueError when it cannot be built."""
    placement = next((p for p in rows['hull_placements'] if p['hull'] == hull), None)
    if placement is None:
        raise ValueError(f'{hull}의 도크 배치(hull_placements)가 없어 재생할 수 없습니다.')
    geo = {g['location']: g for g in rows['location_geo']}
    specs = {s['equipment']: s for s in rows['equipment_specs']}
    names = {e['id']: e['name'] for e in rows['equipment']}
    rails = {r['equipment']: r for r in rows['crane_rails']}
    timeline = [b for b in rows['erection_timeline'] if b['hull'] == hull]
    if not timeline:
        raise ValueError(f'{hull}의 탑재 일정(erection_timeline)이 없습니다.')
    block_ids = {b['block'] for b in timeline}
    stays = {}
    for row in sorted(rows['block_stock_plans'], key=lambda r: (r['block'], r['stock_from'])):
        if row['block'] in block_ids:
            stays.setdefault(row['block'], []).append({
                'location': row['location'], 'x': row['x'], 'y': row['y'], 'heading_deg': row['heading_deg'] % 360.0,
                'from': _at(_day(row['stock_from'])),
                'until': _at(_day(row['stock_until']) + timedelta(days=1)) if row['stock_until'] else None})
    lifts = {}
    other_work = []
    for row in rows['crane_assignments']:
        if row['hull'] != hull:
            continue
        if row['crane_work'] == '블록 탑재' and row.get('block') in block_ids:
            lifts.setdefault(row['block'], row)
        else:
            other_work.append(row)
    confirmed = {}
    for move in rows['moves']:
        if move['block'] in block_ids and move.get('confirmed_at'):
            confirmed.setdefault((move['block'], move.get('origin'), move['destination']), move['equipment'])
    actual_moves = sum(1 for m in rows['moves'] if m['block'] in block_ids)
    actual_move_ids = {m['id'] for m in rows['moves'] if m['block'] in block_ids}
    actual_telemetry = sum(1 for t in rows['telemetry'] if t['move'] in actual_move_ids)

    equipment = {}

    def crane(equipment_id, dock_id, basis):
        if equipment_id in equipment:
            return equipment[equipment_id]
        spec = specs.get(equipment_id, {})
        if equipment_id in rails:
            r = rails[equipment_id]
            rail = Rail(r['rail_x0'], r['rail_y0'], r['rail_x1'], r['rail_y1'], r['gauge_m'], 'plan', 'ERP 크레인 레일')
        elif dock_id in geo and geo[dock_id].get('size_x_m'):
            rail = dock_rail(geo[dock_id])
        else:
            rail = None
        equipment[equipment_id] = {
            'id': equipment_id, 'name': names.get(equipment_id, equipment_id), 'kind': 'crane', 'crane_type': 'gantry',
            'type': spec.get('equipment_type', '크레인'), 'capacity_t': spec.get('capacity_t'), 'station': spec.get('station'),
            'rail': rail, 'basis': basis, 'jobs': [], 'work': []}
        return equipment[equipment_id]

    def vehicle(equipment_id, basis):
        if equipment_id not in equipment:
            spec = specs.get(equipment_id, {})
            home = geo.get(spec.get('station') or '')
            equipment[equipment_id] = {
                'id': equipment_id, 'name': names.get(equipment_id, equipment_id), 'kind': 'vehicle',
                'type': spec.get('equipment_type', '차량'), 'capacity_t': spec.get('capacity_t'), 'station': spec.get('station'),
                'home': (home['x'], home['y']) if home else None, 'basis': basis, 'legs': []}
        return equipment[equipment_id]

    transporters = [s for s in specs.values() if s['equipment_type'] in VEHICLE_TYPES]

    def link_vehicle(block_id, origin, destination, weight):
        """ERP evidence for the vehicle of a transport: a confirmed move of this block on the same route, else the
        transporter stationed at either end, else one stationed in the same area. None when there is none."""
        eligible = [spec for spec in transporters if spec.get('capacity_t') is None or spec['capacity_t'] >= weight]
        moved = confirmed.get((block_id, origin, destination))
        if any(spec['equipment'] == moved for spec in eligible):
            return moved, f'ERP 이동 실적({origin}→{destination})의 장비'
        for spec in eligible:
            if spec.get('station') in (destination, origin):
                return spec['equipment'], f"장비 배치 시설 {spec['station']}"
        for spec in eligible:
            if spec.get('station') and _zone(spec['station']) in (_zone(destination or ''), _zone(origin or '')):
                return spec['equipment'], f"같은 구역 배치({spec['station']})"
        return None, None

    dock_cranes = [s for s in specs.values() if s['equipment_type'] in CRANE_TYPES - {'지브 크레인'}]
    blocks = []
    legs = []
    for order, record in enumerate(sorted(timeline, key=lambda b: (b.get('erected_day') or b.get('forecast') or b.get('baseline') or '9999', b['step'], b['block']))):
        block_id = record['block']
        plan_day = _day(record.get('erected_day') or record.get('forecast') or record.get('baseline'))
        landed = landed_pose(placement, record)
        item = {'id': block_id, 'order': order, 'step': record['step'], 'weight_t': record['weight_t'], 'plan_day': plan_day,
                'stays': stays.get(block_id, []), 'landed': landed, 'ground_z': ground_z(record),
                'height': record['z_max'] - record['z_min'], 'length': record['x_max'] - record['x_min'],
                'breadth': record['y_max'] - record['y_min'], 'lift': None, 'legs': []}
        assignment = lifts.get(block_id)
        if assignment:
            day = _day(assignment['work_day'])
            begin = _at(day, assignment['start_hour'])
            job = {'start': begin, 'end': begin + timedelta(hours=max(1, assignment['hours'])), 'source': 'plan_sim',
                   'basis': f"ERP 크레인 배정 {assignment['id']}", 'crane': assignment['equipment'], 'dock': assignment['work_area']}
        elif plan_day:
            stationed = next((s for s in dock_cranes if s.get('station') == placement['dock']), None)
            begin = _at(plan_day, DEFAULT_LIFT_HOUR)
            job = {'start': begin, 'end': begin + timedelta(hours=DEFAULT_LIFT_HOURS), 'source': 'estimated',
                   'basis': '크레인 배정 없음 · 탑재일 08시 2시간 추정', 'crane': stationed['equipment'] if stationed else None,
                   'dock': placement['dock']}
        else:
            job = None
        if job:
            job['block'] = block_id
            if not item['stays']:
                job['crane'] = None          # no stocking plan: nothing to lift from, the block stays unplaced
            elif job['crane']:
                owner = crane(job['crane'], job['dock'], '탑재 블록의 크레인 배정' if assignment else '도크 배치 크레인')
                if owner['rail'] is None:
                    job['crane'] = None
                else:
                    owner['jobs'].append(job)
            item['lift'] = job
        # Transports between stocking stays (location or slot changes), on the day the next stay starts.
        for previous, following in zip(item['stays'], item['stays'][1:]):
            if previous['location'] == following['location'] and math.hypot(previous['x'] - following['x'], previous['y'] - following['y']) < 1:
                continue
            legs.append({'block': block_id, 'order': order, 'day': following['from'].date(), 'origin': previous['location'],
                         'destination': following['location'], 'from_xy': (previous['x'], previous['y'], previous['heading_deg']),
                         'to_xy': (following['x'], following['y'], following['heading_deg'])})
        blocks.append(item)

    # Out of the crane's reach on the lift day: brought on the ground to the pick-up point under the rail head.
    for item in blocks:
        job = item['lift']
        if not job or not job['crane'] or not item['stays']:
            continue
        rail = equipment[job['crane']]['rail']
        last = item['stays'][-1]
        if not rail.reaches(last['x'], last['y']):
            px, py = rail.point(0.08)
            legs.append({'block': item['id'], 'order': item['order'], 'day': job['start'].date(), 'origin': last['location'],
                         'destination': job['dock'], 'from_xy': (last['x'], last['y'], last['heading_deg']),
                         'to_xy': (px, py, rail.heading), 'pickup': True})

    # Vehicles: each transporter drives its legs one after another from 08:00 (earlier when the lift needs the block).
    free = {}
    position = {}
    for leg in sorted(legs, key=lambda l: (l['day'], l['order'])):
        item = next(b for b in blocks if b['id'] == leg['block'])
        lift_start = item['lift']['start'] if item['lift'] else None
        vehicle_id, basis = link_vehicle(leg['block'], leg['origin'], leg['destination'], item.get('weight_t') or 0)
        early = lift_start is not None and lift_start.date() == leg['day']
        earliest = _at(leg['day'], EARLY_START_HOUR if early else TRANSPORT_START_HOUR)
        leg['basis'] = basis or '운반 장비를 연결할 근거 없음'
        if vehicle_id is None:
            leg['vehicle'] = None
            leg['start'] = leg['load'] = leg['carry'] = leg['unload'] = leg['end'] = earliest
        else:
            owner = vehicle(vehicle_id, basis)
            here = position.get(vehicle_id) or owner['home'] or leg['from_xy'][:2]
            start = max(earliest, free.get(vehicle_id, earliest))
            approach = math.hypot(here[0] - leg['from_xy'][0], here[1] - leg['from_xy'][1]) / EMPTY_SPEED
            carry = math.hypot(leg['to_xy'][0] - leg['from_xy'][0], leg['to_xy'][1] - leg['from_xy'][1]) / LOADED_SPEED
            leg.update(vehicle=vehicle_id, start=start, here=here)
            leg['load'] = start + timedelta(seconds=approach)
            leg['carry'] = leg['load'] + timedelta(minutes=LOAD_MINUTES)
            leg['unload'] = leg['carry'] + timedelta(seconds=carry)
            leg['end'] = leg['unload'] + timedelta(minutes=LOAD_MINUTES)
            free[vehicle_id] = leg['end']
            position[vehicle_id] = leg['to_xy'][:2]
            owner['legs'].append(leg)
        item['legs'].append(leg)

    for owner in equipment.values():
        if owner['kind'] == 'crane' and owner['crane_type'] == 'gantry':
            owner['jobs'].sort(key=lambda j: j['start'])
            param = 0.08   # parked over the PE yard at the head of the rails
            lateral = 0.0
            for job in owner['jobs']:
                item = next(b for b in blocks if b['id'] == job['block'])
                sx, sy = _pick_xy(item, job)
                pick, pick_lat = owner['rail'].project(sx, sy)
                land, land_lat = owner['rail'].project(*item['landed'][:2])
                half = owner['rail'].gauge / 2 - TROLLEY_MARGIN
                job.update(from_param=param, from_lat=lateral, pick=min(1.0, max(0.0, pick)), pick_lat=max(-half, min(half, pick_lat)),
                           land=min(1.0, max(0.0, land)), land_lat=max(-half, min(half, land_lat)))
                param, lateral = job['land'], 0.0
            owner['park'] = 0.08
            owner['work'] = sorted([w for w in other_work if w['equipment'] == owner['id']], key=lambda w: (w['work_day'], w['start_hour']))
            owner['work_windows'] = [(_at(_day(w['work_day']), w['start_hour']), _at(_day(w['work_day']), w['start_hour'] + max(1, w['hours'])), w['crane_work'])
                                     for w in owner['work']]

    moments = [b['lift']['start'] for b in blocks if b['lift']] + [s['from'] for b in blocks for s in b['stays']] + \
              [l['start'] for l in legs]
    ends = [b['lift']['end'] for b in blocks if b['lift']] + [l['end'] for l in legs]
    if not moments:
        raise ValueError(f'{hull}에 재생할 적치·운반·탑재 계획이 없습니다.')
    first = _at(min(moments).date())
    last = _at(max(ends or moments).date() + timedelta(days=1))
    start = _at(_day(range_start)) if range_start else first
    end = _at(_day(range_end) + timedelta(days=1)) if range_end else last
    if end <= start:
        raise ValueError('재생 구간의 종료일이 시작일보다 앞섭니다.')
    jib_windows = add_jib_cranes(rows, hull, equipment, rails, specs, names)
    windows = [(l['start'], l['end'], 'block') for l in legs if l.get('vehicle') and l['end'] > l['start']] + \
        [(b['lift']['start'], b['lift']['end'], 'block') for b in blocks if b['lift'] and b['lift']['crane']] + \
        [(a, b, 'crane') for a, b in jib_windows]
    merged = _segments(windows)
    unplaced_blocks = [b['id'] for b in blocks if not b['stays']]
    return {
        'id': f"{hull}:{start.date()}:{(end - timedelta(days=1)).date()}", 'hull': hull, 'dock': placement['dock'],
        'start': start, 'end': end, 'blocks': blocks, 'equipment': equipment, 'windows': merged,
        'edges': sorted({t for w in merged for t in w[:2] if start < t < end}),
        'unplaced': {'blocks': unplaced_blocks,
                     'lifts_without_crane': [b['id'] for b in blocks if b['stays'] and b['lift'] and not b['lift']['crane']],
                     'transports_without_vehicle': sorted({l['block'] for l in legs if not l.get('vehicle')})},
        'actual': {'moves': actual_moves, 'telemetry': actual_telemetry,
                   'note': '이동 실적은 운반 장비 연결 근거로만 쓰고, 재생 위치는 계획으로 계산합니다.'},
    }


def _segments(windows):
    """Work windows → non-overlapping [start, end, kind] segments; where kinds overlap the block work (slower) wins."""
    edges = sorted({t for a, b, _ in windows for t in (a, b)})
    segments = []
    for a, b in zip(edges, edges[1:]):
        kinds = {kind for w0, w1, kind in windows if w0 <= a and b <= w1}
        if not kinds:
            continue
        kind = 'block' if 'block' in kinds else 'crane'
        if segments and segments[-1][1] == a and segments[-1][2] == kind:
            segments[-1][1] = b
        else:
            segments.append([a, b, kind])
    return segments


def add_jib_cranes(rows, hull, equipment, rails, specs, names, every_crane=False):
    """Rail jib cranes and their ERP work: rails, boom data (crane_kinematics, else defaults flagged as estimated) and
    their crane_assignments (all hulls when hull is None) with the lift points of crane_lifts (else derived from the
    rail and jib direction, estimated). every_crane adds rail cranes without work too. Returns the job windows."""
    kinematics = {r['equipment']: r for r in rows.get('crane_kinematics') or []}
    points = {r['assignment']: r for r in rows.get('crane_lifts') or []}
    charts = {}
    for r in rows.get('crane_load_charts') or []:
        charts.setdefault(r['equipment'], []).append((float(r['radius_m']), float(r['capacity_t'])))

    def owner_of(equipment_id):
        if equipment_id not in equipment:
            r, spec = rails[equipment_id], specs.get(equipment_id, {})
            known = kinematics.get(equipment_id)
            equipment[equipment_id] = {
                'id': equipment_id, 'name': names.get(equipment_id, equipment_id), 'kind': 'crane', 'crane_type': 'jib',
                'type': spec.get('equipment_type'), 'capacity_t': spec.get('capacity_t'), 'station': spec.get('station'),
                'rail': Rail(r['rail_x0'], r['rail_y0'], r['rail_x1'], r['rail_y1'], r['gauge_m'], 'plan', 'ERP 크레인 레일'),
                'k': {key: float(known.get(key) or value) for key, value in DEFAULT_KINEMATICS.items()} if known
                else dict(DEFAULT_KINEMATICS), 'chart': sorted(charts.get(equipment_id, [])),
                'k_source': 'ERP 크레인 붐 제원' if known else '붐 제원 미등록 · 기본값',
                'jib': r['jib_heading_deg'] % 360.0, 'park': home_position(equipment_id), 'jobs': [], 'service': [],
                'basis': 'ERP 크레인 배정' if hull is None else '호선 크레인 배정의 지브 크레인'}
        return equipment[equipment_id]

    if every_crane:
        for equipment_id in rails:
            if specs.get(equipment_id, {}).get('equipment_type') in JIB_TYPES:
                owner_of(equipment_id)
    windows = []
    for row in sorted(rows['crane_assignments'], key=lambda r: (r['work_day'], r['start_hour'], r['id'])):
        if (hull is not None and row['hull'] != hull) or row['equipment'] not in rails or \
                specs.get(row['equipment'], {}).get('equipment_type') not in JIB_TYPES or row['crane_work'] == '블록 탑재':
            continue
        owner = owner_of(row['equipment'])
        rail, k = owner['rail'], owner['k']
        begin = _at(_day(row['work_day']), row['start_hour'])
        finish = begin + timedelta(hours=max(1, row['hours']))
        if row['crane_work'] == '크레인 정비':
            owner['service'].append((begin, finish))
            continue
        lift = points.get(row['id'])
        if lift:
            pick, drop = (lift['pick_x'], lift['pick_y'], lift['pick_z']), (lift['drop_x'], lift['drop_y'], lift['drop_z'])
            source = 'plan_sim' if lift['point_source'] == '현장 계획' else 'estimated'
            basis = f"ERP 크레인 배정 {row['id']} · 인양 지점({lift['point_source']})"
        else:
            param = 0.2 + 0.6 * (sum(ord(c) for c in row['id']) % 100) / 100
            px, py = rail.point(param)
            a = math.radians(owner['jib'])
            reach = k['boom_m'] * math.cos(math.radians(k['luff_min_deg']))
            pick = (px - math.cos(a) * k['staging_offset_m'], py - math.sin(a) * k['staging_offset_m'], PICK_HOOK)
            drop = (px + math.cos(a) * reach * 0.7, py + math.sin(a) * reach * 0.7, 8.0)
            source, basis = 'estimated', f"ERP 크레인 배정 {row['id']} · 인양 지점 미등록(추정)"
        param, _ = rail.project((pick[0] + drop[0]) / 2, (pick[1] + drop[1]) / 2)
        minutes = (finish - begin).total_seconds() / 60 * (1 - TRAVEL_SHARE - RETURN_SHARE)
        owner['jobs'].append({'id': row['id'], 'hull': row['hull'], 'start': begin, 'end': finish, 'work': row['crane_work'],
                              'lift_t': row['lift_t'], 'pick': pick, 'drop': drop, 'param': min(1.0, max(0.0, param)),
                              'lifts': max(1, int(minutes // LIFT_CYCLE_MINUTES)), 'source': source, 'basis': basis})
        windows.append((begin, finish))
    for owner in equipment.values():
        if owner.get('crane_type') == 'jib':
            for job in owner['jobs']:
                job['from_param'] = owner['park']   # every job starts from, and ends back at, the parked spot
    return windows


def _jib_job_pose(owner, job, t):
    """(param, slew, luff, hook above quay, load t, state, lift number) of a jib crane inside its job window: rail
    travel to the job, then repeated lifts (slew to the staged load, lower, hoist, slew over the hull, lower, release,
    short hold; LIFT_CYCLE_MINUTES each), then slew back to rest."""
    rail, k = owner['rail'], owner['k']
    u = (t - job['start']) / (job['end'] - job['start'])
    px, py = rail.point(job['param'])

    def aim(point):
        radius = math.hypot(point[0] - px, point[1] - py)
        return math.degrees(math.atan2(point[1] - py, point[0] - px)) % 360.0, luff_for(k, radius)

    slew0, luff0, top = owner['jib'], k['rest_luff_deg'], rest_hook(k)
    slew_p, luff_p = aim(job['pick'])
    slew_d, luff_d = aim(job['drop'])
    carry = max(job['pick'][2], job['drop'][2]) + 8.0
    if u < TRAVEL_SHARE:
        return _lerp(job['from_param'], job['param'], _smooth(u / TRAVEL_SHARE)), slew0, luff0, top, 0.0, '주행', 0
    if u >= 1 - RETURN_SHARE:
        # Slew back to the jib direction and travel back to the parked spot (the middle of the zone).
        s = _smooth((u - 1 + RETURN_SHARE) / RETURN_SHARE)
        return _lerp(job['param'], owner['park'], s), _lerp_angle(slew_d, slew0, s), _lerp(luff_d, luff0, s), top, 0.0, \
            '선회' if s < 0.5 else '주행', job['lifts']
    span = (u - TRAVEL_SHARE) / (1 - TRAVEL_SHARE - RETURN_SHARE) * job['lifts']
    number = min(job['lifts'] - 1, int(span))
    c = span - number
    # The first lift turns out from the parked jib, the next ones from where the last load was set down.
    from_slew, from_luff = (slew0, luff0) if number == 0 else (slew_d, luff_d)
    start = 0.0
    for end, state, move in LIFT_PHASES:
        if c < end or end == 1.0:
            s = _smooth((c - start) / (end - start))
            break
        start = end
    p = job['param']
    if move == 'to_pick':
        return p, _lerp_angle(from_slew, slew_p, s), _lerp(from_luff, luff_p, s), top, 0.0, state, number + 1
    if move == 'lower_pick':
        return p, slew_p, luff_p, _lerp(top, job['pick'][2], s), 0.0, state, number + 1
    if move == 'hoist':
        return p, slew_p, luff_p, _lerp(job['pick'][2], carry, s), job['lift_t'], '인양', number + 1
    if move == 'to_drop':
        return p, _lerp_angle(slew_p, slew_d, s), _lerp(luff_p, luff_d, s), carry, job['lift_t'], state, number + 1
    if move == 'lower_drop':
        return p, slew_d, luff_d, _lerp(carry, job['drop'][2], s), job['lift_t'], '착지', number + 1
    if move == 'release':
        return p, slew_d, luff_d, _lerp(job['drop'][2], top, s), 0.0, state, number + 1
    return p, slew_d, luff_d, top, 0.0, state, number + 1


def jib_state(owner, t, base):
    """A rail jib crane at t: from its ERP jobs (travel, lifts, slew and travel back); parked in the middle of its zone
    with its boom up between them (no load); '정비' during maintenance."""
    rail, k = owner['rail'], owner['k']
    param, slew, luff, hook, load, state = owner['park'], owner['jib'], k['rest_luff_deg'], rest_hook(k), 0.0, '대기'
    source, basis, work, job_id, lift_no, lifts, hull = 'plan', None, None, None, 0, 0, None
    index = bisect.bisect_right(owner.setdefault('_starts', [j['start'] for j in owner['jobs']]), t) - 1
    if index >= 0:
        job = owner['jobs'][index]
        if t < job['end']:
            param, slew, luff, hook, load, state, lift_no = _jib_job_pose(owner, job, t)
            source, basis, work, job_id, lifts, hull = job['source'], job['basis'], job['work'], job['id'], job['lifts'], job['hull']
    if state == '대기' and any(a <= t < b for a, b in owner['service']):
        state, basis = '정비', 'ERP 크레인 배정(크레인 정비)'
    pose = jib_pose(rail, k, param, slew, luff, hook)
    chart = owner.get('chart') or []
    rated = geometry.chart_capacity(chart, pose['radius_m'])
    return {**base, 'crane_type': 'jib', 'state': state, 'work': work, 'assignment': job_id, 'hull': hull,
            'lift_no': lift_no, 'lifts': lifts, 'x': pose['x'], 'y': pose['y'],
            'heading_deg': pose['slew_deg'], 'slew_deg': pose['slew_deg'], 'luff_deg': pose['luff_deg'],
            'radius_m': pose['radius_m'], 'tip_z': pose['tip_z'], 'hook': pose['hook'], 'rail': rail.as_dict(),
            'rail_pos': param, 'trolley_m': 0.0, 'boom_m': k['boom_m'], 'pivot_height_m': k['pivot_height_m'],
            'load_t': load, 'loaded': load > 0, 'load_kind': '의장품' if load > 0 else None, 'source': source,
            'job_basis': basis, 'kinematics': owner['k_source'],
            # Safety: danger zone radius around the slewing centre (max hook radius + margin) and the load chart rating
            # at the present hook radius (null without a chart; load_t over it is an overload).
            'danger_radius_m': round(geometry.reach(k)[1] + k['danger_margin_m'], 2),
            'rated_t': rated, 'overload': rated is not None and load > rated + 1e-6,
            'load_chart': [{'radius_m': r, 'capacity_t': c} for r, c in chart]}


def crane_activity(rows, t):
    """Every ERP rail jib crane at t (the live view of the equipment monitor and the twin outside a replay): from the
    ERP crane_assignments of all hulls, crane_lifts and crane_kinematics."""
    specs = {s['equipment']: s for s in rows['equipment_specs']}
    names = {e['id']: e['name'] for e in rows['equipment']}
    rails = {r['equipment']: r for r in rows['crane_rails']}
    owners = {}
    add_jib_cranes(rows, None, owners, rails, specs, names, every_crane=True)
    cranes = []
    for owner in owners.values():
        base = {'id': owner['id'], 'name': owner['name'], 'kind': 'crane', 'type': owner['type'], 'capacity_t': owner['capacity_t'],
                'station': owner['station'], 'block': None, 'basis': owner['basis']}
        cranes.append(_rounded(jib_state(owner, t, base)))
    return {'time': _iso(t), 'cranes': sorted(cranes, key=lambda c: c['id']), 'sources': SOURCE_LABELS,
            'rule': 'ERP 크레인 배정 시간에만 작업(인양 지점 crane_lifts), 그 밖에는 대기'}


def _rounded(row):
    for key in ('x', 'y', 'z', 'heading_deg', 'rail_pos', 'trolley_m', 'slew_deg', 'luff_deg', 'radius_m', 'tip_z'):
        if row.get(key) is not None:
            row[key] = _round(row[key], 3 if key == 'rail_pos' else 2)
    if row.get('hook'):
        row['hook'] = {k: _round(v) for k, v in row['hook'].items()}
    return row


def _pick_xy(item, job):
    """Where the block waits for the hook: the pick-up leg's end, else the last stay, else the landing (no stay)."""
    pickup = [l for l in item['legs'] if l.get('pickup')]
    if pickup:
        return pickup[-1]['to_xy'][:2]
    before = [s for s in item['stays'] if s['from'] <= job['start']]
    stay = before[-1] if before else (item['stays'][-1] if item['stays'] else None)
    return (stay['x'], stay['y']) if stay else item['landed'][:2]


# ---------------------------------------------------------------- state at a moment

def _leg_phase(leg, t):
    """(state, x, y, heading, loaded) of a leg at t, or None outside it."""
    if not leg.get('vehicle') or not (leg['start'] <= t < leg['end']):
        return None
    fx, fy, fh = leg['from_xy']
    tx, ty, th = leg['to_xy']
    route_heading = math.degrees(math.atan2(ty - fy, tx - fx)) % 360.0
    if t < leg['load']:
        hx, hy = leg['here']
        u = (t - leg['start']) / max(timedelta(seconds=1), leg['load'] - leg['start'])
        heading = math.degrees(math.atan2(fy - hy, fx - hx)) % 360.0 if math.hypot(fx - hx, fy - hy) > 1 else route_heading
        return '접근', _lerp(hx, fx, u), _lerp(hy, fy, u), heading, False
    if t < leg['carry']:
        return '적재', fx, fy, route_heading, True
    if t < leg['unload']:
        u = (t - leg['carry']) / max(timedelta(seconds=1), leg['unload'] - leg['carry'])
        return '운반 중', _lerp(fx, tx, u), _lerp(fy, ty, u), route_heading, True
    return '하역', tx, ty, route_heading, True


# Lift window shares: travel to the block, hoist, carry along the rails, lower, release.
TRAVEL, HOIST, CARRY, LOWER = 0.2, 0.35, 0.7, 0.85


def _lift_phase(item, job, rail, t):
    """Crane and block at t inside a lift window: dict with crane param/lateral, hook z, block pose and states."""
    u = (t - job['start']) / (job['end'] - job['start'])
    sx, sy = _pick_xy(item, job)
    pickup = [l for l in item['legs'] if l.get('pickup')]
    start_heading = pickup[-1]['to_xy'][2] if pickup else item['stays'][-1]['heading_deg'] if item['stays'] else item['landed'][3]
    lx, ly, lz, lh = item['landed']
    gz = item['ground_z']
    carry_z = max(gz, lz) + CARRY_CLEARANCE
    top = item['height'] / 2 + 3.0
    if u < TRAVEL:
        s = _smooth(u / TRAVEL)
        param, lateral = _lerp(job['from_param'], job['pick'], s), _lerp(job['from_lat'], job['pick_lat'], s)
        return dict(param=param, lateral=lateral, hook=HOOK_PARKED, crane='주행', block='인양 대기', x=sx, y=sy, z=gz, heading=start_heading)
    if u < HOIST:
        z = _lerp(gz, carry_z, _smooth((u - TRAVEL) / (HOIST - TRAVEL)))
        return dict(param=job['pick'], lateral=job['pick_lat'], hook=z + top, crane='인양', block='인양', x=sx, y=sy, z=z, heading=start_heading)
    if u < CARRY:
        s = _smooth((u - HOIST) / (CARRY - HOIST))
        param, lateral = _lerp(job['pick'], job['land'], s), _lerp(job['pick_lat'], job['land_lat'], s)
        x, y = rail.point(param, lateral)
        return dict(param=param, lateral=lateral, hook=carry_z + top, crane='인양', block='인양', x=x, y=y, z=carry_z,
                    heading=_lerp_angle(start_heading, lh, s))
    if u < LOWER:
        z = _lerp(carry_z, lz, _smooth((u - CARRY) / (LOWER - CARRY)))
        return dict(param=job['land'], lateral=job['land_lat'], hook=z + top, crane='착지', block='착지', x=lx, y=ly, z=z, heading=lh)
    hook = _lerp(lz + top, HOOK_PARKED, _smooth((u - LOWER) / (1 - LOWER)))
    return dict(param=job['land'], lateral=_lerp(job['land_lat'], 0.0, _smooth((u - LOWER) / (1 - LOWER))), hook=hook, crane='착지',
                block='탑재 완료', x=lx, y=ly, z=lz, heading=lh)


def _round(value, digits=2):
    return round(value, digits) if value is not None else None


def block_state(scenario, item, t):
    """One block at t: state, place and pose; hidden (in production) blocks carry no position."""
    base = {'id': item['id'], 'step': item['step'], 'plan_day': item['plan_day'].isoformat() if item['plan_day'] else None,
            'length_m': round(item['length'], 2), 'breadth_m': round(item['breadth'], 2),
            'vehicle': None, 'crane': None, 'location': None, 'x': None, 'y': None, 'z': None, 'heading_deg': None,
            'source': 'plan', 'basis': None}
    job = item['lift']
    if job and t >= job['end']:
        x, y, z, h = item['landed']
        return {**base, 'state': '탑재 완료', 'location': scenario['dock'], 'x': x, 'y': y, 'z': z, 'heading_deg': h,
                'basis': 'hull_placements + 블록 형상(착지 위치)'}
    if not item['stays']:
        # No stocking plan: no place to lift it from. Unplaced until its planned erection is over.
        return {**base, 'state': '미배치', 'basis': '적치 계획과 좌표 없음'}
    if job and job['crane'] and t >= job['start']:
        rail = scenario['equipment'][job['crane']]['rail']
        phase = _lift_phase(item, job, rail, t)
        return {**base, 'state': phase['block'], 'location': scenario['dock'] if phase['block'] in ('착지', '탑재 완료') else None,
                'x': phase['x'], 'y': phase['y'], 'z': phase['z'], 'heading_deg': phase['heading'], 'crane': job['crane'],
                'source': job['source'], 'basis': job['basis']}
    for leg in item['legs']:
        phase = _leg_phase(leg, t)
        if phase:
            state, x, y, heading, loaded = phase
            if not loaded:
                fx, fy, fh = leg['from_xy']
                return {**base, 'state': '운반 대기', 'location': leg['origin'], 'x': fx, 'y': fy, 'z': item['ground_z'],
                        'heading_deg': fh, 'vehicle': leg['vehicle'], 'source': 'estimated', 'basis': leg['basis']}
            fh, th = leg['from_xy'][2], leg['to_xy'][2]
            u = 0.0 if state == '적재' else 1.0 if state == '하역' else \
                (t - leg['carry']) / max(timedelta(seconds=1), leg['unload'] - leg['carry'])
            return {**base, 'state': state, 'location': leg['origin'] if state == '적재' else leg['destination'] if state == '하역' else None,
                    'x': x, 'y': y, 'z': item['ground_z'] + 1.6, 'heading_deg': _lerp_angle(fh, th, u),
                    'vehicle': leg['vehicle'], 'source': 'estimated', 'basis': leg['basis']}
    # Resting: the latest place reached (first stay from its start day, then each completed leg's destination).
    place = None
    if item['stays'] and t >= item['stays'][0]['from']:
        first = item['stays'][0]
        place = (first['location'], first['x'], first['y'], first['heading_deg'], 'ERP 블록 적치 계획')
    for leg in item['legs']:
        if t >= leg['end']:
            tx, ty, th = leg['to_xy']
            place = (leg['destination'], tx, ty, th, 'ERP 블록 적치 계획' if not leg.get('pickup') else '인양 위치(추정)')
    if place is None:
        if not item['stays']:
            return {**base, 'state': '미배치', 'basis': '적치 계획과 좌표 없음'}
        return {**base, 'state': '생산 중', 'basis': f"첫 적치 {item['stays'][0]['from'].date()}"}
    location, x, y, h, basis = place
    waiting = job and job['crane'] and t < job['start'] and job['start'] - t < timedelta(hours=24)
    return {**base, 'state': '인양 대기' if waiting else '적치', 'location': location, 'x': x, 'y': y, 'z': item['ground_z'],
            'heading_deg': h, 'basis': basis}


def equipment_state(scenario, owner, t):
    base = {'id': owner['id'], 'name': owner['name'], 'kind': owner['kind'], 'type': owner['type'],
            'capacity_t': owner['capacity_t'], 'station': owner['station'], 'block': None, 'basis': owner['basis']}
    if owner['kind'] == 'crane' and owner['crane_type'] == 'jib':
        return jib_state(owner, t, base)
    if owner['kind'] == 'crane':
        base['crane_type'] = 'gantry'
        rail = owner['rail']
        param, lateral, hook, state, source, job_basis = owner['park'], 0.0, HOOK_PARKED, '대기', rail.source, None
        for job in owner['jobs']:
            if t >= job['end']:
                param, lateral = job['land'], 0.0
            elif t >= job['start']:
                item = next(b for b in scenario['blocks'] if b['id'] == job['block'])
                phase = _lift_phase(item, job, rail, t)
                param, lateral, hook, state = phase['param'], phase['lateral'], phase['hook'], phase['crane']
                base['block'], source, job_basis = job['block'], job['source'], job['basis']
                break
            else:
                break
        if state == '대기':
            other = next((label for a, b, label in owner['work_windows'] if a <= t < b), None)
            if other:
                state, job_basis = other, 'ERP 크레인 배정(블록 외 작업)'
        x, y = rail.point(param)
        hx, hy = rail.point(param, lateral)
        return {**base, 'state': state, 'x': x, 'y': y, 'heading_deg': rail.heading, 'rail': rail.as_dict(),
                'rail_pos': param, 'trolley_m': lateral, 'hook': {'x': hx, 'y': hy, 'z': hook},
                'source': source if state != '대기' else 'plan', 'job_basis': job_basis}
    # Vehicle: on a leg, else where the last leg ended (or its station) waiting.
    here, heading = owner['home'], 0.0
    for leg in owner['legs']:
        phase = _leg_phase(leg, t)
        if phase:
            state, x, y, heading, loaded = phase
            return {**base, 'state': state, 'x': x, 'y': y, 'heading_deg': heading, 'block': leg['block'], 'loaded': loaded,
                    'route': {'origin': leg['origin'], 'destination': leg['destination'], 'from': leg['from_xy'][:2], 'to': leg['to_xy'][:2]},
                    'source': 'estimated', 'job_basis': leg['basis'] + ' · 추정 경로(직선)·소요시간'}
        if t >= leg['end']:
            here = leg['to_xy'][:2]
            heading = math.degrees(math.atan2(leg['to_xy'][1] - leg['from_xy'][1], leg['to_xy'][0] - leg['from_xy'][0])) % 360.0
    if here is None:
        return {**base, 'state': '미배치', 'x': None, 'y': None, 'heading_deg': None, 'loaded': False, 'source': 'plan',
                'job_basis': '배치 시설 좌표 없음'}
    return {**base, 'state': '대기', 'x': here[0], 'y': here[1], 'heading_deg': heading, 'loaded': False, 'source': 'plan', 'job_basis': None}


def snapshot(scenario, t):
    """Every block and equipment item of the scenario at t (rounded for transport)."""
    blocks = [block_state(scenario, item, t) for item in scenario['blocks']]
    equipment = [equipment_state(scenario, owner, t) for owner in scenario['equipment'].values()]
    for row in blocks + equipment:
        for key in ('x', 'y', 'z', 'heading_deg', 'rail_pos', 'trolley_m', 'slew_deg', 'luff_deg', 'radius_m', 'tip_z'):
            if row.get(key) is not None:
                row[key] = _round(row[key], 3 if key == 'rail_pos' else 2)
        if row.get('hook'):
            row['hook'] = {k: _round(v) for k, v in row['hook'].items()}
    activity = [f"{b['id']} {b['state']}" + (f" · {b['crane'] or b['vehicle']}" if (b['crane'] or b['vehicle']) else '')
                for b in blocks if b['state'] in ('운반 대기', '적재', '운반 중', '하역', '인양', '착지')] + \
               [f"{e['id']} {e['state']} · {e['work']}" for e in equipment if e.get('crane_type') == 'jib' and e.get('work')]
    return blocks, equipment, activity


# ---------------------------------------------------------------- session (shared clock)

class Session:
    """The playback clock. Idle days pass at days_per_second; inside a transport or lift window time slows to
    work_minutes_per_second so the motion can be followed; both times the speed multiplier. At the end of the range the
    clock returns to its start (cycle + 1) and the state with it."""

    def __init__(self, scenario, days_per_second=DAYS_PER_SECOND, work_minutes_per_second=WORK_MINUTES_PER_SECOND, wall=None):
        self.scenario = scenario
        self.days_per_second = days_per_second
        self.work_minutes_per_second = work_minutes_per_second
        self.sim = scenario['start']
        self.playing = False
        self.speed = 1
        self.cycle = 0
        self.version = 0
        self.wall = time.monotonic() if wall is None else wall

    def work_kind(self, t):
        """'block' inside a block transport or lift, 'crane' inside a jib crane job, else None."""
        windows = self.scenario['windows']
        i = bisect.bisect_right([w[0] for w in windows], t) - 1
        return windows[i][2] if i >= 0 and windows[i][0] <= t < windows[i][1] else None

    def in_work(self, t):
        return self.work_kind(t) is not None

    def rate(self, t):
        """Simulated seconds per real second at t."""
        kind = self.work_kind(t)
        per_second = self.work_minutes_per_second * 60.0 if kind == 'block' else CRANE_MINUTES_PER_SECOND * 60.0 if kind == 'crane' \
            else self.days_per_second * 86400.0
        return per_second * self.speed

    def advance(self, wall=None):
        now = time.monotonic() if wall is None else wall
        remaining = max(0.0, now - self.wall)
        self.wall = now
        if not self.playing:
            return
        edges, end = self.scenario['edges'], self.scenario['end']
        for _ in range(100000):
            if remaining <= 1e-9:
                break
            i = bisect.bisect_right(edges, self.sim)
            boundary = edges[i] if i < len(edges) else end
            boundary = min(boundary, end)
            rate = self.rate(self.sim)
            needed = (boundary - self.sim).total_seconds() / rate
            if needed > remaining:
                self.sim += timedelta(seconds=remaining * rate)
                break
            remaining -= needed
            self.sim = boundary
            if self.sim >= end:
                self.sim = self.scenario['start']
                self.cycle += 1
                self.version += 1

    def control(self, action, value=None, wall=None):
        self.advance(wall)
        start, end = self.scenario['start'], self.scenario['end']
        if action == 'play':
            self.playing = True
        elif action == 'pause':
            self.playing = False
        elif action == 'speed':
            if type(value) not in (int, float) or not MIN_SPEED <= value <= MAX_SPEED:
                raise ValueError(f'속도는 ×{MIN_SPEED}~×{MAX_SPEED} 범위의 수치로 입력하세요.')
            self.speed = value
        elif action == 'seek':
            moment = datetime.fromisoformat(str(value))
            moment = moment if moment.tzinfo else moment.replace(tzinfo=KST)
            self.sim = min(max(moment, start), end - timedelta(seconds=1))
            self.version += 1
        elif action == 'restart':
            self.sim = start
            self.version += 1
        else:
            raise ValueError('재생 동작은 play, pause, speed, seek, restart 중 하나입니다.')

    def state(self, wall=None):
        self.advance(wall)
        scenario = self.scenario
        blocks, equipment, activity = snapshot(scenario, self.sim)
        return {
            'scenario': {'id': scenario['id'], 'hull': scenario['hull'], 'dock': scenario['dock'],
                         'start': _iso(scenario['start']), 'end': _iso(scenario['end']), 'unplaced': scenario['unplaced'],
                         'actual': scenario['actual']},
            'clock': {'time': _iso(self.sim), 'plan_day': self.sim.date().isoformat(), 'playing': self.playing,
                      'speed': self.speed, 'cycle': self.cycle, 'version': self.version, 'work': self.in_work(self.sim),
                      'work_kind': self.work_kind(self.sim), 'crane_minutes_per_second': CRANE_MINUTES_PER_SECOND,
                      'days_per_second': self.days_per_second, 'work_minutes_per_second': self.work_minutes_per_second,
                      'timezone': 'Asia/Seoul'},
            'blocks': blocks, 'equipment': equipment, 'activity': activity,
            'sources': SOURCE_LABELS,
        }


_lock = threading.Lock()
_session = None


def current():
    with _lock:
        return _session.state() if _session else None


def open_session(hull, rows, start=None, end=None, days_per_second=None, keep=True):
    """Builds the hull's scenario and makes it the shared session. Joining the same scenario keeps its clock."""
    global _session
    scenario = build(hull, rows, start, end)
    with _lock:
        if keep and _session and _session.scenario['id'] == scenario['id']:
            _session.advance()
            clock_state = (_session.sim, _session.playing, _session.speed, _session.cycle, _session.version)
            _session = Session(scenario, _session.days_per_second, _session.work_minutes_per_second)
            _session.sim, _session.playing, _session.speed, _session.cycle, _session.version = clock_state
        else:
            version = (_session.version + 1) if _session else 0
            _session = Session(scenario, days_per_second or DAYS_PER_SECOND)
            _session.version = version
        return _session.state()


def control(action, value=None):
    with _lock:
        if _session is None:
            raise ValueError('재생 중인 시나리오가 없습니다. 먼저 호선을 여세요.')
        _session.control(action, value)
        return _session.state()


def close():
    global _session
    with _lock:
        _session = None
