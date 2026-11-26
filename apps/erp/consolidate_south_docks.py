"""Close dock 6 (DEMO-B-DK) of the south coast and build line 3's hulls in docks 4 and 5 under one goliath.

Docks 4, 5 and 6 sit 65 m apart centre to centre. One goliath (FLEET-GC) already straddles docks 4 and 5; dock 6 kept
its own goliath, so two goliath rails ran 10 m apart between docks 5 and 6 and both cranes parked side by side over
the PE yards. The south coast now has two building docks and one goliath:

1. SN2057-SN2059 (booked in dock 6) move SHIFT days later into free stays of docks 4 and 5 (HOST_LINE in
   seed_hull_simulation.py): their dock booking, placement, milestones, quay berth and shifts, activities, ITP, NCR,
   punch, outfitting items and works and pier crane jobs move with them.
2. Their erection plan, goliath jobs, PE-yard stays, assembly stages and progress (work logs, gate checks, moves,
   erection inspections) are planned again through seed_hull_simulation (the normal ERP writes) for the new dock.
3. Dock 6 and its PE yard leave the yard plan (footprint, shift point, work centre link, location); its goliath and
   jib cranes are removed. Line 3 keeps its shops, warehouse, pier and transporter (now based at its outfitting shop).

Run seed_yard_cranes.py afterwards: it lays out the jib cranes again for the two remaining docks.
Running it again changes nothing once dock 6 is closed.

    apps/erp/shipyard-postgres consolidate_south_docks.py [--dry-run]
"""
import argparse
import os
from contextlib import closing
from datetime import date, timedelta

import app
import seed_hull_simulation as shs
import storage

CLOSED_DOCK, CLOSED_YARD = 'DEMO-B-DK', 'DEMO-B-YARD'
CLOSED_CRANES = ('DEMO-B-GC', 'JC-DEMO-B-DK-1', 'JC-DEMO-B-DK-2')
TRANSPORTER_BASE = ('DEMO-B-TP', 'DEMO-B-OUT')
LINE = 'DEMO-B'
SHIFT = 21   # days: the first free stay of docks 4 and 5 after each hull's dock 6 booking
TODAY = shs.TODAY

# Date columns that move with the hull (table, key column, columns). Recorded results (actual, closed) move too; one
# that would land after today is cleared.
DATED = [
    ('hull_milestones', 'hull', ('baseline', 'forecast', 'actual')),
    ('activities', 'hull', ('baseline', 'forecast', 'actual')),
    ('inspection_points', 'hull', ('planned_day',)),
    ('ncrs', 'hull', ('raised_day', 'closed_day')),
    ('punch_items', 'hull', ('raised_day', 'closed_day')),
    ('outfitting_items', 'hull', ('required_day',)),
    ('outfit_works', 'hull', ('start_day', 'end_day')),
    ('berth_assignments', 'hull', ('start_day', 'end_day')),
    ('quay_shifts', 'hull', ('plan_day', 'actual_day')),
]
RESULTS = {'actual', 'closed_day', 'actual_day'}


def later(text, days=SHIFT):
    return (date.fromisoformat(text) + timedelta(days=days)).isoformat() if text else text


def shift_documents(db, hull, dock):
    for table, key, columns in DATED:
        for row in db.execute(f'SELECT * FROM {table} WHERE {key}=?', (hull,)).fetchall():
            values = {}
            for column in columns:
                moved = later(row[column])
                if column in RESULTS and moved and moved > TODAY.isoformat():
                    moved = None
                values[column] = moved
            db.execute(f'UPDATE {table} SET ' + ', '.join(f'{c}=?' for c in values) + ' WHERE id=?',
                       (*values.values(), row['id']))
    db.execute('UPDATE quay_shifts SET from_point=? WHERE hull=? AND from_point=?', (dock, hull, CLOSED_DOCK))
    db.execute('UPDATE quay_shifts SET to_point=? WHERE hull=? AND to_point=?', (dock, hull, CLOSED_DOCK))
    # Pier crane outfitting lifts follow the berth stay (on a free slot that day, else dropped); maintenance stays put.
    dropped = 0
    jobs = db.execute("""SELECT * FROM crane_assignments WHERE hull=? AND equipment NOT IN (?, ?, ?)
        AND crane_work NOT IN ('크레인 정비', '블록 탑재') ORDER BY work_day DESC, start_hour""", (hull, *CLOSED_CRANES)).fetchall()
    for job in jobs:
        day = later(job['work_day'])
        taken = [(r['start_hour'], r['start_hour'] + r['hours']) for r in db.execute(
            'SELECT start_hour, hours FROM crane_assignments WHERE equipment=? AND work_day=? AND id<>?',
            (job['equipment'], day, job['id']))]
        slot = next((s for s in range(7, 24 - job['hours'] + 1)
                     if all(s + job['hours'] <= a or b <= s for a, b in taken)), None)
        if slot is None:
            db.execute('DELETE FROM crane_lifts WHERE assignment=?', (job['id'],))
            db.execute('DELETE FROM crane_assignments WHERE id=?', (job['id'],))
            dropped += 1
        else:
            db.execute('UPDATE crane_assignments SET work_day=?, start_hour=? WHERE id=?', (day, slot, job['id']))
    return dropped


def clear_plan(db, hull):
    """Plan and progress rows the hull simulation writes; the block division (blocks, geometry, links) stays."""
    blocks = "(SELECT id FROM blocks WHERE hull=?)"
    db.execute(f'DELETE FROM work_logs WHERE block_stage IN (SELECT id FROM block_stages WHERE block IN {blocks})', (hull,))
    for table in ('block_stages', 'block_gate_checks', 'erection_records', 'block_stock_plans', 'erection_plans'):
        db.execute(f'DELETE FROM {table} WHERE block IN {blocks}', (hull,))
    db.execute(f'DELETE FROM telemetry WHERE move IN (SELECT id FROM moves WHERE block IN {blocks})', (hull,))
    db.execute(f'DELETE FROM moves WHERE block IN {blocks}', (hull,))
    jobs = "(SELECT id FROM crane_assignments WHERE hull=? AND (crane_work='블록 탑재' OR equipment IN (?, ?, ?)))"
    db.execute(f'DELETE FROM crane_lifts WHERE assignment IN {jobs}', (hull, *CLOSED_CRANES))
    db.execute(f'DELETE FROM crane_assignments WHERE id IN {jobs}', (hull, *CLOSED_CRANES))
    db.execute('UPDATE blocks SET location=? WHERE hull=?', (shs.LINES[LINE]['fab'], hull))


def plan_erection(w, hull, cfg):
    """Erection days within the new dock stay (precedence steps, day capacity), entered as the hull's plan."""
    db = w.db
    booking = db.execute('SELECT * FROM dock_bookings WHERE hull=? ORDER BY start_day LIMIT 1', (hull,)).fetchone()
    start = date.fromisoformat(booking['start_day'])
    end = date.fromisoformat(booking['end_day']) - timedelta(days=shs.LAUNCH_MARGIN)
    blocks = [{'id': r['id']} for r in db.execute('SELECT id FROM blocks WHERE hull=? ORDER BY id', (hull,))]
    links = [(r['predecessor_block'], r['successor_block']) for r in db.execute(
        'SELECT l.* FROM erection_links l JOIN blocks b ON b.id=l.successor_block WHERE b.hull=?', (hull,))]
    days, steps = shs.erection_days(blocks, links, start, end)
    for block in sorted(blocks, key=lambda b: (days[b['id']], steps[b['id']], b['id'])):
        day = days[block['id']].isoformat()
        w.add('erection_plans', block=block['id'], destination=cfg['dock'], baseline=day, forecast=day, reason='')


def close_dock(db):
    """Dock 6, its PE yard and their cranes leave the plan; returns what could not be deleted (still referenced)."""
    tables = {r[0] for r in db.execute("SELECT table_name FROM information_schema.tables WHERE table_schema='public'")}
    for key in CLOSED_CRANES:
        db.execute('DELETE FROM crane_lifts WHERE assignment IN (SELECT id FROM crane_assignments WHERE equipment=?)', (key,))
        for table, column in (('crane_assignments', 'equipment'), ('crane_load_charts', 'equipment'),
                              ('crane_kinematics', 'equipment'), ('crane_rails', 'equipment'),
                              ('equipment_specs', 'equipment'), ('equipment', 'id')):
            if table not in tables:   # crane_load_charts: only on databases that have load charts
                continue
            db.execute(f'DELETE FROM {table} WHERE {column}=?', (key,))
    db.execute('UPDATE equipment_specs SET station=? WHERE equipment=?', (TRANSPORTER_BASE[1], TRANSPORTER_BASE[0]))
    kept = []
    for location in (CLOSED_DOCK, CLOSED_YARD):
        centres = [r['center'] for r in db.execute('SELECT center FROM center_locations WHERE location=?', (location,))]
        for table, column in (('location_geometry', 'location'), ('center_locations', 'location'),
                              ('shift_point_positions', 'id')):
            db.execute(f'DELETE FROM {table} WHERE {column}=?', (location,))
        db.execute('SAVEPOINT closing')
        try:
            db.execute('DELETE FROM locations WHERE id=?', (location,))
            for centre in centres:
                if not db.execute('SELECT 1 FROM center_locations WHERE center=?', (centre,)).fetchone():
                    db.execute('DELETE FROM centers WHERE id=?', (centre,))
            db.execute('RELEASE SAVEPOINT closing')
        except Exception as error:   # still referenced (e.g. history): the location row stays, without a footprint
            db.execute('ROLLBACK TO SAVEPOINT closing')
            kept.append(f'{location} ({str(error).splitlines()[0][:80]})')
    return kept


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='모두 검증한 뒤 되돌림')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db:
        storage.begin_write(db)
        w = shs.Writer(db, args.dry_run)
        hulls = [h for h in shs.HOST_LINE if db.execute(
            'SELECT 1 FROM dock_bookings WHERE hull=? AND dock=?', (h, CLOSED_DOCK)).fetchone()]
        open_dock = db.execute('SELECT 1 FROM location_geometry WHERE location=?', (CLOSED_DOCK,)).fetchone()
        if not hulls and not open_dock:
            print('6도크는 이미 폐쇄됨 · 변경 없음')
            return
        lines = []
        for hull in hulls:
            cfg = shs.line_config(LINE, hull)
            booking = db.execute('SELECT * FROM dock_bookings WHERE hull=? AND dock=?', (hull, CLOSED_DOCK)).fetchone()
            start, end = later(booking['start_day']), later(booking['end_day'])
            clash = db.execute('''SELECT hull, start_day, end_day FROM dock_bookings WHERE dock=? AND hull<>?
                AND start_day<=? AND end_day>=?''', (cfg['dock'], hull, end, start)).fetchone()
            if clash:
                raise SystemExit(f'{hull}: {cfg["dock"]} {start}~{end} 예약이 {clash["hull"]}({clash["start_day"]}~{clash["end_day"]})와 겹칩니다.')
            db.execute('UPDATE dock_bookings SET dock=?, start_day=?, end_day=?, reason=? WHERE id=?',
                       (cfg['dock'], start, end, '6도크 폐쇄로 이전', booking['id']))
            sibling = db.execute('SELECT * FROM hull_placements WHERE dock=? AND hull<>? LIMIT 1', (cfg['dock'], hull)).fetchone()
            db.execute('UPDATE hull_placements SET dock=?, origin_x=?, origin_y=?, origin_z=?, heading_deg=? WHERE hull=?',
                       (cfg['dock'], sibling['origin_x'], sibling['origin_y'], sibling['origin_z'], sibling['heading_deg'], hull))
            dropped = shift_documents(db, hull, cfg['dock'])
            clear_plan(db, hull)
            lines.append((hull, cfg, f'{hull}: {CLOSED_DOCK} → {cfg["dock"]} {start}~{end}' +
                          (f' · 피어 크레인 작업 {dropped}건은 빈 시간이 없어 삭제' if dropped else '')))
        kept = close_dock(db)
        yards = shs.yards(db)
        shs.REGENERATE.difference_update(shs.HOST_LINE)   # keep the block division; plan from erection_plans
        summary = []
        for hull, cfg, line in lines:
            before = len(w.refusals)
            plan_erection(w, hull, cfg)
            shs.plan_hull(w, LINE, cfg, hull, yards)
            erected = db.execute('''SELECT COUNT(DISTINCT r.block) FROM erection_records r JOIN blocks b ON b.id=r.block
                WHERE b.hull=? AND r.inspection='합격' ''', (hull,)).fetchone()[0]
            lifts = db.execute("SELECT COUNT(*) FROM crane_assignments WHERE hull=? AND crane_work='블록 탑재'", (hull,)).fetchone()[0]
            blocks = db.execute('SELECT COUNT(*) FROM blocks WHERE hull=?', (hull,)).fetchone()[0]
            summary.append(f'{line} · 블록 {blocks}개, 골리앗 탑재 작업 {lifts}건, 탑재 실적 {erected}개, ERP 거부 {len(w.refusals) - before}건')
        if args.dry_run:
            db.connection.rollback()
        else:
            db.connection.commit()
    if not args.dry_run:
        app.bump_external_cache()
    print('\n'.join(summary))
    print(f'6도크·PE 야드 폐쇄 · 크레인 {", ".join(CLOSED_CRANES)} 삭제 · {TRANSPORTER_BASE[0]} 기지 {TRANSPORTER_BASE[1]}' +
          (f' · 위치 행 유지: {", ".join(kept)}' if kept else ''))
    for label, message in w.refusals[:12]:
        print(f'  거부: {label}: {message[:100]}')
    print('(검증만 하고 되돌림)' if args.dry_run else '반영 완료')


if __name__ == '__main__':
    main()
