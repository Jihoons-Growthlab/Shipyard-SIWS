import io
import json
import os
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from datetime import date, timedelta
from pathlib import Path
from urllib.parse import urlencode
from uuid import uuid4

import app
import auth
import cache
import storage


class Request:
    def __init__(self, request):
        self.input = io.BytesIO(request)
        self.output = io.BytesIO()

    def sendall(self, data):
        self.output.write(data)


    def makefile(self, mode, *args):
        if mode == 'rb':
            return self.input
        return self.output


class QuietHandler(app.Handler):
    def log_message(self, *args):
        pass


class ShipYardTest(unittest.TestCase):
    def setUp(self):
        url = os.environ.get('SHIPYARD_TEST_POSTGRES_URL')
        self.addCleanup(setattr, app, 'DATABASE_URL', app.DATABASE_URL)
        self.addCleanup(setattr, app, 'STORE', app.STORE)
        self.addCleanup(setattr, auth, 'PBKDF2_ITERATIONS', auth.PBKDF2_ITERATIONS)
        auth.PBKDF2_ITERATIONS = 1000
        redis_url = os.environ.get('SHIPYARD_TEST_REDIS_URL')
        app.STORE = cache.connect(redis_url)
        if redis_url:
            app.STORE.client.flushdb()
        if url:
            schema = 'test_' + uuid4().hex
            with closing(storage.Postgres(url)) as db, db:
                db.execute(f'CREATE SCHEMA {schema}')
            self.addCleanup(self.drop_schema, url, schema)
            app.DATABASE_URL = storage.psycopg.conninfo.make_conninfo(url, options=f'-c search_path={schema}')
            with closing(app.connect()) as db, db:
                storage.init_schema(db, app.SCHEMA)
                app.seed(db)
            self.bootstrap()
            return
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.addCleanup(setattr, app, 'DATABASE', app.DATABASE)
        app.DATABASE = Path(directory.name) / 'test.sqlite3'
        with closing(app.connect()) as db, db:
            db.executescript(app.SCHEMA)
            app.seed(db)
        self.bootstrap()

    def bootstrap(self):
        with closing(app.connect()) as db, db:
            auth.ensure_groups(db, app.insert)
            auth.create_account(db, app.insert, app.now(), 'admin', '관리자', auth.ADMIN_GROUP, 'admin-pass-1')
        self.token = self.login('admin', 'admin-pass-1')

    def login(self, user, password, expected=200):
        result = self.request('/api/login', {'id': user, 'password': password}, expected, token='')
        return result.get('token')

    @staticmethod
    def drop_schema(url, schema):
        with closing(storage.Postgres(url)) as db, db:
            db.execute(f'DROP SCHEMA {schema} CASCADE')

    def request(self, route, data=None, expected=200, token=None):
        method = 'GET' if data is None else 'POST'
        body = b'' if data is None else json.dumps(data).encode()
        headers = f'Content-Type: application/json\r\nContent-Length: {len(body)}\r\n'
        token = self.token if token is None else token
        if token:
            headers += f'Authorization: Bearer {token}\r\n'
        wire = Request(f'{method} {route} HTTP/1.0\r\n{headers}\r\n'.encode() + body)
        QuietHandler(wire, ('127.0.0.1', 0), None)
        response, body = wire.output.getvalue().split(b'\r\n\r\n', 1)
        status = int(response.split()[1])
        if expected is None:
            return status
        self.assertEqual(status, expected, body)
        return json.loads(body) if b'application/json' in response else body

    def api(self, route, data=None, expected=None, token=None):
        return self.request('/api/' + route, data, expected or (201 if data is not None else 200), token)

    def test_plan_snapshot_and_validation(self):
        self.assertIn(b'Hoon Heavy Industries', self.request('/'))
        first = self.api('plans')[0]
        self.assertEqual(len(first['snapshot']), 6)
        self.api('plans', {'start': '2026-11-17'}, 400)
        activity = first['snapshot'][2]
        self.api('forecast', {'id': 'A3', 'forecast': '2099-01-01'}, 400)
        self.api('forecast', {'id': 'A3', 'forecast': '2099-01-01', 'reason': '재발주'})
        self.api('plans', {'start': first['start']})
        plans = self.api('plans')
        self.assertEqual(plans[0], first)
        latest = next(row for row in plans[-1]['snapshot'] if row['id'] == 'A3')
        self.assertNotEqual(latest['forecast'], activity['forecast'])
        self.assertEqual(latest['baseline'], activity['baseline'])

    def test_work_order_reinspection_and_rollback(self):
        start = self.api('plans')[0]['start']
        self.api('orders', {'id': 'D2', 'activity': 'A1', 'cadence': '일일',
                            'day': start, 'parent': 'W1', 'instruction': '검사'})
        weekly = {'id': 'W1', 'actual': start, 'result': '완료', 'inspection': '합격'}
        self.api('results', weekly, 400)
        daily = {**weekly, 'id': 'D2'}
        self.api('results', {**daily, 'inspection': '불합격'})
        self.api('results', weekly, 400)
        self.api('results', daily)
        self.api('results', {**weekly, 'actual': '2099-01-01'}, 400)
        self.assertIsNone(next(row for row in self.api('orders') if row['id'] == 'W1')['actual'])
        self.api('results', weekly)
        self.assertEqual(self.api('activities')[0]['actual_variance_days'], 0)

    def test_vessel_detail_update_and_planning_constraints(self):
        original = self.api('vessel_details')[0]
        self.api('vessel_details', {**original, 'length_m': 299.125})
        self.assertEqual(self.api('vessel_details')[0]['length_m'], 299.125)
        self.api('vessel_details', {**original, 'length_m': -1}, 400)
        need = self.api('mrp')[0]
        self.assertEqual(need['shortage'], 5)
        self.assertEqual([row['planning_status'] for row in self.api('aps')],
                         ['자재 부족', '선행 작업 보류'])
        self.api('receipts', {'id': 'R1', 'material': 'PLATE', 'qty': 5, 'receipt_day': need['need_day']})
        self.assertEqual(self.api('mrp')[0]['shortage'], 0)
        self.api('operations', {'activity': 'A4', 'center': 'C1', 'duration': 8,
                                'earliest': need['need_day']})
        scheduled = {row['activity']: row for row in self.api('aps')}
        self.assertLess(scheduled['A2']['scheduled_end'], scheduled['A3']['scheduled_start'])
        self.assertLess(scheduled['A3']['scheduled_end'], scheduled['A4']['scheduled_start'])
        self.assertEqual(scheduled['A2']['scheduled_start'], need['need_day'])
        self.assertEqual(self.api('activities')[1]['forecast'], scheduled['A2']['forecast'])

    def test_procure_receive_issue_and_post_actual(self):
        today = date.today().isoformat()
        week = self.api('plans')[0]['start']
        self.api('suppliers', {'id': 'S1', 'name': '강판 공급사'})
        self.api('purchase_requests', {'id': 'PR1', 'requirement': 'REQ1', 'qty': 5})
        self.api('purchase_orders', {'id': 'PO1', 'request': 'PR1', 'supplier': 'S1', 'promised_day': today})
        self.assertEqual(self.api('mrp')[0]['shortage'], 0)
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간',
                            'day': week, 'instruction': '조립'})
        actual = {'id': 'W2', 'actual': today, 'result': '조립 완료', 'inspection': '합격'}
        self.api('results', actual, 400)
        issue = {'id': 'I1', 'requirement': 'REQ1', 'work_order': 'W2', 'qty': 25, 'day': today, 'source': 'STORE'}
        self.api('issues', issue, 400)
        receipt = {'id': 'G1', 'purchase_order': 'PO1', 'day': today, 'qty': 2, 'inspection': '불합격', 'location': 'STORE'}
        self.api('goods_receipts', receipt)
        self.assertEqual(self.api('materials')[0]['stock'], 20)
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 5)
        self.api('goods_receipts', {**receipt, 'id': 'G2', 'inspection': '합격'})
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 3)
        self.api('goods_receipts', {**receipt, 'id': 'G3', 'qty': 4, 'inspection': '합격'}, 400)
        self.api('issues', issue, 400)
        self.api('goods_receipts', {**receipt, 'id': 'G3', 'qty': 3, 'inspection': '합격'})
        self.assertEqual(self.api('purchase_orders')[0]['open_qty'], 0)
        self.api('issues', issue)
        self.assertEqual(self.api('materials')[0]['stock'], 0)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 0)
        self.assertEqual(self.api('issues')[0]['destination'], 'SHOP')
        ledger = self.api('inventory_ledger')
        self.assertEqual([(row['source_document'], row['qty_delta'], row['balance']) for row in ledger],
                         [('OPEN-PLATE', 20, 20), ('G2', 2, 22), ('G3', 3, 25), ('I1', -25, 0)])
        self.assertEqual(self.api('mrp')[0]['remaining_qty'], 0)
        self.api('results', actual)
        self.assertEqual(self.api('activities')[1]['actual'], today)

    def test_concurrent_issues_do_not_overdraw_stock(self):
        week = self.api('plans')[0]['start']
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간', 'day': week, 'instruction': '조립'})
        self.api('requirements', {'id': 'REQ2', 'activity': 'A2', 'material': 'PLATE',
                                  'qty': 25, 'need_day': week})

        def issue(index):
            return self.request('/api/issues', {'id': f'I{index}', 'requirement': f'REQ{index}',
                'work_order': 'W2', 'qty': 15, 'day': date.today().isoformat(), 'source': 'STORE'}, None)

        with ThreadPoolExecutor(max_workers=2) as executor:
            self.assertEqual(sorted(executor.map(issue, (1, 2))), [201, 400])
        self.assertEqual(self.api('materials')[0]['stock'], 5)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 5)
        self.assertEqual(len(self.api('issues')), 1)

    def test_document_numbering_and_plan_lineage(self):
        plan = self.api('plans')[0]
        self.assertEqual(plan['document_no'], 'PLAN-000001')
        work = {'activity': 'A1', 'cadence': '주간', 'day': plan['start'],
                'instruction': '도면 확인', 'plan_id': str(plan['id'])}
        self.api('orders', {**work, 'day': '2099-01-05'}, 400)
        order = self.api('orders', work)['id']
        self.assertEqual(order, 'WO-000001')
        child = self.api('orders', {**work, 'cadence': '일일', 'plan_id': '', 'parent': order})['id']
        by_id = {row['id']: row for row in self.api('orders')}
        self.assertEqual(by_id[child]['plan_id'], plan['id'])
        actual = {'id': child, 'actual': plan['start'], 'result': '도면 확인'}
        failed = self.api('results', {**actual, 'inspection': '불합격'})['id']
        passed = self.api('results', {**actual, 'inspection': '합격'})['id']
        results = {row['id']: row for row in self.api('production_results')}
        self.assertNotEqual(failed, passed)
        self.assertEqual(results[failed]['inspection'], '불합격')
        self.assertEqual(results[passed]['work_order'], child)
        self.assertRegex(results[failed]['lot_no'], r'^PLOT-\d{6}$')
        self.assertEqual(results[failed]['lot_no'], results[passed]['lot_no'])
        self.assertEqual(results[passed]['inspection_sequence'], 2)
        self.assertEqual(results[passed]['hull'], 'H101')
        self.assertEqual(results[passed]['parent_order'], order)
        self.assertEqual(results[passed]['recorded_by'], 'admin')

    def test_outfit_plan_order_installation_and_reinspection(self):
        today = date.today().isoformat()
        work = {'hull': 'H101', 'block': 'B101', 'outfit_zone': '기관실', 'outfit_trade': '배관',
                'work_phase': '선행 의장', 'work_area': 'SHOP', 'start_day': today, 'end_day': today,
                'planned_mh': 12, 'crew': 2, 'progress_pct': 0, 'work_status': '계획'}
        plan = self.api('outfit_works', work)['id']
        stage = self.api('block_stages', {'block':'B101', 'assembly_stage':'대조립', 'center':'C1',
                         'planned_mh':10, 'baseline':today, 'forecast':today})['id']
        self.api('outfit_conditions', {'id':plan, 'block_stage':stage, 'erection_required':'필수'})
        self.api('outfit_conditions', {'id':plan, 'block_stage':stage, 'erection_required':'필수'})
        self.api('outfit_materials', {'work':plan, 'requirement':'REQ1'})
        self.api('outfit_materials', {'work':plan, 'requirement':'REQ1'}, 400)
        self.assertIn('탑재 전 필수 의장 검사 미완료', self.ready('B101')['blockers'])
        issue = {'work': plan, 'system_code': 'CW-01', 'planned_qty': 10, 'outfit_unit': 'm',
                 'issued_day': today, 'instruction': '냉각수 배관 설치'}
        order = self.api('outfit_orders', issue)['id']
        self.api('outfit_orders', issue, 400)
        self.api('outfit_works', {**work, 'id': plan, 'progress_pct': 100, 'work_status': '완료'}, 400)
        install = {'outfit_order': order, 'work_day': today, 'installed_qty': 6, 'manhours': 5}
        self.assertIn('조립 단계 미완료', self.api('outfit_installations', install, 400)['error'])
        self.api('work_logs', {'block_stage':stage, 'work_day':today, 'manhours':10, 'progress_pct':100})
        self.assertIn('자재 미출고', self.api('outfit_installations', install, 400)['error'])
        week = self.api('plans')[0]['start']
        self.api('orders', {'id':'OUT-MAT-WO', 'activity':'A2', 'cadence':'주간', 'day':week, 'instruction':'출고 지시'})
        request = self.api('purchase_requests', {'requirement':'REQ1','qty':5})['id']
        purchase = self.api('purchase_orders', {'request':request,'supplier':'SUP-STEEL','promised_day':today})['id']
        receipt = self.api('goods_receipts', {'purchase_order':purchase,'day':today,'qty':5,
                           'inspection':'합격','location':'STORE'})['id']
        issue_id = self.api('issues', {'requirement':'REQ1','work_order':'OUT-MAT-WO','qty':25,
                            'day':today,'source':'STORE'})['id']
        material, = self.api('outfit_materials')
        self.assertEqual((material['received_qty'],material['issued_qty'],material['remaining_qty']), (5,25,0))
        self.assertEqual(material['receipt_documents'][0]['id'], receipt)
        self.assertEqual(material['issue_documents'][0]['id'], issue_id)
        first = self.api('outfit_installations', install)['id']
        self.api('outfit_installations', install, 400)
        inspection = {'outfit_installation': first, 'inspect_day': today, 'inspector': '검사 담당',
                      'inspection': '불합격', 'evidence': '치수 확인'}
        self.api('outfit_inspections', inspection)
        pending = self.api('outfit_orders')[0]
        self.assertEqual((pending['installed_qty'], pending['accepted_qty'], pending['progress_pct']), (6, 0, 0))
        self.api('outfit_inspections', {**inspection, 'inspection': '합격', 'evidence': '교정 후 재검사'})
        self.assertEqual(self.api('outfit_orders')[0]['progress_pct'], 60)
        second = self.api('outfit_installations', {**install, 'installed_qty': 4})['id']
        self.api('outfit_inspections', {**inspection, 'outfit_installation': second, 'inspection': '합격'})
        finished = self.api('outfit_orders?hull=H101')['rows'][0]
        self.assertEqual((finished['block'], finished['system_code'], finished['work_status']), ('B101', 'CW-01', '완료'))
        self.assertEqual((finished['actual_mh'], finished['accepted_qty'], finished['progress_pct']), (10, 10, 100))
        self.assertNotIn('탑재 전 필수 의장 검사 미완료', self.ready('B101')['blockers'])
        self.assertEqual(len(self.api('outfit_inspections')), 3)

    def test_result_details_and_existing_record_backfill(self):
        actual = (date.today() - timedelta(days=1)).isoformat()
        work = {'id': 'W1', 'actual': actual, 'result': '도면 검토', 'inspection': '합격', 'reason': '검토 일정 조정',
                'actual_start_at': actual + 'T08:00', 'actual_end_at': actual + 'T09:00',
                'work_area': 'SHOP', 'equipment': 'CR1', 'shift_code': '주간',
                'output_qty': 2.5, 'labor_mh': 1.25, 'inspection_evidence': '검토 기록'}
        self.api('results', work, 400)  # 수량 단위 누락
        # Seeded daily order has a later actual date so the parent cannot close on yesterday.
        with closing(app.connect()) as db, db:
            db.execute("UPDATE orders SET actual=? WHERE id='D1'", (actual,))
        result = self.api('results', {**work, 'output_unit': 'm²'})['id']
        row = next(row for row in self.api('production_results') if row['id'] == result)
        self.assertEqual(row['actual_start_at'], actual + 'T08:00:00+09:00')
        self.assertEqual((row['work_area'], row['equipment'], row['output_qty'], row['labor_mh']),
                         ('SHOP', 'CR1', 2.5, 1.25))
        self.assertEqual(row['issued_lot_status'], '출고 기록 없음')
        with closing(app.connect()) as db, db:
            db.execute("UPDATE orders SET actual=?,result='기존 작업',inspection='합격' WHERE id='W1'", (actual,))
            db.execute('DELETE FROM production_result_details WHERE result_id=?', (result,))
            db.execute('DELETE FROM production_results WHERE id=?', (result,))
            app.results.upgrade(db, app.insert)
            app.results.upgrade(db, app.insert)
            legacy, = [row for row in app.results.read(db) if row['work_order'] == 'W1']
            self.assertEqual(legacy['lot_no'], row['lot_no'])
            self.assertIsNone(legacy['recorded_at'])
            self.assertIsNone(legacy['labor_mh'])

    def test_shipbuilding_dates_relationships_and_readiness(self):
        self.api('hull_milestones', {'hull':'H101','milestone':'진수','baseline':'2027-01-04',
                  'forecast':'2027-01-04','reason':''})
        milestone = self.api('hull_milestones')[0]
        self.api('milestone_update', {'id':milestone['id'],'forecast':'2027-01-05',
                  'actual':'','reason':''}, 400)
        self.api('milestone_update', {'id':milestone['id'],'forecast':'2027-01-05',
                  'actual':'','reason':'도크 일정 조정'})
        self.assertEqual(self.api('hull_milestones')[0]['baseline'], '2027-01-04')
        self.assertEqual(len(self.api('milestone_history')), 1)
        self.api('block_structure', {'parent_block':'B101','child_block':'B102'})
        self.api('block_structure', {'parent_block':'B102','child_block':'B101'}, 400)
        self.api('erection_links', {'predecessor_block':'B101','successor_block':'B102'})
        self.api('erection_links', {'predecessor_block':'B102','successor_block':'B101'}, 400)
        self.api('hulls', {'id':'OTHER','name':'다른 호선'})
        self.api('blocks', {'id':'OTHER-B','hull':'OTHER','location':''})
        self.api('erection_links', {'predecessor_block':'B101','successor_block':'OTHER-B'}, 400)
        readiness = {row['id']:row for row in self.api('block_readiness')}
        self.assertIn('도면 출도 미확인', readiness['B101']['blockers'])
        self.assertIn('구성 블록 조립 미확인 B102', readiness['B101']['blockers'])
        self.assertIn('선행 블록 탑재 미확인 B101', readiness['B102']['blockers'])
        for gate in app.shipbuilding.GATES:
            self.api('block_gate_checks', {'block':'B102','gate':gate,'verdict':'확인','evidence':'검증용 근거'})
        self.api('erection_plans', {'block':'B102','destination':'DOCK','baseline':'2027-01-04',
                                    'forecast':'2027-01-04','reason':''})
        readiness = {row['id']:row for row in self.api('block_readiness')}
        self.assertEqual(readiness['B102']['readiness'], '준비 필요')
        self.assertIn('선행 블록', readiness['B102']['blockers'])

    def test_movement_and_telemetry(self):
        before = self.api('blocks')
        self.assertEqual([(row['position_status'], row['location']) for row in before],
                         [('in_transit', 'SHOP'), ('unknown', None)])
        self.api('telemetry', {'move': 'M1', 'x': 100, 'y': 100, 'load': 0, 'status': '정지'})
        self.assertEqual(self.api('blocks'), before)
        move = {'id': 'M2', 'block': 'B102', 'equipment': 'CR1', 'destination': 'QUAY'}
        self.api('moves', move, 409)
        self.api('confirm', {'id': 'M1'})
        self.api('confirm', {'id': 'M1'}, 400)
        self.assertEqual(self.api('blocks')[0]['position_status'], 'confirmed')
        self.assertEqual(self.api('blocks')[0]['location'], 'DOCK')
        self.api('moves', move)
        self.assertEqual(self.api('blocks')[1]['position_status'], 'in_transit')
        self.assertIsNone(self.api('moves')[1]['origin'])

    def ready(self, block):
        return {row['id']: row for row in self.api('block_readiness')}[block]

    def test_assembly_stages_manhours_and_progress(self):
        today = date.today().isoformat()
        self.api('contractors', {'id': 'SUB1', 'name': '모의 용접 협력사', 'trade': '용접'})
        small = self.api('block_stages', {'block': 'B102', 'assembly_stage': '소조립', 'center': 'C1',
                                          'planned_mh': 100, 'baseline': today, 'forecast': today})['id']
        grand = self.api('block_stages', {'block': 'B102', 'assembly_stage': '대조립', 'center': 'C1',
                                          'planned_mh': 300, 'baseline': today, 'forecast': today})['id']
        parent = self.api('block_stages', {'block': 'B101', 'assembly_stage': '대조립', 'center': 'C1',
                                           'planned_mh': 200, 'baseline': today, 'forecast': today})['id']
        self.api('block_structure', {'parent_block': 'B101', 'child_block': 'B102'})
        log = {'work_day': today, 'contractor': '', 'note': ''}
        self.api('work_logs', {**log, 'block_stage': grand, 'manhours': 10, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 40, 'progress_pct': 50, 'contractor': 'SUB1'})
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 5, 'progress_pct': 40}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 60, 'progress_pct': 101}, 400)
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 60, 'progress_pct': 100, 'contractor': 'SUB1'})
        self.api('work_logs', {**log, 'block_stage': small, 'manhours': 1, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': parent, 'manhours': 10, 'progress_pct': 100}, 400)
        self.api('work_logs', {**log, 'block_stage': grand, 'manhours': 100, 'progress_pct': 50})
        progress = {row['block']: row for row in self.api('block_progress')}
        self.assertEqual((progress['B102']['planned_mh'], progress['B102']['actual_mh']), (400, 200))
        self.assertEqual(progress['B102']['progress_pct'], 62.5)
        self.assertEqual(progress['B102']['productivity'], 1.25)
        self.assertEqual(progress['B102']['current_stage'], '대조립')
        hull = self.api('hull_progress')[0]
        self.assertEqual((hull['weight_t'], hull['weight_progress_pct'], hull['mh_progress_pct']), (120, 0.0, 41.7))
        summary = {row['contractor']: row['actual_mh'] for row in self.api('contractor_summary')}
        self.assertEqual(summary, {'SUB1': 100, '직영': 100})
        self.assertIn('조립 단계 미완료 대조립', self.ready('B102')['blockers'])

    def test_crane_berth_and_outfitting_plans(self):
        self.api('equipment_specs', {'equipment': 'CR1', 'equipment_type': '골리앗 크레인', 'capacity_t': 300, 'station': 'DOCK'})
        lift = {'equipment': 'CR1', 'hull': 'H101', 'block': 'B101', 'crane_work': '블록 탑재', 'work_area': 'DOCK',
                'work_day': '2027-03-02', 'start_hour': 8, 'hours': 4, 'lift_t': 120}
        first = self.api('crane_assignments', lift)['id']
        self.api('crane_assignments', {**lift, 'start_hour': 10}, 400)            # same crane, overlapping hours
        self.api('crane_assignments', {**lift, 'start_hour': 12, 'lift_t': 301}, 400)  # over capacity
        self.api('crane_assignments', {**lift, 'start_hour': 22}, 400)            # past midnight
        self.api('crane_assignments', {**lift, 'id': first, 'start_hour': 6})      # revise the same job
        self.api('crane_assignments', {**lift, 'start_hour': 10, 'block': '', 'crane_work': '크레인 정비', 'lift_t': 0})
        load = self.api('crane_load')
        self.assertEqual((load[0]['booked_hours'], load[0]['jobs'], load[0]['peak_lift_t']), (8, 2, 120))

        berth = {'id': 'Q1-A', 'name': '1안벽 A', 'quay': 'QUAY', 'berth_side': '안벽 전면', 'start_m': 0,
                 'berth_length_m': 320, 'water_depth_m': 13.5}
        self.api('berths', {**berth, 'quay': 'DOCK'}, 400)
        self.api('berths', berth)
        self.api('berths', {**berth, 'id': 'Q1-B', 'name': '1안벽 B', 'start_m': 320, 'berth_length_m': 200})
        stay = {'hull': 'H101', 'berth': 'Q1-A', 'start_day': '2027-06-01', 'end_day': '2027-09-30', 'berth_purpose': '안벽 의장'}
        self.api('berth_assignments', {**stay, 'berth': 'Q1-B'}, 400)            # ship longer than the berth
        self.api('berth_assignments', stay)
        self.api('berth_assignments', {**stay, 'start_day': '2027-09-01', 'end_day': '2027-10-15'}, 400)
        self.assertEqual(self.api('berth_occupancy')[0]['spare_m'], 320 - self.api('vessel_details')[0]['length_m'])
        shift = {'hull': 'H101', 'from_point': 'DOCK', 'to_point': 'Q1-A', 'plan_day': '2027-06-01', 'tugs': 4,
                 'shift_purpose': '진수 후 이동'}
        self.api('quay_shifts', {**shift, 'to_point': 'DOCK'}, 400)
        self.api('quay_shifts', {**shift, 'to_point': 'NOWHERE'}, 400)
        self.api('quay_shifts', {**shift, 'actual_day': '2099-01-01'}, 400)
        self.api('quay_shifts', shift)
        self.assertIn('SEA-TRIAL', [row['id'] for row in self.api('shift_points')])
        points = {row['id']: row for row in self.api('shift_points')}
        self.assertEqual(points['Q1-A']['quay'], 'QUAY')
        self.assertFalse(points['Q1-A']['position_known'])
        self.assertIsNone(points['Q1-A']['position'])
        position = {'id': 'Q1-A', 'x': -12.5, 'y': 0, 'z': 3, 'heading_deg': 90.5}
        self.api('shift_point_positions', {**position, 'id': 'NOWHERE'}, 400)
        self.api('shift_point_positions', {**position, 'heading_deg': 360}, 400)
        self.api('shift_point_positions', {**position, 'x': 'NaN'}, 400)
        self.api('shift_point_positions', position)
        self.api('shift_point_positions', {**position, 'x': 0})
        points = {row['id']: row for row in self.api('shift_points')}
        self.assertTrue(points['Q1-A']['position_known'])
        self.assertEqual(points['Q1-A']['position'], {'x': 0, 'y': 0, 'z': 3, 'heading_deg': 90.5})

        work = {'hull': 'H101', 'block': '', 'outfit_zone': '기관실', 'outfit_trade': '배관', 'work_phase': '안벽 의장',
                'work_area': 'QUAY', 'start_day': '2020-01-01', 'end_day': '2020-01-31', 'planned_mh': 1000, 'crew': 6,
                'progress_pct': 100, 'work_status': '완료'}
        self.api('outfit_works', {**work, 'work_status': '진행'}, 400)          # 100% must be 완료
        pipe = self.api('outfit_works', work)['id']
        lagging = self.api('outfit_works', {**work, 'outfit_trade': '보온', 'start_day': '2020-02-01',
                                            'end_day': '2020-02-28', 'progress_pct': 40, 'work_status': '진행'})['id']
        self.api('outfit_links', {'work': lagging, 'predecessor_work': pipe})
        self.api('outfit_links', {'work': pipe, 'predecessor_work': lagging}, 400)  # cycle
        rows = {row['id']: row for row in self.api('outfit_works')}
        self.assertIn('종료일 경과', rows[lagging]['schedule_check'])
        self.assertIn('작업 기간의 선석 배정 확인', rows[lagging]['schedule_check'])
        self.assertEqual(rows[lagging]['earned_mh'], 400)
        progress = self.api('outfit_progress')[0]
        self.assertEqual((progress['planned_mh'], progress['earned_mh'], progress['progress_status']), (2000, 1400, '지연'))

    def test_menu_rename_and_hierarchy_checks(self):
        with closing(app.connect()) as db, db:
            db.execute("DELETE FROM group_permissions WHERE group_id='PLAN'")
            db.execute("INSERT INTO group_permissions VALUES('PLAN','12주 계획',1,1)")
            db.execute("INSERT INTO group_permissions VALUES('PLAN','도크·크레인 계획',1,0)")
            auth.migrate_menus(db)
            rows = {r['menu']: (r['can_read'], r['can_write']) for r in db.execute("SELECT * FROM group_permissions WHERE group_id='PLAN'")}
        self.assertEqual(rows['소일정 (주간·일일)'], (1, 1))
        self.assertEqual(rows['대일정'], (1, 0))
        self.assertNotIn('12주 계획', rows)
        for key, day in (('기공', '2027-02-01'), ('진수', '2027-05-31'), ('시운전', '2027-10-01'), ('인도', '2027-11-30')):
            self.api('hull_milestones', {'hull': 'H101', 'milestone': key, 'baseline': day, 'forecast': day, 'reason': ''})
        work = {'hull': 'H101', 'block': '', 'outfit_zone': '기관실', 'outfit_trade': '배관', 'work_phase': '안벽 의장',
                'work_area': 'QUAY', 'start_day': '2027-05-01', 'end_day': '2027-06-30', 'planned_mh': 100, 'crew': 2,
                'progress_pct': 0, 'work_status': '계획'}
        self.api('outfit_works', work)
        self.assertIn('대일정 범위 밖', self.api('outfit_works')[0]['schedule_check'])
        overview = next(r for r in self.api('hull_overview') if r['hull'] == 'H101')
        self.assertIn('도크 예약 없음', overview['allocation_check'])
        self.assertEqual(overview['risk'], '위험')

    def test_hull_overview_reports_overdue_delivery_until_an_actual_is_recorded(self):
        earlier = (date.today() - timedelta(days=1)).isoformat()
        self.api('hull_milestones', {'hull': 'H101', 'milestone': '인도',
                                    'baseline': earlier, 'forecast': earlier, 'reason': ''})
        def overview():
            return next(row for row in self.api('hull_overview') if row['hull'] == 'H101')
        self.assertIn('예정일 경과', overview()['lifecycle_status'])
        self.assertIsNone(overview()['delivery_actual'])
        milestone = next(row for row in self.api('hull_milestones') if row['milestone'] == '인도')
        self.api('milestone_update', {'id': milestone['id'], 'forecast': earlier,
                                     'actual': earlier, 'reason': '인도 확인'})
        self.assertEqual(overview()['lifecycle_status'], '인도 완료')
        self.assertEqual(overview()['delivery_actual'], earlier)

    def test_crane_rails(self):
        self.api('equipment', {'id': 'JB1', 'name': '1도크 지브 크레인 1호'})
        rail = {'equipment': 'JB1', 'rail_x0': 0, 'rail_y0': -40, 'rail_x1': 150, 'rail_y1': -40, 'gauge_m': 10,
                'jib_heading_deg': 90}
        self.api('crane_rails', rail, 400)   # no lifting rating yet
        self.api('equipment_specs', {'equipment': 'JB1', 'equipment_type': '트랜스포터', 'capacity_t': 50, 'station': ''})
        self.api('crane_rails', rail, 400)   # a transporter does not run on rails
        self.api('equipment_specs', {'equipment': 'JB1', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DOCK'})
        self.api('crane_rails', {**rail, 'rail_x1': 5}, 400)
        self.api('crane_rails', {**rail, 'gauge_m': 1}, 400)
        self.api('crane_rails', {**rail, 'jib_heading_deg': 360}, 400)
        self.api('crane_rails', {**rail, 'jib_heading_deg': 0})
        self.api('crane_rails', rail)   # upsert by equipment
        rows = self.api('crane_rails')
        self.assertEqual(len(rows), 1)
        self.assertEqual((rows[0]['equipment_type'], rows[0]['capacity_t'], rows[0]['station'], rows[0]['jib_heading_deg']),
                         ('지브 크레인', 50.0, 'DOCK', 90.0))

    def test_goliath_rails_serve_every_dock_between_them(self):
        import resources
        with closing(app.connect()) as db, db:
            for key in ('DOCK2', 'DOCK3'):
                db.execute('INSERT INTO locations (id, name, kind) VALUES (?, ?, ?)', (key, key, '도크'))
            for key, x in (('DOCK', 0), ('DOCK2', 65), ('DOCK3', 130)):
                db.execute('''INSERT INTO location_geometry (location, x, y, z, heading_deg, size_x_m, size_y_m, size_z_m)
                    VALUES (?, ?, 0, 0, 90, 180, 45, 0)''', (key, x))
            db.execute("INSERT INTO equipment (id, name) VALUES ('GC2', '골리앗')")
            db.execute('''INSERT INTO equipment_specs (equipment, equipment_type, capacity_t, station)
                VALUES ('GC2', '골리앗 크레인', 600, 'DOCK')''')
            # Rails along the docks (heading 90), 120 m apart, straddling DOCK and DOCK2 but not DOCK3.
            db.execute('''INSERT INTO crane_rails (equipment, rail_x0, rail_y0, rail_x1, rail_y1, gauge_m, jib_heading_deg)
                VALUES ('GC2', 32.5, -150, 32.5, 100, 120, 90)''')
            by_station = resources.cranes(db)
        self.assertIn((600.0, 'GC2'), by_station['DOCK'])
        self.assertIn((600.0, 'GC2'), by_station['DOCK2'])
        self.assertNotIn((600.0, 'GC2'), by_station.get('DOCK3', []))

    def test_hull_and_block_geometry(self):
        self.api('hull_geometry', {'hull': 'H101', 'depth_m': 26.5, 'draft_m': 30, 'frame_spacing_m': 0.8}, 400)
        self.api('hull_geometry', {'hull': 'H101', 'depth_m': 26.5, 'draft_m': 11.5, 'frame_spacing_m': 0.8})
        box = {'block': 'B101', 'x_min': 0, 'x_max': 12.8, 'y_min': -23, 'y_max': -3.5, 'z_min': 0, 'z_max': 3,
               'cog_x': 6.4, 'cog_y': -13.25, 'cog_z': 1.5, 'geometry_source': '추정'}
        self.api('block_geometry', box)
        row = self.api('block_geometry')[0]
        self.assertEqual((row['size_x_m'], row['size_y_m'], row['frame_from'], row['frame_to']), (12.8, 19.5, 0, 16))
        self.assertEqual((row['hull'], row['weight_t']), ('H101', 120))
        self.api('block_geometry', {**box, 'x_max': 0}, 400)
        self.api('block_geometry', {**box, 'cog_z': 4}, 400)
        self.api('block_geometry', {**box, 'y_min': -24}, 400)
        self.api('block_geometry', {**box, 'x_max': 300}, 400)
        self.api('block_geometry', {**box, 'z_min': -1}, 400)
        self.api('block_geometry', {**box, 'geometry_source': '측정'}, 400)
        self.api('block_geometry', {**box, 'geometry_source': 'CAD', 'z_max': 3.25})
        self.assertEqual([(r['z_max'], r['geometry_source']) for r in self.api('block_geometry')], [(3.25, 'CAD')])
        meta = self.api('meta')
        self.assertIn('cog_y', meta['signed'])
        self.assertIn('block_geometry', meta['menu_actions']['설계 도면'])

    def test_yard_placement_model_key_and_erection_timeline(self):
        self.api('location_geometry', {'location': 'DOCK', 'x': 430, 'y': -170.5, 'z': 0, 'heading_deg': 0})
        self.api('location_geometry', {'location': 'DOCK', 'x': 430, 'y': 170, 'z': 0, 'heading_deg': 360}, 400)
        self.assertEqual(self.api('location_geometry'),
                         [{'location': 'DOCK', 'x': 430, 'y': -170.5, 'z': 0, 'heading_deg': 0,
                           'size_x_m': 0, 'size_y_m': 0, 'size_z_m': 0}])
        place = {'hull': 'H101', 'dock': 'DOCK', 'origin_x': 280.5, 'origin_y': 170, 'origin_z': 0, 'heading_deg': 90}
        self.api('hull_placements', place)
        self.api('hull_placements', {**place, 'dock': 'SHOP'}, 400)
        self.assertEqual(self.api('hull_placements')[0]['heading_deg'], 90)
        box = {'x_min': 0, 'x_max': 12, 'y_min': -5, 'y_max': 5, 'z_min': 0, 'z_max': 3,
               'cog_x': 6, 'cog_y': 0, 'cog_z': 1.5, 'geometry_source': '추정'}
        self.api('block_geometry', {**box, 'block': 'B101', 'model_asset': '../secret.obj'}, 400)
        self.api('block_geometry', {**box, 'block': 'B101', 'model_asset': 'H101/B101.obj'})
        self.api('block_geometry', {**box, 'block': 'B102', 'x_min': 12, 'x_max': 24, 'cog_x': 18})
        self.api('erection_links', {'predecessor_block': 'B101', 'successor_block': 'B102'})
        timeline = self.api('erection_timeline')
        self.assertEqual([(row['block'], row['step'], row['predecessors'], row['model_asset']) for row in timeline],
                         [('B101', 1, [], 'H101/B101.obj'), ('B102', 2, ['B101'], None)])
        self.assertEqual(self.api('erection_timeline?hull=H101')['total'], 2)

    def test_yard_site_geo_reference_and_footprints(self):
        self.assertEqual(self.api('location_geo'), [])
        self.api('location_geometry', {'location': 'DOCK', 'x': 100, 'y': 0, 'z': 0, 'heading_deg': 0,
                                       'size_x_m': 40, 'size_y_m': 20, 'size_z_m': 0})
        self.api('location_geometry', {'location': 'SHOP', 'x': 0, 'y': 50, 'z': 0, 'heading_deg': 0, 'size_x_m': -1}, 400)
        self.assertIsNone(self.api('location_geo')[0]['lat'], 'No site yet: no lat/lon.')
        site = {'id': 'GY', 'name': '가상 조선소', 'lat': 34.8512345, 'lon': 128.5598765, 'x_axis_bearing_deg': 90}
        self.api('yard_sites', {**site, 'lat': 91}, 400)
        self.api('yard_sites', {**site, 'x_axis_bearing_deg': 360}, 400)
        self.api('yard_sites', site)
        self.api('yard_sites', {**site, 'id': 'GY2'}, 400)
        self.assertEqual(self.api('yard_sites')[0]['lat'], 34.8512345)
        dock = self.api('location_geo')[0]
        # Bearing 90: yard +x points east, so x=100 m is ~0.0010952 deg of longitude east at 34.85 N, same latitude.
        self.assertAlmostEqual(dock['lat'], 34.8512345, places=7)
        self.assertAlmostEqual(dock['lon'] - 128.5598765, 100 / (6378137.0 * 0.820684) * 57.29578, places=6)
        self.assertEqual(len(dock['corners']), 4)
        lats = sorted(c[0] for c in dock['corners'])
        self.assertAlmostEqual((lats[-1] - lats[0]) * 111319.49, 20, places=0)  # footprint y (north-south) = 20 m
        self.request('/map', expected=404)

    def test_crane_capacity_dock_booking_schedule_and_erection(self):
        today = date.today()
        forecast = today + timedelta(days=14 - today.weekday())
        plan = {'destination': 'DOCK', 'baseline': forecast.isoformat(), 'forecast': forecast.isoformat(), 'reason': ''}
        self.api('equipment', {'id': 'TR1', 'name': '블록 트랜스포터'})
        self.api('equipment_specs', {'equipment': 'TR1', 'equipment_type': '트랜스포터', 'capacity_t': 100, 'station': ''})
        self.api('block_details', {'block': 'B102', 'zone': '선수', 'assembly': '대조립', 'weight_t': 150, 'drawing': 'D'})
        self.api('moves', {'block': 'B102', 'equipment': 'TR1', 'destination': 'YARD'}, 400)
        self.api('equipment_specs', {'equipment': 'TR1', 'equipment_type': '트랜스포터', 'capacity_t': 200, 'station': ''})
        self.api('moves', {'block': 'B102', 'equipment': 'TR1', 'destination': 'YARD'})
        self.api('equipment_specs', {'equipment': 'CR1', 'equipment_type': '갠트리 크레인', 'capacity_t': 100, 'station': 'DOCK'})
        self.api('erection_plans', {**plan, 'block': 'B101'})
        self.api('erection_plans', {**plan, 'block': 'B102'})
        self.api('erection_links', {'predecessor_block': 'B101', 'successor_block': 'B102'})
        blockers = self.ready('B101')['blockers']
        self.assertIn('도크 점유 예약 없음', blockers)
        self.assertIn('크레인 용량 부족 (120.0t > 100.0t)', blockers)
        schedule = {row['block']: row for row in self.api('erection_schedule')}
        self.assertEqual(schedule['B101']['planning_status'], '크레인 용량 부족')
        self.assertEqual(schedule['B102']['planning_status'], '선행 블록 보류')
        self.api('equipment_specs', {'equipment': 'CR1', 'equipment_type': '갠트리 크레인', 'capacity_t': 400, 'station': 'DOCK'})
        self.api('holidays', {'holiday': forecast.isoformat(), 'name': '모의 휴무'})
        booking = {'dock': 'DOCK', 'hull': 'H101', 'start_day': today.isoformat(),
                   'end_day': (forecast + timedelta(days=30)).isoformat(), 'reason': ''}
        self.api('dock_bookings', booking)
        self.api('hulls', {'id': 'H102', 'name': '다른 호선'})
        self.api('dock_bookings', {**booking, 'hull': 'H102', 'start_day': forecast.isoformat()}, 400)
        schedule = {row['block']: row for row in self.api('erection_schedule')}
        self.assertEqual(schedule['B101']['scheduled_day'], (forecast + timedelta(days=1)).isoformat())
        self.assertEqual(schedule['B102']['scheduled_day'], (forecast + timedelta(days=2)).isoformat())
        self.assertEqual(schedule['B101']['crane'], 'CR1')
        self.assertNotIn('도크', self.ready('B101')['blockers'])
        # A clean block goes through the full erection proof: move confirmed, all gates, inspection record.
        self.api('blocks', {'id': 'B201', 'hull': 'H101', 'location': 'SHOP'})
        self.api('block_details', {'block': 'B201', 'zone': '선미', 'assembly': '대조립', 'weight_t': 80, 'drawing': 'D'})
        self.api('erection_plans', {**plan, 'block': 'B201'})
        record = {'block': 'B201', 'dock': 'DOCK', 'erect_day': today.isoformat(), 'inspection': '합격', 'evidence': '탑재 검사'}
        self.api('erection_records', record, 400)
        milestone = self.api('hull_milestones', {'hull': 'H101', 'milestone': '기공', 'baseline': today.isoformat(),
                                                 'forecast': today.isoformat(), 'reason': ''})['id']
        keel = {'id': milestone, 'forecast': today.isoformat(), 'actual': today.isoformat(), 'reason': '실적 등록'}
        self.api('milestone_update', keel, 400)
        self.api('equipment', {'id': 'CR2', 'name': '골리앗 크레인'})
        move = self.api('moves', {'block': 'B201', 'equipment': 'CR2', 'destination': 'DOCK'})['id']
        self.api('confirm', {'id': move})
        self.api('erection_records', record, 400)
        for gate in app.shipbuilding.GATES:
            self.api('block_gate_checks', {'block': 'B201', 'gate': gate, 'verdict': '확인', 'evidence': '근거'})
        self.assertEqual(self.ready('B201')['readiness'], '준비 확인')
        self.api('erection_records', record)
        self.api('erection_records', record, 400)
        self.assertEqual(self.ready('B201')['readiness'], '탑재 완료')
        self.api('milestone_update', keel)
        launch = self.api('hull_milestones', {'hull': 'H101', 'milestone': '진수', 'baseline': today.isoformat(),
                                              'forecast': today.isoformat(), 'reason': ''})['id']
        self.api('milestone_update', {**keel, 'id': launch}, 400)

    def test_steel_heat_lots_returns_and_rollup(self):
        today = date.today().isoformat()
        week = self.api('plans')[0]['start']
        self.api('suppliers', {'id': 'S1', 'name': '강판 공급사'})
        self.api('purchase_requests', {'id': 'PR1', 'requirement': 'REQ1', 'qty': 5})
        self.api('purchase_orders', {'id': 'PO1', 'request': 'PR1', 'supplier': 'S1', 'promised_day': today})
        self.api('goods_receipts', {'id': 'G0', 'purchase_order': 'PO1', 'day': today, 'qty': 1,
                                    'inspection': '불합격', 'location': 'STORE'})
        self.api('goods_receipts', {'id': 'G1', 'purchase_order': 'PO1', 'day': today, 'qty': 5,
                                    'inspection': '합격', 'location': 'STORE'})
        self.api('receipt_lots', {'receipt': 'G0', 'heat_no': 'H-0', 'cert_no': 'C-0', 'qty': 1}, 400)
        lot1 = self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-1', 'cert_no': 'MC-1', 'qty': 3})['id']
        self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-2', 'cert_no': 'MC-2', 'qty': 3}, 400)
        lot2 = self.api('receipt_lots', {'receipt': 'G1', 'heat_no': 'H-2', 'cert_no': 'MC-2', 'qty': 2})['id']
        self.api('orders', {'id': 'W2', 'activity': 'A2', 'cadence': '주간', 'day': week, 'instruction': '조립'})
        self.api('issues', {'id': 'I1', 'requirement': 'REQ1', 'work_order': 'W2', 'qty': 25, 'day': today, 'source': 'STORE'})
        self.api('issue_lots', {'issue': 'I1', 'lot': lot1, 'qty': 4}, 400)
        self.api('issue_lots', {'issue': 'I1', 'lot': lot1, 'qty': 3})
        self.api('issue_lots', {'issue': 'I1', 'lot': lot2, 'qty': 2})
        trace = self.api('block_traceability')
        self.assertEqual(sorted((row['heat_no'], row['qty']) for row in trace if row['block'] == 'B101'),
                         [('H-1', 3), ('H-2', 2), ('로트 미배정', 20)])
        self.assertEqual({row['supplier'] for row in trace if row['lot']}, {'S1'})
        result = self.api('results', {'id': 'W2', 'actual': today, 'result': '치수 보정 필요',
                                    'inspection': '불합격', 'reason': '검사 보류'})['id']
        row = next(row for row in self.api('production_results') if row['id'] == result)
        self.assertEqual(row['issued_lot_status'], '일부 배정')
        self.assertEqual({(lot['heat_no'], lot['lot_qty']) for lot in row['issued_lots']}, {('H-1', 3), ('H-2', 2)})
        remnant = {'issue': 'I1', 'lot': lot1, 'return_type': '잔재', 'qty': 1, 'return_day': today, 'warehouse': 'STORE'}
        self.api('material_returns', {**remnant, 'warehouse': ''}, 400)
        self.api('material_returns', remnant)
        self.api('material_returns', {**remnant, 'lot': '', 'return_type': '폐기', 'qty': 30, 'warehouse': ''}, 400)
        self.assertEqual(self.api('materials')[0]['stock'], 1)
        self.assertEqual(self.api('warehouse_stock')[0]['qty'], 1)
        self.assertEqual(self.api('inventory_ledger')[-1]['movement'], '잔재 입고')
        self.assertEqual({row['id']: row['balance'] for row in self.api('receipt_lots')}[lot1], 1)
        self.api('block_structure', {'parent_block': 'B101', 'child_block': 'B102'})
        self.api('activities', {'id': 'A9', 'hull': 'H101', 'block': 'B102', 'stage': '가공·조립',
                                'baseline': week, 'forecast': week, 'reason': ''})
        self.api('requirements', {'id': 'REQ9', 'activity': 'A9', 'material': 'PLATE', 'qty': 4, 'need_day': week})
        rollup = {row['block']: row for row in self.api('material_rollup')}
        self.assertEqual((rollup['B101']['own_qty'], rollup['B101']['tree_qty'], rollup['B101']['levels']), (25, 29, 2))
        self.assertEqual(rollup['B101']['remaining_qty'], 4)

    def test_itp_ncr_punch_outfitting_and_delivery_gate(self):
        today = date.today().isoformat()
        point = self.api('inspection_points', {'hull': 'H101', 'block': 'B101', 'activity_ref': '',
                                               'item': '용접 비파괴검사', 'witness': '선급', 'planned_day': today})['id']
        record = {'point': point, 'inspect_day': today, 'inspector': '모의 선급 검사관', 'remarks': '균열 지시'}
        self.api('inspection_records', {**record, 'inspection': '불합격'})
        ncr = self.api('ncrs')[0]
        self.assertEqual((ncr['point'], ncr['disposition'], ncr['description']), (point, '재작업', '균열 지시'))
        blockers = self.ready('B101')['blockers']
        self.assertIn('미결 NCR 1건', blockers)
        self.assertIn('ITP 검사 미합격 1건', blockers)
        close = {'id': ncr['id'], 'closed_day': today, 'evidence': '재용접 후 UT 합격'}
        self.api('ncr_close', close, 400)
        self.api('inspection_records', {**record, 'inspection': '합격', 'remarks': ''})
        self.api('inspection_records', {**record, 'inspection': '합격'}, 400)
        self.api('ncr_close', close)
        self.api('ncr_close', close, 400)
        self.assertNotIn('NCR', self.ready('B101')['blockers'])
        self.api('outfitting_items', {'hull': 'H101', 'block': '', 'item_type': '배관 스풀', 'name': 'BW 스풀',
                                      'outfit_stage': '선행의장', 'required_day': today}, 400)
        item = self.api('outfitting_items', {'hull': 'H101', 'block': 'B101', 'item_type': '배관 스풀',
                                             'name': 'BW 스풀', 'outfit_stage': '선행의장', 'required_day': today})['id']
        self.assertIn('선행의장품 미설치 1건', self.ready('B101')['blockers'])
        event = {'item': item, 'event_day': today, 'evidence': '현장 확인'}
        self.api('outfitting_events', {**event, 'event': '설치'}, 400)
        self.api('outfitting_events', {**event, 'event': '입고'})
        self.api('outfitting_events', {**event, 'event': '설치'})
        self.assertNotIn('선행의장품', self.ready('B101')['blockers'])
        self.assertEqual(self.api('outfitting_items')[0]['status'], '설치')
        punch = self.api('punch_items', {'hull': 'H101', 'block': '', 'category': 'A', 'description': '비상 발전기 기동 불량',
                                         'raised_by': '선주', 'raised_day': today})['id']
        delivery = self.api('hull_milestones', {'hull': 'H101', 'milestone': '인도', 'baseline': today,
                                                'forecast': today, 'reason': ''})['id']
        deliver = {'id': delivery, 'forecast': today, 'actual': today, 'reason': '인도 실적'}
        self.api('milestone_update', deliver, 400)
        self.api('punch_close', {'id': punch, 'closed_day': today, 'evidence': '재시험 합격'})
        self.api('milestone_update', deliver, 400)
        self.api('outfitting_events', {**event, 'event': '검사 완료'})
        self.api('milestone_update', deliver)

    def test_drawing_revision_requires_reconfirmed_release(self):
        today = date.today().isoformat()
        drawing = {'drawing_no': 'D-B101', 'block': 'B101', 'issued_day': today, 'description': '구조 도면'}
        self.api('drawing_revisions', {**drawing, 'revision': 'R01', 'change_type': '설계 변경'}, 400)
        self.api('drawing_revisions', {**drawing, 'revision': 'R01', 'change_type': '최초 출도'})
        self.assertIn('최신 도면 D-B101 R01 출도 확인 필요', self.ready('B101')['blockers'])
        self.api('block_gate_checks', {'block': 'B101', 'gate': '도면 출도', 'verdict': '확인', 'evidence': 'R01'})
        self.assertNotIn('최신 도면', self.ready('B101')['blockers'])
        self.api('drawing_revisions', {**drawing, 'revision': 'R02', 'change_type': '설계 변경', 'description': '보강재 추가'})
        self.assertIn('최신 도면 D-B101 R02 출도 확인 필요', self.ready('B101')['blockers'])

    def test_aps_holidays_and_multiple_predecessors(self):
        need = self.api('mrp')[0]
        self.api('receipts', {'id': 'R1', 'material': 'PLATE', 'qty': 5, 'receipt_day': need['need_day']})
        self.api('operations', {'activity': 'A4', 'center': 'C1', 'duration': 8, 'earliest': need['need_day']})
        self.api('operation_links', {'activity': 'A4', 'predecessor': 'A3'})
        self.api('operation_links', {'activity': 'A2', 'predecessor': 'A4'}, 400)
        self.api('operation_links', {'activity': 'A4', 'predecessor': ''}, 400)
        before = {row['activity']: row for row in self.api('aps')}
        self.api('holidays', {'holiday': need['need_day'], 'name': '모의 휴무'})
        after = {row['activity']: row for row in self.api('aps')}
        self.assertGreater(after['A2']['scheduled_start'], before['A2']['scheduled_start'])
        self.assertNotEqual(after['A2']['scheduled_start'], need['need_day'])
        self.assertLess(after['A3']['scheduled_end'], after['A4']['scheduled_start'])
        self.assertEqual(after['A4']['predecessor'], 'A3')

    def test_login_sessions_and_lockout(self):
        self.api('blocks', token='', expected=401)
        self.api('blocks', token='forged-token', expected=401)
        self.assertEqual(self.api('me')['group_id'], auth.ADMIN_GROUP)
        self.login('admin', 'wrong-pass-1', 401)
        self.login('ghost', 'whatever-1', 401)
        events = self.api('login_events')
        self.assertEqual([row['success'] for row in events][:3], [0, 0, 1])
        self.assertNotIn('password', json.dumps(events))
        self.api('accounts', {'id': 'lee', 'name': '이생산', 'group_id': 'PROD', 'password': 'short'}, 400)
        self.api('accounts', {'id': 'lee', 'name': '이생산', 'group_id': 'PROD', 'password': 'onlyletters'}, 400)
        self.api('accounts', {'id': 'lee', 'name': '이생산', 'group_id': 'PROD', 'password': 'prod-pass-1'})
        self.assertNotIn('password_hash', self.api('accounts')[0])
        for _ in range(auth.LOCK_LIMIT):
            self.login('lee', 'bad-pass-99', 401)
        self.login('lee', 'prod-pass-1', 429)
        app.STORE.delete('login_fail:lee')
        first = self.login('lee', 'prod-pass-1')
        second = self.login('lee', 'prod-pass-1')
        self.api('me', token=first)
        self.api('change_password', {'current_password': 'nope', 'new_password': 'prod-pass-2'}, 400, token=first)
        self.api('change_password', {'current_password': 'prod-pass-1', 'new_password': 'prod-pass-2'}, token=first)
        self.api('me', token=first)
        self.api('me', token=second, expected=401)
        self.api('logout', {}, 200, token=first)
        self.api('me', token=first, expected=401)
        third = self.login('lee', 'prod-pass-2')
        self.api('account_update', {'id': 'lee', 'name': '이생산', 'group_id': 'PROD', 'status': '중지'})
        self.api('me', token=third, expected=401)
        self.login('lee', 'prod-pass-2', 401)
        self.api('account_update', {'id': 'admin', 'name': '관리자', 'group_id': 'PROD', 'status': '사용'}, 400)
        audit = json.dumps(self.api('audit_log'), ensure_ascii=False)
        self.assertIn('change_password', audit)
        self.assertNotIn('prod-pass', audit)

    def test_group_menu_permissions(self):
        self.api('user_groups', {'id': 'SUB', 'name': '협력사', 'description': ''})
        self.api('accounts', {'id': 'sub1', 'name': '협력사 반장', 'group_id': 'SUB', 'password': 'sub-pass-1'})
        sub = self.login('sub1', 'sub-pass-1')
        me = self.api('me', token=sub)
        self.assertEqual((me['menus'], me['actions']), ({}, []))
        self.api('hulls', token=sub)
        self.api('work_logs', token=sub, expected=403)
        self.api('contractors', {'id': 'C9', 'name': '협력사', 'trade': '용접'}, 403, token=sub)
        self.api('group_permissions_bulk', {'group_id': 'SUB', 'permissions': {'블록 조립·공수': {'read': True, 'write': True},
                                                                              '물량·공수 진척': {'read': True}}})
        me = self.api('me', token=sub)
        self.assertEqual(me['menus']['블록 조립·공수'], {'read': True, 'write': True})
        self.assertIn('work_logs', me['actions'])
        self.assertNotIn('orders', me['actions'])
        self.api('work_logs', token=sub)
        self.api('block_progress', token=sub)
        self.api('purchase_orders', token=sub, expected=403)
        self.api('orders', {'activity': 'A1', 'cadence': '주간', 'day': '2099-01-05', 'instruction': 'x'}, 403, token=sub)
        self.api('group_permissions_bulk', {'group_id': 'SUB', 'permissions': {'블록 조립·공수': {'read': True}}})
        self.assertNotIn('work_logs', self.api('me', token=sub)['actions'])
        self.api('group_permissions_bulk', {'group_id': auth.ADMIN_GROUP, 'permissions': {}}, 400)
        self.api('group_permissions_bulk', {'group_id': 'SUB', 'permissions': {'없는 메뉴': {'read': True}}}, 400)
        self.api('accounts', token=sub, expected=403)
        self.api('group_permissions_bulk', {'group_id': 'SUB', 'permissions': {}}, 403, token=sub)
        view = self.login('admin', 'admin-pass-1')
        self.api('accounts', {'id': 'v1', 'name': '조회', 'group_id': 'VIEW', 'password': 'view-pass-1'}, token=view)
        viewer = self.login('v1', 'view-pass-1')
        self.api('purchase_orders', token=viewer)
        self.api('suppliers', {'id': 'S9', 'name': '공급사'}, 403, token=viewer)

    def test_versioned_cache_and_server_search(self):
        first = self.request('/api/mrp', expected=None)
        self.assertEqual(first, 200)
        self.assertEqual(self.api('mrp')[0]['shortage'], 5)
        self.api('receipts', {'id': 'R1', 'material': 'PLATE', 'qty': 5, 'receipt_day': self.api('mrp')[0]['need_day']})
        self.assertEqual(self.api('mrp')[0]['shortage'], 0)
        for index in range(30):
            self.api('telemetry', {'move': 'M1', 'x': index, 'y': 1, 'load': 10, 'status': f'관측 {index}'})
        page = self.api('telemetry?size=10&page=1&sort=x&dir=desc')
        self.assertEqual((page['total'], page['pages'], page['page']), (31, 4, 1))
        self.assertEqual([row['x'] for row in page['rows']][:2], [20.0, 19.0])
        self.assertEqual(self.api('telemetry?' + urlencode({'q': '관측 2', 'size': 50}))['total'], 11)
        self.assertEqual(self.api('telemetry?hull=H101')['total'], 31)
        self.assertEqual(self.api('telemetry?hull=NONE')['total'], 0)
        self.assertEqual(self.api('telemetry?' + urlencode({'f.status': '관측 1', 'size': 50}))['total'], 11)
        self.assertEqual(self.api('telemetry?' + urlencode({'f.x': '>=25', 'sort': 'x'}))['total'], 5)
        self.assertEqual(self.api('telemetry?' + urlencode({'f.x': '=3', 'f.status': '관측'}))['total'], 1)
        self.assertEqual(self.api('telemetry?' + urlencode({'f.recorded_at': '2000-01-01~'}))['total'], 31)
        self.assertEqual(self.api('telemetry?' + urlencode({'f.recorded_at': '~2000-01-01'}))['total'], 0)
        self.api('telemetry?f.nope=1', expected=400)
        self.api('telemetry?f.Bad-Key=1', expected=400)
        self.api('telemetry?bogus=1', expected=400)
        self.api('telemetry?sort=nope', expected=400)
        self.api('telemetry?size=999', expected=400)
        ledger = self.api('inventory_ledger?material=PLATE&location=STORE&date_key=day&from=2000-01-01')
        self.assertEqual(ledger['total'], 1)
        # Cache hits are served without touching the database; a write moves to a new version.
        app.STORE.set('cache:%s:route:hulls' % (app.STORE.get(cache.VERSION_KEY) or 0), '[{"id": "cached"}]')
        self.assertEqual(self.api('hulls'), [{'id': 'cached'}])
        self.api('hulls', {'id': 'H102', 'name': '추가 호선'})
        self.assertEqual([row['id'] for row in self.api('hulls')], ['H101', 'H102'])


    def test_openapi_document_and_swagger_page(self):
        self.assertIn(b'swagger-ui', self.request('/docs', token=''))
        spec = self.request('/api/openapi.json', token='')
        self.assertEqual(spec['openapi'], '3.0.3')
        paths = spec['paths']
        for route in app.READS:
            self.assertIn('get', paths[f'/api/{route}'], route)
        for action in app.FIELDS:
            self.assertIn('post', paths[f'/api/{action}'], action)
        self.assertEqual(paths['/api/login']['post']['security'], [])
        body = paths['/api/work_logs']['post']['requestBody']['content']['application/json']['schema']
        self.assertEqual(body['required'], ['block_stage', 'work_day', 'manhours', 'progress_pct'])
        self.assertEqual(paths['/api/block_stages']['post']['requestBody']['content']['application/json']['schema']
                         ['properties']['assembly_stage']['enum'][0], '절단·가공')
        self.assertNotIn('password_hash', json.dumps(spec))


class BomFlowTest(unittest.TestCase):
    """DT-DEMO-01 twin data + BOM example: EBOM → MBOM → requirements → MRP → purchase, design change, re-run."""

    def setUp(self):
        import seed_bom_demo
        import seed_twin_workflow
        self.seed_bom = seed_bom_demo
        self.addCleanup(setattr, app, 'DATABASE', app.DATABASE)
        self.addCleanup(setattr, app, 'DATABASE_URL', app.DATABASE_URL)
        url = os.environ.get('SHIPYARD_TEST_POSTGRES_URL')
        if url:
            schema = 'test_' + uuid4().hex
            with closing(storage.Postgres(url)) as db, db:
                db.execute(f'CREATE SCHEMA {schema}')
            self.addCleanup(ShipYardTest.drop_schema, url, schema)
            app.DATABASE_URL = storage.psycopg.conninfo.make_conninfo(url, options=f'-c search_path={schema}')
        else:
            directory = tempfile.TemporaryDirectory()
            self.addCleanup(directory.cleanup)
            app.DATABASE, app.DATABASE_URL = Path(directory.name) / 'bom.sqlite3', None
        with closing(app.connect()) as db, db:
            storage.init_schema(db, app.SCHEMA)
            seed_twin_workflow.seed(db)
            self.added = seed_bom_demo.seed(db)

    def write(self, action, **values):
        with closing(app.connect()) as db, db:
            return app.write(db, action, values)

    def rejects(self, action, message, **values):
        with self.assertRaises(ValueError) as caught:
            self.write(action, **values)
        self.assertIn(message, str(caught.exception))

    def read(self, table):
        with closing(app.connect()) as db:
            return app.read(db, table)

    def count(self, table):
        with closing(app.connect()) as db:
            return db.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0]

    def test_seed_flow_history_shortage_and_rerun(self):
        self.assertEqual(self.added, 3)
        mrp = {row['material']: row for row in self.read('mrp') if row['material'].startswith('DTW')}
        self.assertEqual(sorted(mrp), ['DTW-BOLT-M20', 'DTW-MHC-600', 'DTW-VLV-150'])  # B01 history makes no demand
        self.assertEqual(mrp['DTW-MHC-600']['shortage'], 4)
        self.assertEqual(mrp['DTW-MHC-600']['source'], 'MBOM R01 · 최초 소요')
        trace = [row for row in self.read('bom_trace') if row['block'].endswith('B01')]
        self.assertEqual({row['trace_status'] for row in trace}, {'완료 공정 이력'})
        plate = next(row for row in trace if row['item_no'] == 'S110')
        self.assertEqual((plate['input_qty'], plate['unit'], plate['design_unit']), (2, '장', 'EA'))
        self.assertIn('수율 84%', plate['basis'])
        self.assertEqual([row['item_name'] for row in trace if row['input_type'] == '용접재'], ['(설계 대응 없음)'])
        links = {row['block'][-3:]: row['link_type'] for row in self.read('order_boms')}
        self.assertEqual(links, {'B01': '완료 작업 이력 연결', 'B05': '적용'})
        readiness = {row['id'][-3:]: row['readiness'] for row in self.read('block_readiness')}
        self.assertEqual(readiness['B01'], '탑재 완료')
        before = {table: self.count(table) for table in ('requirements', 'order_boms', 'bom_adjustments', 'plans',
                                                         'activities', 'moves', 'telemetry', 'location_geometry')}
        with closing(app.connect()) as db, db:
            self.assertEqual(self.seed_bom.seed(db), 0)
        self.assertEqual(before, {table: self.count(table) for table in before})

    def test_design_change_preserves_documents(self):
        need = next(row for row in self.read('mrp') if row['material'] == 'DTW-MHC-600')
        request = self.write('purchase_requests', id='', requirement=need['id'], qty=need['shortage'])
        b05 = 'DT-DEMO-01-B05'
        old_ebom = next(row for row in self.read('ebom_revisions') if row['block'] == b05)
        drawing = self.write('drawing_revisions', id='', drawing_no=f'{b05}-OFT', block=b05, revision='R02',
                             issued_day=date.today().isoformat(), change_type='설계 변경', description='맨홀 커버 추가')
        self.assertTrue(next(row for row in self.read('ebom_revisions') if row['id'] == old_ebom['id'])['drawing_check']
                        .endswith('EBOM 개정 필요'))
        self.rejects('ebom_items', '발행본은 고칠 수 없으니', id='', ebom=old_ebom['id'], item_no='O120', parent_no='',
                     item_level='부품', name='x', spec='', unit='EA', design_qty=8, supply='구매', material='DTW-MHC-600',
                     net_weight_kg='')
        ebom = self.write('ebom_revisions', id='', drawing_revision=drawing, base_ebom=old_ebom['id'], description='R02')
        self.write('ebom_items', id='', ebom=ebom, item_no='O120', parent_no='', item_level='부품', name='맨홀 커버 600×400',
                   spec='', unit='EA', design_qty=8, supply='구매', material='DTW-MHC-600', net_weight_kg='')
        self.write('ebom_items', id='', ebom=ebom, item_no='O110', parent_no='O100', item_level='부품',
                   name='버터플라이 밸브 DN150 PN10', spec='', unit='EA', design_qty=3, supply='구매',
                   material='DTW-VLV-150', net_weight_kg='')
        self.write('ebom_publish', id=ebom)
        old_mbom = next(row for row in self.read('mbom_revisions') if row['block'] == b05)
        self.assertIn('더 새 EBOM', old_mbom['publish_check'])
        mbom = self.write('mbom_revisions', id='', ebom=ebom, revision='R02', base_mbom=old_mbom['id'], description='')
        self.assertIn('설계 수량 8', next(row for row in self.read('mbom_revisions') if row['id'] == mbom)['publish_check'])
        op = next(row['id'] for row in self.read('mbom_ops') if row['mbom'] == mbom)
        for material, qty in (('DTW-MHC-600', 8), ('DTW-VLV-150', 3)):
            self.write('mbom_inputs', id='', op=op, input_type='구매품', material=material, qty=qty, basis='')
        changes = {row['material']: row for row in self.read('mbom_changes') if row['mbom'] == mbom}
        self.assertEqual((changes['DTW-MHC-600']['change'], changes['DTW-MHC-600']['delta']), ('추가 소요', 2))
        self.assertEqual((changes['DTW-VLV-150']['change'], changes['DTW-VLV-150']['delta']), ('수량 감소', -1))
        self.assertEqual(changes['DTW-BOLT-M20']['change'], '변경 없음')
        self.write('mbom_publish', id=mbom)
        self.write('mbom_publish', id=mbom)  # same revision again: nothing duplicated
        self.rejects('mbom_publish', '이전 리비전은 재처리 불가', id=old_mbom['id'])
        reqs = {row['id']: row for row in self.read('requirements') if row['material'] == 'DTW-MHC-600'}
        self.assertEqual(sorted(row['qty'] for row in reqs.values()), [2, 6])
        self.assertEqual(reqs[need['id']]['qty'], 6)  # the requirement behind the purchase request is untouched
        self.assertEqual(next(row for row in self.read('purchase_requests') if row['id'] == request)['qty'], 4)
        self.assertEqual([row['qty'] for row in self.read('requirements') if row['material'] == 'DTW-VLV-150'], [3])
        self.assertEqual(self.count('bom_adjustments'), 0)
        self.assertEqual({row['source'] for row in self.read('requirements') if row['material'] == 'DTW-MHC-600'},
                         {'MBOM R01 · 최초 소요', 'MBOM R02 · 추가 소요'})
        # Lowering the purchased quantity below what is already requested is flagged, not written over.
        drawing3 = self.write('drawing_revisions', id='', drawing_no=f'{b05}-OFT', block=b05, revision='R03',
                              issued_day=date.today().isoformat(), change_type='설계 변경', description='커버 축소')
        ebom3 = self.write('ebom_revisions', id='', drawing_revision=drawing3, base_ebom=ebom, description='')
        self.write('ebom_items', id='', ebom=ebom3, item_no='O120', parent_no='', item_level='부품', name='맨홀 커버',
                   spec='', unit='EA', design_qty=4, supply='구매', material='DTW-MHC-600', net_weight_kg='')
        self.write('ebom_publish', id=ebom3)
        mbom3 = self.write('mbom_revisions', id='', ebom=ebom3, revision='R03', base_mbom=mbom, description='')
        op3 = next(o['id'] for o in self.read('mbom_ops') if o['mbom'] == mbom3)
        self.write('mbom_inputs', id='', op=op3, input_type='구매품', material='DTW-MHC-600', qty=4, basis='')
        self.write('mbom_publish', id=mbom3)
        self.write('mbom_publish', id=mbom3)
        adjustments = self.read('bom_adjustments')
        self.assertEqual([(row['material'], row['qty_delta']) for row in adjustments], [('DTW-MHC-600', -2)])
        self.assertEqual(sorted(row['qty'] for row in self.read('requirements') if row['material'] == 'DTW-MHC-600'), [6])

    def test_completed_work_and_validation_rules(self):
        b01 = 'DT-DEMO-01-B01'
        fab = next(row['id'] for row in self.read('activities') if row['block'] == b01 and row['stage'] == '가공·조립')
        self.rejects('requirements', '완료한 공정에는 자재 소요', id='', activity=fab, material='DTW-FCW-12', qty=5,
                     need_day=date.today().isoformat())
        b05_ebom = next(row['id'] for row in self.read('ebom_revisions') if row['block'].endswith('B05'))
        mbom = self.write('mbom_revisions', id='', ebom=b05_ebom, revision='R09', base_mbom='', description='검증')
        pe = next(row['id'] for row in self.read('activities') if row['block'].endswith('B05') and row['stage'] == '선탑재·탑재')
        self.rejects('mbom_ops', '블록의 공정을 선택하세요', id='', mbom=mbom, seq=10, process='도장', activity=fab)
        op = self.write('mbom_ops', id='', mbom=mbom, seq=30, process='도장', activity=pe)
        items = {row['item_no']: row['id'] for row in self.read('ebom_items') if row['ebom'] == b05_ebom}
        bolt = self.write('mbom_inputs', id='', op=op, input_type='소모품', material='DTW-BOLT-M20', qty=8, basis='')
        self.rejects('mbom_links', '설계 대응 없이', id='', input=bolt, ebom_item=items['O120'])
        plate = self.write('mbom_inputs', id='', op=op, input_type='원판', material='DTW-AH36-12', qty=1, basis='근거')
        self.rejects('mbom_links', '구매 품목이라 원판', id='', input=plate, ebom_item=items['O120'])
        cover = self.write('mbom_inputs', id='', op=op, input_type='구매품', material='DTW-VLV-150', qty=6, basis='')
        self.rejects('mbom_links', '자재 DTW-MHC-600와 투입품 자재 DTW-VLV-150', id='', input=cover, ebom_item=items['O120'])
        self.rejects('ebom_items', '단위', id='', ebom=self.write('ebom_revisions', id='', drawing_revision=self.write(
            'drawing_revisions', id='', drawing_no='DT-DEMO-01-B06-OFT', block='DT-DEMO-01-B06', revision='R01',
            issued_day=date.today().isoformat(), change_type='최초 출도', description='의장도'), base_ebom='', description=''),
            item_no='X1', parent_no='', item_level='부품', name='밸브', spec='', unit='kg', design_qty=1, supply='구매',
            material='DTW-VLV-150', net_weight_kg='')
        problems = next(row for row in self.read('mbom_revisions') if row['id'] == mbom)['publish_check']
        self.assertIn('공정 미배정 설계품목', problems)
        self.rejects('mbom_publish', '발행할 수 없습니다', id=mbom)
        # A finished B01 process cannot get a new MBOM quantity silently: it becomes an adjustment item.
        b01_ebom = next(row for row in self.read('ebom_revisions') if row['block'] == b01)
        b01_mbom = next(row for row in self.read('mbom_revisions') if row['block'] == b01)
        copy = self.write('mbom_revisions', id='', ebom=b01_ebom['id'], revision='R02', base_mbom=b01_mbom['id'], description='')
        weld = next(row for row in self.read('mbom_inputs') if row['mbom'] == copy and row['input_type'] == '용접재')
        ops = {row['seq']: row['id'] for row in self.read('mbom_ops') if row['mbom'] == copy}
        self.write('mbom_inputs', id='', op=ops[20], input_type='용접재', material='DTW-FCW-12', qty=60, basis=weld['basis'])
        before = self.count('requirements')
        self.write('mbom_publish', id=copy)
        self.assertEqual(self.count('requirements'), before)
        self.assertEqual([(row['adjustment'], row['qty_delta']) for row in self.read('bom_adjustments')],
                         [('조정 필요 (완료 공정)', 8)])
        self.assertEqual(next(row for row in self.read('block_readiness') if row['id'] == b01)['readiness'], '탑재 완료')

    def test_predecessor_and_stage_material_gates(self):
        pe = next(row for row in self.read('activities') if row['block'].endswith('B05') and row['stage'] == '선탑재·탑재')
        order = next(row for row in self.read('orders') if row['activity'] == pe['id'])
        self.assertEqual(order['mbom'], next(row['id'] for row in self.read('mbom_revisions') if row['block'].endswith('B05')))
        blocked = next(row for row in self.read('activities') if row['block'].endswith('B07') and row['stage'] == '가공·조립')
        prior = next(row for row in self.read('activities') if row['block'].endswith('B07') and row['stage'] == '설계·구매')
        self.write('operations', activity=prior['id'], center='DTW-WC-PE', duration=8, predecessor='', earliest=prior['baseline'])
        self.write('operations', activity=blocked['id'], center='DTW-WC-PE', duration=8, predecessor=prior['id'],
                   earliest=blocked['baseline'])
        work = next(row for row in self.read('orders') if row['activity'] == blocked['id'])
        self.rejects('results', '선행 공정', id=work['id'], actual=date.today().isoformat(), result='완료',
                     inspection='합격', reason='')
        # A block assembly stage mapped in the MBOM cannot finish before its inputs are issued.
        stage = self.write('block_stages', id='', block='DT-DEMO-01-B05', assembly_stage='선행탑재(PE)', center='DTW-WC-PE',
                           planned_mh=40, baseline=pe['baseline'], forecast=pe['forecast'])
        self.rejects('work_logs', 'MBOM 투입품', id='', block_stage=stage, work_day=date.today().isoformat(), manhours=8,
                     progress_pct=100, contractor='', note='')

if __name__ == '__main__':
    unittest.main()
