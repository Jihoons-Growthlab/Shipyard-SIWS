"""Example yard operations for the ERP plans: fleet, cranes, berths, quay shifts and outfitting works.

    python3 pipeline/generate_operations.py [--as-of 2026-11-17] [-o data/operations.jsonl]
    python3 ../../tools/migration/migrate.py data/operations.jsonl --apply

Ten example hulls besides SN2040 move through the two docks of the generate_yard layout in 2026-2027
(keel to launch about 10 weeks), then lie at the four berths of the 640 m pier for outfitting, go on sea trials and
are delivered. Generated rows (all marked 가상/예시):
  hulls, vessel_details, hull_milestones (착공·기공·진수·시운전·인도 plans), dock_bookings;
  equipment + equipment_specs (one goliath per dock, two jib cranes per dock, six luffing cranes on the pier,
  transporters); berths (4 on GY-PIER);
  crane_assignments for Oct 2026 - Apr 2027, SN2040's lifts taken from its erection plans: no two jobs of one crane overlap
  and every lift is within the crane's rated load;
  berth_assignments chosen without overlaps (inner berths first, a 선석 이동 to an inner berth when one frees up)
  and the matching quay_shifts (actual day recorded for moves before --as-of);
  outfit_works per hull, zone, trade and phase with man-hours and progress as of --as-of, and outfit_links.
SN2040 (from generate_blocks.py) gets milestones, a dock booking, its erection lifts and outfitting works.
Run on an ERP that has the generate_yard facilities and SN2040; insert-only rows (hulls, milestones, bookings,
equipment, links) make a second run on the same ERP fail, so load once per database.
"""
import argparse
import json
import random
from datetime import date, timedelta
from pathlib import Path

from paths import DATA

TYPES = {
    'LNG': ('174,000㎥ LNG 운반선', 299.0, 46.4, 1.0, 16, 4),       # name, length, beam, man-hour factor, quay weeks, trial weeks
    'VLGC': ('88,000㎥ VLGC (LPG 운반선)', 230.0, 36.6, 0.7, 12, 3),
    'AFRA': ('115,000 DWT 아프라막스 탱커', 250.0, 44.0, 0.55, 10, 3),
}
# hull, type, dock, keel (기공 = first block erected), launch (진수)
FLEET = [
    ('SN2031', 'LNG', 'DT-DOCK', '2026-01-05', '2026-03-20'),
    ('SN2032', 'LNG', 'GY-DOCK2', '2026-02-02', '2026-04-17'),
    ('SN2033', 'VLGC', 'DT-DOCK', '2026-03-30', '2026-06-05'),
    ('SN2034', 'LNG', 'GY-DOCK2', '2026-04-27', '2026-07-10'),
    ('SN2035', 'LNG', 'DT-DOCK', '2026-06-15', '2026-08-28'),
    ('SN2042', 'VLGC', 'GY-DOCK2', '2026-07-20', '2026-09-25'),
    ('SN2041', 'LNG', 'DT-DOCK', '2026-09-07', '2026-11-20'),
    ('SN2044', 'LNG', 'GY-DOCK2', '2026-10-05', '2026-12-18'),
    ('SN2043', 'AFRA', 'DT-DOCK', '2026-11-30', '2027-02-12'),
    ('SN2045', 'LNG', 'GY-DOCK2', '2026-12-28', '2027-03-12'),
    ('SN2040', 'LNG', 'DT-DOCK', '2027-03-01', '2027-05-14'),
]
OWNERS = ['모의 선주 A (가상)', '모의 선주 B (가상)', '모의 선주 C (가상)']
PE = {'DT-DOCK': 'GY-PE1', 'GY-DOCK2': 'GY-PE2'}
GOLIATH = {'DT-DOCK': 'GY-GC1', 'GY-DOCK2': 'GY-GC2'}
JIBS = {'DT-DOCK': ['GY-JC11', 'GY-JC12'], 'GY-DOCK2': ['GY-JC21', 'GY-JC22']}
# Berths: (id, name, side, start_m); the inner berths sit nearer the quay-side outfitting shop.
BERTHS = [('PB-SE1', '피어 남동측 1선석', '피어 남동측', 0), ('PB-NW1', '피어 북서측 1선석', '피어 북서측', 0),
          ('PB-SE2', '피어 남동측 2선석', '피어 남동측', 320), ('PB-NW2', '피어 북서측 2선석', '피어 북서측', 320)]
INNER = {'PB-SE2': 'PB-SE1', 'PB-NW2': 'PB-NW1'}
# Luffing cranes alternate sides along the pier (generate_scenery.py), so neighbouring berths share one.
BERTH_CRANES = {'PB-NW1': ['GY-LC1', 'GY-LC3'], 'PB-NW2': ['GY-LC3', 'GY-LC5'],
                'PB-SE1': ['GY-LC2', 'GY-LC4'], 'PB-SE2': ['GY-LC4', 'GY-LC6']}
ZONE_TRADES = {
    '선수부': ['배관', '선장', '도장'], '화물창': ['배관', '전장', '도장'],
    '기관실': ['배관', '기장', '전장', '계장', '보온', '통풍·공조'], '거주구': ['선장', '전장', '배관', '통풍·공조'],
    '선미부': ['배관', '선장', '기장', '도장'], '상갑판': ['배관', '선장', '전장', '도장'],
    '화물 시스템': ['배관', '계장', '보온', '전장'],
}
ZONE_WEIGHT = {'기관실': 1.6, '화물 시스템': 1.5, '거주구': 1.0, '상갑판': 0.9, '화물창': 0.8, '선수부': 0.5, '선미부': 0.6}
TRADE_WEIGHT = {'배관': 1.5, '전장': 1.2, '기장': 1.1, '선장': 0.8, '도장': 0.9, '보온': 0.6, '계장': 0.7, '통풍·공조': 0.6}
PHASE_MH = {'선행 의장': 2500, '탑재 후 의장': 3500, '안벽 의장': 6000, '시운전 준비': 1200}
FINISHING = {'보온', '도장', '계장'}            # follow piping/electrical in the same zone and phase
FOLLOWS = {'보온': '배관', '도장': '배관', '계장': '전장'}

d = date.fromisoformat


def workdays(start, end):
    day = start
    while day <= end:
        if day.weekday() < 5:
            yield day
        day += timedelta(days=1)


class Rows(list):
    def add(self, action, **data):
        self.append({'action': action, 'data': data})


def milestones(keel, launch, kind):
    _, _, _, _, quay_weeks, trial_weeks = TYPES[kind]
    trial = launch + timedelta(weeks=quay_weeks)
    back = trial + timedelta(weeks=trial_weeks)
    delivery = back + timedelta(weeks=2)
    return {'착공': keel - timedelta(weeks=16), '기공': keel, '진수': launch, '시운전': trial, '귀항': back, '인도': delivery}


def fleet(rows, existing, as_of):
    plans = {}
    for i, (hull, kind, dock, keel, launch) in enumerate(FLEET):
        name, length, beam, *_ = TYPES[kind]
        m = milestones(d(keel), d(launch), kind)
        plans[hull] = {'kind': kind, 'dock': dock, 'm': m, 'length': length}
        if hull not in existing:
            rows.add('hulls', id=hull, name=f'{hull} · {name} (예시)')
            rows.add('vessel_details', hull=hull, vessel_type=name, vessel_name=f'Example {hull[-2:]} (가상)',
                     customer=OWNERS[i % len(OWNERS)], length_m=length, beam_m=beam, delivery=m['인도'].isoformat())
        for key in ('착공', '기공', '진수', '시운전', '인도'):
            baseline = m[key]
            # A few hulls slipped by a week against the contract plan.
            slip = hull in ('SN2034', 'SN2044') and key in ('시운전', '인도')
            rows.add('hull_milestones', id='', hull=hull, milestone=key,
                     baseline=(baseline - timedelta(weeks=1) if slip else baseline).isoformat(), forecast=baseline.isoformat(),
                     reason='선행 호선 안벽 체류 연장으로 1주 순연 (예시)' if slip else '')
        rows.add('dock_bookings', id=f'DKB-{hull}', dock=dock, hull=hull,
                 start_day=(d(keel) - timedelta(days=7)).isoformat(), end_day=launch, reason='건조 도크 점유 (예시)')
    return plans


def equipment(rows):
    cranes = []
    for dock, gc in GOLIATH.items():
        n = '1' if dock == 'DT-DOCK' else '2'
        cranes.append((gc, f'{n}도크 골리앗 크레인 900t', '골리앗 크레인', 900, dock))
        for k, jc in enumerate(JIBS[dock]):
            cranes.append((jc, f'{n}도크 지브 크레인 {k + 1}호 50t', '지브 크레인', 50, dock))
    for k in range(6):
        cranes.append((f'GY-LC{k + 1}', f'의장 피어 러핑 크레인 {k + 1}호 40t', '지브 크레인', 40, 'GY-PIER'))
    for k in range(3):
        cranes.append((f'GY-TP{k + 1}', f'블록 트랜스포터 {k + 1}호 600t', '트랜스포터', 600, None))
    for key, name, kind, capacity, station in cranes:
        rows.add('equipment', id=key, name=name)
        rows.add('equipment_specs', equipment=key, equipment_type=kind, capacity_t=capacity, station=station)
    return {key: capacity for key, _, _, capacity, _ in cranes}


def berths(rows):
    for key, name, side, start in BERTHS:
        rows.add('berths', id=key, name=name, quay='GY-PIER', berth_side=side, start_m=start, berth_length_m=320, water_depth_m=13.5)


def allocate(plans, rows, as_of, rng):
    """Berth stays (outfitting, then after the sea trial until delivery) and the moves between them."""
    taken = {key: [] for key, *_ in BERTHS}
    order = [key for key, *_ in BERTHS]

    def free(berth, start, end):
        return all(end < s or e < start for s, e in taken[berth])

    stays = []
    for hull, p in plans.items():
        m = p['m']
        stays.append((m['진수'], m['시운전'] - timedelta(days=1), hull, '안벽 의장', 'launch'))
        stays.append((m['귀항'], m['인도'] - timedelta(days=1), hull, '인도 대기', 'return'))
    berth_of = {}
    count = 0
    for start, end, hull, purpose, kind in sorted(stays):
        choice = next((b for b in order if free(b, start, end)), None)
        if choice is None:
            raise SystemExit(f'{hull} {start}~{end}: 빈 선석 없음 — FLEET 일정을 조정하세요.')
        p = plans[hull]
        segments = [(choice, start, end)]
        inner = INNER.get(choice)
        if inner and purpose == '안벽 의장' and (end - start).days > 56:
            for move in (start + timedelta(weeks=k) for k in range(4, (end - start).days // 7 - 2)):
                if free(inner, move, end):
                    segments = [(choice, start, move - timedelta(days=1)), (inner, move, end)]
                    break
        for berth, s, e in segments:
            taken[berth].append((s, e))
            count += 1
            rows.add('berth_assignments', id=f'BTA-{hull}-{count:02d}', hull=hull, berth=berth, start_day=s.isoformat(),
                     end_day=e.isoformat(), berth_purpose=purpose if purpose != '안벽 의장' or s < p['m']['시운전'] - timedelta(weeks=3)
                     else '시운전 준비', note='예시')
        # Shifts into, between and out of the berths.
        if kind == 'launch':
            moves = [(p['dock'], segments[0][0], start, '진수 후 이동', 4)]
            if len(segments) > 1:
                moves.append((segments[0][0], segments[1][0], segments[1][1], '선석 이동', 2))
            moves.append((segments[-1][0], 'SEA-TRIAL', p['m']['시운전'], '시운전 출항', 3))
        else:
            moves = [('SEA-TRIAL', choice, start, '시운전 귀항', 3), (choice, 'DEPARTED', p['m']['인도'], '인도 출항', 3)]
        for origin, target, day, why, tugs in moves:
            done = day < as_of
            actual = day + timedelta(days=rng.choice([0, 0, 0, 1])) if done else None
            if actual and actual >= as_of:
                actual = day
            rows.add('quay_shifts', id=f'QSH-{hull}-{why[:2]}-{day.strftime("%m%d")}', hull=hull, from_point=origin,
                     to_point=target, plan_day=day.isoformat(), actual_day=actual.isoformat() if actual else '', tugs=tugs,
                     shift_purpose=why, note='기상 대기로 하루 순연 (예시)' if actual and actual != day else '예시')
        for berth, s, e in segments:
            berth_of.setdefault(hull, []).append((berth, s, e))
    return berth_of


def cranes(rows, plans, berth_of, capacity, erections, start, end, rng):
    """Crane jobs per working day; each crane's jobs run back to back between 07:00 and 23:00 without overlap."""
    jobs = []
    serial = {}

    def book(crane, day, hull, block, work, area, hours, lift):
        cursor = serial.setdefault((crane, day), 7)
        if cursor + hours > 23:
            return False
        lift = min(lift, capacity[crane])
        serial[(crane, day)] = cursor + hours + rng.choice([0, 0, 1])
        jobs.append(dict(equipment=crane, hull=hull, block=block, crane_work=work, work_area=area,
                         work_day=day.isoformat(), start_hour=cursor, hours=hours, lift_t=round(lift, 1)))
        return True

    in_dock = lambda dock, day: next((h for h, p in plans.items() if p['dock'] == dock and
                                      p['m']['기공'] - timedelta(days=7) <= day <= p['m']['진수']), None)
    at_berth = lambda crane, day: next((h for h, stays in berth_of.items() for berth, s, e in stays
                                        if s <= day <= e and crane in BERTH_CRANES[berth]), None)
    # SN2040's erection forecasts bunch up (several blocks a day, some on weekends): the goliath takes them in
    # forecast order and carries what does not fit into the next working day, as a crane plan would.
    waiting = sorted((d(day_text), block, weight) for day_text, block, weight in erections)
    backlog = []
    day = start - timedelta(days=1)
    while True:
        day += timedelta(days=1)
        if day > end and not backlog and not waiting:
            break
        if day.weekday() >= 5:
            continue
        while waiting and waiting[0][0] <= day:
            backlog.append(waiting.pop(0)[1:])
        if day.day <= 7 and day.weekday() == 0:   # first Monday of the month: crane inspection, 07:00-11:00
            for dock, gc in GOLIATH.items():
                for crane in [gc] + JIBS[dock]:
                    hull = in_dock(dock, day)
                    if hull:
                        book(crane, day, hull, '', '크레인 정비', dock, 4, 0)
            for k in range(1, 7):
                hull = at_berth(f'GY-LC{k}', day)
                if hull:
                    book(f'GY-LC{k}', day, hull, '', '크레인 정비', 'GY-PIER', 4, 0)
        for dock, gc in GOLIATH.items():
            hull = in_dock(dock, day)
            if hull is None:
                continue
            if hull == 'SN2040':
                # The real erection lifts from SN2040's erection plans (generate_blocks.py), 2-3 h each.
                while backlog and book(gc, day, hull, backlog[0][0], '블록 탑재', dock, 3 if backlog[0][1] > 150 else 2, backlog[0][1]):
                    backlog.pop(0)
            else:
                for _ in range(rng.choice([1, 2, 2, 3])):
                    book(gc, day, hull, '', '블록 탑재', dock, rng.choice([3, 4, 4, 5]), rng.uniform(180, 850))
            if not (hull == 'SN2040' and backlog):
                book(gc, day, hull, '', 'PE 리프트', PE[dock], rng.choice([2, 3]), rng.uniform(60, 300))
            if rng.random() < 0.15:
                book(gc, day, hull, '', '블록 턴오버', PE[dock], 3, rng.uniform(80, 250))
            for jc in JIBS[dock]:
                for _ in range(rng.choice([1, 2, 2, 3])):
                    book(jc, day, hull, '', '의장품 인양', dock, rng.choice([2, 2, 3, 4]), rng.uniform(3, 45))
        for hull, stays in berth_of.items():
            for berth, s, e in stays:
                if s <= day <= e:
                    for crane in BERTH_CRANES[berth]:
                        for _ in range(rng.choice([1, 2, 2, 3])):
                            book(crane, day, hull, '', '안벽 의장 인양', 'GY-PIER', rng.choice([2, 2, 3, 4]), rng.uniform(2, 38))
    jobs.sort(key=lambda job: (job['work_day'], job['equipment'], job['start_hour']))
    for n, job in enumerate(jobs, 1):
        rows.add('crane_assignments', id=f'CRA-{job["work_day"][2:4]}{n:05d}', **job, note='')
    return len(jobs)


def outfitting(rows, plans, as_of, rng):
    total = links = 0
    for hull, p in plans.items():
        m, factor = p['m'], TYPES[p['kind']][3]
        windows = {'선행 의장': (m['착공'] + timedelta(weeks=3), m['기공'] + timedelta(weeks=1), 'GY-OUTFIT'),
                   '탑재 후 의장': (m['기공'] + timedelta(weeks=1), m['진수'] - timedelta(days=3), p['dock']),
                   '안벽 의장': (m['진수'] + timedelta(days=3), m['시운전'] - timedelta(weeks=1), 'GY-PIER'),
                   '시운전 준비': (m['시운전'] - timedelta(weeks=3), m['시운전'] - timedelta(days=1), 'GY-PIER')}
        ids = {}
        n = 0
        for zone, trades in ZONE_TRADES.items():
            for trade in trades:
                for phase, (w0, w1, area) in windows.items():
                    if phase == '선행 의장' and zone in ('화물창', '화물 시스템') or \
                       phase == '시운전 준비' and zone not in ('기관실', '화물 시스템', '거주구'):
                        continue
                    span = (w1 - w0).days
                    lead = 0.4 if trade in FINISHING else 0.0
                    s = w0 + timedelta(days=round(span * rng.uniform(lead, lead + 0.25)))
                    e = min(w1, s + timedelta(days=max(5, round(span * rng.uniform(0.45, 0.75)))))
                    mh = round(PHASE_MH[phase] * ZONE_WEIGHT[zone] * TRADE_WEIGHT[trade] * factor * rng.uniform(0.85, 1.15), -1)
                    days = max(1, sum(1 for _ in workdays(s, e)))
                    crew = max(2, round(mh / (days * 8)))
                    if e < as_of:
                        late = rng.random() < 0.04
                        progress, status = (rng.randint(85, 95), '진행') if late else (100, '완료')
                    elif s > as_of:
                        progress, status = 0, '보류' if rng.random() < 0.03 else '계획'
                    else:
                        share = ((as_of - s).days + 1) / ((e - s).days + 1)
                        progress = max(5, min(95, round(100 * share * rng.uniform(0.75, 1.15))))
                        status = '보류' if rng.random() < 0.04 else '진행'
                    note = {'보류': '자재(기자재) 미입고로 보류 (예시)'}.get(status, '')
                    n += 1
                    key = f'OFW-{hull}-{n:03d}'
                    ids[(zone, trade, phase)] = key
                    rows.add('outfit_works', id=key, hull=hull, block='', outfit_zone=zone, outfit_trade=trade,
                             work_phase=phase, work_area=area, start_day=s.isoformat(), end_day=e.isoformat(),
                             planned_mh=int(mh), crew=crew, progress_pct=progress, work_status=status, note=note)
        total += n
        k = 0
        order = list(windows)
        for (zone, trade, phase), key in ids.items():
            prior = []
            earlier = [ph for ph in order[:order.index(phase)] if (zone, trade, ph) in ids]
            if earlier:
                prior.append(ids[(zone, trade, earlier[-1])])
            if trade in FOLLOWS and (zone, FOLLOWS[trade], phase) in ids:
                prior.append(ids[(zone, FOLLOWS[trade], phase)])
            for before in prior:
                k += 1
                rows.add('outfit_links', id=f'OFL-{hull}-{k:03d}', work=key, predecessor_work=before)
        links += k
    return total, links


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--as-of', default=date.today().isoformat(), help='진척·실적 기준일 (기본 오늘)')
    parser.add_argument('--blocks', type=Path, default=DATA / 'SN2040-blocks.jsonl', help='SN2040 탑재 계획 (generate_blocks.py)')
    parser.add_argument('--seed', type=int, default=20261117)
    parser.add_argument('-o', '--output', type=Path, default=DATA / 'operations.jsonl')
    args = parser.parse_args()
    as_of, rng = d(args.as_of), random.Random(args.seed)

    erections, existing = [], set()
    if args.blocks.is_file():
        source = [json.loads(line) for line in args.blocks.read_text(encoding='utf-8').splitlines() if line.strip()]
        weights = {r['data']['block']: r['data']['weight_t'] for r in source if r['action'] == 'block_details'}
        erections = sorted((r['data']['forecast'], r['data']['block'], weights.get(r['data']['block'], 150.0))
                           for r in source if r['action'] == 'erection_plans')
        existing = {r['data']['id'] for r in source if r['action'] == 'hulls'}

    rows = Rows()
    plans = fleet(rows, existing, as_of)
    capacity = equipment(rows)
    berths(rows)
    berth_of = allocate(plans, rows, as_of, rng)
    jobs = cranes(rows, plans, berth_of, capacity, erections, d('2026-10-01'), d('2027-04-30'), rng)
    works, links = outfitting(rows, plans, as_of, rng)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in rows), encoding='utf-8')
    counts = {}
    for row in rows:
        counts[row['action']] = counts.get(row['action'], 0) + 1
    print(f'{args.output}: {len(rows)}행 (기준일 {as_of}) · 호선 {len(plans)}척 · 크레인 작업 {jobs} · 의장 작업 {works} · 선후행 {links}')
    print('  ' + ', '.join(f'{k} {v}' for k, v in counts.items()))


if __name__ == '__main__':
    main()
