"""Server-side simulated measurements, independent of browser sessions."""
import csv
import io
import json
import logging
import math
import os
import re
import sys
import time
from pathlib import Path
from datetime import datetime, timedelta
from threading import Lock
from urllib.parse import urlencode
from urllib.request import Request, urlopen

ASSETS = (
    ('GC-01', 'crane', 82, 4), ('GC-02', 'crane', 56, 3),
    ('TP-01', 'heavy', 45, 6), ('FL-01', 'heavy', 3, 5),
    ('VH-01', 'vehicle', 0, 14), ('VH-02', 'vehicle', 0, 12),
)
DEMO_IDS = {asset[0] for asset in ASSETS}
CYCLE = 120   # s, one work cycle of a rail crane (shared/contracts/README.md, 레일 주행 크레인)


# Rail jib cranes follow the ERP crane plan, computed by the ERP's own module (apps/erp/yardsim.py crane_activity):
# working only inside their crane_assignments (outfitting lifts at the crane_lifts points), otherwise parked and idle.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'erp'))
import yardsim  # noqa: E402  (pure standard library; the ERP module is the single source of the crane motion)

CRANE_ROUTES = ('equipment_specs', 'equipment', 'crane_rails', 'crane_assignments', 'crane_kinematics', 'crane_lifts')


class EquipmentFeed:
    def __init__(self):
        self.url = 'http://127.0.0.1:8086'
        self.token = os.getenv('SIWS_INFLUX_TOKEN')
        self._lock = Lock()
        self._enabled = False
        self._plan = None   # ERP rows of the crane plan (CRANE_ROUTES), refreshed with a user's session

    def set_crane_plan(self, rows):
        """ERP crane plan rows (route -> rows, CRANE_ROUTES) read with a user's session; the rail cranes observed."""
        with self._lock:
            self._plan = rows

    def crane_plan(self):
        with self._lock:
            return self._plan

    def collection_status(self):
        with self._lock:
            return self._enabled

    def set_collection(self, enabled):
        with self._lock:
            self._enabled = enabled
            return self._enabled

    def request(self, endpoint, body, content_type, **query):
        if not self.token:
            raise ValueError('SIWS_INFLUX_TOKEN is not configured.')
        request = Request(f'{self.url}/api/v2/{endpoint}?{urlencode(query)}',
                          data=body.encode(), headers={'Authorization': 'Token ' + self.token,
                          'Content-Type': content_type, 'Accept': 'application/csv'})
        with urlopen(request, timeout=3) as response:
            return response.read().decode()

    def write(self, samples):
        lines = []
        for asset_id, kind, second, position, state, speed, load, hook, *jib in samples:
            fields = f'position={float(position)},state={json.dumps(state, ensure_ascii=False)},speed={speed}i,load={load}i'
            if hook is not None:
                fields += f',hook={hook}i'
            if jib:
                fields += f',slew={float(jib[0])},luff={float(jib[1])}'
            lines.append(f'equipment,asset_id={asset_id},kind={kind},source=simulation {fields} {second}')
        if lines:
            self.request('write', '\n'.join(lines), 'text/plain; charset=utf-8',
                         org='siws', bucket='equipment', precision='s')

    def collect(self, second):
        samples = []
        for i, (asset_id, kind, load, speed) in enumerate(ASSETS):
            phase = (second + i * 4) % 36
            position = phase / 16 if phase < 16 else 1 if phase < 20 else (36 - phase) / 16
            moving = phase < 16 or phase >= 20
            state = ('이송' if moving else '하역 대기') if kind == 'crane' else ('주행' if moving else '정차')
            hook = (12 if moving else 5) if kind == 'crane' else None
            samples.append((asset_id, kind, second, position, state, speed if moving else 0, load, hook))
        plan = self.crane_plan()
        if plan:
            now = datetime.fromtimestamp(second, yardsim.KST)
            before = {c['id']: c for c in yardsim.crane_activity(plan, now - timedelta(seconds=1))['cranes']}
            for crane in yardsim.crane_activity(plan, now)['cranes']:
                rail = crane['rail']
                length = math.hypot(rail['x1'] - rail['x0'], rail['y1'] - rail['y0'])
                speed = abs(crane['rail_pos'] - before.get(crane['id'], crane)['rail_pos']) * length * 60   # m/min
                samples.append((crane['id'], 'crane', second, crane['rail_pos'], crane['state'], round(speed),
                                round(crane['load_t']), round(crane['hook']['z'] - yardsim.QUAY_LEVEL),
                                crane['slew_deg'], crane['luff_deg']))
        self.write(samples)

    def run(self, stop):
        failed = False
        while not stop.is_set():
            second = int(time.time())
            try:
                # collect() takes its own rail snapshot and performs network I/O.
                if self.collection_status():
                    self.collect(second)
                if failed:
                    logging.info('Equipment InfluxDB write recovered')
                failed = False
            except (OSError, ValueError):
                if not failed:
                    logging.exception('Equipment InfluxDB write failed')
                failed = True
            stop.wait(max(0.05, second + 1 - time.time()))

    def recent(self, kind):
        if kind not in {asset[1] for asset in ASSETS}:
            raise ValueError('Unknown equipment kind')
        flux = f'''from(bucket: "equipment")
          |> range(start: -7d)
          |> filter(fn: (r) => r._measurement == "equipment" and r.kind == "{kind}")
          |> sort(columns: ["_time"], desc: true)
          |> limit(n: 3)
          |> pivot(rowKey: ["_time"], columnKey: ["_field"], valueColumn: "_value")
          |> keep(columns: ["_time", "asset_id", "kind", "source", "position", "state", "speed", "load", "hook", "slew", "luff"])
        '''
        rows = self.read_samples(flux)
        # Newest first; every asset keeps its latest sample even when many cranes report in the same second.
        rows.sort(key=lambda row: (row['observed_at'], row['asset_id']), reverse=True)
        seen, latest, rest = set(), [], []
        for row in rows:
            (rest if row['asset_id'] in seen else latest).append(row)
            seen.add(row['asset_id'])
        return latest + rest[:max(0, 12 - len(latest))]


    def history(self, asset_id):
        if not re.fullmatch(r'[A-Za-z0-9_-]{1,100}', asset_id):
            raise ValueError('Invalid equipment ID')
        flux = f'''from(bucket: "equipment")
          |> range(start: -10m)
          |> filter(fn: (r) => r._measurement == "equipment" and r.asset_id == "{asset_id}" and r.source == "simulation")
          |> pivot(rowKey: ["_time"], columnKey: ["_field"], valueColumn: "_value")
          |> keep(columns: ["_time", "asset_id", "kind", "source", "position", "state", "speed", "load", "hook", "slew", "luff"])
        '''
        return sorted(self.read_samples(flux), key=lambda row: row['observed_at'])

    def read_samples(self, flux):
        data = self.request('query', flux, 'application/vnd.flux', org='siws')
        rows = []
        header = None
        for cells in csv.reader(io.StringIO(data)):
            if not cells or cells[0].startswith('#'):
                continue
            if '_time' in cells:
                header = cells
                continue
            if header is None:
                continue
            row = dict(zip(header, cells))
            if not row.get('asset_id') or not row.get('position'):
                continue
            row['observed_at'] = row.pop('_time')
            row['position'] = float(row['position'])
            for field in ('speed', 'load', 'hook'):
                row[field] = int(row[field]) if row.get(field) else None
            for field in ('slew', 'luff'):
                row[field] = float(row[field]) if row.get(field) else None
            # Stored demo speeds are km/h. Rail simulation has historically stored m/min.
            if (row.get('source') == 'simulation' and row.get('kind') == 'crane' and
                    row['asset_id'] not in DEMO_IDS and row['speed'] is not None):
                row['speed'] = round(row['speed'] * 0.06, 2)
            rows.append({key: row.get(key) for key in ('observed_at', 'asset_id', 'kind', 'source',
                                                  'position', 'state', 'speed', 'load', 'hook', 'slew', 'luff')})
        return rows
