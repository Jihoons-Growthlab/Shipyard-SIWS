"""Generate block stocking plans (ERP block_stock_plans) for hulls from their block files: where each finished block
waits in the yard before its erection lift. Illustrative plans, not ERP production data.

    python3 pipeline/generate_stock_plans.py data/SN2040-blocks.jsonl data/SN2046-blocks.jsonl
    python3 ../../tools/migration/migrate.py data/stock_plans.jsonl --apply

Rules (one crane per dock lifts from the PE yard at the dock head, see GantryCraneView):
- Staging: a block waits in its dock's PE yard (the yard at the dock head) for --pe-days before its erection
  forecast, until the lift day.
- Storage: before that it waits --store-days in a storage yard (block stockyard, outfitting stock area), away from the
  goliath rails; the twin moves it to the PE yard by transporter on the staging day.
- Slots: block footprints (ship x × ship y plus --clearance) are packed on a --grid metre grid inside each yard, with
  no overlap in space and time; a block turns 90° when that is the only way it fits. When no slot is free the
  stay is skipped, and a block with no stay at all stays unplaced (the twin shows it as 미배치).
- Blocks with no erection forecast get no plan.
Ids are STK-<block>-<n>, so uploading again updates the same rows.
"""

import argparse
import json
import math
from datetime import date, timedelta
from pathlib import Path

from generate_yard import FACILITIES
from paths import DATA


def load(path):
    rows = {}
    for line in Path(path).read_text(encoding='utf-8').splitlines():
        if line.strip():
            record = json.loads(line)
            rows.setdefault(record['action'], []).append(record['data'])
    return rows


class Yard:
    """A yard footprint with time-stamped rectangles already placed."""

    def __init__(self, facility, grid):
        self.id, _, self.name, self.x, self.y, self.sx, self.sy, _ = facility
        self.grid = grid
        self.placed = []   # (x0, x1, y0, y1, day0, day1)

    def place(self, length, width, day0, day1):
        """Free slot centre and heading for a length × width footprint over [day0, day1], or None."""
        for heading, (lx, ly) in ((0, (length, width)), (90, (width, length))):
            if lx > self.sx or ly > self.sy:
                continue
            x_lo, y_lo = self.x - self.sx / 2 + lx / 2, self.y - self.sy / 2 + ly / 2
            nx, ny = int((self.sx - lx) // self.grid) + 1, int((self.sy - ly) // self.grid) + 1
            for j in range(ny):
                for i in range(nx):
                    cx, cy = x_lo + i * self.grid, y_lo + j * self.grid
                    box = (cx - lx / 2, cx + lx / 2, cy - ly / 2, cy + ly / 2)
                    if all(day1 < p[4] or p[5] < day0 or box[1] <= p[0] or p[1] <= box[0] or box[3] <= p[2] or p[3] <= box[2]
                           for p in self.placed):
                        self.placed.append((*box, day0, day1))
                        return round(cx, 2), round(cy, 2), heading
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('blocks', nargs='+', type=Path, help='generate_blocks.py 출력 (호선별 JSONL)')
    parser.add_argument('--pe-days', type=int, default=10, help='탑재 전 PE장 대기 일수')
    parser.add_argument('--store-days', type=int, default=30, help='PE장 대기 전 적치장 보관 일수')
    parser.add_argument('--clearance', type=float, default=4.0, help='블록 사이 통로 (m)')
    parser.add_argument('--grid', type=float, default=2.0, help='슬롯 탐색 간격 (m)')
    parser.add_argument('-o', '--output', type=Path, default=DATA / 'stock_plans.jsonl')
    args = parser.parse_args()

    yards = {f[0]: Yard(f, args.grid) for f in FACILITIES if f[1] == '야드'}
    docks = {f[0]: f for f in FACILITIES if f[1] == '도크'}
    out, summary = [], []
    hulls = [load(path) for path in args.blocks]
    # Earliest erections first, across hulls, so the nearer lifts get the best slots.
    jobs = []
    for rows in hulls:
        placement = rows['hull_placements'][0]
        dock = docks[placement['dock']]
        head_x = dock[3] - dock[5] / 2
        # PE yard of this dock: the yard whose centre line is nearest the dock's, inland of the dock head.
        pe = min((y for y in yards.values() if y.x < head_x), key=lambda y: (abs(y.y - dock[4]), head_x - y.x))
        stores = [y for y in yards.values() if y is not pe and not y.id.startswith('GY-PE')]
        geometry = {g['block']: g for g in rows['block_geometry']}
        for plan in rows['erection_plans']:
            if plan.get('forecast'):
                jobs.append((date.fromisoformat(plan['forecast']), plan['block'], geometry[plan['block']], pe, stores,
                             placement['hull']))
    jobs.sort(key=lambda j: (j[0], j[1]))
    counts = {}
    for erect, block, g, pe, stores, hull in jobs:
        length = g['x_max'] - g['x_min'] + args.clearance
        width = g['y_max'] - g['y_min'] + args.clearance
        stays = []
        stage_from, stage_until = erect - timedelta(days=args.pe_days), erect
        staged = pe.place(length, width, stage_from.toordinal(), stage_until.toordinal())
        if staged:
            stays.append((pe.id, staged, stage_from, stage_until))
        store_until = (stage_from if staged else erect) - timedelta(days=1)
        store_from = store_until - timedelta(days=args.store_days)
        for store in stores:
            slot = store.place(length, width, store_from.toordinal(), store_until.toordinal())
            if slot:
                stays.insert(0, (store.id, slot, store_from, store_until))
                break
        if not staged and stays:
            # No room at the dock head: the block waits in storage until the lift.
            location, slot, start, _ = stays[0]
            stays[0] = (location, slot, start, erect)
        for n, (location, (x, y, heading), start, until) in enumerate(stays, 1):
            out.append({'action': 'block_stock_plans', 'data': {
                'id': f'STK-{block}-{n}', 'block': block, 'location': location, 'x': x, 'y': y, 'heading_deg': heading,
                'stock_from': start.isoformat(), 'stock_until': until.isoformat(), 'note': '예시 적치 계획 (가상)'}})
        key = (hull, 'PE장' if staged else ('적치장만' if stays else '미배치'))
        counts[key] = counts.get(key, 0) + 1
    args.output.write_text(''.join(json.dumps(r, ensure_ascii=False) + '\n' for r in out), encoding='utf-8')
    print(f'{args.output}: 적치 계획 {len(out)}건')
    for (hull, kind), n in sorted(counts.items()):
        print(f'  {hull} {kind}: {n}')


if __name__ == '__main__':
    main()
