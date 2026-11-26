"""Outfitting plans and their progress for the digital-twin hulls, through the ERP write rules (app.write).

The hull simulation (seed_hull_simulation.py) plans steel: blocks, stages, stays, erection, launch and quay shifts.
What happens to a hull's outfitting was a handful of sample rows. This adds, for every hull it plans:

1. 선행 의장 (pre-outfitting) of every block at its outfitting shop during its 대조립 stage, by the block's zone
   (기관실 배관, 거주구 전장, 화물창 배관, 선수·선미·갑판 철의장), gated on its 중조립 stage, with one outfitting item
   (spool, cable tray, fitting set) linked to it.
2. 탑재 후 의장 in the dock during the second half of the dock stay (machinery, piping and cabling across blocks).
3. 안벽 의장 at the berth from the launch shift to the sea trial (zone x trade packages, staggered), 시운전 준비 the
   last two days before the trial, and 귀항 후 보완·인도 준비 from the return to the day before delivery. The work area is
   the quay of the berth the hull lies at (the ERP gates installation on that berthing).
4. Work orders on the start day, and installations with a passing inspection every few days up to today, so the
   progress (and 완료) comes from the ERP's own rule: planned quantity accepted.
5. Crane jobs: outfitting lifts of the dock jib cranes over the whole dock stay and of the two pier cranes nearest the
   berth over the whole berth stay, on days without a job for that hull yet (then seed_yard_cranes' lift points).

Rows already present (same hull, block or zone/trade and phase) are left as they are; running it again adds nothing.

    apps/erp/shipyard-postgres seed_outfitting_plan.py [--dry-run]
"""
import argparse
import math
import os
from contextlib import closing
from datetime import date, timedelta

import app
import seed_hull_simulation as shs
import seed_yard_cranes as cranes
import storage

TODAY = date.today()
INSPECTOR = '의장 검사원'
# Trade -> (unit, quantity per man-hour, system code prefix, item type, item name).
TRADES = {
    '배관': ('m', 0.6, 'PIPE', '배관 스풀', '배관 스풀'), '전장': ('m', 1.5, 'CABLE', '전장품', '케이블 트레이'),
    '기장': ('세트', 0.02, 'MACH', '기자재', '보기 장비'), '철의장': ('kg', 12.0, 'HULLOF', '철의장품', '사다리·난간'),
    '도장': ('m²', 4.0, 'PAINT', '도료', '도장'), '계장': ('개', 0.08, 'INST', '계장품', '계기·센서'),
    '통풍·공조': ('m', 0.8, 'HVAC', '공조 덕트', '통풍 덕트'), '선장': ('세트', 0.03, 'ACCOM', '선실 의장품', '선실 가구'),
    '보온': ('m²', 2.0, 'INSUL', '보온재', '보온'),
}
# 안벽 의장 packages: (zone, trade, start share, end share of the berth window, crew).
QUAY = [('기관실', '배관', 0.0, 0.7, 10), ('기관실', '전장', 0.1, 0.8, 8), ('기관실', '기장', 0.2, 0.9, 6),
        ('기관실', '계장', 0.5, 1.0, 4), ('거주구', '전장', 0.0, 0.6, 6), ('거주구', '통풍·공조', 0.2, 0.8, 5),
        ('거주구', '선장', 0.4, 1.0, 6), ('화물창', '배관', 0.0, 0.6, 8), ('상갑판', '철의장', 0.0, 0.5, 6),
        ('상갑판', '도장', 0.6, 1.0, 8), ('화물 시스템', '계장', 0.3, 0.9, 4)]
DOCK = [('기관실', '배관', 8), ('기관실', '전장', 6), ('화물창', '배관', 6), ('거주구', '전장', 4)]
TRIAL = [('기관실', '계장', 4), ('기관실', '기장', 4)]
FINAL = [('기관실', '기장', 4), ('거주구', '선장', 4)]
RECORD_EVERY = 3   # days between recorded installations


def iso(day):
    return day.isoformat()


def day_of(text):
    return date.fromisoformat(text) if text else None


def zone_of(block_zone):
    for key, zone in (('기관실', '기관실'), ('거주구', '거주구'), ('선수', '선수부'), ('선미', '선미부'), ('화물창', '화물창'),
                      ('갑판', '상갑판')):
        if key in (block_zone or ''):
            return zone
    return '화물창'


TRADE_OF_ZONE = {'기관실': '배관', '거주구': '전장', '화물창': '배관', '선수부': '철의장', '선미부': '철의장', '상갑판': '철의장'}


class Plan:
    def __init__(self, w):
        self.w, self.db = w, w.db
        self.counts = {}

    def count(self, key, n=1):
        self.counts[key] = self.counts.get(key, 0) + n

    def work(self, key, hull, block, zone, trade, phase, area, start, end, crew, note):
        """Plans one work package (unless it exists), issues its order on the start day and records progress to today."""
        db = self.db
        if not db.execute('SELECT 1 FROM outfit_works WHERE id=?', (key,)).fetchone():
            if db.execute('''SELECT 1 FROM outfit_works WHERE hull=? AND work_phase=? AND outfit_zone=? AND outfit_trade=?
                    AND COALESCE(block,'')=?''', (hull, phase, zone, trade, block or '')).fetchone():
                return None   # an earlier sample row already covers it
            days = (end - start).days + 1
            mh = max(16, round(days * crew * 8 * 0.8 * shs.jitter(key, 0.8, 1.1)))
            self.w.add('outfit_works', id=key, hull=hull, block=block, outfit_zone=zone, outfit_trade=trade, work_phase=phase,
                       work_area=area, start_day=iso(start), end_day=iso(end), planned_mh=mh, crew=crew, progress_pct=0,
                       work_status='계획', note=note)
            self.count(phase)
        work = db.execute('SELECT * FROM outfit_works WHERE id=?', (key,)).fetchone()
        self.progress(work)
        return key

    def progress(self, work):
        db = self.db
        start, end = day_of(work['start_day']), day_of(work['end_day'])
        if start > TODAY:
            return
        order = db.execute('SELECT * FROM outfit_orders WHERE work=?', (work['id'],)).fetchone()
        if not order:
            if work['work_status'] != '계획' or work['progress_pct'] != 0:
                return
            unit, per_mh, code, _, _ = TRADES[work['outfit_trade']]
            qty = max(1.0, round(work['planned_mh'] * per_mh, 0 if unit in ('세트', '개') else 1))
            _, refused = self.w.attempt(f'{work["id"]} 발행', lambda: self.w.add(
                'outfit_orders', id=f'OFWO-{work["id"]}', work=work['id'], system_code=f'{code}-{work["outfit_zone"]}',
                planned_qty=qty, outfit_unit=unit, drawing_revision=None, issued_day=work['start_day'],
                instruction=f'{work["outfit_zone"]} {work["outfit_trade"]} {qty:g}{unit} 설치 후 검사'))
            if refused:
                return
            self.count('작업지시')
            order = db.execute('SELECT * FROM outfit_orders WHERE work=?', (work['id'],)).fetchone()
        done = db.execute('SELECT COALESCE(SUM(installed_qty),0) AS q, MAX(work_day) AS last FROM outfit_installations WHERE outfit_order=?',
                          (order['id'],)).fetchone()
        installed = done['q']
        day = max(start, day_of(done['last']) + timedelta(days=1)) if done['last'] else start
        span = max(1, (end - start).days + 1)
        while day <= min(end, TODAY) and installed < order['planned_qty'] - 1e-6:
            last = day + timedelta(days=RECORD_EVERY) > end or day == end
            share = 1.0 if last else min(1.0, ((day - start).days + 1) / span)
            target = order['planned_qty'] * share
            if order['outfit_unit'] in ('세트', '개'):
                target = math.floor(target) if not last else order['planned_qty']
            qty = round(target - installed, 3)
            if qty > 0:
                def record(day=day, qty=qty, last=last):
                    key = self.w.add('outfit_installations', outfit_order=order['id'], work_day=iso(day), installed_qty=qty,
                                     manhours=max(1, round(work['planned_mh'] * qty / order['planned_qty'])),
                                     note='완료' if last else None)
                    self.w.add('outfit_inspections', outfit_installation=key, inspect_day=iso(day), inspection='합격',
                               inspector=INSPECTOR, evidence=f'{work["outfit_trade"]} 설치 상태·치수 확인')
                _, refused = self.w.attempt(f'{work["id"]} {iso(day)}', record)
                if refused:
                    break
                installed += qty
                self.count('설치 실적')
            if last:
                break
            day = min(end, day + timedelta(days=RECORD_EVERY))


def pre_outfitting(plan, line, hull):
    """One 선행 의장 package per block during its 대조립 stage at the outfitting shop, with its item."""
    db = plan.db
    centres = {r['center']: r['location'] for r in db.execute('SELECT * FROM center_locations')}
    for block in db.execute('''SELECT b.id, d.zone, d.weight_t FROM blocks b JOIN block_details d ON d.block=b.id
            WHERE b.hull=? ORDER BY b.id''', (hull,)).fetchall():
        stages = shs.stage_ids(db, block['id'])
        assembly, middle = stages.get('대조립'), stages.get('중조립')
        if not assembly or not middle:
            continue
        end = day_of(assembly['actual'] or assembly['forecast'])
        start = max(day_of(middle['actual'] or middle['forecast']) + timedelta(days=1), end - timedelta(days=5))
        zone = zone_of(block['zone'])
        trade = TRADE_OF_ZONE.get(zone, '배관')
        area = centres.get(assembly['center']) or shs.line_config(line, hull)['out']
        key = f'OFW-P-{block["id"]}'
        if db.execute("SELECT 1 FROM outfit_works WHERE block=? AND work_phase='선행 의장'", (block['id'],)).fetchone() and \
                not db.execute('SELECT 1 FROM outfit_works WHERE id=?', (key,)).fetchone():
            continue   # a sample package already covers this block
        if not db.execute('SELECT 1 FROM outfit_works WHERE id=?', (key,)).fetchone():
            plan.w.add('outfit_works', id=key, hull=hull, block=block['id'], outfit_zone=zone, outfit_trade=trade,
                       work_phase='선행 의장', work_area=area, start_day=iso(start), end_day=iso(end),
                       planned_mh=max(16, round(block['weight_t'] * 0.5)), crew=4, progress_pct=0, work_status='계획',
                       note=f'{block["zone"]} 선행 {trade}')
            plan.w.add('outfit_conditions', id=key, block_stage=middle['id'], erection_required='필수')
            _, _, _, item_type, name = TRADES[trade]
            item = f'OF-P-{block["id"]}'
            plan.w.add('outfitting_items', id=item, hull=hull, block=block['id'], item_type=item_type,
                       name=f'{zone} {name}', outfit_stage='선행의장', required_day=iso(start))
            plan.w.add('outfit_item_links', id=item, work=key)
            plan.count('선행 의장')
        plan.progress(db.execute('SELECT * FROM outfit_works WHERE id=?', (key,)).fetchone())


def shore_outfitting(plan, hull):
    """탑재 후 의장 in the dock, then 안벽 의장, 시운전 준비 and 귀항 후 보완 along the hull's quay shifts."""
    db = plan.db
    booking = db.execute('SELECT * FROM dock_bookings WHERE hull=? ORDER BY start_day LIMIT 1', (hull,)).fetchone()
    if booking:
        start, end = day_of(booking['start_day']), day_of(booking['end_day'])
        mid = start + timedelta(days=(end - start).days // 2)
        for i, (zone, trade, crew) in enumerate(DOCK):
            plan.work(f'OFW-D-{hull}-{i + 1}', hull, None, zone, trade, '탑재 후 의장', booking['dock'],
                      mid + timedelta(days=i), end - timedelta(days=1), crew, f'도크 내 {zone} {trade} (탑재 블록 간 연결)')
    shifts = db.execute('SELECT q.*, b.quay FROM quay_shifts q LEFT JOIN berths b ON b.id=q.to_point WHERE q.hull=? ORDER BY plan_day, id',
                        (hull,)).fetchall()
    out = next((s for s in shifts if s['shift_purpose'] == '시운전 출항'), None)
    back = next((s for s in shifts if s['shift_purpose'] == '시운전 귀항'), None)
    gone = next((s for s in shifts if s['shift_purpose'] == '인도 출항'), None)
    if not out:
        return
    trial = day_of(out['actual_day'] or out['plan_day'])
    berthing = [s for s in shifts if s['quay'] and day_of(s['actual_day'] or s['plan_day']) < trial]
    if not berthing:
        return
    last = berthing[-1]   # the berth the hull fits out at (after any berth change)
    quay = last['quay']
    first = day_of(last['actual_day'] or last['plan_day']) + timedelta(days=1)
    window_end = trial - timedelta(days=3)
    if window_end >= first:
        span = (window_end - first).days
        for i, (zone, trade, s0, s1, crew) in enumerate(QUAY):
            plan.work(f'OFW-Q-{hull}-{i + 1}', hull, None, zone, trade, '안벽 의장', quay,
                      first + timedelta(days=round(span * s0)), first + timedelta(days=max(round(span * s0), round(span * s1))),
                      crew, f'안벽 {zone} {trade}')
    for i, (zone, trade, crew) in enumerate(TRIAL):
        plan.work(f'OFW-T-{hull}-{i + 1}', hull, None, zone, trade, '시운전 준비', quay,
                  trial - timedelta(days=2), trial - timedelta(days=1), crew, f'시운전 전 {zone} {trade} 점검·시험')
    if back and gone:
        ret, leave = day_of(back['actual_day'] or back['plan_day']), day_of(gone['actual_day'] or gone['plan_day'])
        back_quay = back['quay'] or quay
        if leave - timedelta(days=1) >= ret:
            for i, (zone, trade, crew) in enumerate(FINAL):
                plan.work(f'OFW-F-{hull}-{i + 1}', hull, None, zone, trade, shs_return(), back_quay, ret,
                          leave - timedelta(days=1), crew, f'시운전 지적 보완 · {zone} {trade}')


def shs_return():
    import yardplan
    return yardplan.RETURN_PHASE


def crane_jobs(plan, hull):
    """Outfitting lifts over the whole dock stay (dock jib cranes) and berth stay (the two nearest pier cranes)."""
    db = plan.db
    rails = {r['equipment']: r for r in db.execute('SELECT r.*, s.station FROM crane_rails r JOIN equipment_specs s ON s.equipment=r.equipment')}
    stays = []
    for b in db.execute('SELECT * FROM dock_bookings WHERE hull=?', (hull,)):
        ids = [k for k, r in rails.items() if r['station'] == b['dock'] and k.startswith('JC-')]
        stays.append(('의장품 인양', b['dock'], ids, day_of(b['start_day']), day_of(b['end_day']), 2))
    points = {r['id']: r for r in db.execute('SELECT * FROM shift_point_positions')}
    for b in db.execute('SELECT a.*, r.quay FROM berth_assignments a JOIN berths r ON r.id=a.berth WHERE a.hull=?', (hull,)):
        at = points.get(b['berth'])
        ids = [k for k, r in rails.items() if r['station'] == b['quay'] and k.startswith('LC-')]
        if at:
            ids.sort(key=lambda k: math.hypot((rails[k]['rail_x0'] + rails[k]['rail_x1']) / 2 - at['x'],
                                              (rails[k]['rail_y0'] + rails[k]['rail_y1']) / 2 - at['y']))
        stays.append(('안벽 의장 인양', b['quay'], ids[:2], day_of(b['start_day']), day_of(b['end_day']), 3))
    for work, area, ids, start, end, per_day in stays:
        for key in ids:
            capacity = db.execute('SELECT capacity_t FROM equipment_specs WHERE equipment=?', (key,)).fetchone()['capacity_t']
            day = start
            while day <= end:
                text = iso(day)
                if day.weekday() < 6 and not db.execute('SELECT 1 FROM crane_assignments WHERE equipment=? AND hull=? AND work_day=?',
                                                        (key, hull, text)).fetchone():
                    for k in range(per_day):
                        hours = 2 if shs.jitter(f'{key}{hull}{text}{k}', 0, 1) < 0.6 else 3
                        slot = cranes.free_slot(db, key, text, hours)
                        if slot is None:
                            break
                        plan.w.add('crane_assignments', equipment=key, hull=hull, block=None, crane_work=work, work_area=area,
                                   work_day=text, start_hour=slot, hours=hours,
                                   lift_t=round(shs.jitter(f'{key}{hull}{text}{k}t', 4, capacity * 0.8), 1), note='의장 작업 계획 연동')
                        plan.count('크레인 의장 인양')
                day += timedelta(days=1)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--dry-run', action='store_true', help='모두 검증한 뒤 되돌림')
    args = parser.parse_args()
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db:
        storage.begin_write(db)
        w = shs.Writer(db, args.dry_run)
        plan = Plan(w)
        for line, cfg in shs.LINES.items():
            for hull in cfg['hulls']:
                pre_outfitting(plan, line, hull)
                shore_outfitting(plan, hull)
                crane_jobs(plan, hull)

        def add(action, **values):
            return app.write(db, action, values)

        lifts = cranes.assign_lifts(db, add)
        summary = db.execute('''SELECT work_phase, work_status, COUNT(*) AS n FROM outfit_works w
            JOIN hull_placements h ON h.hull=w.hull GROUP BY 1, 2 ORDER BY 1, 2''').fetchall()
        if args.dry_run:
            db.connection.rollback()
        else:
            db.connection.commit()
    if not args.dry_run:
        app.bump_external_cache()
    print('추가: ' + ', '.join(f'{k} {v}' for k, v in plan.counts.items()) + f' · 크레인 인양 지점 {lifts}건')
    print('의장 작업 현황: ' + ', '.join(f'{r["work_phase"]}/{r["work_status"]} {r["n"]}' for r in summary))
    reasons = {}
    for label, message in w.refusals:
        reasons[message[:70]] = reasons.get(message[:70], 0) + 1
    for message, n in sorted(reasons.items(), key=lambda x: -x[1])[:10]:
        print(f'  거부 {n}건: {message}')
    print('(검증만 하고 되돌림)' if args.dry_run else '반영 완료')


if __name__ == '__main__':
    main()
