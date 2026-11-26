"""Register the yard's small rail-mounted cranes in the ERP and give them work, through the normal ERP writes.

Every building dock gets jib cranes on a rail along its outer long side (outside the goliath rails; between two docks
there is no room), every fitting-out pier level-luffing cranes about every 105 m on alternating sides. Cranes that
share a rail split it into travel zones so they never meet. Each crane is ERP equipment with a lifting rating, a
station (its dock or pier) and a crane_rails row (rail end points, gauge, jib direction) that the digital twin and
the portal's equipment monitor draw and move it on. Work: outfitting lifts while a hull is in the dock (dock
bookings), quay outfitting lifts while it lies at a berth of the pier (berth assignments), monthly maintenance.

Each crane also gets its boom data (crane_kinematics) and each of its lifts a pick-up point on the quay behind the
rail and a set-down point on the hull deck in its dock (crane_lifts, point_source 추정), so the twin and the equipment
monitor can slew, luff and hoist it. A crane without a load chart (crane_load_charts) gets a demo chart from its boom
data; a chart already in the ERP is kept as it is. Running it again changes nothing that is already registered, except
that estimated lift points are recomputed (points entered as 현장 계획 are kept). Planned lift weights are never
changed: a lift heavier than the chart allows at its estimated radius gets no estimated points and is counted as held.
--dry-run runs everything, schema preparation included, in one transaction and rolls it all back.

    apps/erp/shipyard-postgres seed_yard_cranes.py [--dry-run]
"""
import argparse
import hashlib
import math
import os
from contextlib import closing
from datetime import date, timedelta

import app
import geometry
import storage

TODAY = date.today()
WORK_FROM, WORK_UNTIL = TODAY - timedelta(days=21), TODAY + timedelta(days=42)
DOCK_JIB = ('지브 크레인', 50)     # outfitting and small lifts in the dock
QUAY_JIB = ('지브 크레인', 40)     # level-luffing jib cranes on the piers
DOCK_RAIL_OFFSET = 22.0           # from the dock wall outward (goliath rails are 8 m out)
PIER_RAIL_INSET = 8.0             # from the pier edge inward
QUAY_PITCH = 105.0
TAIL_SWING = 11.0                 # slewing counterweight radius from the rail centre line: kept clear of buildings
TWO_CRANES_FROM = 80.0            # a dock side gets two jib cranes only when this much rail is clear of buildings
# Boom data of each crane kind (crane_kinematics): boom m, pivot height m, boom angle min / max / parked deg,
# distance behind the rail where loads are staged on the quay m.
KINEMATICS = {'JC': (72.0, 32.0, 15.0, 82.0, 74.0, 14.0),    # dock jib cranes: reach across the dock to the hull deck
              'LC': (48.0, 26.0, 12.0, 80.0, 70.0, 12.0)}    # pier level-luffing cranes
# Load chart of each crane kind (crane_load_charts): full rating up to this hook radius (m), then rating · radius / r
# (constant load moment), every CHART_STEP m out to the largest radius, rounded down to 0.5 t.
FULL_LOAD_RADIUS = {'JC': 25.0, 'LC': 20.0}
CHART_STEP = 5.0
QUAY_LEVEL = 12.0   # quay surface in the yard / twin frame (m); lift heights are stored above it
NAMES = {'DT-DOCK': '1도크', 'GY-DOCK2': '2도크', 'FLEET-DK': '남측 1라인 도크', 'DEMO-A-DK': '남측 2라인 도크',
         'GY-PIER': '의장 피어', 'FLEET-Q': '남측 1라인 피어', 'DEMO-A-Q': '남측 2라인 피어',
         'DEMO-B-Q': '남측 3라인 피어'}


def jitter(key, low, high):
    h = int(hashlib.sha1(key.encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
    return low + (high - low) * h


def facilities(db):
    """Docks and piers with footprints, one per physical footprint (ERP aliases share it; the non-'트윈' name wins)."""
    rows = db.execute('''SELECT g.*, l.name, l.kind FROM location_geometry g JOIN locations l ON l.id=g.location
        WHERE l.kind IN ('도크', '안벽') AND g.size_x_m > 0 AND g.size_y_m > 0 ORDER BY g.location''').fetchall()
    kept = {}
    for r in sorted(rows, key=lambda r: ('트윈' in r['name'], r['location'])):
        key = (r['kind'], round(r['x'], 1), round(r['y'], 1), round(r['size_x_m'], 1), round(r['size_y_m'], 1),
               round(r['heading_deg'], 1))
        kept.setdefault(key, dict(r))
    return list(kept.values())


def to_yard(f, u, v):
    a = math.radians(f['heading_deg'])
    return f['x'] + u * math.cos(a) - v * math.sin(a), f['y'] + u * math.sin(a) + v * math.cos(a)


def zones(length, count, margin):
    """Equal travel zones along a rail of the given length (centred on 0), 6 m apart."""
    usable = length - 2 * margin
    step = usable / count
    return [(-usable / 2 + i * step + (3 if i else 0), -usable / 2 + (i + 1) * step - (3 if i < count - 1 else 0))
            for i in range(count)]


def _overlap(p, q):
    """Two convex quadrilaterals (lists of 4 (x, y)) overlap (separating axis test)."""
    for poly in (p, q):
        for i in range(4):
            (x1, y1), (x2, y2) = poly[i], poly[(i + 1) % 4]
            nx, ny = y1 - y2, x2 - x1
            a = [nx * x + ny * y for x, y in p]
            b = [nx * x + ny * y for x, y in q]
            if max(a) <= min(b) or max(b) <= min(a):
                return False
    return True


def footprint(f, margin=0.0):
    a = math.radians(f['heading_deg'])
    hx, hy = f['size_x_m'] / 2 + margin, f['size_y_m'] / 2 + margin
    return [(f['x'] + u * math.cos(a) - v * math.sin(a), f['y'] + u * math.sin(a) + v * math.cos(a))
            for u, v in ((-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy))]


def clear_zone(f, u0, u1, offset, gauge, buildings):
    """The longest part of a travel zone (dock axis u0..u1 at the given offset) where the crane's portal and its
    slewing counterweight (TAIL_SWING around the rail) keep 2 m away from every building; None when less than 30 m is left."""
    half = max(gauge / 2 + 4, TAIL_SWING)   # the portal, and the counterweight when the crane slews
    blocked = []
    u = u0
    while u <= u1 + 1e-6:
        a = math.radians(f['heading_deg'])
        cx, cy = to_yard(f, u, offset)
        portal = [(cx + du * math.cos(a) - dv * math.sin(a), cy + du * math.sin(a) + dv * math.cos(a))
                  for du, dv in ((-half, -half), (half, -half), (half, half), (-half, half))]
        blocked.append((u, any(_overlap(portal, footprint(b, 2.0)) for b in buildings)))
        u += 1.0
    best, start = None, None
    for i, (u, hit) in enumerate(blocked + [(u1 + 1, True)]):
        if not hit and start is None:
            start = u
        if hit and start is not None:
            end = blocked[i - 1][0]
            if best is None or end - start > best[1] - best[0]:
                best = (start, end)
            start = None
    return best if best and best[1] - best[0] >= 30 else None


def layout(db):
    """(equipment id, name, (type, t), station, rail ends in yard m, gauge, jib heading) for every crane."""
    sites = facilities(db)
    docks = [f for f in sites if f['kind'] == '도크']
    buildings = [dict(r) for r in db.execute('''SELECT g.*, l.kind FROM location_geometry g JOIN locations l ON l.id=g.location
        WHERE l.kind IN ('작업장', '창고') AND g.size_x_m > 0 AND g.size_y_m > 0''')]
    cranes = []
    for f in docks:
        half_w = f['size_y_m'] / 2
        n = 0
        for side in (-1, 1):
            offset = side * (half_w + DOCK_RAIL_OFFSET)
            # Skip a side that faces a neighbouring dock (its rails, road and goliath leave no room).
            if any(o is not f and abs(o['heading_deg'] - f['heading_deg']) < 1 and
                   0 < side * ((o['x'] - f['x']) * -math.sin(math.radians(f['heading_deg'])) +
                               (o['y'] - f['y']) * math.cos(math.radians(f['heading_deg']))) < abs(offset) + o['size_y_m']
                   for o in docks):
                continue
            jib = (f['heading_deg'] + (90 if side < 0 else 270)) % 360   # over the dock
            # The rail runs along the part of the side clear of shops and warehouses (e.g. a line's shops at the
            # dock head); its two cranes split that part. IDs stay JC-<dock>-1, -2 whatever the length.
            usable = f['size_x_m'] / 2 - 12
            clear = clear_zone(f, -usable, usable, offset, 10.0, buildings)
            if clear is None:
                continue
            middle = (clear[0] + clear[1]) / 2
            parts = ((clear[0], middle - 3), (middle + 3, clear[1])) if clear[1] - clear[0] >= TWO_CRANES_FROM else (clear,)
            for u0, u1 in parts:
                n += 1
                cranes.append((f'JC-{f["location"]}-{n}', f'{NAMES.get(f["location"], f["name"])} 지브 크레인 {n}호', DOCK_JIB,
                               f['location'], (*to_yard(f, u0, offset), *to_yard(f, u1, offset)), 10.0, jib))
    for f in (s for s in sites if s['kind'] == '안벽'):
        count = max(2, round(f['size_x_m'] / QUAY_PITCH))
        per_side = {1: (count + 1) // 2, -1: count // 2}
        n = 0
        for side in (1, -1):
            offset = side * (f['size_y_m'] / 2 - PIER_RAIL_INSET)
            jib = (f['heading_deg'] + (90 if side > 0 else 270)) % 360   # out over the berth on that side
            for u0, u1 in zones(f['size_x_m'], per_side[side], 10):
                n += 1
                cranes.append((f'LC-{f["location"]}-{n}', f'{NAMES.get(f["location"], f["name"])} 러핑 크레인 {n}호', QUAY_JIB,
                               f['location'], (*to_yard(f, u0, offset), *to_yard(f, u1, offset)), 10.0, jib))
    return cranes


# Goliaths of the south production lines: the docks sit 65 m apart centre to centre, so one goliath per dock put
# neighbouring rails 4 m apart. The line-1 goliath straddles docks 4 and 5 (line 1 and line 2) and is the coast's only
# goliath: dock 6 and its goliath were closed (consolidate_south_docks.py). The line-2 goliath leaves crane service
# (its jobs move to the wide one; its equipment row stays for its moves history).
GOLIATHS = {'FLEET-GC': ('FLEET-DK', 'DEMO-A-DK')}
RETIRED_GOLIATHS = {'DEMO-A-GC': 'FLEET-GC'}
GOLIATH_RAIL_OFFSET = 5.0         # rails this far outside the outer dock walls
GOLIATH_HEAD_RAIL, GOLIATH_GATE_STOP = 100.0, 10.0
MERGED_FIRST_HOUR = 5             # the wide goliath works 05-23 h to erect both lines' blocks


def goliath_rail(db, docks):
    """Rail centre line ends, gauge and heading of a goliath straddling the given docks (same heading, side by side)."""
    rows = [dict(db.execute('SELECT * FROM location_geometry WHERE location=?', (d,)).fetchone()) for d in docks]
    a = math.radians(rows[0]['heading_deg'])
    ux, uy = math.cos(a), math.sin(a)
    cx = sum(r['x'] for r in rows) / len(rows)
    cy = sum(r['y'] for r in rows) / len(rows)
    half = max(abs(-(r['x'] - cx) * uy + (r['y'] - cy) * ux) + r['size_y_m'] / 2 for r in rows) + GOLIATH_RAIL_OFFSET
    pit = max(r['size_x_m'] for r in rows) / 2 - 6.0
    start, end = -pit - GOLIATH_HEAD_RAIL, pit - GOLIATH_GATE_STOP
    return (cx + ux * start, cy + uy * start, cx + ux * end, cy + uy * end), 2 * half, rows[0]['heading_deg']


def arrange_goliaths(db, add):
    """Registers the south goliaths' rails and retires the goliath the wide one replaces."""
    for key, docks in GOLIATHS.items():
        if not db.execute('SELECT 1 FROM equipment_specs WHERE equipment=?', (key,)).fetchone():
            continue
        (x0, y0, x1, y1), gauge, heading = goliath_rail(db, docks)
        add('crane_rails', equipment=key, rail_x0=round(x0, 2), rail_y0=round(y0, 2), rail_x1=round(x1, 2), rail_y1=round(y1, 2),
            gauge_m=round(gauge, 2), jib_heading_deg=round(heading % 360, 1))
    moved = shifted = 0
    retired = []
    changes = {'forecast': 0, 'stays': 0, 'bookings': []}
    for key, target in RETIRED_GOLIATHS.items():
        if not db.execute('SELECT 1 FROM equipment_specs WHERE equipment=?', (key,)).fetchone():
            continue
        # The wide goliath now erects both lines' blocks: each job of the retired one goes, in its original order, to
        # the first free slot of the wide one from its own day on (05-23 h), never before the job before it.
        cursor = (date.min, 0)
        last = {}
        jobs = db.execute('SELECT * FROM crane_assignments WHERE equipment=? ORDER BY work_day, start_hour, id', (key,)).fetchall()
        for job in jobs:
            day = max(date.fromisoformat(job['work_day']), cursor[0])
            while True:
                earliest = cursor[1] if day == cursor[0] else MERGED_FIRST_HOUR
                slot = free_slot(db, target, day.isoformat(), job['hours'], max(MERGED_FIRST_HOUR, earliest))
                if slot is not None:
                    break
                day += timedelta(days=1)
            cursor = (day, slot + job['hours'])
            add('crane_assignments', **{**dict(job), 'equipment': target, 'work_day': day.isoformat(), 'start_hour': slot,
                                        'note': f'{key} 운영 중지로 {target} 재배정'})
            moved += 1
            if day.isoformat() != job['work_day']:
                shifted += 1
            if job['block']:
                # The erection forecast follows the crane; a stay that ended before the new lift lasts until it.
                changes['forecast'] += db.execute(
                    'UPDATE erection_plans SET forecast=?, reason=? WHERE block=? AND forecast<?',
                    (day.isoformat(), f'골리앗 통합({target})으로 탑재 크레인 시간 조정', job['block'], day.isoformat())).rowcount
                stay = db.execute('SELECT id, stock_until FROM block_stock_plans WHERE block=? ORDER BY stock_from DESC LIMIT 1',
                                  (job['block'],)).fetchone()
                if stay and stay['stock_until'] and stay['stock_until'] < day.isoformat():
                    db.execute('UPDATE block_stock_plans SET stock_until=? WHERE id=?', (day.isoformat(), stay['id']))
                    changes['stays'] += 1
                hull = job['hull']
                last[hull] = max(last.get(hull, day), day)
        for hull, day in last.items():
            booking = db.execute('SELECT id, end_day FROM dock_bookings WHERE hull=? ORDER BY end_day DESC LIMIT 1', (hull,)).fetchone()
            if booking and booking['end_day'] < day.isoformat():
                db.execute('UPDATE dock_bookings SET end_day=? WHERE id=?', (day.isoformat(), booking['id']))
                changes['bookings'].append(f"{hull} {booking['end_day']}→{day}")
        for table in ('crane_load_charts', 'crane_kinematics', 'crane_rails', 'equipment_specs'):
            db.execute(f'DELETE FROM {table} WHERE equipment=?', (key,))
        name = db.execute('SELECT name FROM equipment WHERE id=?', (key,)).fetchone()['name']
        if '운영 중지' not in name:
            db.execute('UPDATE equipment SET name=? WHERE id=?', (f'{name} (운영 중지 · {target} 통합)', key))
        retired.append(key)
    return retired, moved, shifted, changes


def free_slot(db, crane, day, hours, first_hour=7):
    taken = [(r['start_hour'], r['start_hour'] + r['hours']) for r in db.execute(
        'SELECT start_hour, hours FROM crane_assignments WHERE equipment=? AND work_day=?', (crane, day))]
    for start in range(first_hour, 24 - hours + 1):
        if all(start + hours <= a or b <= start for a, b in taken):
            return start
    return None


def assign_work(db, add, cranes):
    """Outfitting lifts for the hull in each dock / at each berth of a pier, plus monthly maintenance."""
    added = 0
    stays = {}
    for b in db.execute('SELECT dock AS site, hull, start_day, end_day FROM dock_bookings'):
        stays.setdefault(b['site'], []).append(('의장품 인양', b['hull'], b['start_day'], b['end_day']))
    for b in db.execute('''SELECT r.quay AS site, a.hull, a.start_day, a.end_day FROM berth_assignments a
            JOIN berths r ON r.id=a.berth'''):
        stays.setdefault(b['site'], []).append(('안벽 의장 인양', b['hull'], b['start_day'], b['end_day']))
    aliases = {}
    for r in db.execute('''SELECT a.location AS alias, b.location AS kept FROM location_geometry a JOIN location_geometry b
            ON a.x=b.x AND a.y=b.y AND a.size_x_m=b.size_x_m AND a.size_y_m=b.size_y_m AND a.location<>b.location'''):
        aliases.setdefault(r['kept'], []).append(r['alias'])
    for key, _, (_, capacity), station, _, _, _ in cranes:
        if db.execute("SELECT 1 FROM crane_assignments WHERE equipment=? LIMIT 1", (key,)).fetchone():
            continue
        jobs = [s for site in [station] + aliases.get(station, []) for s in stays.get(site, [])]
        day = WORK_FROM
        while day <= WORK_UNTIL:
            text = day.isoformat()
            # First Monday of the month: 4 h inspection (the ERP books crane jobs against a hull: the berth's own).
            if day.weekday() == 0 and day.day <= 7 and jobs:
                slot = free_slot(db, key, text, 4)
                if slot is not None:
                    add('crane_assignments', equipment=key, hull=jobs[0][1], block=None, crane_work='크레인 정비',
                        work_area=station, work_day=text, start_hour=slot, hours=4, lift_t=0, note='월간 정기 점검')
            if day.weekday() < 6:
                for work, hull, start, end in jobs:
                    if not (start <= text <= end):
                        continue
                    for k in range(2 if work == '의장품 인양' else 3):
                        hours = 2 if jitter(f'{key}{text}{k}', 0, 1) < 0.6 else 3
                        slot = free_slot(db, key, text, hours)
                        if slot is None:
                            break
                        lift = round(jitter(f'{key}{text}{k}t', 4, capacity * 0.8), 1)
                        add('crane_assignments', equipment=key, hull=hull, block=None, crane_work=work,
                            work_area=station, work_day=text, start_hour=slot, hours=hours, lift_t=lift,
                            note='의장 작업 계획 연동')
                        added += 1
            day += timedelta(days=1)
    return added


def retire(db, add, cranes):
    """Rail cranes this script registered (JC-/LC- IDs) that the layout no longer places (e.g. a dock side with room for
    one crane): their jobs move to the remaining crane of the same station on a free slot that day (else they are
    removed with their lift points), then the crane's lift points, boom data, rail, rating and equipment row go."""
    placed = {c[0] for c in cranes}
    keep = {}
    for key, _, _, station, *_ in cranes:
        keep.setdefault(station, key)
    moved = dropped = 0
    gone = []
    for row in db.execute("""SELECT r.equipment, s.station FROM crane_rails r JOIN equipment_specs s ON s.equipment=r.equipment
            WHERE (r.equipment LIKE 'JC-%' OR r.equipment LIKE 'LC-%') ORDER BY r.equipment""").fetchall():
        if row['equipment'] in placed:
            continue
        target = keep.get(row['station'])
        for job in db.execute('SELECT * FROM crane_assignments WHERE equipment=? ORDER BY work_day, start_hour, id',
                              (row['equipment'],)).fetchall():
            slot = free_slot(db, target, job['work_day'], job['hours']) if target else None
            if slot is not None:
                add('crane_assignments', **{**dict(job), 'equipment': target, 'start_hour': slot})
                moved += 1
            else:
                db.execute('DELETE FROM crane_lifts WHERE assignment=?', (job['id'],))
                db.execute('DELETE FROM crane_assignments WHERE id=?', (job['id'],))
                dropped += 1
        for table, key in (('crane_load_charts', 'equipment'), ('crane_kinematics', 'equipment'), ('crane_rails', 'equipment'), ('equipment_specs', 'equipment'),
                           ('equipment', 'id')):
            db.execute(f'DELETE FROM {table} WHERE {key}=?', (row['equipment'],))
        gone.append(row['equipment'])
    return gone, moved, dropped


def assign_kinematics(add, cranes):
    for key, *_ in cranes:
        boom, pivot, low, high, rest, staging = KINEMATICS[key.split('-')[0]]
        add('crane_kinematics', equipment=key, boom_m=boom, pivot_height_m=pivot, luff_min_deg=low, luff_max_deg=high,
            rest_luff_deg=rest, staging_offset_m=staging)
    return len(cranes)


def chart_for(kind, capacity):
    """[(radius m, rated t)] of a crane kind from its smallest to its largest hook radius."""
    boom, _, low, high, _, _ = KINEMATICS[kind]
    near, far = boom * math.cos(math.radians(high)), boom * math.cos(math.radians(low))
    radii = [round(near, 2)] + [r for r in (CHART_STEP * n for n in range(1, int(far // CHART_STEP) + 1))
                                if near + 1 < r < far - 1] + [round(far, 2)]
    return [(r, math.floor(min(capacity, capacity * FULL_LOAD_RADIUS[kind] / r) * 2) / 2) for r in radii]


def assign_load_charts(db, add, cranes):
    """A demo load chart for every rail jib crane that has none; a chart already in the ERP is kept whole.
    Returns (cranes charted, cranes whose chart was kept)."""
    filled = kept = 0
    for key, _, (_, capacity), *_ in cranes:
        if geometry.chart_points(db, key):
            kept += 1
            continue
        for radius, rated in chart_for(key.split('-')[0], capacity):
            add('crane_load_charts', equipment=key, radius_m=radius, capacity_t=rated)
        filled += 1
    return filled, kept


def lift_points(db, job, rail, kinematics, aliases):
    """Pick-up on the quay behind the rail and set-down on the hull deck in the crane's dock (else over the jib side),
    within reach; estimates from the ERP geometry (crane_lifts.point_source = 추정)."""
    boom, _, low, _, _, staging = kinematics
    reach = boom * math.cos(math.radians(low)) * 0.95
    ax, ay, bx, by = rail['rail_x0'], rail['rail_y0'], rail['rail_x1'], rail['rail_y1']
    length = math.hypot(bx - ax, by - ay)
    ux, uy = (bx - ax) / length, (by - ay) / length
    jx, jy = math.cos(math.radians(rail['jib_heading_deg'])), math.sin(math.radians(rail['jib_heading_deg']))
    t = jitter(job['id'] + 't', 0.15, 0.85)
    drop, hull = None, None
    placement = db.execute('SELECT * FROM hull_placements WHERE hull=?', (job['hull'],)).fetchone()
    if placement and placement['dock'] in [job['work_area']] + aliases.get(job['work_area'], []):
        vessel = db.execute('SELECT length_m, beam_m FROM vessel_details WHERE hull=?', (job['hull'],)).fetchone()
        geometry = db.execute('SELECT depth_m FROM hull_geometry WHERE hull=?', (job['hull'],)).fetchone()
        if vessel:
            a = math.radians(placement['heading_deg'])
            hx, hy = math.cos(a), math.sin(a)
            # Ship x under the crane's travel zone, on the deck towards the crane's side.
            s0, s1 = sorted(((x - placement['origin_x']) * hx + (y - placement['origin_y']) * hy for x, y in ((ax, ay), (bx, by))))
            lo, hi = max(s0, vessel['length_m'] * 0.08), min(s1, vessel['length_m'] * 0.92)
            # Ship x under the crane's travel zone; when the zone does not overlap the hull, its nearest end.
            along = lo + (hi - lo) * jitter(job['id'] + 's', 0, 1) if lo < hi else \
                (vessel['length_m'] * 0.08 if s1 < vessel['length_m'] * 0.08 else vessel['length_m'] * 0.92)
            cx, cy = placement['origin_x'] + hx * along, placement['origin_y'] + hy * along
            side = -1 if (ax - cx) * -hy + (ay - cy) * hx < 0 else 1
            offset = side * vessel['beam_m'] * jitter(job['id'] + 'o', 0.1, 0.35)
            drop = [cx - hy * offset, cy + hx * offset, 0.0]
            hull = (placement, hx, hy, vessel, geometry)
    if drop is None:   # no hull of this job in the crane's dock (e.g. at a pier berth): over the jib side
        px, py = ax + ux * length * t, ay + uy * length * t
        drop = [px + jx * reach * 0.6, py + jy * reach * 0.6, 6.0]
    # The crane stands at the rail point nearest the set-down; the load waits behind it on the quay.
    along = max(0.0, min(length, (drop[0] - ax) * ux + (drop[1] - ay) * uy))
    px, py = ax + ux * along, ay + uy * along
    dx, dy = drop[0] - px, drop[1] - py
    if math.hypot(dx, dy) > reach:
        scale = reach / math.hypot(dx, dy)
        drop[0], drop[1] = px + dx * scale, py + dy * scale
    if hull:
        # On the deck when the set-down is over the hull, else on the dock floor beside it (heights above the quay).
        placement, hx, hy, vessel, geometry = hull
        sx = (drop[0] - placement['origin_x']) * hx + (drop[1] - placement['origin_y']) * hy
        sy = -(drop[0] - placement['origin_x']) * hy + (drop[1] - placement['origin_y']) * hx
        on_deck = 0 <= sx <= vessel['length_m'] and abs(sy) <= vessel['beam_m'] / 2
        depth = geometry['depth_m'] if geometry else vessel['beam_m'] * 0.55
        drop[2] = round(placement['origin_z'] + (depth if on_deck else 0.0) + 1.0 - QUAY_LEVEL, 2)
    shift = jitter(job['id'] + 'p', -6, 6)
    pick = [px - jx * staging + ux * shift, py - jy * staging + uy * shift, 1.5]
    return pick, drop


def assign_lifts(db, add):
    """crane_lifts for every rail-crane lift (not maintenance, not block erection): estimates are (re)computed,
    points entered as 현장 계획 are kept. The planned lift_t is never changed: a lift heavier than the load chart at
    the radius of its estimated points is held (no points written). Returns (lifts written, lifts held)."""
    aliases = {}
    for r in db.execute('''SELECT a.location AS alias, b.location AS kept FROM location_geometry a JOIN location_geometry b
            ON a.x=b.x AND a.y=b.y AND a.size_x_m=b.size_x_m AND a.size_y_m=b.size_y_m AND a.location<>b.location'''):
        aliases.setdefault(r['kept'], []).append(r['alias'])
    jobs = db.execute('''SELECT a.* FROM crane_assignments a JOIN crane_rails r ON r.equipment=a.equipment
        WHERE a.crane_work NOT IN ('크레인 정비', '블록 탑재')
        AND NOT EXISTS (SELECT 1 FROM crane_lifts l WHERE l.assignment=a.id AND l.point_source='현장 계획')
        ORDER BY a.id''').fetchall()
    added = held = 0
    for job in jobs:
        rail = db.execute('SELECT * FROM crane_rails WHERE equipment=?', (job['equipment'],)).fetchone()
        kind = job['equipment'].split('-')[0]
        if kind not in KINEMATICS:
            continue
        pick, drop = lift_points(db, job, rail, KINEMATICS[kind], aliases)
        # The chart check and the stored row use the same rounded points (the ERP write checks the stored ones).
        pick = [round(pick[0], 2), round(pick[1], 2), pick[2]]
        drop = [round(drop[0], 2), round(drop[1], 2), drop[2]]
        rated = geometry.chart_capacity(geometry.chart_points(db, job['equipment']), geometry.lift_radius(rail, pick, drop))
        if rated is not None and job['lift_t'] and job['lift_t'] > rated:
            held += 1   # the planned weight stays; the planner moves the points or splits the lot
            continue
        add('crane_lifts', assignment=job['id'], pick_x=pick[0], pick_y=pick[1], pick_z=pick[2],
            drop_x=drop[0], drop_y=drop[1], drop_z=drop[2], point_source='추정')
        added += 1
    return added, held


def prepare(db, dry_run):
    """Tables and columns this script needs on a database created before them. PostgreSQL runs DDL inside the
    transaction: a dry run keeps it open (with the write lock init_schema took) so the final rollback undoes the schema
    changes with the preview data; a real run commits them first and starts the write transaction."""
    storage.init_schema(db, app.SCHEMA)   # crane_rails on a database created before it existed
    geometry.upgrade_crane_kinematics(db)
    if dry_run and isinstance(db, storage.Postgres):
        return
    db.connection.commit()
    storage.begin_write(db)


def finish(db, dry_run):
    if dry_run:
        db.connection.rollback()
    else:
        db.connection.commit()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='검증만 하고 되돌림')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db:
        prepare(db, args.dry_run)

        def add(action, **values):
            return app.write(db, action, {k: v for k, v in values.items()})

        cranes = layout(db)
        registered = 0
        for key, name, (kind, capacity), station, (x0, y0, x1, y1), gauge, jib in cranes:
            if not db.execute('SELECT 1 FROM equipment WHERE id=?', (key,)).fetchone():
                add('equipment', id=key, name=name)
                registered += 1
            add('equipment_specs', equipment=key, equipment_type=kind, capacity_t=capacity, station=station)
            add('crane_rails', equipment=key, rail_x0=round(x0, 2), rail_y0=round(y0, 2), rail_x1=round(x1, 2),
                rail_y1=round(y1, 2), gauge_m=gauge, jib_heading_deg=round(jib, 1))
        goliaths = arrange_goliaths(db, add)
        gone, moved, dropped = retire(db, add, cranes)
        jobs = assign_work(db, add, cranes)
        booms = assign_kinematics(add, cranes)
        charts = assign_load_charts(db, add, cranes)
        lifts, held = assign_lifts(db, add)
        finish(db, args.dry_run)
    if not args.dry_run:
        app.bump_external_cache()
    by_station = {}
    for c in cranes:
        by_station[c[3]] = by_station.get(c[3], 0) + 1
    print(f'크레인 {len(cranes)}대 (신규 {registered}) · ' + ', '.join(f'{k} {v}' for k, v in sorted(by_station.items())))
    if goliaths[0]:
        retired, moved, shifted, changes = goliaths
        print(f'골리앗 {", ".join(retired)} 운영 중지 · 탑재 작업 {moved}건 이동(날짜 변경 {shifted}건) · 탑재 전망일 {changes["forecast"]}건, '
              f'적치 종료일 {changes["stays"]}건 조정 · 도크 예약 연장 {", ".join(changes["bookings"]) or "없음"}')
    if gone:
        print(f'배치에서 빠진 크레인 {", ".join(gone)} · 작업 {moved}건은 같은 도크 크레인으로 옮김, {dropped}건은 빈 시간이 없어 삭제')
    print(f'크레인 작업 배정 {jobs}건 · 붐 제원 {booms}대 · 정격하중표 신규 {charts[0]}대(기존 표 보존 {charts[1]}대) · '
          f'인양 지점 {lifts}건 · 정격하중 초과로 등록 보류 {held}건' + (' (검증만 하고 되돌림)' if args.dry_run else ''))


if __name__ == '__main__':
    main()
