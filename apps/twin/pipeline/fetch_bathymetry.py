"""Download surveyed water depths (KHOA, 150 m grid) for the satellite area and grid them for the Unity depth map.

    python3 pipeline/fetch_bathymetry.py [--refresh]

Source: 공공데이터포털 "해양수산부 국립해양조사원_자연과학용 수심정보 조회" (data.go.kr 15142498,
https://apis.data.go.kr/1192136/waterDepth/GetWaterDepthApiService, fields lat / lot / dpwt). Licence: KOGL Type 1
(attribution). Key: BATHYMETRY_SERVICE_KEY from the environment, else read (only that line) from apps/twin/.env.bathymetry.
The key is used in request URLs only and never printed or written.

Area: the satellite image extent (data/satellite/satellite.json, run fetch_satellite.py first).
Outputs:
- data/bathymetry/points.json  raw points [[lat, lon, depth_m], ...] (cached; --refresh downloads again)
- data/bathymetry/bathymetry.bytes  float32 depth in metres (positive = below the chart datum), (grid+1)² vertices on
  the satellite grid (row 0 = north), NaN where no survey point is within reach
- data/bathymetry/bathymetry.json  meta (grid, counts, range, attribution)
Copied to unity/.../Resources/ShipyardTwin/Satellite/ as bathymetry_grid.bytes and bathymetry_meta.json (distinct
names: Resources.Load cannot tell same-named .bytes and .json apart).
"""

import argparse
import array
import json
import math
import os
import shutil
import time
import urllib.error
import urllib.parse
import urllib.request

from paths import DATA, ROOT, UNITY_RESOURCES

ENDPOINT = 'https://apis.data.go.kr/1192136/waterDepth/GetWaterDepthApiService'
ATTRIBUTION = '국립해양조사원 수심정보(150m 격자)'
PAGE = 300


def service_key():
    key = os.environ.get('BATHYMETRY_SERVICE_KEY', '').strip()
    if key:
        return key
    env = ROOT / '.env.bathymetry'
    if env.is_file():
        for line in env.read_text(encoding='utf-8').splitlines():
            name, _, value = line.strip().partition('=')
            if name == 'BATHYMETRY_SERVICE_KEY':
                return value.strip().strip('"\'')
    raise SystemExit('BATHYMETRY_SERVICE_KEY가 없습니다. 환경변수로 주거나 apps/twin/.env.bathymetry에 넣으세요.')


def pixel_to_geo(zoom, px, py):
    n = 256 * 2 ** zoom
    lon = px / n * 360 - 180
    lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * py / n))))
    return lat, lon


def fetch_page(key, bbox, page):
    # The API rejects more than 5 decimals (INVALID_REQUEST_PARAMETER_ERROR); the bbox is already rounded outward to 4.
    query = dict(type='json', ymin=f'{bbox[0]:.4f}', ymax=f'{bbox[1]:.4f}', xmin=f'{bbox[2]:.4f}', xmax=f'{bbox[3]:.4f}',
                 pageNo=str(page), numOfRows=str(PAGE))
    url = ENDPOINT + '?serviceKey=' + urllib.parse.quote(key, safe='') + '&' + urllib.parse.urlencode(query)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                body = json.loads(response.read().decode('utf-8'))
            break
        except (OSError, ValueError) as exc:  # do not echo the URL: it contains the key
            if attempt == 2:
                raise SystemExit(f'수심 {page}쪽 요청 실패: {type(exc).__name__}') from None
            time.sleep(1 + attempt)
    header = body.get('header') or {}
    if header.get('resultCode') != '00':
        raise SystemExit(f'수심 API 오류: {header.get("resultCode")} {header.get("resultMsg")}')
    items = ((body.get('body') or {}).get('items') or {}).get('item') or []
    if isinstance(items, dict):
        items = [items]
    return [[float(i['lat']), float(i['lot']), float(i['dpwt'])] for i in items], int(body['body'].get('totalCount', 0))


def download(key, bbox):
    points, total = fetch_page(key, bbox, 1)
    pages = math.ceil(total / PAGE)
    for page in range(2, pages + 1):
        points += fetch_page(key, bbox, page)[0]
        time.sleep(0.05)
    if len(points) != total:
        print(f'경고: totalCount {total}, 받은 점 {len(points)}')
    return points


def grid_depths(meta, points):
    """Inverse-distance depth at each satellite grid vertex from the survey points within reach (about 1.5 cells)."""
    grid, size, zoom = meta['grid'], meta['size_px'], meta['zoom']
    lat0 = meta['centre_lat']
    m_lat = 111_320.0
    m_lon = 111_320.0 * math.cos(math.radians(lat0))
    reach = 230.0                                    # metres: survey spacing is about 150 m
    cell = 150.0
    buckets = {}
    for lat, lon, depth in points:
        key = (int(lat * m_lat // cell), int(lon * m_lon // cell))
        buckets.setdefault(key, []).append((lat * m_lat, lon * m_lon, depth))
    out = array.array('f', [float('nan')] * (grid + 1) ** 2)
    filled = 0
    for j in range(grid + 1):
        for i in range(grid + 1):
            lat, lon = pixel_to_geo(zoom, meta['tile_x0'] * 256 + i * size / grid, meta['tile_y0'] * 256 + j * size / grid)
            y, x = lat * m_lat, lon * m_lon
            by, bx = int(y // cell), int(x // cell)
            wsum = dsum = 0.0
            for dy in (-2, -1, 0, 1, 2):
                for dx in (-2, -1, 0, 1, 2):
                    for py, px, depth in buckets.get((by + dy, bx + dx), ()):
                        d = math.hypot(py - y, px - x)
                        if d > reach:
                            continue
                        w = 1.0 / max(d, 1.0) ** 2
                        wsum += w
                        dsum += w * depth
            if wsum > 0:
                out[j * (grid + 1) + i] = dsum / wsum
                filled += 1
    return out, filled


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--refresh', action='store_true', help='캐시된 points.json을 무시하고 다시 받는다')
    args = parser.parse_args()

    sat = DATA / 'satellite' / 'satellite.json'
    if not sat.is_file():
        raise SystemExit('data/satellite/satellite.json이 없습니다. fetch_satellite.py를 먼저 실행하세요.')
    meta = json.loads(sat.read_text(encoding='utf-8'))
    north, west = pixel_to_geo(meta['zoom'], meta['tile_x0'] * 256, meta['tile_y0'] * 256)
    south, east = pixel_to_geo(meta['zoom'], meta['tile_x0'] * 256 + meta['size_px'], meta['tile_y0'] * 256 + meta['size_px'])
    bbox = (math.floor(south * 1e4) / 1e4, math.ceil(north * 1e4) / 1e4, math.floor(west * 1e4) / 1e4, math.ceil(east * 1e4) / 1e4)

    out = DATA / 'bathymetry'
    out.mkdir(parents=True, exist_ok=True)
    cache = out / 'points.json'
    if cache.is_file() and not args.refresh:
        points = json.loads(cache.read_text(encoding='utf-8'))['points']
    else:
        points = download(service_key(), bbox)
        cache.write_text(json.dumps({'source': ENDPOINT, 'attribution': ATTRIBUTION, 'bbox': bbox, 'points': points}),
                         encoding='utf-8')

    depths, filled = grid_depths(meta, points)
    values = [p[2] for p in points]
    info = {'format': 'shipyard-bathymetry/1', 'grid': meta['grid'], 'attribution': ATTRIBUTION,
            'datum': '기준면 미확인',                     # API page does not state it (약최저저조면 or 평균해면)
            'points': len(points), 'filled_vertices': filled,
            'min_m': round(min(values), 2) if values else None, 'max_m': round(max(values), 2) if values else None}
    (out / 'bathymetry.bytes').write_bytes(depths.tobytes())
    (out / 'bathymetry.json').write_text(json.dumps(info, ensure_ascii=False, indent=1), encoding='utf-8')
    target = UNITY_RESOURCES / 'Satellite'
    target.mkdir(parents=True, exist_ok=True)
    for old in ('bathymetry.bytes', 'bathymetry.json'):
        (target / old).unlink(missing_ok=True)
        (target / (old + '.meta')).unlink(missing_ok=True)
    shutil.copyfile(out / 'bathymetry.bytes', target / 'bathymetry_grid.bytes')
    shutil.copyfile(out / 'bathymetry.json', target / 'bathymetry_meta.json')
    print(f'수심 점 {len(points)}개 ({info["min_m"]} ~ {info["max_m"]} m), 격자 꼭짓점 {filled}/{(meta["grid"] + 1) ** 2} → {target}')


if __name__ == '__main__':
    main()
