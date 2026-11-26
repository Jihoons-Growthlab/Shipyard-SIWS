"""Export the yard-wide twin snapshot from the ShipYard-ERP API: every hull placed on a dock of this yard, all of
their blocks (erection timeline), block stocking plans and the yard facilities.

    python3 pipeline/export_yard.py [--facilities data/yard.jsonl]

The Unity app reads the same ERP routes at startup (YardDataSource); this file is what it falls back to when the
ERP cannot be reached, so its fields are the ERP rows unchanged, one array per route:
hulls, vessel_details, hull_geometry, hull_placements, erection_timeline, block_stock_plans, location_geo, yard_sites,
dock_bookings (which of several hulls placed on a dock is in it on the plan day).
Writes data/twin/yard.snapshot.json and copies it plus the block models (Models/<model_asset>.bytes) into Unity
Resources. --facilities limits facilities to the yard plan (other demo locations in the ERP are left out); hulls
whose placement dock is not among them are left out with their blocks.
"""

import argparse
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path

from erp_client import base_url, login, request
from paths import DATA, MODELS, TWIN, UNITY_RESOURCES

ROUTES = ('hulls', 'vessel_details', 'hull_geometry', 'hull_placements', 'erection_timeline', 'block_stock_plans',
          'location_geo', 'yard_sites', 'dock_bookings')
# Yard planning read by the twin when the ERP grants it (rail cranes, their boom data and load charts, crane jobs,
# actual moves); a route the account cannot read (403) or an older ERP without it (404) is left out of the snapshot.
OPTIONAL_ROUTES = ('crane_assignments', 'moves', 'crane_rails', 'crane_kinematics', 'crane_load_charts',
                   'shift_points', 'berth_assignments', 'quay_shifts', 'hull_overview', 'block_stages', 'center_locations', 'equipment_specs')
# Assembly stages: only what the yard calendar needs (shop, stage and its days), to keep the shipped snapshot small.
STAGE_FIELDS = ('block', 'assembly_stage', 'center', 'baseline', 'forecast', 'actual', 'progress_pct')


def yard_ids(path):
    ids = set()
    for line in Path(path).read_text(encoding='utf-8').splitlines():
        if line.strip():
            record = json.loads(line)
            if record['action'] == 'locations':
                ids.add(record['data']['id'])
    return ids


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--facilities', type=Path, default=DATA / 'yard.jsonl', help='야드 시설 목록 (generate_yard.py 출력)')
    parser.add_argument('--models', type=Path, default=MODELS)
    parser.add_argument('-o', '--output', type=Path, default=TWIN / 'yard.snapshot.json')
    parser.add_argument('--no-unity', action='store_true')
    args = parser.parse_args()
    base = base_url(parser)
    token = login(base, parser)
    rows = {route: request(base, route, token=token) for route in ROUTES}
    for route in OPTIONAL_ROUTES:
        try:
            rows[route] = request(base, route, token=token)
        except RuntimeError as error:
            print(f'{route}: 건너뜀 ({error})')

    keep = yard_ids(args.facilities) if args.facilities and args.facilities.is_file() else None
    if keep:
        rows['location_geo'] = [f for f in rows['location_geo'] if f['location'] in keep]
    docks = {f['location'] for f in rows['location_geo'] if f['kind'] == '도크'}
    hulls = {p['hull'] for p in rows['hull_placements'] if p['dock'] in docks}
    hulls &= {b['hull'] for b in rows['erection_timeline']}
    for route in ('hulls',):
        rows[route] = [r for r in rows[route] if r['id'] in hulls]
    for route in ('vessel_details', 'hull_geometry', 'hull_placements', 'erection_timeline', 'block_stock_plans',
                  'dock_bookings', 'berth_assignments', 'quay_shifts', 'hull_overview'):
        if route in rows:
            rows[route] = [r for r in rows[route] if r['hull'] in hulls]
    if 'block_stages' in rows:
        blocks = {b['block'] for b in rows['erection_timeline']}
        rows['block_stages'] = [{k: r.get(k) for k in STAGE_FIELDS} for r in rows['block_stages'] if r['block'] in blocks]
    snapshot = {'format': 'shipyard-yard/1', 'exported_at': datetime.now(timezone.utc).isoformat(timespec='seconds'),
                'source': base.split('//', 1)[-1].split('/', 1)[0], **rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(snapshot, ensure_ascii=False), encoding='utf-8')
    print(f'{args.output}: 호선 {len(hulls)}척 ({", ".join(sorted(hulls))}), 블록 {len(rows["erection_timeline"])}개, '
          f'적치 계획 {len(rows["block_stock_plans"])}건, 시설 {len(rows["location_geo"])}곳')
    if args.no_unity:
        return
    UNITY_RESOURCES.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(args.output, UNITY_RESOURCES / 'yard.snapshot.json')
    copied = missing = 0
    for block in rows['erection_timeline']:
        asset = block.get('model_asset')
        if not asset:
            continue
        source = args.models / asset
        if source.is_file():
            target = UNITY_RESOURCES / 'Models' / (asset + '.bytes')
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            copied += 1
        else:
            missing += 1
    print(f'{UNITY_RESOURCES}: yard.snapshot.json, 모델 {copied}개 복사' + (f', 없음 {missing}개' if missing else ''))


if __name__ == '__main__':
    main()
