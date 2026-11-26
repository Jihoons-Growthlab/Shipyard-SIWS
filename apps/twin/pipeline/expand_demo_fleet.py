"""Expand the simplified SN2050–SN2059 blocks in an offline Unity yard snapshot."""
import argparse
import json
import subprocess
import sys
from datetime import date, timedelta
from pathlib import Path
from tempfile import TemporaryDirectory

from generate_blocks import build, erection_steps, records
from paths import UNITY_RESOURCES


DOCKS = ('DT-DOCK', 'GY-DOCK2', 'DTW-DOCK', 'FLEET-DK', 'DEMO-A-DK', 'DEMO-B-DK')
QUAYS = ('GY-PIER', 'FLEET-Q', 'DEMO-A-Q', 'DEMO-B-Q')
FACILITY_NAMES = {key: f'{i}도크' for i, key in enumerate(DOCKS, 1)}
FACILITY_NAMES.update({key: f'{letter}안벽' for key, letter in zip(QUAYS, 'JKLM')})
FLEET = {f'SN{i}' for i in range(2050, 2060)}


def expand(snapshot, resources):
    vessels = {row['hull']: row for row in snapshot['vessel_details']}
    geometries = {row['hull']: row for row in snapshot['hull_geometry']}
    placements = {row['hull']: row for row in snapshot['hull_placements']}
    bookings = {row['hull']: row for row in snapshot['dock_bookings'] if row['hull'] in FLEET}
    selected = FLEET & vessels.keys()
    timeline = []
    with TemporaryDirectory() as temporary:
        work = Path(temporary)
        for hull in sorted(selected):
            vessel, geometry, booking = vessels[hull], geometries[hull], bookings[hull]
            dock = placements[hull]['dock']
            if dock != booking['dock']:
                raise ValueError(f'{hull}: 도크 배치와 예약 불일치')
            start, end = (date.fromisoformat(booking[key]) for key in ('start_day', 'end_day'))
            args = argparse.Namespace(
                hull=hull, length=vessel['length_m'], beam=vessel['beam_m'],
                depth=geometry['depth_m'], draft=geometry['draft_m'], frame_spacing=geometry['frame_spacing_m'],
                holds=2 if vessel['length_m'] < 140 else 3, segment_frames=20, deckhouse_tiers=3,
                existing_hull=True, dock=dock, dock_position=(0, 0), erection_start=start, days_per_step=1,
            )
            blocks, links = build(args)
            steps = erection_steps(blocks, links)
            last_step = max(steps.values())
            span = (end - start).days
            if span < last_step - 1:
                raise ValueError(f'{hull}: 도크 예약이 탑재 단계보다 짧습니다.')
            predecessors = {}
            for before, after in links:
                predecessors.setdefault(after, []).append(before)
            block_rows = records(args, blocks, links)
            boxes = {r['data']['block']: r['data'] for r in block_rows if r['action'] == 'block_geometry'}
            for block in blocks:
                key = block['id']
                day = (start + timedelta(days=(steps[key] - 1) * span // (last_step - 1))).isoformat()
                box = {k: v for k, v in boxes[key].items() if k != 'geometry_source'}
                timeline.append({**box, 'hull': hull, 'zone': block['zone'], 'weight_t': block['weight_t'],
                                 'step': steps[key], 'predecessors': sorted(predecessors.get(key, [])),
                                 'destination': dock, 'baseline': day, 'forecast': day, 'erected_day': None})
            if resources:
                source = work / f'{hull}.jsonl'
                source.write_text('\n'.join(json.dumps(r, ensure_ascii=False) for r in
                    [{'action': 'vessel_details', 'data': vessel}, *block_rows]), encoding='utf-8')
                subprocess.run([sys.executable, '-B', str(Path(__file__).with_name('generate_models.py')),
                                str(source), '-o', str(work), '--no-preview'], check=True)
                for obj in sorted((work / hull).glob('*.obj')):
                    target = resources / 'Models' / hull / (obj.name + '.bytes')
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(obj.read_bytes())
    snapshot['erection_timeline'] = sorted(
        [r for r in snapshot['erection_timeline'] if r['hull'] not in selected] + timeline,
        key=lambda r: (r['hull'], r['step'] is None, r['step'] or 0, r['forecast'] or '', r['block']))
    # Stock rows for the legacy placeholder blocks do not describe the generated geometry.
    current_ids = {r['block'] for r in timeline}
    snapshot['block_stock_plans'] = [
        r for r in snapshot['block_stock_plans'] if r['hull'] not in selected or r['block'] in current_ids]
    for location in snapshot['location_geo']:
        location['name'] = FACILITY_NAMES.get(location['location'], location['name'])
    return len(selected), len(timeline)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('-o', '--output', type=Path, required=True)
    parser.add_argument('--models', action='store_true', help='Unity Resources에 블록 모델 생성')
    args = parser.parse_args()
    snapshot = json.loads(args.input.read_text(encoding='utf-8'))
    hulls, blocks = expand(snapshot, UNITY_RESOURCES if args.models else None)
    args.output.write_text(json.dumps(snapshot, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f'{hulls}척 · 모의 탑재 블록 {blocks}개')


if __name__ == '__main__':
    main()
