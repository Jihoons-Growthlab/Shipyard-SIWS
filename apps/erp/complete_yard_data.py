"""Bring the digital-twin hulls' ERP data up to today as planned, so every block has a place and every past step a record.

The twin shows each block where the ERP says it is on a day: in its shop (assembly stages), in a yard slot
(block_stock_plans), on the goliath hook, on the hull in the dock, and the hull at its berth, at sea or delivered.
Gaps this fills, all through the ERP write rules (app.write), for the hulls the hull simulation plans (seed_hull_simulation.LINES):

1. PE yards of docks 4 and 5: with dock 6 closed (consolidate_south_docks.py) each grows to PE_SIZE inside the goliath
   rails, and blocks that found no slot in the old 65 x 45 m yard get their PE stay before the lift.
2. Open NCRs of blocks not yet erected: rework re-inspected 합격 and closed the day before the planned lift (today at the
   latest); a block held by them is erected on its planned day (moved from the PE yard by the goliath, inspected).
3. Past milestones (기공, 진수, 시운전, 인도) and quay shifts get their actuals on the planned day, in date order (the ERP
   gates each on the step before: 진수 after the last erection, 인도 after the sea-trial return, ...).

Running it again changes nothing that is already recorded.

    apps/erp/shipyard-postgres complete_yard_data.py [--dry-run]
"""
import argparse
import os
from contextlib import closing
from datetime import date, timedelta

import app
import arrange_yard_lines as lines
import seed_hull_simulation as shs
import storage

TODAY = date.today()
PE_SIZE = {'FLEET-YARD': (80.0, 55.0), 'DEMO-A-YARD': (80.0, 55.0)}
INSPECTOR = '모의 선급 검사관'


def iso(day):
    return day.isoformat()


def hulls():
    return [(line, hull) for line, cfg in shs.LINES.items() for hull in cfg['hulls']]


def grow_pe_yards(db):
    """Resize the south PE yards to their arrange_yard_lines targets (centre and size)."""
    changed = []
    for prefix in ('FLEET', 'DEMO-A'):
        key = f'{prefix}-YARD'
        x, y, heading = lines.targets(prefix)['YARD']
        size_x, size_y = PE_SIZE[key]
        row = db.execute('SELECT * FROM location_geometry WHERE location=?', (key,)).fetchone()
        if row and (abs(row['x'] - x) > .01 or abs(row['y'] - y) > .01 or row['size_x_m'] != size_x or row['size_y_m'] != size_y):
            db.execute('UPDATE location_geometry SET x=?, y=?, heading_deg=?, size_x_m=?, size_y_m=? WHERE location=?',
                       (x, y, heading, size_x, size_y, key))
            changed.append(key)
    return changed


def missing_stays(w, yards):
    """A PE stay (PE_DAYS before the lift, else from the day before) for every planned block without any stay."""
    db = w.db
    added, failed = 0, []
    for line, hull in hulls():
        cfg = shs.line_config(line, hull)
        rows = db.execute('''SELECT p.block, p.forecast, g.* FROM erection_plans p JOIN blocks b ON b.id=p.block
            JOIN block_geometry g ON g.block=p.block WHERE b.hull=? AND p.block NOT IN (SELECT block FROM block_stock_plans)
            ORDER BY p.forecast, p.block''', (hull,)).fetchall()
        for g in rows:
            lift = date.fromisoformat(g['forecast'])
            length, width = g['x_max'] - g['x_min'] + shs.CLEARANCE, g['y_max'] - g['y_min'] + shs.CLEARANCE
            for days in (shs.PE_DAYS, 1, 0):
                since = lift - timedelta(days=days)
                slot = yards[cfg['pe']].place(length, width, since.toordinal(), lift.toordinal())
                if slot:
                    x, y, heading = slot
                    w.add('block_stock_plans', id=f'STK-{g["block"]}-PE', block=g['block'], location=cfg['pe'], x=x, y=y,
                          heading_deg=heading, stock_from=iso(since), stock_until=iso(lift), note='PE 야드 확장 후 적치 보완')
                    added += 1
                    break
            else:
                failed.append(g['block'])
    return added, failed


def close_ncrs(w):
    """Open NCRs of twin-hull blocks that are not erected: re-inspected 합격 and closed before the planned lift."""
    db = w.db
    closed = []
    names = [hull for _, hull in hulls()]
    marks = ','.join('?' * len(names))
    for ncr in db.execute(f'''SELECT n.*, p.baseline FROM ncrs n JOIN erection_plans p ON p.block=n.block
            WHERE n.closed_day IS NULL AND n.hull IN ({marks})
            AND n.block NOT IN (SELECT block FROM erection_records) ORDER BY n.id''', names).fetchall():
        day = iso(min(TODAY, max(date.fromisoformat(ncr['raised_day']) + timedelta(days=1),
                                 date.fromisoformat(ncr['baseline']) - timedelta(days=1))))
        if ncr['point']:
            w.add('inspection_records', point=ncr['point'], inspect_day=day, inspection='합격', inspector=INSPECTOR,
                  remarks='재작업 후 재검사 합격')
        w.add('ncr_close', id=ncr['id'], closed_day=day, evidence='용접 보완 후 재검사 합격')
        closed.append(ncr['id'])
    return closed


def erect_held(w):
    """Blocks whose planned lift (baseline) is past but that are not erected (held by an NCR, an unfinished stage or a
    gate): the plan goes back to the baseline and the hull simulation's progress replays everything up to today for
    them as it would have gone (stage work logs, gate checks, moves, erection and its inspection)."""
    db = w.db
    erected = []
    for line, hull in hulls():
        cfg = shs.line_config(line, hull)
        held = db.execute('''SELECT p.* FROM erection_plans p JOIN blocks b ON b.id=p.block WHERE b.hull=?
            AND p.baseline<=? AND p.block NOT IN (SELECT block FROM erection_records) ORDER BY p.baseline, p.block''',
                          (hull, iso(TODAY))).fetchall()
        if not held:
            continue
        for plan in held:
            db.execute("UPDATE erection_plans SET forecast=baseline, reason='' WHERE id=?", (plan['id'],))
            job = db.execute("SELECT * FROM crane_assignments WHERE block=? AND crane_work='블록 탑재'", (plan['block'],)).fetchone()
            if job and job['work_day'] != plan['baseline']:
                slot = shs.free_crane_slot(db, job['equipment'], plan['baseline'])
                if slot is not None:
                    db.execute("UPDATE crane_assignments SET work_day=?, start_hour=?, note='탑재 계획 연동' WHERE id=?",
                               (plan['baseline'], slot, job['id']))
            # Stages planned to end after the (restored) lift end with it.
            for stage in db.execute('SELECT * FROM block_stages WHERE block=? AND actual IS NULL', (plan['block'],)).fetchall():
                before = next((d for name, _, d, _ in shs.STAGES if name == stage['assembly_stage']), 0)
                due = iso(date.fromisoformat(plan['baseline']) - timedelta(days=before))
                if stage['forecast'] > due:
                    db.execute('UPDATE block_stages SET forecast=? WHERE id=?', (due, stage['id']))
        rows = db.execute('''SELECT p.block, p.forecast FROM erection_plans p JOIN blocks b ON b.id=p.block WHERE b.hull=?
            ORDER BY p.forecast, p.block''', (hull,)).fetchall()
        days = {r['block']: date.fromisoformat(r['forecast']) for r in rows}
        order = [r['block'] for r in rows]
        links = [(r['predecessor_block'], r['successor_block']) for r in db.execute(
            'SELECT l.* FROM erection_links l JOIN blocks b ON b.id=l.successor_block WHERE b.hull=?', (hull,))]
        stays = {}
        for r in db.execute('''SELECT s.* FROM block_stock_plans s JOIN blocks b ON b.id=s.block WHERE b.hull=?
                ORDER BY s.stock_from''', (hull,)):
            stays.setdefault(r['block'], []).append((r['location'], None, date.fromisoformat(r['stock_from']), None))
        still = shs.progress(w, line, cfg, hull, order, days, links, stays)
        for plan in held:
            key = plan['block']
            erected.append(key if key not in still else f'{key} 보류: {still[key][:60]}')
    return erected


def record_past(w):
    """Milestones and quay shifts planned up to today get their actual on the planned day, in date order."""
    db = w.db
    done = []
    for _, hull in hulls():
        events = [(r['forecast'], 0, 'milestone', r) for r in db.execute(
            "SELECT * FROM hull_milestones WHERE hull=? AND actual IS NULL AND forecast<=?", (hull, iso(TODAY)))]
        events += [(r['plan_day'], 1, 'shift', r) for r in db.execute(
            'SELECT * FROM quay_shifts WHERE hull=? AND actual_day IS NULL AND plan_day<=?', (hull, iso(TODAY)))]
        for day, _, kind, row in sorted(events, key=lambda e: (e[0], e[1], e[3]['id'])):
            if kind == 'milestone':
                _, refused = w.attempt(f'{hull} {row["milestone"]}', lambda: w.add(
                    'milestone_update', id=row['id'], forecast=row['forecast'], actual=day, reason='실적 등록'))
                label = row['milestone']
            else:
                _, refused = w.attempt(f'{hull} {row["shift_purpose"]}', lambda: w.add(
                    'quay_shifts', **{**dict(row), 'actual_day': day}))
                label = row['shift_purpose']
            done.append(f'{hull} {label} {day}' + (f' 거부: {refused[:70]}' if refused else ''))
    return done


def align_inspections(db):
    """Inspection records of the hulls moved from dock 6 (shifted 21 days) that were left on their old day."""
    return db.execute('''UPDATE inspection_records r SET inspect_day=TO_CHAR(TO_DATE(r.inspect_day, 'YYYY-MM-DD') + 21, 'YYYY-MM-DD')
        FROM inspection_points p WHERE p.id=r.point AND p.hull IN ('SN2057', 'SN2058', 'SN2059')
        AND TO_DATE(r.inspect_day, 'YYYY-MM-DD') < TO_DATE(p.planned_day, 'YYYY-MM-DD') - 7''').rowcount


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='모두 검증한 뒤 되돌림')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db:
        storage.begin_write(db)
        w = shs.Writer(db, args.dry_run)
        grown = grow_pe_yards(db)
        aligned = align_inspections(db)
        added, failed = missing_stays(w, shs.yards(db))
        closed = close_ncrs(w)
        erected = erect_held(w)
        recorded = record_past(w)
        if args.dry_run:
            db.connection.rollback()
        else:
            db.connection.commit()
    if not args.dry_run:
        app.bump_external_cache()
    print(f'PE 야드 확장: {", ".join(grown) or "없음"} · 검사일 정렬 {aligned}건')
    print(f'적치 보완 {added}건' + (f' · 자리 없음 {len(failed)}건: {", ".join(failed[:10])}' if failed else ''))
    print(f'NCR 종결: {", ".join(closed) or "없음"} · 탑재 실적: {", ".join(erected) or "없음"}')
    print(f'일정 실적 {len(recorded)}건')
    for line in recorded:
        print('  ' + line)
    print('(검증만 하고 되돌림)' if args.dry_run else '반영 완료')


if __name__ == '__main__':
    main()
