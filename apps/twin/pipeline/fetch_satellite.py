"""Download VWorld satellite tiles around the exported yard site and stitch them (with Blender) into one image
whose water is transparent, plus a terrain height grid for the Unity ground.

    python3 pipeline/fetch_satellite.py [--hull SN2040] [--zoom 16] [--tiles 12]

Key: VWORLD_API_KEY from the environment, else read (only that line) from ../erp/.env. The key is used
in request URLs only and never written to any output. Centre: the yard plan centre of data/twin/<hull>.twin.json
(site lat/lon + facility footprints). Heights: Terrarium elevation tiles (AWS open data, Mapzen/SRTM-derived,
about 30 m, with bathymetry) for the same area; the imagery's water mask sets the coastline (sea below 0 m,
land at least 0.5 m). Outputs data/satellite/satellite.jpg, terrain.bytes (float32 metres, grid × grid,
row 0 = north) and satellite.json (Web Mercator tile origin, zoom, size, grid), copied to
unity/.../Resources/ShipyardTwin/Satellite/. Imagery: VWorld (MOLIT); elevation: Mapzen Terrain Tiles (AWS).
"""

import argparse
import json
import math
import os
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path

from paths import DATA, ROOT, TWIN, UNITY_RESOURCES

R = 6378137.0


def vworld_key():
    key = os.environ.get('VWORLD_API_KEY', '').strip()
    if key:
        return key
    env = ROOT.parent / 'erp' / '.env'
    if env.is_file():
        for line in env.read_text(encoding='utf-8').splitlines():
            name, _, value = line.strip().partition('=')
            if name == 'VWORLD_API_KEY':
                return value.strip().strip('"\'')
    raise SystemExit('VWORLD_API_KEY가 없습니다. 환경변수로 주거나 apps/erp/.env에 넣으세요.')


def yard_to_geo(site, x, y):
    b = math.radians(site['x_axis_bearing_deg'])
    east, north = x * math.sin(b) - y * math.cos(b), x * math.cos(b) + y * math.sin(b)
    return site['lat'] + math.degrees(north / R), site['lon'] + math.degrees(east / (R * math.cos(math.radians(site['lat']))))


def tile_of(lat, lon, zoom):
    n = 2 ** zoom
    x = (lon + 180) / 360 * n
    y = (1 - math.log(math.tan(math.radians(lat)) + 1 / math.cos(math.radians(lat))) / math.pi) / 2 * n
    return x, y


def download(key, zoom, x0, y0, n, folder):
    folder.mkdir(parents=True, exist_ok=True)
    fetched = 0
    for row in range(n):
        for col in range(n):
            path = folder / f'{zoom}_{y0 + row}_{x0 + col}.jpeg'
            if path.is_file() and path.stat().st_size > 0:
                continue
            url = f'https://api.vworld.kr/req/wmts/1.0.0/{key}/Satellite/{zoom}/{y0 + row}/{x0 + col}.jpeg'
            try:
                with urllib.request.urlopen(url, timeout=20) as response:
                    path.write_bytes(response.read())
            except OSError as exc:  # do not echo the URL: it contains the key
                raise SystemExit(f'타일 {zoom}/{y0 + row}/{x0 + col} 다운로드 실패: {type(exc).__name__}') from None
            fetched += 1
            time.sleep(0.05)
    return fetched


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--hull', default='SN2040')
    parser.add_argument('--zoom', type=int, default=16, help='16 ≈ 2 m/화소')
    parser.add_argument('--tiles', type=int, default=12, help='가로·세로 타일 수 (12 × 256 = 3072 화소, 약 6 km)')
    parser.add_argument('--grid', type=int, default=384, help='지형 격자 칸 수 (꼭짓점은 +1)')
    parser.add_argument('--inner-zoom', type=int, default=18, help='조선소 주변 상세 영상 zoom (18 ≈ 0.5 m/화소)')
    parser.add_argument('--inner-tiles', type=int, default=12, help='상세 영상 타일 수 (12 → 약 1.5 km 사방)')
    parser.add_argument('--blender', default='/Applications/Blender.app/Contents/MacOS/Blender')
    args = parser.parse_args()
    if not 12 <= args.zoom <= 19 or not 2 <= args.tiles <= 16:
        parser.error('zoom 12~19, tiles 2~16')

    twin = json.loads((TWIN / f'{args.hull}.twin.json').read_text(encoding='utf-8'))
    site = (twin.get('yard') or {}).get('site')
    facilities = [f for f in (twin.get('yard') or {}).get('facilities', []) if f['size_x_m'] > 0]
    if not site or not facilities:
        raise SystemExit('twin 파일에 부지(yard.site)와 시설이 없습니다. export_twin.py를 다시 실행하세요.')
    cx = sum(f['x'] for f in facilities) / len(facilities)
    cy = sum(f['y'] for f in facilities) / len(facilities)
    lat, lon = yard_to_geo(site, cx, cy)
    tx, ty = tile_of(lat, lon, args.zoom)
    x0, y0 = int(tx) - args.tiles // 2, int(ty) - args.tiles // 2

    key = vworld_key()
    out = DATA / 'satellite'
    tiles_dir = out / 'tiles'
    fetched = download(key, args.zoom, x0, y0, args.tiles, tiles_dir)
    # Detail imagery around the yard (about 0.5 m/px): same centre, its own tile grid.
    ix, iy = tile_of(lat, lon, args.inner_zoom)
    ix0, iy0 = int(ix) - args.inner_tiles // 2, int(iy) - args.inner_tiles // 2
    inner_dir = out / 'tiles_inner'
    fetched += download(key, args.inner_zoom, ix0, iy0, args.inner_tiles, inner_dir)
    # Open-sea seeds for the water mask: 300-600 m seaward (+x) of each dock gate.
    seeds = []
    for f in (f for f in facilities if f['kind'] == '도크'):
        for d in (300, 450, 600):
            slat, slon = yard_to_geo(site, f['x'] + f['size_x_m'] / 2 + d, f['y'])
            sx, sy = tile_of(slat, slon, args.zoom)
            seeds.append([round((sx - x0) * 256), round((sy - y0) * 256)])
    # Elevation tiles (Terrarium PNG, public): two zoom levels below the imagery cover the same pixels at 1/4 scale.
    dz = 2
    tz = args.zoom - dz
    ex0, ey0 = x0 >> dz, y0 >> dz
    ex1, ey1 = (x0 + args.tiles - 1) >> dz, (y0 + args.tiles - 1) >> dz
    terrain_dir = out / 'terrain'
    terrain_dir.mkdir(parents=True, exist_ok=True)
    for ty_ in range(ey0, ey1 + 1):
        for tx_ in range(ex0, ex1 + 1):
            path = terrain_dir / f'{tz}_{ty_}_{tx_}.png'
            if not (path.is_file() and path.stat().st_size > 0):
                with urllib.request.urlopen(f'https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{tz}/{tx_}/{ty_}.png', timeout=20) as r:
                    path.write_bytes(r.read())
    meta = {'format': 'shipyard-satellite/1', 'zoom': args.zoom, 'tile_x0': x0, 'tile_y0': y0, 'tiles': args.tiles,
            'size_px': args.tiles * 256, 'attribution': 'VWorld (MOLIT); elevation: Mapzen Terrain Tiles (AWS)',
            'centre_lat': lat, 'centre_lon': lon, 'sea_seeds': seeds, 'grid': args.grid,
            'terrain_zoom': tz, 'terrain_x0': ex0, 'terrain_y0': ey0, 'terrain_tiles_x': ex1 - ex0 + 1, 'terrain_tiles_y': ey1 - ey0 + 1,
            'inner_zoom': args.inner_zoom, 'inner_tile_x0': ix0, 'inner_tile_y0': iy0, 'inner_tiles': args.inner_tiles,
            'inner_size_px': args.inner_tiles * 256}
    (out / 'satellite.json').write_text(json.dumps(meta, indent=1), encoding='utf-8')
    script = ROOT / 'pipeline' / 'blender' / 'stitch_satellite.py'
    result = subprocess.run([args.blender, '-b', '--factory-startup', '-P', str(script), '--', str(tiles_dir), str(out / 'satellite.json'),
                             str(out / 'satellite.jpg'), str(terrain_dir), str(out / 'terrain.bytes'), str(inner_dir),
                             str(out / 'satellite_inner.jpg')], capture_output=True, text=True)
    if result.returncode != 0 or 'stitched' not in result.stdout:
        print(result.stdout[-2000:], result.stderr[-2000:])
        raise SystemExit('Blender 이어붙이기 실패')
    print([line for line in result.stdout.splitlines() if line.startswith('stitched')][0])
    target = UNITY_RESOURCES / 'Satellite'
    target.mkdir(parents=True, exist_ok=True)
    for old in ('satellite.png', 'satellite.png.meta'):
        (target / old).unlink(missing_ok=True)
    shutil.copyfile(out / 'satellite.jpg', target / 'satellite.jpg')
    shutil.copyfile(out / 'satellite.json', target / 'satellite_meta.json')
    shutil.copyfile(out / 'terrain.bytes', target / 'terrain.bytes')
    shutil.copyfile(out / 'satellite_inner.jpg', target / 'satellite_inner.jpg')
    km = args.tiles * 256 * 156543.03 * math.cos(math.radians(lat)) / 2 ** args.zoom / 1000
    print(f'{out}: 타일 {args.tiles}×{args.tiles} (새로 받음 {fetched}), 약 {km:.1f} km 사방 → {target}')


if __name__ == '__main__':
    main()
