"""Give every dock-placed hull a full, simulation-ready erection plan, entered through the ERP write rules.

The digital twin plays a hull's erection from ERP data: block boxes (block_geometry with the Unity model key), the
erection plan and its precedence, where each block waits in the yard (block_stock_plans) and the dock bookings that
say when the hull stands in its dock. The south-coast lines (SN2051-SN2059) and SN2050 only had 3-8 placeholder
boxes; SN2040 and SN2046 had blocks but no dock booking, crane or progress. This script:

1. Replaces the placeholder boxes of SN2050-SN2059 by the rule-based block division of the twin pipeline
   (apps/twin/pipeline/generate_blocks.py, the same IDs as the shipped Unity block models). Documents that hang on
   a placeholder (operations, ITP, NCR, outfitting, drawings) move to the new block of the same zone; plan rows of
   the placeholder (geometry, erection plan and links, stages, gate checks, moves, records) are removed.
2. Plans every hull through the normal ERP writes (app.write, the same validation as the API): dock booking and
   milestones, erection plan days from the precedence steps within the dock booking and the goliath's day capacity,
   goliath crane jobs, PE-yard (and stockyard) stays packed without overlap, assembly stages with planned man-hours.
3. Replays progress up to today the way the yard would record it: work logs per completed stage, readiness gate
   checks, transporter and crane moves (confirmed), and erection inspection records. A block the ERP refuses to
   record (open NCR, failed ITP, predecessor not erected) is re-forecast after today with the blocker as the reason
   (the ERP has no re-plan write, so that one update goes to the plan row directly, with the same reason rule).
   Moves are stamped with the time the script runs (the ERP sets move times itself).

Running it again changes nothing for hulls that are already planned (checked by their block count).

    apps/erp/shipyard-postgres seed_hull_simulation.py [--dry-run] [--hull SN2052 ...]
"""
import argparse
import hashlib
import math
import os
import sys
from contextlib import closing
from datetime import date, timedelta
from pathlib import Path

import app
import production
import quality
import storage

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'twin' / 'pipeline'))
from generate_blocks import build, erection_steps  # noqa: E402  (twin pipeline: pure Python)

TODAY = date.today()
PE_DAYS = 2                # a block waits this many days in the PE yard before its lift
STORE_DAYS = 14            # ... and before that in the block stockyard, where the line has one
LIFTS_PER_DAY = 7          # goliath: 2 h per lift, 07-21 h
LAUNCH_MARGIN = 2          # last lift this many days before the booking ends (flooding, launch)
CLEARANCE = 3.0            # passage between stocked blocks (m)
GRID = 1.0                 # slot search step (m)

# Stage, share of a block's man-hours per tonne, days before the lift it ends, centre role.
STAGES = [('절단·가공', 6, 35, 'FAB'), ('소조립', 10, 28, 'FAB'), ('중조립', 14, 21, 'FAB'), ('대조립', 18, 14, 'OUT'),
          ('선행탑재(PE)', 6, PE_DAYS, 'PE'), ('탑재', 3, 0, 'DK')]
# Readiness gate -> confirmed this many days before the lift.
GATES = {'도면 출도': 60, '자재 준비': 40, '조립 완료': 14, '의장 완료': 7, '도장 완료': 5, '검사 승인': 3,
         '시설 확보': 20, '이동 준비': PE_DAYS}

LINES = {
    # line: hulls, dock, PE yard, stockyard (or None), fab shop, outfitting shop, goliath, transporter
    'FLEET': dict(hulls=('SN2051', 'SN2052', 'SN2053'), dock='FLEET-DK', pe='FLEET-YARD', store=None,
                  fab='FLEET-FAB', out='FLEET-OUT', gc='FLEET-GC', tp='FLEET-TP', sub='FLEET-SUB'),
    'DEMO-A': dict(hulls=('SN2054', 'SN2055', 'SN2056'), dock='DEMO-A-DK', pe='DEMO-A-YARD', store=None,
                   fab='DEMO-A-FAB', out='DEMO-A-OUT', gc='DEMO-A-GC', tp='DEMO-A-TP', sub='DEMO-A-SUB'),
    'DEMO-B': dict(hulls=('SN2057', 'SN2058', 'SN2059'), dock='DEMO-B-DK', pe='DEMO-B-YARD', store=None,
                   fab='DEMO-B-FAB', out='DEMO-B-OUT', gc='DEMO-B-GC', tp='DEMO-B-TP', sub='DEMO-B-SUB'),
    'DTW': dict(hulls=('SN2050',), dock='DTW-DOCK', pe='DTW-YARD', store='GY-STOCK',
                fab='DTW-FAB', out='DTW-OUT', gc='DTW-GL', tp='DTW-TP', sub='GY-SUB'),
    # Main-yard hulls that already have their block division and stays: plan and progress only.
    'GY2': dict(hulls=('SN2046',), dock='GY-DOCK2', pe='GY-PE2', store='GY-STOCK',
                fab='GY-ASSY1', out='GY-OUTFIT', gc='GY-GL2', tp='DTW-TP', sub='GY-SUB'),
    'DT': dict(hulls=('SN2040',), dock='DT-DOCK', pe='GY-PE1', store='GY-STOCK',
               fab='GY-ASSY2', out='GY-OUTFIT', gc='DT-GL1', tp='DTW-TP', sub='GY-SUB'),
}
# Dock 6 (DEMO-B-DK) and its PE yard were closed (consolidate_south_docks.py): line 3 keeps its shops, pier and
# transporter but builds its hulls in docks 4 and 5, under the one goliath that straddles them.
HOST_LINE = {'SN2057': 'FLEET', 'SN2058': 'DEMO-A', 'SN2059': 'FLEET'}


def line_config(line, hull):
    """The line's config, with the dock, PE yard and goliath of the line whose dock the hull is built in."""
    host = HOST_LINE.get(hull)
    if not host:
        return LINES[line]
    return {**LINES[line], **{k: LINES[host][k] for k in ('dock', 'pe')}, 'gc': 'FLEET-GC', 'host': host}


REGENERATE = {f'SN{n}' for n in range(2050, 2060)}
# Main-yard dock 2 builds SN2046 (aft, at the dock head) and SN2050 (forward, at the gate) in tandem.
PLACEMENTS = {'SN2046': 252.0, 'SN2050': 490.0}
NEW_BOOKINGS = {'SN2046': ('GY-DOCK2', '2026-10-12', '2027-01-14'), 'SN2040': ('DT-DOCK', '2027-02-22', '2027-05-14')}
NEW_MILESTONES = {
    'SN2046': [('착공', '2026-08-24'), ('기공', '2026-10-19'), ('진수', '2027-01-14'), ('시운전', '2027-06-14'),
               ('인도', '2027-08-31')],
    'SN2040': [('착공', '2027-01-04'), ('기공', '2027-03-01'), ('진수', '2027-05-14'), ('시운전', '2028-04-10'),
               ('인도', '2028-06-30')],
}
# Master data the main yard lacks: goliaths over docks 1 and 2, work centres and the hull subcontractor.
EQUIPMENT = [('GY-GL2', '2도크 골리앗 크레인', '골리앗 크레인', 900, 'GY-DOCK2'),
             ('DT-GL1', '1도크 골리앗 크레인', '골리앗 크레인', 900, 'DT-DOCK')]
CENTERS = {'DTW': {'FAB': 'DTW-FAB', 'OUT': 'DTW-OUT', 'PE': 'DTW-YARD', 'DK': 'DTW-DOCK'},
           'GY2': {'FAB': 'GY-ASSY1', 'OUT': 'GY-OUTFIT', 'PE': 'GY-PE2', 'DK': 'GY-DOCK2'},
           'DT': {'FAB': 'GY-ASSY2', 'OUT': 'GY-OUTFIT', 'PE': 'GY-PE1', 'DK': 'DT-DOCK'}}
CENTER_NAMES = {'FAB': '선체 조립반', 'OUT': '선행의장반', 'PE': 'PE 작업반', 'DK': '탑재반'}
# Placeholder documents follow the block; plan rows of the placeholder are rebuilt for the new blocks.
REPOINT = {'activities', 'outfitting_items', 'outfit_works', 'drawing_revisions', 'inspection_points', 'ncrs',
           'punch_items', 'ebom_revisions', 'mbom_revisions', 'bom_requirements', 'bom_adjustments'}
REMOVE = ['block_structure', 'erection_links', 'erection_records', 'crane_assignments', 'block_gate_checks',
          'block_stock_plans', 'erection_plans', 'block_geometry', 'block_details']


def iso(day):
    return day.isoformat()


def day_of(text):
    return date.fromisoformat(text)


def jitter(key, low, high):
    """Deterministic factor in [low, high] from a key (re-runs give the same numbers)."""
    h = int(hashlib.sha1(key.encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
    return low + (high - low) * h


class Writer:
    def __init__(self, db, dry_run):
        self.db, self.dry_run, self.counts, self.refusals = db, dry_run, {}, []

    def add(self, action, **values):
        self.counts[action] = self.counts.get(action, 0) + 1
        return app.write(self.db, action, values)

    def attempt(self, label, fn):
        """Runs fn inside a savepoint; an ERP refusal (ValueError) is rolled back and returned."""
        self.db.execute('SAVEPOINT step')
        try:
            result = fn()
        except ValueError as error:
            self.db.execute('ROLLBACK TO SAVEPOINT step')
            self.refusals.append((label, str(error)))
            return None, str(error)
        self.db.execute('RELEASE SAVEPOINT step')
        return result, None


# ---------------------------------------------------------------- master data

def ensure_master(w):
    db = w.db
    for key, name, kind, capacity, station in EQUIPMENT:
        if not db.execute('SELECT 1 FROM equipment WHERE id=?', (key,)).fetchone():
            w.add('equipment', id=key, name=name)
            w.add('equipment_specs', equipment=key, equipment_type=kind, capacity_t=capacity, station=station)
    if not db.execute('SELECT 1 FROM contractors WHERE id=?', ('GY-SUB',)).fetchone():
        w.add('contractors', id='GY-SUB', name='본 야드 선체 협력사', trade='용접')
    for line, roles in CENTERS.items():
        for role, location in roles.items():
            center = f'{line}-WC-{role}'
            if not db.execute('SELECT 1 FROM centers WHERE id=?', (center,)).fetchone():
                w.add('centers', id=center, name=f'{line} {CENTER_NAMES[role]}', hours=16)
                w.add('center_locations', center=center, location=location)


def center(db, line, role):
    key = f'{line}-WC-{role}'
    if db.execute('SELECT 1 FROM centers WHERE id=?', (key,)).fetchone():
        return key
    return f'{line}-WC-YARD' if role == 'PE' else key


# ---------------------------------------------------------------- block division

def hull_spec(db, hull):
    vessel = db.execute('SELECT * FROM vessel_details WHERE hull=?', (hull,)).fetchone()
    geometry = db.execute('SELECT * FROM hull_geometry WHERE hull=?', (hull,)).fetchone()
    return argparse.Namespace(hull=hull, length=vessel['length_m'], beam=vessel['beam_m'], depth=geometry['depth_m'],
                              draft=geometry['draft_m'], frame_spacing=geometry['frame_spacing_m'],
                              holds=2 if vessel['length_m'] < 140 else 3, segment_frames=20, deckhouse_tiers=3)


def zone_group(zone):
    return '선수부' if '선수' in zone else '선미부' if '선미' in zone else '기관실' if '기관' in zone else \
        '거주구' if '거주' in zone else '화물창'


def placeholder_targets(db, hull, old_blocks, new_blocks, days):
    """Placeholder -> new block of the same zone group. Blocks with open work (unfinished operations, open NCR or
    ITP) go to the zone's last leaf block that is still ahead, so the hold shows where the plan has slack."""
    leaves = {b['id'] for b in new_blocks} - {before for before, _ in new_blocks_links(new_blocks)}
    targets, used = {}, set()
    for old in old_blocks:
        zone = db.execute('SELECT zone FROM block_details WHERE block=?', (old,)).fetchone()
        group = zone_group(zone['zone'] or '') if zone else '화물창'
        group = group if group != '화물창' or any(zone_group(b['zone']) == '화물창' for b in new_blocks) else '기관실'
        candidates = sorted((b for b in new_blocks if zone_group(b['zone']) == group and b['id'] not in used),
                            key=lambda b: (days[b['id']], b['id']))
        if not candidates:
            candidates = sorted((b for b in new_blocks if b['id'] not in used), key=lambda b: (days[b['id']], b['id']))
        open_work = db.execute('''SELECT
            (SELECT COUNT(*) FROM activities WHERE block=? AND actual IS NULL) +
            (SELECT COUNT(*) FROM ncrs WHERE block=? AND closed_day IS NULL) AS n''', (old, old)).fetchone()['n'] + \
            quality.open_points(db, 'block', old)
        if open_work:
            ahead = [b for b in candidates if b['id'] in leaves and days[b['id']] > TODAY] or \
                    [b for b in candidates if days[b['id']] > TODAY] or [b for b in candidates if b['id'] in leaves]
            target = (ahead or candidates)[-1 if not any(days[b['id']] > TODAY for b in ahead) else 0]
        else:
            target = candidates[0]
        used.add(target['id'])
        targets[old] = target['id']
    return targets


_LINKS = {}


def new_blocks_links(blocks):
    return _LINKS[blocks[0]['id'].split('-')[0]]


def references(db):
    return [(r['t'], r['c']) for r in db.execute("""SELECT source.relname AS t, col.attname AS c FROM pg_constraint fk
        JOIN pg_class source ON source.oid=fk.conrelid
        JOIN pg_attribute col ON col.attrelid=fk.conrelid AND col.attnum=fk.conkey[1]
        WHERE fk.contype='f' AND fk.confrelid='blocks'::regclass AND cardinality(fk.conkey)=1""")]


def retire_placeholders(db, old_blocks, targets):
    """Documents follow their block; the placeholder's plan rows go (children first)."""
    refs = references(db)
    for old in old_blocks:
        new = targets[old]
        for table, column in refs:
            if table in REPOINT:
                db.execute(f'UPDATE "{table}" SET "{column}"=? WHERE "{column}"=?', (new, old))
        stages = [r['id'] for r in db.execute('SELECT id FROM block_stages WHERE block=?', (old,))]
        for stage in stages:
            db.execute('DELETE FROM work_logs WHERE block_stage=?', (stage,))
        db.execute('DELETE FROM block_stages WHERE block=?', (old,))
        for move in [r['id'] for r in db.execute('SELECT id FROM moves WHERE block=?', (old,))]:
            db.execute('DELETE FROM telemetry WHERE move=?', (move,))
            db.execute('DELETE FROM moves WHERE id=?', (move,))
        for table in REMOVE:
            for column in [c for t, c in refs if t == table]:
                db.execute(f'DELETE FROM "{table}" WHERE "{column}"=?', (old,))
        left = [(t, c) for t, c in refs if db.execute(f'SELECT 1 FROM "{t}" WHERE "{c}"=? LIMIT 1', (old,)).fetchone()]
        if left:
            raise ValueError(f'{old}: 처리하지 않은 참조 {left}')
        db.execute('DELETE FROM blocks WHERE id=?', (old,))


# ---------------------------------------------------------------- schedule

def erection_days(blocks, links, start, end):
    """Precedence steps spread over the dock stay, at most LIFTS_PER_DAY lifts a day, never before a predecessor."""
    steps = erection_steps(blocks, links)
    last = max(steps.values())
    span = max(0, (end - start).days)
    predecessors = {}
    for before, after in links:
        predecessors.setdefault(after, []).append(before)
    load, days = {}, {}
    for block in sorted(blocks, key=lambda b: (steps[b['id']], b['id'])):
        key = block['id']
        day = start + timedelta(days=(steps[key] - 1) * span // max(1, last - 1))
        day = max([day] + [days[p] for p in predecessors.get(key, ())])
        while load.get(day, 0) >= LIFTS_PER_DAY:
            day += timedelta(days=1)
        load[day] = load.get(day, 0) + 1
        days[key] = day
    if max(days.values()) > end:
        raise ValueError(f'{blocks[0]["id"]}: 도크 예약 기간 안에 탑재를 마칠 수 없습니다 ({max(days.values())} > {end}).')
    return days, steps


class Yard:
    """A yard footprint (turned by its heading) with time-stamped rectangles in its own axes."""

    def __init__(self, geometry):
        self.x, self.y = geometry['x'], geometry['y']
        self.heading = geometry['heading_deg'] or 0.0
        self.sx, self.sy = geometry['size_x_m'], geometry['size_y_m']
        self.placed = []

    def local(self, x, y):
        a = math.radians(self.heading)
        dx, dy = x - self.x, y - self.y
        return dx * math.cos(a) + dy * math.sin(a), -dx * math.sin(a) + dy * math.cos(a)

    def occupy(self, x, y, heading, length, width, day0, day1):
        u, v = self.local(x, y)
        turned = abs(((heading - self.heading) % 180) - 90) < 45
        lu, lv = (width, length) if turned else (length, width)
        self.placed.append((u - lu / 2, u + lu / 2, v - lv / 2, v + lv / 2, day0, day1))

    def place(self, length, width, day0, day1):
        for turn, (lu, lv) in ((0, (length, width)), (90, (width, length))):
            if lu > self.sx or lv > self.sy:
                continue
            nx, ny = int((self.sx - lu) // GRID) + 1, int((self.sy - lv) // GRID) + 1
            for j in range(ny):
                for i in range(nx):
                    u, v = -self.sx / 2 + lu / 2 + i * GRID, -self.sy / 2 + lv / 2 + j * GRID
                    box = (u - lu / 2, u + lu / 2, v - lv / 2, v + lv / 2)
                    if all(day1 < p[4] or p[5] < day0 or box[1] <= p[0] or p[1] <= box[0] or box[3] <= p[2] or p[3] <= box[2]
                           for p in self.placed):
                        self.placed.append((*box, day0, day1))
                        a = math.radians(self.heading)
                        return (round(self.x + u * math.cos(a) - v * math.sin(a), 2),
                                round(self.y + u * math.sin(a) + v * math.cos(a), 2), (self.heading + turn) % 360)
        return None


def footprint_key(g):
    return tuple(round(g[k], 1) for k in ('x', 'y', 'size_x_m', 'size_y_m', 'heading_deg'))


def yards(db):
    """One packer per physical yard footprint (ERP aliases share it), preloaded with every recorded stay."""
    by_key, of_location = {}, {}
    for g in db.execute("SELECT g.* FROM location_geometry g JOIN locations l ON l.id=g.location WHERE l.kind='야드'"):
        key = footprint_key(g)
        by_key.setdefault(key, Yard(g))
        of_location[g['location']] = by_key[key]
    for s in db.execute('''SELECT s.*, g.x_min, g.x_max, g.y_min, g.y_max FROM block_stock_plans s
            JOIN block_geometry g ON g.block=s.block'''):
        yard = of_location.get(s['location'])
        if yard:
            until = s['stock_until'] or '9999-12-31'
            yard.occupy(s['x'], s['y'], s['heading_deg'], s['x_max'] - s['x_min'] + CLEARANCE, s['y_max'] - s['y_min'] + CLEARANCE,
                        day_of(s['stock_from']).toordinal(), day_of(until).toordinal())
    return of_location


def free_crane_slot(db, crane, day):
    taken = [(r['start_hour'], r['start_hour'] + r['hours']) for r in db.execute(
        'SELECT start_hour, hours FROM crane_assignments WHERE equipment=? AND work_day=?', (crane, day))]
    for start in range(7, 22, 2):
        if all(start + 2 <= a or b <= start for a, b in taken):
            return start
    return None


# ---------------------------------------------------------------- one hull

def plan_hull(w, line, cfg, hull, yards_by_location):
    db = w.db
    booking = db.execute('SELECT * FROM dock_bookings WHERE hull=? ORDER BY start_day LIMIT 1', (hull,)).fetchone()
    if not booking and hull in NEW_BOOKINGS:
        dock, start, end = NEW_BOOKINGS[hull]
        w.add('dock_bookings', dock=dock, hull=hull, start_day=start, end_day=end, reason='')
        booking = db.execute('SELECT * FROM dock_bookings WHERE hull=? ORDER BY start_day LIMIT 1', (hull,)).fetchone()
    actuals = []   # recorded after the progress: the ERP gates e.g. 기공 on the first erected block
    if not db.execute('SELECT 1 FROM hull_milestones WHERE hull=?', (hull,)).fetchone():
        for milestone, day in NEW_MILESTONES.get(hull, []):
            key = w.add('hull_milestones', hull=hull, milestone=milestone, baseline=day, forecast=day, reason='')
            if day_of(day) <= TODAY:
                actuals.append((key, milestone, day))
    if hull in PLACEMENTS:
        p = db.execute('SELECT * FROM hull_placements WHERE hull=?', (hull,)).fetchone()
        w.add('hull_placements', hull=hull, dock=p['dock'], origin_x=PLACEMENTS[hull], origin_y=p['origin_y'],
              origin_z=p['origin_z'], heading_deg=p['heading_deg'])
    dock = cfg['dock']
    start, end = day_of(booking['start_day']), day_of(booking['end_day']) - timedelta(days=LAUNCH_MARGIN)

    if hull in REGENERATE:
        spec = hull_spec(db, hull)
        blocks, links = build(spec)
        _LINKS[hull] = links
        days, steps = erection_days(blocks, links, start, end)
        old = [r['id'] for r in db.execute('SELECT id FROM blocks WHERE hull=? ORDER BY id', (hull,))
               if r['id'] not in days]
        targets = placeholder_targets(db, hull, old, blocks, days)
        for block in blocks:
            key = block['id']
            code = key[len(hull) + 1:]
            w.add('blocks', id=key, hull=hull, location=cfg['fab'])
            w.add('block_details', block=key, zone=block['zone'], assembly='대조립', weight_t=block['weight_t'],
                  drawing=f'{hull}-STR-{code}-R01')
            w.add('block_geometry', block=key, **{k: block[k] for k in ('x_min', 'x_max', 'y_min', 'y_max', 'z_min',
                                                                        'z_max', 'cog_x', 'cog_y', 'cog_z')},
                  geometry_source='추정', model_asset=f'{hull}/{key}.obj')
        for before, after in links:
            w.add('erection_links', predecessor_block=before, successor_block=after)
        for block in sorted(blocks, key=lambda b: (days[b['id']], steps[b['id']], b['id'])):
            day = iso(days[block['id']])
            w.add('erection_plans', block=block['id'], destination=dock, baseline=day, forecast=day, reason='')
        retire_placeholders(db, old, targets)   # after the new blocks exist: documents move over to them
        geometry = {b['id']: b for b in blocks}
        order = [b['id'] for b in sorted(blocks, key=lambda b: (days[b['id']], steps[b['id']], b['id']))]
    else:
        rows = db.execute('''SELECT p.block, p.forecast, g.* FROM erection_plans p JOIN blocks b ON b.id=p.block
            JOIN block_geometry g ON g.block=p.block WHERE b.hull=? ORDER BY p.forecast, p.block''', (hull,)).fetchall()
        days = {r['block']: day_of(r['forecast']) for r in rows}
        geometry = {r['block']: dict(r) for r in rows}
        order = [r['block'] for r in rows]
        links = [(r['predecessor_block'], r['successor_block']) for r in db.execute(
            '''SELECT l.* FROM erection_links l JOIN blocks b ON b.id=l.successor_block WHERE b.hull=?''', (hull,))]
        for key in order:
            if not db.execute('SELECT location FROM blocks WHERE id=?', (key,)).fetchone()['location']:
                db.execute('UPDATE blocks SET location=? WHERE id=?', (cfg['fab'], key))   # 공장 착수 위치

    weights = {r['block']: r['weight_t'] for r in db.execute(
        'SELECT d.block, d.weight_t FROM block_details d JOIN blocks b ON b.id=d.block WHERE b.hull=?', (hull,))}

    # Goliath jobs: one 2 h lift per block on its erection day.
    for key in order:
        if db.execute("SELECT 1 FROM crane_assignments WHERE block=? AND crane_work='블록 탑재'", (key,)).fetchone():
            continue
        day = iso(days[key])
        slot = free_crane_slot(db, cfg['gc'], day)
        if slot is not None:
            w.add('crane_assignments', equipment=cfg['gc'], hull=hull, block=key, crane_work='블록 탑재', work_area=dock,
                  work_day=day, start_hour=slot, hours=2, lift_t=weights[key], note='탑재 계획 연동')

    # Yard stays: stockyard (if the line has one), then the PE yard at the dock head until the lift.
    stays = {}
    have_stays = {r['block'] for r in db.execute(
        'SELECT s.block FROM block_stock_plans s JOIN blocks b ON b.id=s.block WHERE b.hull=?', (hull,))}
    for key in order:
        if key in have_stays:
            continue
        g = geometry[key]
        length, width = g['x_max'] - g['x_min'] + CLEARANCE, g['y_max'] - g['y_min'] + CLEARANCE
        lift = days[key]
        pe_from = lift - timedelta(days=PE_DAYS)
        slot = yards_by_location[cfg['pe']].place(length, width, pe_from.toordinal(), lift.toordinal())
        if not slot:   # a full PE yard: brought in the day before the lift
            pe_from = lift - timedelta(days=1)
            slot = yards_by_location[cfg['pe']].place(length, width, pe_from.toordinal(), lift.toordinal())
        rows = []
        if slot:
            rows.append((cfg['pe'], slot, pe_from, lift))
        if cfg['store']:
            until = (pe_from if slot else lift) - timedelta(days=1)
            since = until - timedelta(days=STORE_DAYS)
            kept = yards_by_location[cfg['store']].place(length, width, since.toordinal(), until.toordinal())
            if kept:
                rows.insert(0, (cfg['store'], kept, since, until if slot else lift))
        for n, (location, (x, y, heading), since, until) in enumerate(rows, 1):
            w.add('block_stock_plans', id=f'STK-{key}-{n}', block=key, location=location, x=x, y=y, heading_deg=heading,
                  stock_from=iso(since), stock_until=iso(until), note='탑재 계획 연동 적치')
        stays[key] = rows
    for r in db.execute('''SELECT s.* FROM block_stock_plans s JOIN blocks b ON b.id=s.block WHERE b.hull=?
            ORDER BY s.stock_from''', (hull,)):
        if r['block'] in have_stays:
            stays.setdefault(r['block'], []).append((r['location'], None, day_of(r['stock_from']),
                                                     day_of(r['stock_until']) if r['stock_until'] else days[r['block']]))

    # Assembly stages with planned man-hours; completed ones get their work log.
    for key in order:
        if db.execute('SELECT 1 FROM block_stages WHERE block=?', (key,)).fetchone():
            continue
        lift = days[key]
        for stage, per_tonne, before, role in STAGES:
            finish = lift - timedelta(days=before)
            mh = max(8, round(weights[key] * per_tonne * jitter(key + stage, 0.85, 1.15)))
            w.add('block_stages', block=key, assembly_stage=stage,
                  center=center(db, cfg.get('host', line) if role in ('PE', 'DK') else line, role), planned_mh=mh,
                  baseline=iso(finish), forecast=iso(finish))

    progress(w, line, cfg, hull, order, days, links, stays)
    for key, milestone, day in actuals:
        w.attempt(f'{hull} {milestone}', lambda: w.add('milestone_update', id=key, forecast=day, actual=day, reason='실적 등록'))


def stage_ids(db, key):
    return {r['assembly_stage']: dict(r) for r in db.execute('SELECT * FROM block_stages WHERE block=?', (key,))}


def log_stage(w, cfg, stage, day, pct):
    mh = round(stage['planned_mh'] * (pct / 100) * jitter(stage['id'], 0.92, 1.12))
    w.add('work_logs', block_stage=stage['id'], work_day=iso(day), manhours=max(1, mh), progress_pct=pct,
          contractor=cfg['sub'] if w.db.execute('SELECT 1 FROM contractors WHERE id=?', (cfg['sub'],)).fetchone() else None,
          note='')


def move(w, cfg, key, destination, equipment):
    current = w.db.execute('SELECT location FROM blocks WHERE id=?', (key,)).fetchone()['location']
    if current == destination:
        return
    moved = w.add('moves', block=key, equipment=equipment, destination=destination)
    w.add('confirm', id=moved)


def progress(w, line, cfg, hull, order, days, links, stays):
    """Everything up to today, in the order the yard records it."""
    db = w.db
    predecessors = {}
    for before, after in links:
        predecessors.setdefault(after, []).append(before)
    held = {}
    for key in order:
        lift = days[key]
        stages = stage_ids(db, key)
        # Work logs: finished stages at 100 %, the running one with its share so far.
        previous_end = None
        for stage, _, before, _ in STAGES[:-1]:
            row = stages[stage]
            finish = day_of(row['forecast'])
            if row['actual']:
                previous_end = finish
                continue
            if finish <= TODAY:
                w.attempt(f'{key} {stage}', lambda: log_stage(w, cfg, row, finish, 100))
                previous_end = finish
            else:
                began = previous_end or finish - timedelta(days=7)
                if began < TODAY:
                    pct = max(5, min(95, round(100 * (TODAY - began).days / max(1, (finish - began).days))))
                    if not db.execute('SELECT 1 FROM work_logs WHERE block_stage=?', (row['id'],)).fetchone():
                        w.attempt(f'{key} {stage}', lambda: log_stage(w, cfg, row, TODAY, pct))
                break
        # Readiness gates confirmed ahead of the lift.
        done = {r['gate'] for r in db.execute("SELECT gate FROM block_gate_checks WHERE block=? AND verdict='확인'", (key,))}
        booking = db.execute('SELECT id FROM dock_bookings WHERE hull=? ORDER BY start_day LIMIT 1', (hull,)).fetchone()
        evidence = {'도면 출도': f'{hull}-STR-{key[len(hull) + 1:]}-R01 출도', '자재 준비': '강재·의장 자재 출고 완료',
                    '조립 완료': '대조립 치수·용접 검사 완료', '의장 완료': '선행의장품 설치 확인', '도장 완료': '블록 도장 막두께 검사 합격',
                    '검사 승인': '선급 블록 검사 승인', '시설 확보': f'{cfg["dock"]} 예약 {booking["id"] if booking else ""}',
                    '이동 준비': f'{cfg["tp"]} 배차·{cfg["gc"]} 인양 계획 확인'}
        for gate, lead in GATES.items():
            if gate not in done and lift - timedelta(days=lead) <= TODAY:
                w.add('block_gate_checks', block=key, gate=gate, verdict='확인', evidence=evidence[gate])
        # Moves along the stays: shop -> outfitting shop -> stockyard -> PE yard -> dock.
        if day_of(stages['대조립']['forecast']) - timedelta(days=7) <= TODAY:
            w.attempt(f'{key} 이동', lambda: move(w, cfg, key, cfg['out'], cfg['tp']))
        for location, _, since, _ in stays.get(key, []):
            if since <= TODAY:
                w.attempt(f'{key} 이동', lambda: move(w, cfg, key, location, cfg['tp']))
        if lift > TODAY or production.erected(db, key):
            continue
        blocked = [p for p in predecessors.get(key, ()) if p in held]
        if blocked:
            held[key] = f'선행 블록 {blocked[0]} 탑재 보류'
            continue

        def erect():
            move(w, cfg, key, cfg['dock'], cfg['gc'])
            w.add('erection_records', block=key, dock=cfg['dock'], erect_day=iso(lift), inspection='합격',
                  evidence=f'{cfg["gc"]} 탑재 · 정도 계측 합격')
            log_stage(w, cfg, stage_ids(db, key)['탑재'], lift, 100)

        _, refused = w.attempt(f'{key} 탑재', erect)
        if refused:
            held[key] = refused
    # Held blocks: the plan moves them after today with the blocker as the reason (successors follow).
    shift = TODAY + timedelta(days=2)
    for key in order:
        if key not in held:
            continue
        plan = db.execute('SELECT * FROM erection_plans WHERE block=?', (key,)).fetchone()
        after = max([shift] + [day_of(db.execute('SELECT forecast FROM erection_plans WHERE block=?', (p,)).fetchone()['forecast'])
                               for p in predecessors.get(key, ())])
        reason = held[key][:120]
        # The ERP keeps one plan row per block and has no re-plan write: same rule as its entry (a later forecast
        # needs a reason), applied to the row in place; the goliath job follows the plan.
        app.require(reason, '예정 지연 사유를 입력하세요.')
        db.execute('UPDATE erection_plans SET forecast=?, reason=? WHERE id=?', (iso(after), reason, plan['id']))
        db.execute("UPDATE crane_assignments SET work_day=?, note=? WHERE block=? AND crane_work='블록 탑재'",
                   (iso(after), '탑재 보류 재계획', key))
    return held


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='모두 검증한 뒤 되돌림')
    parser.add_argument('--hull', action='append', help='이 호선만 (여러 번 지정)')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db:
        storage.begin_write(db)
        w = Writer(db, args.dry_run)
        ensure_master(w)
        yards_by_location = yards(db)
        summary = []
        for line, cfg in LINES.items():
            for hull in cfg['hulls']:
                if args.hull and hull not in args.hull:
                    continue
                # Planned = its (generated) blocks already carry assembly stages; placeholders do not count.
                planned = db.execute('''SELECT COUNT(*) FROM block_stages s JOIN blocks b ON b.id=s.block
                    JOIN block_geometry g ON g.block=b.id WHERE b.hull=? AND (? = 0 OR g.model_asset LIKE ?)''',
                                     (hull, int(hull in REGENERATE), hull + '/%')).fetchone()[0]
                if planned:
                    summary.append(f'{hull}: 이미 계획됨')
                    continue
                before = len(w.refusals)
                plan_hull(w, line, line_config(line, hull), hull, yards_by_location)
                erected = db.execute('''SELECT COUNT(DISTINCT r.block) FROM erection_records r JOIN blocks b ON b.id=r.block
                    WHERE b.hull=? AND r.inspection='합격' ''', (hull,)).fetchone()[0]
                blocks = db.execute('SELECT COUNT(*) FROM blocks WHERE hull=?', (hull,)).fetchone()[0]
                delayed = db.execute('''SELECT COUNT(*) FROM erection_plans p JOIN blocks b ON b.id=p.block
                    WHERE b.hull=? AND p.forecast>p.baseline''', (hull,)).fetchone()[0]
                summary.append(f'{hull}: 블록 {blocks}개, 탑재 실적 {erected}개, 재계획 {delayed}개, ERP 거부 {len(w.refusals) - before}건')
        if args.dry_run:
            db.connection.rollback()
        else:
            db.connection.commit()
    if not args.dry_run:
        app.bump_external_cache()
    print('\n'.join(summary))
    print('등록:', ', '.join(f'{k} {v}' for k, v in sorted(w.counts.items())))
    reasons = {}
    for _, message in w.refusals:
        reasons[message.split(':')[0][:60]] = reasons.get(message.split(':')[0][:60], 0) + 1
    for message, n in sorted(reasons.items(), key=lambda x: -x[1])[:12]:
        print(f'  거부 {n}건: {message}')
    print('(검증만 하고 되돌림)' if args.dry_run else '반영 완료')


if __name__ == '__main__':
    main()
