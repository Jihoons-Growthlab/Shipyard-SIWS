"""Seed a fictional, georeferenced block-to-dock workflow."""
import os
from contextlib import closing
from datetime import date, datetime, time, timedelta, timezone

import app
import storage

HULL = 'DT-DEMO-01'
FAB, OUT, YARD, DOCK = ('DTW-' + key for key in ('FAB', 'OUT', 'YARD', 'DOCK'))
GANTRY, TRANSPORT, GOLIATH = ('DTW-' + key for key in ('GC', 'TP', 'GL'))
KST = timezone(timedelta(hours=9))

# ShipyardTwin/data/yard.jsonl: GY-ASSY2, GY-OUTFIT, GY-PE2 and GY-DOCK2.
SITE = dict(name='가상 거제 조선소', lat=34.833114, lon=128.59749, x_axis_bearing_deg=235)
FACILITIES = {
    FAB: ('조립 2공장', '작업장', 40, 190, 170, 80, 30),
    OUT: ('선행의장 공장', '작업장', 40, 80, 170, 70, 24),
    YARD: ('2 PE장', '야드', 197.5, 10, 95, 100, 0),
    DOCK: ('2도크', '도크', 430, 10, 370, 100, 0),
}
ORIGIN_X, ORIGIN_Y = 370, 10


def align_site(db):
    site = db.execute('SELECT * FROM yard_sites').fetchone()
    if not site or site['id'] == 'DTW-SITE':
        app.write(db, 'yard_sites', dict(id=site['id'] if site else 'GY-SITE', **SITE))
    else:
        app.require(all(site[key] == SITE[key] for key in ('lat', 'lon', 'x_axis_bearing_deg')),
                    '기존 야드 기준점이 ShipyardTwin과 다릅니다.')


def align_layout(db):
    for key, (_, _, x, y, width, breadth, height) in FACILITIES.items():
        app.write(db, 'location_geometry', dict(location=key, x=x, y=y, z=0, heading_deg=0,
                  size_x_m=width, size_y_m=breadth, size_z_m=height))
    app.write(db, 'hull_placements', dict(hull=HULL, dock=DOCK, origin_x=ORIGIN_X,
              origin_y=ORIGIN_Y, origin_z=0, heading_deg=0))


def align_existing_tracks(db):
    for move in db.execute("""SELECT m.*, g.cog_x FROM moves m JOIN blocks b ON b.id=m.block
            JOIN block_geometry g ON g.block=b.id WHERE b.hull=?""", (HULL,)):
        if move['origin'] not in FACILITIES or move['destination'] not in FACILITIES:
            continue
        samples = list(db.execute('SELECT id, recorded_at FROM telemetry WHERE move=? ORDER BY recorded_at,id',
                                  (move['id'],)))
        if not samples:
            continue
        start = datetime.fromisoformat(samples[0]['recorded_at'])
        x0, y0 = FACILITIES[move['origin']][2:4]
        x1, y1 = FACILITIES[move['destination']][2:4]
        if move['destination'] == DOCK:
            x1, y1 = ORIGIN_X + move['cog_x'], ORIGIN_Y
        for sample in samples:
            fraction = min(1, (datetime.fromisoformat(sample['recorded_at']) - start).total_seconds() / 200)
            db.execute('UPDATE telemetry SET x=?,y=? WHERE id=?',
                       (round(x0+(x1-x0)*fraction, 2), round(y0+(y1-y0)*fraction, 2), sample['id']))



def seed(db):
    def add(action, **values):
        return app.write(db, action, values)

    align_site(db)
    if db.execute('SELECT 1 FROM hulls WHERE id=?', (HULL,)).fetchone():
        align_layout(db)
        align_existing_tracks(db)
        return False

    for key, (name, kind, *_dimensions) in FACILITIES.items():
        add('locations', id=key, name='트윈 ' + name, kind=kind)

    for key, kind, capacity, station in (
        (GANTRY, '갠트리 크레인', 250, OUT),
        (TRANSPORT, '트랜스포터', 350, YARD),
        (GOLIATH, '골리앗 크레인', 600, DOCK),
    ):
        add('equipment', id=key, name='트윈 ' + kind)
        add('equipment_specs', equipment=key, equipment_type=kind,
            capacity_t=capacity, station=station)

    now = datetime.now(KST)
    today = now.date()
    monday = today - timedelta(days=today.weekday())

    def day(offset):
        return (monday + timedelta(days=offset)).isoformat()

    add('hulls', id=HULL, name='디지털 트윈 상선 (가상)')
    add('vessel_details', hull=HULL, vessel_type='연안 화물선', vessel_name='Twin Demo 01',
        customer='가상 선주', length_m=120, beam_m=24, delivery=day(150))
    add('hull_geometry', hull=HULL, depth_m=12, draft_m=7, frame_spacing_m=0.6)
    align_layout(db)
    add('dock_bookings', dock=DOCK, hull=HULL, start_day=day(-7), end_day=day(60), reason='')
    for milestone, offset in [('착공', -21), ('기공', -7), ('진수', 59), ('시운전', 90), ('인도', 150)]:
        add('hull_milestones', hull=HULL, milestone=milestone, baseline=day(offset), forecast=day(offset))

    def move(block, destination, equipment, start, weight, active=False, target=None):
        source = app.lookup(db, 'blocks', block)['location']
        key = add('moves', block=block, equipment=equipment, destination=destination)
        db.execute('UPDATE moves SET started_at=? WHERE id=?', (start.astimezone(timezone.utc).isoformat(), key))
        x0, y0 = FACILITIES[source][2:4]
        x1, y1 = target or FACILITIES[destination][2:4]
        for point in range(13 if active else 21):
            fraction = point / 20
            stamp = (start + timedelta(seconds=point * 10)).astimezone(timezone.utc).isoformat()
            status = '인양 준비' if point == 0 else '하역 완료' if point == 20 else '이송 중'
            app.insert(db, 'telemetry', move=key, recorded_at=stamp,
                       x=round(x0 + (x1 - x0) * fraction, 2),
                       y=round(y0 + (y1 - y0) * fraction, 2),
                       load=0 if point in (0, 20) else weight, status=status)
        if not active:
            add('confirm', id=key)
            db.execute('UPDATE moves SET confirmed_at=? WHERE id=?', (stamp, key))

    previous = None
    for index, offset in enumerate((-7, -6, -5, today.weekday(), 7, 8, 9, 10)):
        block = f'{HULL}-B{index + 1:02}'
        weight = 105 + 14 * index
        x0 = 15 * index
        add('blocks', id=block, hull=HULL, location=FAB)
        add('block_details', block=block, zone='선체', assembly='대조립',
            weight_t=weight, drawing=f'{block}-STR-R01')
        add('block_geometry', block=block, x_min=x0 + 0.2, x_max=x0 + 14.8,
            y_min=-11, y_max=11, z_min=0, z_max=6, cog_x=x0 + 7.5, cog_y=0, cog_z=3,
            geometry_source='추정')
        add('drawing_revisions', drawing_no=f'{block}-STR', block=block, revision='R01',
            issued_day=day(-21), change_type='최초 출도', description='트윈 실습 구조도')
        add('erection_plans', block=block, destination=DOCK, baseline=day(offset),
            forecast=day(offset), reason='')
        if previous:
            add('erection_links', predecessor_block=previous, successor_block=block)
        previous = block
        add('crane_assignments', equipment=GOLIATH, hull=HULL, block=block, crane_work='블록 탑재',
            work_area=DOCK, work_day=day(offset), start_hour=10, hours=2, lift_t=weight,
            note='트윈 실습 배정')

        for phase, (stage, days_before) in enumerate(zip(app.STAGES[:3], (7, 3, 2))):
            work_day = day((min(offset, -1) if index <= 5 else offset) - days_before)
            if index == 5 and phase == 2:
                work_day = day(today.weekday() + 1)
            work_date = date.fromisoformat(work_day)
            week_start = (work_date - timedelta(days=work_date.weekday())).isoformat()
            op = add('activities', hull=HULL, block=block, stage=stage,
                     baseline=work_day, forecast=work_day, reason='')
            order = add('orders', activity=op, cadence='주간', day=week_start, instruction=f'{block} {stage}')
            if index <= 4 or (index == 5 and phase < 2):
                add('results', id=order, actual=work_day, result='트윈 실습 작업 완료', inspection='합격', reason='')

        if index <= 4:
            for gate in app.shipbuilding.GATES:
                add('block_gate_checks', block=block, gate=gate, verdict='확인',
                    evidence=f'{block} 트윈 실습 {gate}')
        if index <= 5:
            transfer_day = date.fromisoformat(day(min(offset, -1) - 3))
            move(block, OUT, GANTRY, datetime.combine(transfer_day, time(13 + index % 3), KST), weight)
        if index <= 4:
            move(block, YARD, TRANSPORT, datetime.combine(transfer_day + timedelta(days=1),
                 time(14 + index % 3), KST) if transfer_day + timedelta(days=1) < today else now - timedelta(hours=1), weight)
        if index <= 3:
            active = index == 3
            lift_day = date.fromisoformat(day(offset))
            start = now - timedelta(minutes=2) if active else datetime.combine(lift_day, time(10), KST)
            move(block, DOCK, GOLIATH, start, weight, active=active, target=(ORIGIN_X + x0 + 7.5, ORIGIN_Y))
            if not active:
                add('erection_records', block=block, dock=DOCK, erect_day=lift_day.isoformat(),
                    inspection='합격', evidence='트윈 실습 탑재 검사')

    plan = add('plans', start=day(-14))
    db.execute('''INSERT INTO order_plans(work_order,plan_id)
        SELECT o.id,? FROM orders o JOIN activities a ON a.id=o.activity WHERE a.hull=?''',
        (int(plan.split('-')[-1]), HULL))
    return True


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        app.geometry.upgrade_location_footprints(db)
        changed = seed(db)
    app.bump_external_cache()
    print(f'{HULL} ' + ('등록 완료' if changed else '기존 좌표 동기화 완료'))
