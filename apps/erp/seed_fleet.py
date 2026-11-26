"""Load three fictional hulls through the normal ERP write rules."""
import argparse
import os
from contextlib import closing
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path

import app
import storage

HULLS = ('FLEET-01', 'FLEET-02', 'FLEET-03')
PREFIX = 'FLEET'
# One production line in line-local yard metres (centre x, y); seed() shifts it by its offset.
# The dock gate (x + 90) is the seaward end: arrange_yard_lines.py puts it on the yard shoreline.
LINE_LAYOUT = {
    'ST': ('후판 창고', '창고', 20, 25),
    'FAB': ('선체 조립장', '작업장', 90, 90),
    'OUT': ('선행의장장', '작업장', 200, 90),
    'YARD': ('PE 야드', '야드', 310, 90),
    'DK': ('건조 도크', '도크', 485, 90),
    'Q': ('의장 안벽', '안벽', 605, 175),
}


def seed(db, hull_ids=HULLS, prefix=PREFIX, names=('해솔', '해온', '해람'), offset=(0, 0)):
    existing = [key for key in hull_ids if db.execute('SELECT 1 FROM hulls WHERE id=?', (key,)).fetchone()]
    if existing:
        if tuple(existing) != tuple(hull_ids):
            raise ValueError('실습 호선 ID 일부가 이미 사용 중입니다.')
        return False

    def add(action, **values):
        return app.write(db, action, values)

    today = date.today()
    monday = today - timedelta(days=today.weekday())

    def day(offset):
        return (monday + timedelta(days=offset)).isoformat()

    def week(offset):
        return day(offset - offset % 7)

    dx, dy = offset
    facilities = {f'{prefix}-{key}': (name, kind, x + dx, y + dy) for key, (name, kind, x, y) in LINE_LAYOUT.items()}
    if not db.execute('SELECT 1 FROM yard_sites').fetchone():
        add('yard_sites', id=prefix, name='가상 신조 조선소', lat=34.89, lon=128.72,
            x_axis_bearing_deg=90)
    for key, (name, kind, x, y) in facilities.items():
        add('locations', id=key, name=name, kind=kind)
        add('location_geometry', location=key, x=x, y=y, z=0, heading_deg=0,
            size_x_m=180 if kind in ('도크', '안벽') else 65, size_y_m=45, size_z_m=0)
    for key in ('FAB', 'OUT', 'YARD', 'DK', 'Q'):
        center = f'{prefix}-WC-{key}'
        add('centers', id=center, name=f'{key} 작업반', hours=16)
        add('center_locations', center=center, location=f'{prefix}-{key}')
    for key, kind, capacity, station in (
        (f'{prefix}-TP', '트랜스포터', 380, f'{prefix}-YARD'),
        (f'{prefix}-GC', '골리앗 크레인', 600, f'{prefix}-DK'),
    ):
        add('equipment', id=key, name=kind)
        add('equipment_specs', equipment=key, equipment_type=kind, capacity_t=capacity, station=station)
    add('berths', id=f'{prefix}-BERTH', name='1선석', quay=f'{prefix}-Q', berth_side='안벽 전면',
        start_m=0, berth_length_m=185, water_depth_m=13)
    add('suppliers', id=f'{prefix}-SUP', name='철강 공급사')
    add('contractors', id=f'{prefix}-SUB', name='선체 조립 협력사', trade='용접')

    for hull, name, vessel_type, length, beam, keel, state in (
        (hull_ids[0], names[0], '근해 일반화물선', 132, 23, -56, '인도'),
        (hull_ids[1], names[1], '근해 컨테이너선', 146, 25, -14, '탑재'),
        (hull_ids[2], names[2], '근해 다목적화물선', 157, 27, 28, '준비'),
    ):
        completed = state == '인도'
        launch, trial, delivery = keel + 18, keel + 32, keel + 39
        add('hulls', id=hull, name=f'{name} · {state} (가상)')
        add('vessel_details', hull=hull, vessel_type=vessel_type, vessel_name=name, customer='가상 선주',
            length_m=length, beam_m=beam, delivery=day(delivery))
        add('hull_geometry', hull=hull, depth_m=12, draft_m=8, frame_spacing_m=0.6)
        add('hull_placements', hull=hull, dock=f'{prefix}-DK', origin_x=405 + dx, origin_y=90 + dy,
            origin_z=0, heading_deg=0)
        add('dock_bookings', hull=hull, dock=f'{prefix}-DK', start_day=day(keel), end_day=day(launch), reason='')
        add('berth_assignments', hull=hull, berth=f'{prefix}-BERTH', start_day=day(launch), end_day=day(delivery),
            berth_purpose='안벽 의장', note='가상 호선 스케줄')
        milestones = {}
        for title, offset in [('착공', keel - 17), ('기공', keel), ('진수', launch),
                              ('시운전', trial), ('인도', delivery)]:
            milestones[title] = (add('hull_milestones', hull=hull, milestone=title,
                                     baseline=day(offset), forecast=day(offset)), day(offset))

        def actual(title):
            key, stamp = milestones[title]
            add('milestone_update', id=key, forecast=stamp, actual=stamp, reason='모의 실적 등록')

        if state != '준비':
            actual('착공')
        material = f'{hull}-AH36'
        add('materials', id=material, name='AH36 후판 20 mm · ' + hull, unit='장', stock=0, lead_days=10)
        blocks = []
        for i, zone in enumerate(('기관실', '화물창', '선수부')):
            block = f'{hull}-B{i + 1:02}'
            weight, lift = 150 + i * 24, keel + i + (0 if i == 0 else i)
            cut, fit = keel - 17 + 2 * i, keel - 5 + i
            last = i == 2
            fabricated = completed or state == '탑재'
            erected = completed or (state == '탑재' and i == 0)
            blocked = state == '탑재' and i == 1
            release = cut - 14 if fabricated else -8 + i
            add('blocks', id=block, hull=hull, location=f'{prefix}-FAB')
            add('block_details', block=block, zone=zone, assembly='대조립', weight_t=weight, drawing=f'{block}-R01')
            x = length / 3 * i
            add('block_geometry', block=block, x_min=round(x + 0.1, 3), x_max=round(x + length / 3 - 0.1, 3),
                y_min=-beam / 2 + 0.2, y_max=beam / 2 - 0.2, z_min=0, z_max=8,
                cog_x=round(x + length / 6, 3), cog_y=0, cog_z=4, geometry_source='추정')
            add('erection_plans', block=block, destination=f'{prefix}-DK', baseline=day(lift), forecast=day(lift), reason='')
            if blocks:
                add('erection_links', predecessor_block=blocks[-1], successor_block=block)
            blocks.append(block)
            add('crane_assignments', equipment=f'{prefix}-GC', hull=hull, block=block, crane_work='블록 탑재',
                work_area=f'{prefix}-DK', work_day=day(lift), start_hour=10, hours=2, lift_t=weight, note='가상 배정')
            add('drawing_revisions', drawing_no=f'{block}-STR', block=block, revision='R01',
                issued_day=day(release), change_type='최초 출도', description='가상 블록 승인 구조도')
            design, fab, outfit = [
                add('activities', hull=hull, block=block, stage=stage, baseline=day(offset), forecast=day(offset), reason='')
                for stage, offset in zip(app.STAGES, (release, cut, fit))
            ]
            add('operations', activity=fab, center=f'{prefix}-WC-FAB', duration=16, earliest=day(cut))
            add('operations', activity=outfit, center=f'{prefix}-WC-OUT', duration=16, predecessor=fab, earliest=day(fit))
            requirement = add('requirements', activity=fab, material=material, qty=12, need_day=day(cut))
            if fabricated or not last:
                req = add('purchase_requests', requirement=requirement, qty=12)
                po = add('purchase_orders', request=req, supplier=f'{prefix}-SUP', promised_day=day(cut - 3))
                received = 12 if fabricated else (6 if i == 0 else 0)
                if received:
                    receipt = add('goods_receipts', purchase_order=po, day=day(cut - 3 if fabricated else -2), qty=received,
                                  inspection='합격', location=f'{prefix}-ST')
                    lot = add('receipt_lots', receipt=receipt, heat_no=f'HT-{block}', cert_no=f'MC-{block}', qty=received)

            plan = add('plans', start=week(release))
            for activity, offset in ((design, release), (fab, cut), (outfit, fit)):
                order = add('orders', activity=activity, cadence='주간', day=week(offset), plan_id=str(int(plan.split('-')[-1])),
                            instruction=f'{block} {app.lookup(db, "activities", activity)["stage"]}')
                if activity == fab and fabricated:
                    issue = add('issues', requirement=requirement, work_order=order, day=day(cut), qty=12, source=f'{prefix}-ST')
                    add('issue_lots', issue=issue, lot=lot, qty=12)
                if fabricated or activity == design:
                    detail = add('orders', activity=activity, cadence='일일', day=day(offset), parent=order,
                                 instruction='모의 일일 작업과 검사')
                    for key in (detail, order):
                        add('results', id=key, actual=day(offset),
                            result='모의 작업 완료', inspection='합격')

            for stage, center, offset, mh in (
                ('절단·가공', 'FAB', cut, 80), ('소조립', 'FAB', cut + 1, 110),
                ('중조립', 'FAB', cut + 2, 140), ('대조립', 'FAB', cut + 3, 180),
                ('선행탑재(PE)', 'YARD', fit + 1, 120), ('탑재', 'DK', lift, 95),
            ):
                stage_key = add('block_stages', block=block, assembly_stage=stage, center=f'{prefix}-WC-{center}',
                                planned_mh=mh, baseline=day(offset), forecast=day(offset))
                if fabricated and stage != '탑재':
                    add('work_logs', block_stage=stage_key, work_day=day(offset), manhours=mh, progress_pct=100,
                        contractor=f'{prefix}-SUB')
            item = add('outfitting_items', hull=hull, block=block, item_type='배관 스풀', name=f'{zone} 밸러스트 스풀',
                       outfit_stage='선행의장', required_day=day(fit))
            if fabricated:
                for event, offset in [('입고', fit - 1), ('설치', fit), ('검사 완료', fit + 1)]:
                    add('outfitting_events', item=item, event=event, event_day=day(offset), evidence=f'{block} 모의 {event}')
            pre = add('outfit_works', hull=hull, block=block, outfit_zone=zone, outfit_trade='배관',
                      work_phase='선행 의장', work_area=f'{prefix}-OUT', start_day=day(fit - 1), end_day=day(fit + 1),
                      planned_mh=90, crew=4, progress_pct=100 if fabricated else 0,
                      work_status='완료' if fabricated else '계획')
            point = add('inspection_points', hull=hull, block=block, activity_ref=fab, item='블록 용접 및 치수 ITP',
                        witness='선급', planned_day=day(fit + 1))
            if fabricated:
                if blocked or (completed and last):
                    add('inspection_records', point=point, inspect_day=day(fit + 1), inspection='불합격',
                        inspector='모의 선급 검사관', remarks='용접 보완 필요')
                if not blocked:
                    add('inspection_records', point=point, inspect_day=day(fit + 2), inspection='합격',
                        inspector='모의 선급 검사관', remarks='치수·용접 확인')
                    if completed and last:
                        ncr = db.execute('SELECT id FROM ncrs WHERE point=?', (point,)).fetchone()['id']
                        add('ncr_close', id=ncr, closed_day=day(fit + 2), evidence='보수 후 재검사 합격')
                for gate in app.shipbuilding.GATES:
                    verdict = '보류' if blocked and gate == '검사 승인' else '확인'
                    add('block_gate_checks', block=block, gate=gate, verdict=verdict, evidence=f'{block} 모의 {gate}')
                transfer(db, add, facilities, block, f'{prefix}-OUT', f'{prefix}-TP', day(fit - 1), 8, weight)
                transfer(db, add, facilities, block, f'{prefix}-YARD', f'{prefix}-TP', day(fit + 1), 15, weight)
            if erected:
                transfer(db, add, facilities, block, f'{prefix}-DK', f'{prefix}-GC', day(lift), 10, weight)
                add('erection_records', block=block, dock=f'{prefix}-DK', erect_day=day(lift), inspection='합격',
                    evidence=f'{block} 모의 탑재 합격서')
                add('work_logs', block_stage=stage_key, work_day=day(lift), manhours=95, progress_pct=100)
                if i == 0:
                    actual('기공')

        follow = add('outfit_works', hull=hull, outfit_zone='기관실', outfit_trade='기장', work_phase='안벽 의장',
                     work_area=f'{prefix}-Q', start_day=day(launch + 1), end_day=day(trial - 3), planned_mh=240,
                     crew=8, progress_pct=100 if completed else 0, work_status='완료' if completed else '계획')
        add('outfit_links', work=follow, predecessor_work=pre)
        final_item = add('outfitting_items', hull=hull, item_type='기자재', name='기관실 보조펌프',
                         outfit_stage='후행의장', required_day=day(launch + 1))
        if completed:
            actual('진수')
            for event, offset in [('입고', launch + 1), ('설치', launch + 5), ('검사 완료', trial - 3)]:
                add('outfitting_events', item=final_item, event=event, event_day=day(offset), evidence='모의 기자재 실적')
            punch = add('punch_items', hull=hull, category='A', description='보조펌프 계기 경보 확인',
                        raised_by='선주', raised_day=day(trial - 4))
            add('punch_close', id=punch, closed_day=day(trial - 2), evidence='모의 재시험 합격')
            actual('시운전')
            actual('인도')
        for origin, destination, offset, purpose in (
            (f'{prefix}-DK', f'{prefix}-BERTH', launch, '진수 후 이동'),
            (f'{prefix}-BERTH', 'SEA-TRIAL', trial, '시운전 출항'),
            ('SEA-TRIAL', f'{prefix}-BERTH', trial + 2, '시운전 귀항'),
            (f'{prefix}-BERTH', 'DEPARTED', delivery, '인도 출항'),
        ):
            add('quay_shifts', hull=hull, from_point=origin, to_point=destination, plan_day=day(offset),
                actual_day=day(offset) if completed else '', tugs=2, shift_purpose=purpose)
    return True


def transfer(db, add, facilities, block, destination, equipment, day, hour, weight):
    origin = app.lookup(db, 'blocks', block)['location']
    move = add('moves', block=block, equipment=equipment, destination=destination)
    start = datetime.combine(date.fromisoformat(day), time(hour), timezone(timedelta(hours=9)))
    start = start.astimezone(timezone.utc)
    db.execute('UPDATE moves SET started_at=? WHERE id=?', (start.isoformat(), move))
    x0, y0 = facilities[origin][2:]
    x1, y1 = facilities[destination][2:]
    for point in range(5):
        fraction = point / 4
        stamp = (start + timedelta(seconds=30 * point)).isoformat()
        app.insert(db, 'telemetry', move=move, recorded_at=stamp, x=x0 + (x1 - x0) * fraction,
                   y=y0 + (y1 - y0) * fraction, load=weight if 0 < point < 4 else 0,
                   status='하역 완료' if point == 4 else '이송 중' if point else '인양 준비')
    add('confirm', id=move)
    db.execute('UPDATE moves SET confirmed_at=? WHERE id=?', (stamp, move))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--db', type=Path, help='실습 데이터를 적재할 SQLite 파일')
    args = parser.parse_args()
    if args.db:
        app.DATABASE = args.db
    else:
        app.DATABASE_URL = os.environ.get('SHIPYARD_DATABASE_URL')
        if not app.DATABASE_URL:
            parser.error('--db 또는 SHIPYARD_DATABASE_URL을 지정하세요.')
    with closing(app.connect()) as db, db:
        storage.init_schema(db, app.SCHEMA)
    with closing(app.connect()) as db, db:
        storage.begin_write(db)
        changed = seed(db)
    if not args.db:
        app.bump_external_cache()
    print('실습 호선 3척 등록 완료' if changed else '실습 호선 3척이 이미 등록되어 있습니다.')
