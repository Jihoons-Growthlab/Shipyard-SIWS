"""Place the added production lines (FLEET, DEMO-A, DEMO-B) on the coastal plain south of the main yard.

The plain lies across the river mouth from the main yard; its coast runs at about 22° in yard axes and faces the
bay. The three building docks sit side by side with their gates on that coast (heading 292°, the seaward
direction), each line's PE yard behind its dock head, the fitting-out piers reach into the bay east of the docks,
each line's plate warehouse stands on the coastal strip behind its pier and the shops on the plain west of the docks.
Each pier berths its line's hull on the face toward the docks, so the water in front of that face holds the moored
hull and a lane for the tugs; the piers are spaced by that width (see PIERS), not packed side by side.
Positions are given in a coast frame (u seaward from the coast line, v along it) and stored in yard metres.

Hulls placed on a line's dock move with it (aft end inland, bow at the gate), and the line's transporter and
crane telemetry is redrawn between the new facility centres. Running it again changes nothing.

    apps/erp/shipyard-postgres arrange_yard_lines.py [--dry-run]
"""
import argparse
import math
import os
from contextlib import closing

import app
import storage

SEAWARD = 292.0            # dock and pier heading on this coast (deg from yard +x toward +y)
ORIGIN = (1055.0, 532.0)   # a point on the coast line (yard m)
LINES = {'FLEET': -45.0, 'DEMO-A': 20.0, 'DEMO-B': 85.0}   # dock centre along the coast (v, m)
SHOP_COLUMNS = {'FLEET': -110.0, 'DEMO-A': -180.0, 'DEMO-B': -250.0}
DOCK_LENGTH = 180.0
DOCK_WIDTH = PIER_WIDTH = 45.0
MAX_BEAM = 27.0            # widest hull these lines build (seed_fleet)
FENDER = 3.0               # fender and mooring gap between the pier face and the hull
TUG_LANE = 45.0            # open water beside the moored hull for tug-assisted berthing and launched hulls
BASIN = FENDER + MAX_BEAM + TUG_LANE
# Pier centre along the coast: the first basin opens from the last dock's side wall, then one pier + basin per line.
_FIRST_PIER = max(LINES.values()) + DOCK_WIDTH / 2 + BASIN + PIER_WIDTH / 2
PIERS = {prefix: _FIRST_PIER + i * (PIER_WIDTH + BASIN) for i, prefix in enumerate(LINES)}
WAREHOUSES = {prefix: (-40.0, v) for prefix, v in PIERS.items()}   # inland of the pier root
HULL_AFT = 80.0            # ship origin (aft end) inland of the dock centre, as in seed_fleet
# PE yards at the dock heads: docks 4 and 5 share the goliath (dock 6 closed), so their PE yards are 80 x 55 m (along x
# across, complete_yard_data.py) and lean towards each other to keep 3.5 m inside the goliath rails on the outer sides.
PE_LENGTH = 80.0
PE_SHIFT = {'FLEET': 3.5, 'DEMO-A': -3.5}


def coast_to_yard(u, v):
    a = math.radians(SEAWARD)
    return (round(ORIGIN[0] + u * math.cos(a) - v * math.sin(a), 2),
            round(ORIGIN[1] + u * math.sin(a) + v * math.cos(a), 2))


def targets(prefix):
    """Facility key -> (x, y, heading_deg) for one line; footprint sizes stay as registered."""
    v = LINES[prefix]
    along = (SEAWARD + 90.0) % 360.0   # shops face the coast with their long side
    places = {
        'DK': (-DOCK_LENGTH / 2, v, SEAWARD),
        'YARD': (-DOCK_LENGTH - 10 - PE_LENGTH / 2, v + PE_SHIFT.get(prefix, 0.0), SEAWARD),
        'Q': (80.0, PIERS[prefix], SEAWARD),
        'FAB': (-120.0, SHOP_COLUMNS[prefix], along),
        'OUT': (-175.0, SHOP_COLUMNS[prefix], along),
        'ST': (*WAREHOUSES[prefix], along),
    }
    return {key: (*coast_to_yard(u, w), heading) for key, (u, w, heading) in places.items()}


def arrange(db, dry_run=False):
    changed = []
    for prefix in LINES:
        rows = {r['location']: r for r in db.execute(
            'SELECT location, x, y, heading_deg FROM location_geometry WHERE location LIKE ?', (prefix + '-%',))}
        goal = {f'{prefix}-{key}': value for key, value in targets(prefix).items() if f'{prefix}-{key}' in rows}
        if not goal or all(abs(rows[k]['x'] - x) < .01 and abs(rows[k]['y'] - y) < .01 and abs(rows[k]['heading_deg'] - h) < .01
                           for k, (x, y, h) in goal.items()):
            continue
        changed.append(prefix)
        if dry_run:
            continue
        old = {k: (r['x'], r['y']) for k, r in rows.items()}
        for key, (x, y, heading) in goal.items():
            db.execute('UPDATE location_geometry SET x=?, y=?, heading_deg=? WHERE location=?', (x, y, heading, key))
        dock = goal.get(f'{prefix}-DK')
        if dock:
            a = math.radians(SEAWARD)
            ox, oy = dock[0] - HULL_AFT * math.cos(a), dock[1] - HULL_AFT * math.sin(a)
            db.execute('UPDATE hull_placements SET origin_x=?, origin_y=?, heading_deg=? WHERE dock=?',
                       (round(ox, 2), round(oy, 2), SEAWARD, f'{prefix}-DK'))
        redraw_telemetry(db, prefix, old, {k: (x, y) for k, (x, y, _) in goal.items()})
    return changed


def redraw_telemetry(db, prefix, old, new):
    """Each recorded transfer runs straight from one facility centre to another: keep its samples' order and
    spacing, and move its ends to the facilities' new centres."""
    def nearest(x, y):
        return min(old, key=lambda k: (old[k][0] - x) ** 2 + (old[k][1] - y) ** 2)

    moves = [r['id'] for r in db.execute('SELECT id FROM moves WHERE equipment IN (?, ?)', (f'{prefix}-GC', f'{prefix}-TP'))]
    for move in moves:
        samples = db.execute('SELECT id, x, y FROM telemetry WHERE move=? ORDER BY recorded_at, id', (move,)).fetchall()
        if len(samples) < 2:
            continue
        start, end = nearest(samples[0]['x'], samples[0]['y']), nearest(samples[-1]['x'], samples[-1]['y'])
        if start not in new or end not in new:
            continue
        (x0, y0), (x1, y1) = new[start], new[end]
        for i, sample in enumerate(samples):
            t = i / (len(samples) - 1)
            db.execute('UPDATE telemetry SET x=?, y=? WHERE id=?', (round(x0 + (x1 - x0) * t, 2), round(y0 + (y1 - y0) * t, 2), sample['id']))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='옮길 생산 라인만 출력')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        moved = arrange(db, args.dry_run)
    if moved and not args.dry_run:
        app.bump_external_cache()
    print(('옮길 생산 라인: ' if args.dry_run else '해안 배치 완료: ') + ', '.join(moved) if moved else '생산 라인 배치 변경 없음')
