"""Shared yard replay (yardsim.py, /api/yard_sim): plan-driven motion, the shared clock, the loop and read-only use."""

import math
import unittest
from contextlib import closing
from datetime import datetime, timedelta
from unittest import mock

import app
import geometry
import seed_yard_cranes
import storage
import test_app
import yardsim

KST = yardsim.KST
_Base = test_app.ShipYardTest


def rows():
    """One hull on a dock with a goliath (no rails registered), a transporter stationed at the PE yard and two blocks:
    B1 moves from a stock yard to the PE yard and is lifted by assignment; B2 has no stocking plan (unplaced)."""
    block = dict(zone='z', weight_t=50, predecessors=[], destination='DK', erected_day=None, model_asset=None,
                 x_min=0, x_max=10, y_min=-4, y_max=4, z_min=0, z_max=3, cog_x=5, cog_y=0, cog_z=1.5)
    return {
        'hull_placements': [{'hull': 'H1', 'dock': 'DK', 'origin_x': 300, 'origin_y': 0, 'origin_z': 0, 'heading_deg': 0}],
        'location_geo': [
            {'location': 'DK', 'kind': '도크', 'name': '도크', 'x': 400, 'y': 0, 'z': 0, 'heading_deg': 0, 'size_x_m': 300, 'size_y_m': 60},
            {'location': 'PE', 'kind': '야드', 'name': 'PE', 'x': 170, 'y': 0, 'z': 0, 'heading_deg': 0, 'size_x_m': 60, 'size_y_m': 40},
            {'location': 'STK', 'kind': '야드', 'name': '적치장', 'x': -100, 'y': 0, 'z': 0, 'heading_deg': 0, 'size_x_m': 60, 'size_y_m': 40}],
        'erection_timeline': [
            {'block': 'H1-B1', 'hull': 'H1', 'step': 1, 'baseline': '2027-03-03', 'forecast': '2027-03-03', **block},
            {'block': 'H1-B2', 'hull': 'H1', 'step': 2, 'baseline': '2027-03-04', 'forecast': '2027-03-04', **block}],
        'block_stock_plans': [
            {'id': 'S1', 'block': 'H1-B1', 'location': 'STK', 'x': -100, 'y': 0, 'heading_deg': 0, 'stock_from': '2027-03-01', 'stock_until': '2027-03-01'},
            {'id': 'S2', 'block': 'H1-B1', 'location': 'PE', 'x': 170, 'y': 0, 'heading_deg': 90, 'stock_from': '2027-03-02', 'stock_until': None}],
        'crane_assignments': [
            {'id': 'CRA-1', 'equipment': 'GL', 'hull': 'H1', 'block': 'H1-B1', 'crane_work': '블록 탑재', 'work_area': 'DK',
             'work_day': '2027-03-03', 'start_hour': 7, 'hours': 2, 'lift_t': 50}],
        'crane_rails': [],
        'equipment_specs': [{'equipment': 'GL', 'equipment_type': '골리앗 크레인', 'capacity_t': 600, 'station': 'DK'},
                            {'equipment': 'TP', 'equipment_type': '트랜스포터', 'capacity_t': 300, 'station': 'PE'}],
        'equipment': [{'id': 'GL', 'name': '골리앗'}, {'id': 'TP', 'name': '트랜스포터'}],
        'moves': [], 'telemetry': [],
    }


def at(text):
    return datetime.fromisoformat(text).replace(tzinfo=KST)


def find(items, key):
    return next(item for item in items if item['id'] == key)


class ReplayTest(unittest.TestCase):
    def setUp(self):
        self.scenario = yardsim.build('H1', rows())

    def state(self, moment):
        blocks, equipment, _ = yardsim.snapshot(self.scenario, at(moment))
        return find(blocks, 'H1-B1'), find(equipment, 'GL'), find(equipment, 'TP'), find(blocks, 'H1-B2')

    def test_stock_transport_lift_land_in_order(self):
        self.assertEqual(self.scenario['start'], at('2027-03-01T00:00'))
        self.assertEqual(self.scenario['end'], at('2027-03-05T00:00'), 'Through the last plan day.')
        block, crane, vehicle, unplaced = self.state('2027-03-01T12:00')
        self.assertEqual((block['state'], block['location'], block['x']), ('적치', 'STK', -100))
        self.assertEqual(unplaced['state'], '미배치', 'No stocking plan: no coordinates, listed as unplaced.')
        self.assertEqual((crane['state'], vehicle['state']), ('대기', '대기'))
        self.assertEqual(crane['rail']['source'], 'estimated', 'No ERP rail: dock axis, flagged as estimated.')

        # Transport on the day the next stay starts, from 08:00: approach, load, carry, unload (estimated).
        block, _, vehicle, _ = self.state('2027-03-02T08:00:30')
        self.assertEqual((block['state'], vehicle['state'], vehicle['block']), ('운반 대기', '접근', 'H1-B1'))
        leg = self.scenario['blocks'][0]['legs'][0]
        middle = leg['carry'] + (leg['unload'] - leg['carry']) / 2
        blocks, equipment, _ = yardsim.snapshot(self.scenario, middle)
        block, vehicle = find(blocks, 'H1-B1'), find(equipment, 'TP')
        self.assertEqual((block['state'], block['source'], block['vehicle']), ('운반 중', 'estimated', 'TP'))
        self.assertAlmostEqual(block['x'], 35, delta=1, msg='Half way along the straight route.')
        self.assertEqual((block['x'], block['y']), (vehicle['x'], vehicle['y']), 'The block rides on the vehicle.')
        self.assertIn('장비 배치 시설 PE', block['basis'])

        block, crane, _, _ = self.state('2027-03-02T12:00')
        self.assertEqual((block['state'], block['location'], block['x'], block['heading_deg']), ('인양 대기', 'PE', 170, 90))

        # Lift window 07:00-09:00 from the ERP crane assignment: travel, hoist, carry, lower.
        block, crane, _, _ = self.state('2027-03-03T07:10')
        self.assertEqual((crane['state'], block['state']), ('주행', '인양 대기'))
        block, crane, _, _ = self.state('2027-03-03T07:30')
        self.assertEqual((crane['state'], block['state'], block['source']), ('인양', '인양', 'plan_sim'))
        self.assertGreater(block['z'], 12)
        block, crane, _, _ = self.state('2027-03-03T08:00')
        self.assertEqual((block['x'], block['y']), (crane['hook']['x'], crane['hook']['y']), 'The block hangs from the hook.')
        block, crane, _, _ = self.state('2027-03-03T08:30')
        self.assertEqual(crane['state'], '착지')
        block, crane, _, _ = self.state('2027-03-04T12:00')
        self.assertEqual((block['state'], block['location']), ('탑재 완료', 'DK'))
        self.assertEqual((block['x'], block['y'], block['z']), (305, 0, 1.5), 'Hull origin + ship cog.')
        self.assertEqual(crane['state'], '대기')

    def test_only_registered_vehicles_carry_blocks(self):
        data = rows()
        # A confirmed move by equipment that is no longer a registered vehicle (e.g. a retired crane) is no evidence.
        data['moves'] = [{'id': 'M1', 'block': 'H1-B1', 'equipment': 'OLD-GC', 'origin': 'STK', 'destination': 'PE',
                          'started_at': '2026-11-01T00:00:00+00:00', 'confirmed_at': '2026-11-01T00:10:00+00:00'}]
        scenario = yardsim.build('H1', data)
        self.assertNotIn('OLD-GC', scenario['equipment'])
        self.assertEqual(scenario['blocks'][0]['legs'][0]['vehicle'], 'TP')

    def test_transport_skips_underrated_equipment(self):
        data = rows()
        data['equipment_specs'][1]['capacity_t'] = 20
        data['equipment_specs'].append({
            'equipment': 'TP-HEAVY', 'equipment_type': '트랜스포터', 'capacity_t': 100, 'station': 'PE',
        })
        data['moves'] = [{
            'id': 'M1', 'block': 'H1-B1', 'equipment': 'TP', 'origin': 'STK', 'destination': 'PE',
            'started_at': '2026-11-01T00:00:00+00:00', 'confirmed_at': '2026-11-01T00:10:00+00:00',
        }]
        scenario = yardsim.build('H1', data)
        self.assertEqual(scenario['blocks'][0]['legs'][0]['vehicle'], 'TP-HEAVY')

    def test_unplaced_and_missing_evidence(self):
        data = rows()
        data['equipment_specs'] = data['equipment_specs'][:1]                        # no transporter at all
        data['erection_timeline'][1]['forecast'] = data['erection_timeline'][1]['baseline'] = None
        scenario = yardsim.build('H1', data)
        self.assertEqual(scenario['unplaced']['blocks'], ['H1-B2'], 'No stay, no lift: unplaced.')
        self.assertEqual(scenario['unplaced']['transports_without_vehicle'], ['H1-B1'])
        blocks, equipment, _ = yardsim.snapshot(scenario, at('2027-03-02T12:00'))
        self.assertEqual(find(blocks, 'H1-B2')['state'], '미배치')
        self.assertEqual(find(blocks, 'H1-B1')['location'], 'PE', 'Without a vehicle the block changes slot, no vehicle drawn.')
        self.assertEqual({e['id'] for e in equipment}, {'GL'})
        with self.assertRaises(ValueError):
            yardsim.build('NONE', rows())

    def test_clock_slows_for_work_and_loops_back(self):
        session = yardsim.Session(self.scenario, days_per_second=4, work_minutes_per_second=20, wall=0.0)
        session.control('play', wall=0.0)
        session.advance(0.25)
        self.assertEqual(session.sim, at('2027-03-02T00:00'), 'Idle: 4 days per second.')
        session.control('seek', '2027-03-03T07:00:00+09:00', wall=0.25)
        session.advance(1.25)
        self.assertEqual(session.sim, at('2027-03-03T07:20'), 'In a lift window: 20 minutes per second.')
        session.control('speed', 2, wall=1.25)
        session.advance(1.75)
        self.assertEqual(session.sim, at('2027-03-03T07:40'))
        session.control('pause', wall=1.75)
        session.advance(9.0)
        self.assertEqual(session.sim, at('2027-03-03T07:40'), 'Paused.')
        session.control('play', wall=9.0)
        version = session.version
        session.advance(9.0 + 3600)
        self.assertGreaterEqual(session.cycle, 1)
        self.assertGreater(session.version, version)
        self.assertLess(session.sim, self.scenario['end'])
        session.control('restart', wall=9.0 + 3600)
        self.assertEqual(session.sim, self.scenario['start'])
        blocks, _, _ = yardsim.snapshot(self.scenario, session.sim)
        self.assertEqual(find(blocks, 'H1-B1')['state'], '적치', 'Reset: the start state again.')
        with self.assertRaises(ValueError):
            session.control('speed', 0.01)


class JibCraneTest(unittest.TestCase):
    """Rail jib cranes: ERP job windows with pick / drop points become travel, slew, luff and hook motion."""

    def setUp(self):
        data = rows()
        data['equipment_specs'].append({'equipment': 'JC', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DK'})
        data['equipment'].append({'id': 'JC', 'name': '지브 크레인'})
        data['crane_rails'] = [{'equipment': 'JC', 'rail_x0': 300, 'rail_y0': 60, 'rail_x1': 500, 'rail_y1': 60,
                                'gauge_m': 10, 'jib_heading_deg': 270}]
        data['crane_kinematics'] = [{'equipment': 'JC', 'boom_m': 60, 'pivot_height_m': 30, 'luff_min_deg': 15,
                                     'luff_max_deg': 82, 'rest_luff_deg': 72, 'staging_offset_m': 14}]
        data['crane_assignments'] += [
            {'id': 'CRA-J1', 'equipment': 'JC', 'hull': 'H1', 'block': None, 'crane_work': '의장품 인양', 'work_area': 'DK',
             'work_day': '2027-03-02', 'start_hour': 10, 'hours': 2, 'lift_t': 12},
            {'id': 'CRA-J2', 'equipment': 'JC', 'hull': 'H1', 'block': None, 'crane_work': '크레인 정비', 'work_area': 'DK',
             'work_day': '2027-03-02', 'start_hour': 14, 'hours': 4, 'lift_t': 0}]
        data['crane_lifts'] = [{'assignment': 'CRA-J1', 'pick_x': 400, 'pick_y': 74, 'pick_z': 2, 'drop_x': 400, 'drop_y': 20,
                                'drop_z': 15, 'point_source': '추정'}]
        self.scenario = yardsim.build('H1', data)

    def crane(self, moment):
        _, equipment, activity = yardsim.snapshot(self.scenario, at(moment))
        return find(equipment, 'JC'), activity

    def test_job_lifts_outfitting_loads_and_waits_without_work(self):
        crane, _ = self.crane('2027-03-02T09:00')
        self.assertEqual((crane['state'], crane['crane_type'], crane['slew_deg'], crane['luff_deg'], crane['load_t']), ('대기', 'jib', 270, 72, 0))
        self.assertEqual(crane['rail_pos'], round(yardsim.home_position('JC'), 3), 'Parked at its home spot, nothing on the hook.')
        self.assertEqual(crane['kinematics'], 'ERP 크레인 붐 제원')
        # 10:00-12:00: travel (6 %), lifts of 12 min (here 8 in 105.6 min, 13.2 min each), slew back (6 %).
        job = self.scenario['equipment']['JC']['jobs'][0]
        self.assertEqual(job['lifts'], 8)
        crane, _ = self.crane('2027-03-02T10:03')
        self.assertEqual(crane['state'], '주행')
        cycle = timedelta(minutes=105.6 / 8)
        first = at('2027-03-02T10:07:12')                                  # first lift starts after 7.2 min of travel

        def during(share):
            blocks, equipment, activity = yardsim.snapshot(self.scenario, first + cycle * share)
            return find(equipment, 'JC'), activity

        states = [during(share)[0]['state'] for share in (0.1, 0.25, 0.4, 0.55, 0.75, 0.85, 0.95)]
        self.assertEqual(states, ['선회', '권하', '인양', '선회', '착지', '권상', '작업 대기'])
        crane, activity = during(0.3199)                                   # hook at the staged load
        self.assertAlmostEqual(crane['hook']['x'], 400, delta=0.5)
        self.assertAlmostEqual(crane['hook']['y'], 74, delta=0.5)
        self.assertAlmostEqual(crane['hook']['z'], 12 + 2, delta=0.3)
        self.assertAlmostEqual(crane['slew_deg'], 90, delta=0.5, msg='Turned behind the rail to the staged load.')
        self.assertIn('JC 권하 · 의장품 인양', activity)
        crane, _ = during(0.7999)                                          # set down on board
        self.assertAlmostEqual(crane['slew_deg'], 270, delta=0.5)
        self.assertAlmostEqual(crane['radius_m'], 40, delta=0.5, msg='Boom angle set for the drop radius.')
        self.assertAlmostEqual(crane['hook']['z'], 12 + 15, delta=0.3)
        self.assertEqual((crane['load_kind'], crane['lift_no'], crane['lifts']), ('의장품', 1, 8))
        crane, _ = during(1.1)
        self.assertEqual((crane['state'], crane['lift_no']), ('선회', 2), 'The next lift turns back from the drop.')
        self.assertAlmostEqual(self.crane('2027-03-02T11:59:59')[0]['slew_deg'], 270, delta=1, msg='Slews back to rest at the end.')
        self.assertAlmostEqual(self.crane('2027-03-02T11:59:59')[0]['rail_pos'], 0.5, delta=0.01, msg='and travels back to its parked spot.')
        crane, _ = self.crane('2027-03-02T15:00')
        self.assertEqual((crane['state'], crane['load_t']), ('정비', 0))
        self.assertEqual(crane['rail_pos'], 0.5, 'Back in the middle of its zone after the job.')

    def test_crane_windows_slow_the_clock_less_than_block_work(self):
        session = yardsim.Session(self.scenario, days_per_second=4, work_minutes_per_second=20, wall=0.0)
        session.control('seek', '2027-03-02T10:00:00+09:00', wall=0.0)
        self.assertEqual(session.work_kind(session.sim), 'crane')
        session.control('play', wall=0.0)
        session.advance(1.0)
        self.assertEqual(session.sim, at('2027-03-02T10:30'), 'Jib crane work: 30 minutes per second.')

    def test_live_activity_of_every_rail_crane(self):
        data = rows()
        data['equipment_specs'].append({'equipment': 'JC', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DK'})
        data['equipment_specs'].append({'equipment': 'JC2', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DK'})
        data['equipment'] += [{'id': 'JC', 'name': 'A'}, {'id': 'JC2', 'name': 'B'}]
        data['crane_rails'] = [{'equipment': e, 'rail_x0': 300, 'rail_y0': y, 'rail_x1': 500, 'rail_y1': y, 'gauge_m': 10,
                                'jib_heading_deg': 270} for e, y in (('JC', 60), ('JC2', -60))]
        data['crane_assignments'].append({'id': 'CRA-J1', 'equipment': 'JC', 'hull': 'OTHER', 'block': None, 'crane_work': '의장품 인양',
                                          'work_area': 'DK', 'work_day': '2027-03-02', 'start_hour': 10, 'hours': 2, 'lift_t': 12})
        live = yardsim.crane_activity(data, at('2027-03-02T10:30'))
        states = {c['id']: (c['state'], c['loaded']) for c in live['cranes']}
        self.assertEqual(states['JC2'], ('대기', False), 'No ERP job: waiting, nothing on the hook.')
        self.assertNotEqual(states['JC'][0], '대기', 'Any hull\'s job counts in the live view.')
        self.assertEqual(live['cranes'][0]['source'], 'estimated', 'No crane_lifts row: derived points.')
        jc2 = next(c for c in live['cranes'] if c['id'] == 'JC2')
        self.assertEqual((jc2['rated_t'], jc2['overload'], jc2['load_chart']), (None, False, []), 'No chart: no rating.')
        self.assertEqual(jc2['danger_radius_m'], round(50 * math.cos(math.radians(15)) + 5, 2), 'Default boom + 5 m.')
        data['crane_load_charts'] = [{'equipment': 'JC2', 'radius_m': r, 'capacity_t': c} for r, c in ((20, 50), (48.3, 20))]
        jc2 = next(c for c in yardsim.crane_activity(data, at('2027-03-02T10:30'))['cranes'] if c['id'] == 'JC2')
        self.assertEqual((jc2['rated_t'], len(jc2['load_chart'])), (50 if jc2['radius_m'] <= 20 else 20, 2))


class ReplayApiTest(unittest.TestCase):
    setUp, bootstrap, login, request, api = _Base.setUp, _Base.bootstrap, _Base.login, _Base.request, _Base.api
    drop_schema = staticmethod(_Base.drop_schema)

    def test_crane_boom_data_and_lift_points_are_validated(self):
        self.api('equipment', {'id': 'JC1', 'name': '지브 크레인'})
        self.api('equipment_specs', {'equipment': 'JC1', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DOCK'})
        boom = {'equipment': 'JC1', 'boom_m': 60, 'pivot_height_m': 30, 'luff_min_deg': 15, 'luff_max_deg': 82,
                'rest_luff_deg': 72, 'staging_offset_m': 14}
        self.api('crane_kinematics', boom, 400)                                        # no rail yet
        self.api('crane_rails', {'equipment': 'JC1', 'rail_x0': 0, 'rail_y0': 0, 'rail_x1': 100, 'rail_y1': 0,
                                 'gauge_m': 10, 'jib_heading_deg': 90})
        self.api('crane_kinematics', {**boom, 'luff_min_deg': 85}, 400)                # min above max
        self.api('crane_kinematics', {**boom, 'rest_luff_deg': 10}, 400)               # parked angle out of range
        self.api('crane_kinematics', boom)
        job = self.api('crane_assignments', {'equipment': 'JC1', 'hull': 'H101', 'block': None, 'crane_work': '의장품 인양',
                                             'work_area': 'DOCK', 'work_day': '2027-01-05', 'start_hour': 9, 'hours': 2,
                                             'lift_t': 10, 'note': None})['id']
        lift = {'assignment': job, 'pick_x': 50, 'pick_y': -14, 'pick_z': 1.5, 'drop_x': 50, 'drop_y': 40, 'drop_z': 10,
                'point_source': '추정'}
        self.api('crane_lifts', {**lift, 'drop_y': 70}, 400)                            # beyond the 58 m reach
        self.api('crane_lifts', {**lift, 'point_source': '기타'}, 400)
        self.api('crane_lifts', lift)
        self.api('crane_lifts', {**lift, 'drop_z': 12})                                 # same assignment: updated
        rows = [row for row in self.api('crane_lifts') if row['assignment'] == job]
        self.assertEqual(len(rows), 1)
        self.assertEqual((rows[0]['drop_z'], rows[0]['equipment']), (12, 'JC1'))
        self.assertEqual(self.api('crane_kinematics')[0]['boom_m'], 60)

    def test_load_chart_limits_lift_by_radius(self):
        self.api('equipment', {'id': 'JC1', 'name': '지브 크레인'})
        self.api('equipment_specs', {'equipment': 'JC1', 'equipment_type': '지브 크레인', 'capacity_t': 50, 'station': 'DOCK'})
        self.api('crane_rails', {'equipment': 'JC1', 'rail_x0': 0, 'rail_y0': 0, 'rail_x1': 100, 'rail_y1': 0,
                                 'gauge_m': 10, 'jib_heading_deg': 90})
        chart = {'equipment': 'JC1', 'radius_m': 20, 'capacity_t': 50}
        self.api('crane_load_charts', chart, 400)                                        # no boom data yet
        self.api('crane_kinematics', {'equipment': 'JC1', 'boom_m': 60, 'pivot_height_m': 30, 'luff_min_deg': 15,
                                      'luff_max_deg': 82, 'rest_luff_deg': 72, 'staging_offset_m': 14})
        self.assertEqual(self.api('crane_kinematics')[0]['danger_margin_m'], 5, 'Empty margin: 5 m.')
        self.api('crane_load_charts', {**chart, 'radius_m': 70}, 400)                    # beyond the 58 m reach
        self.api('crane_load_charts', {**chart, 'capacity_t': 60}, 400)                  # above the crane rating
        self.api('crane_load_charts', chart)
        self.api('crane_load_charts', {**chart, 'radius_m': 40, 'capacity_t': 25})
        self.api('crane_load_charts', {**chart, 'radius_m': 30, 'capacity_t': 20}, 400)  # must fall with the radius
        self.api('crane_load_charts', {**chart, 'radius_m': 57.9, 'capacity_t': 15})
        self.assertEqual([r['radius_m'] for r in self.api('crane_load_charts')], [20, 40, 57.9])
        job = {'equipment': 'JC1', 'hull': 'H101', 'block': None, 'crane_work': '의장품 인양', 'work_area': 'DOCK',
               'work_day': '2027-01-05', 'start_hour': 9, 'hours': 2, 'lift_t': 30, 'note': None}
        job['id'] = self.api('crane_assignments', job)['id']
        lift = {'assignment': job['id'], 'pick_x': 50, 'pick_y': -14, 'pick_z': 1.5, 'drop_x': 50, 'drop_y': 35,
                'drop_z': 10, 'point_source': '추정'}
        self.api('crane_lifts', lift, 400)                                               # 30 t at 35 m: chart 25 t
        self.api('crane_lifts', {**lift, 'drop_y': 18})                                  # 30 t at 18 m: chart 50 t
        self.api('crane_assignments', {**job, 'lift_t': 49})
        self.api('crane_lifts', {**lift, 'drop_y': 35}, 400)
        self.api('crane_assignments', {**job, 'lift_t': 24})
        self.api('crane_lifts', {**lift, 'drop_y': 35})
        self.api('crane_assignments', {**job, 'lift_t': 30}, 400)                        # heavier than the chart there
        self.assertEqual(geometry.chart_capacity([(20, 50), (40, 25)], 41), 0.0)
        self.assertIsNone(geometry.chart_capacity([], 10))

    def test_shared_session_is_read_only(self):
        self.addCleanup(yardsim.close)
        yardsim.close()
        self.assertEqual(self.api('yard_sim'), {'scenario': None})
        counts = {route: len(self.api(route)) for route in ('moves', 'block_stock_plans', 'crane_assignments', 'blocks', 'telemetry')}
        # The seed hull has no placement on a dock: a clear error, nothing changed.
        self.api('yard_sim', {'action': 'open', 'hull': 'H101'}, 400)
        self.api('yard_sim', {'action': 'play'}, 400)
        self.api('location_geometry', {'location': 'DOCK', 'x': 400, 'y': 0, 'z': 0, 'heading_deg': 0,
                                       'size_x_m': 300, 'size_y_m': 60, 'size_z_m': 0})
        self.api('location_geometry', {'location': 'YARD', 'x': 170, 'y': 0, 'z': 0, 'heading_deg': 0,
                                       'size_x_m': 60, 'size_y_m': 40, 'size_z_m': 0})
        self.api('hull_placements', {'hull': 'H101', 'dock': 'DOCK', 'origin_x': 300, 'origin_y': 0, 'origin_z': 0, 'heading_deg': 0})
        self.api('block_geometry', {'block': 'B101', 'x_min': 0, 'x_max': 10, 'y_min': -4, 'y_max': 4, 'z_min': 0, 'z_max': 3,
                                    'cog_x': 5, 'cog_y': 0, 'cog_z': 1.5, 'geometry_source': '추정', 'model_asset': None})
        self.api('block_stock_plans', {'block': 'B101', 'location': 'YARD', 'x': 170, 'y': 0, 'heading_deg': 0,
                                       'stock_from': '2027-01-04', 'stock_until': None, 'note': ''})
        self.api('erection_plans', {'block': 'B101', 'destination': 'DOCK', 'baseline': '2027-01-10', 'forecast': '2027-01-10', 'reason': ''})
        counts = {route: len(self.api(route)) for route in counts}
        timeline = [row for row in self.api('erection_timeline') if row['block'] == 'B101']
        if not timeline or not (timeline[0].get('forecast') or timeline[0].get('baseline')):
            self.skipTest('seed block has no erection day in this schema')
        opened = self.api('yard_sim', {'action': 'open', 'hull': 'H101'}, 200)
        self.assertEqual(opened['scenario']['hull'], 'H101')
        self.assertEqual(opened['clock']['time'], opened['scenario']['start'])
        moved = self.api('yard_sim', {'action': 'seek', 'value': opened['scenario']['end']}, 200)
        self.assertLess(moved['clock']['time'], opened['scenario']['end'])
        self.assertEqual(self.api('yard_sim')['clock']['time'], moved['clock']['time'], 'Every reader sees the same clock.')
        self.api('yard_sim', {'action': 'play'}, 200)
        self.api('yard_sim', {'action': 'restart'}, 200)
        self.assertEqual(self.api('yard_sim', {'action': 'speed', 'value': 0.25}, 200)['clock']['speed'], 0.25)
        self.api('yard_sim', {'action': 'speed', 'value': 33}, 400)
        self.assertEqual({route: len(self.api(route)) for route in counts}, counts, 'Replay writes nothing.')


class FakePostgres(storage.Postgres):
    """A PostgreSQL connection that records statements, commits and rollbacks without a server."""
    def __init__(self):
        self.log = []
        log = self.log

        class Connection:
            def commit(self):
                log.append('COMMIT')

            def rollback(self):
                log.append('ROLLBACK')

        self.connection = Connection()

    def execute(self, sql, parameters=()):
        self.log.append(' '.join(sql.split())[:60])
        return mock.MagicMock()


class SeedYardCranesTest(unittest.TestCase):
    """seed_yard_cranes.py on an isolated fixture: dry runs roll back schema work, ERP charts and plan weights stay."""
    setUp, bootstrap, login, request, api = _Base.setUp, _Base.bootstrap, _Base.login, _Base.request, _Base.api
    drop_schema = staticmethod(_Base.drop_schema)

    def test_dry_run_rolls_back_schema_and_preview_in_one_transaction(self):
        db = FakePostgres()
        seed_yard_cranes.prepare(db, True)
        db.execute("INSERT INTO crane_load_charts VALUES ('preview')")
        seed_yard_cranes.finish(db, True)
        self.assertNotIn('COMMIT', db.log, 'Nothing is committed in a dry run, not even the schema work.')
        self.assertEqual(db.log[-1], 'ROLLBACK')
        self.assertTrue(any(s.startswith('ALTER TABLE crane_kinematics') for s in db.log))
        self.assertLess(db.log.index('SELECT pg_advisory_xact_lock(781935443)'),
                        next(i for i, s in enumerate(db.log) if s.startswith('CREATE TABLE')), 'Locked before the DDL.')

        db = FakePostgres()
        seed_yard_cranes.prepare(db, False)
        db.execute("INSERT INTO crane_load_charts VALUES ('real')")
        seed_yard_cranes.finish(db, False)
        commit = db.log.index('COMMIT')
        self.assertLess(next(i for i, s in enumerate(db.log) if s.startswith('ALTER TABLE')), commit)
        self.assertEqual(db.log[commit + 1], 'SELECT pg_advisory_xact_lock(781935443)', 'Real run: schema, then a write lock.')
        self.assertEqual(db.log[-2:], ["INSERT INTO crane_load_charts VALUES ('real')", 'COMMIT'])
        self.assertNotIn('ROLLBACK', db.log)

    def crane(self, db, add, key):
        add('equipment', id=key, name=key)
        add('equipment_specs', equipment=key, equipment_type='지브 크레인', capacity_t=50, station='DOCK')
        add('crane_rails', equipment=key, rail_x0=0, rail_y0=0, rail_x1=100, rail_y1=0, gauge_m=10, jib_heading_deg=90)
        seed_yard_cranes.assign_kinematics(add, [(key,)])
        return (key, key, ('지브 크레인', 50), 'DOCK', (0, 0, 100, 0), 10.0, 90)

    def test_rerun_keeps_an_existing_load_chart(self):
        with closing(app.connect()) as db, db:
            add = lambda action, **values: app.write(db, action, values)
            own, demo = self.crane(db, add, 'JC-T-1'), self.crane(db, add, 'JC-T-2')
            for radius, rated in ((15, 50), (30, 32), (60, 12)):
                add('crane_load_charts', equipment='JC-T-1', radius_m=radius, capacity_t=rated)
            self.assertEqual(seed_yard_cranes.assign_load_charts(db, add, [own, demo]), (1, 1))
            self.assertEqual(geometry.chart_points(db, 'JC-T-1'), [(15, 50), (30, 32), (60, 12)], 'ERP chart kept whole.')
            first = geometry.chart_points(db, 'JC-T-2')
            self.assertEqual(first, seed_yard_cranes.chart_for('JC', 50))
            db.execute('UPDATE crane_load_charts SET capacity_t=45 WHERE equipment=? AND radius_m=?', ('JC-T-2', first[0][0]))
            self.assertEqual(seed_yard_cranes.assign_load_charts(db, add, [own, demo]), (0, 2), 'Re-run: nothing replaced.')
            self.assertEqual(geometry.chart_points(db, 'JC-T-2')[0], (first[0][0], 45))

    def test_over_chart_lift_is_held_and_planned_weight_kept(self):
        with closing(app.connect()) as db, db:
            add = lambda action, **values: app.write(db, action, values)
            self.crane(db, add, 'JC-T-1')
            for radius, rated in ((20.008, 50), (40, 25), (69, 15)):
                add('crane_load_charts', equipment='JC-T-1', radius_m=radius, capacity_t=rated)
            job = dict(equipment='JC-T-1', hull='H101', block=None, crane_work='의장품 인양', work_area='DOCK',
                       work_day='2027-01-05', hours=2, lift_t=30, note=None)
            ids = [add('crane_assignments', **job, start_hour=h) for h in (7, 10, 13)]
            points = {ids[0]: ([50, -14, 1.5], [50, 18, 0]),          # 18 m: 50 t
                      ids[1]: ([50, -14, 1.5], [50, 35, 0]),          # 35 m: 25 t < 30 t
                      ids[2]: ([50, -14, 1.5], [50, 20.006, 0])}      # 20.006 m ≤ 20.008, stored 20.01 m: 25 t
            fake = lambda db, job, *_: tuple(list(p) for p in points[job['id']])
            with mock.patch.object(seed_yard_cranes, 'lift_points', fake):
                self.assertEqual(seed_yard_cranes.assign_lifts(db, add), (1, 2))
            weights = {r['id']: r['lift_t'] for r in db.execute('SELECT id, lift_t FROM crane_assignments')}
            self.assertEqual([weights[i] for i in ids], [30, 30, 30], 'Planned weights unchanged.')
            lifts = db.execute('SELECT assignment, drop_y FROM crane_lifts').fetchall()
            self.assertEqual([(r['assignment'], r['drop_y']) for r in lifts], [(ids[0], 18)], 'Only the lift within the chart.')


if __name__ == '__main__':
    unittest.main()
