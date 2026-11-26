import http.client
import json
import os
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from email.utils import formatdate, parsedate_to_datetime
from pathlib import Path
from urllib.parse import urlsplit

from equipment_feed import ASSETS, CRANE_ROUTES, EquipmentFeed

ROOT = Path(__file__).resolve().parent
PORT = 8080
EQUIPMENT_FEED = EquipmentFeed()
EQUIPMENT_KINDS = {asset[1] for asset in ASSETS}
RAILS_READ_AT = -1e9   # monotonic time of the last ERP crane_rails read
BIND_HOSTS = [h.strip() for h in os.getenv('SHIPYARD_BIND_HOSTS', '127.0.0.1').split(',') if h.strip()]
EXTRA_HOSTS = [h.strip().lower() for h in os.getenv('SHIPYARD_ALLOWED_HOSTS', '').split(',') if h.strip()]
ALLOWED_HOSTS = {h.lower().removesuffix('.') for h in BIND_HOSTS + EXTRA_HOSTS}
ERP_URL = os.getenv('SHIPYARD_ERP_URL', 'http://127.0.0.1:8001/')
UNITY_APP = Path(os.getenv('SHIPYARD_UNITY_APP', str(ROOT.parent / 'twin' / 'Builds' / 'ShipyardTwin.app')))
TWIN_URL = os.getenv('SHIPYARD_TWIN_URL', '')
TWIN_WEB = Path(os.getenv('SHIPYARD_TWIN_WEB', str(ROOT.parent / 'twin' / 'Builds' / 'WebGL'))).resolve()
WEB_TYPES = {'.html':'text/html; charset=utf-8', '.js':'application/javascript', '.wasm':'application/wasm',
             '.data':'application/octet-stream', '.json':'application/json', '.css':'text/css', '.png':'image/png',
             '.ico':'image/x-icon', '.svg':'image/svg+xml', '.jpg':'image/jpeg'}
UPSTREAM = urlsplit(ERP_URL)
ASSISTANT_PORT = int(os.getenv('SIWS_ASSISTANT_PORT', '8787'))
DOWNLOAD = os.getenv('SHIPYARD_UNITY_DOWNLOAD_URL', '')
if any(url and urlsplit(url).scheme != 'https' for url in (DOWNLOAD,TWIN_URL)):
    raise ValueError('다운로드 주소는 HTTPS를 사용하세요.')


class Portal(BaseHTTPRequestHandler):
    def send(self, status, body, content_type='application/json'):
        if not isinstance(body, bytes):
            body = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type if content_type.startswith('image/') else content_type + '; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Content-Security-Policy', "frame-ancestors 'none'; base-uri 'none'")
        self.end_headers()
        self.wfile.write(body)

    def allowed_host(self):
        host, _, port = (self.headers.get('Host') or '').lower().rpartition(':')
        return port == str(PORT) and host.removesuffix('.') in ALLOWED_HOSTS

    def twin_web(self):
        relative = urlsplit(self.path).path[len('/twin/'):] or 'index.html'
        try:
            target = (TWIN_WEB / relative).resolve()
        except (OSError, ValueError):
            return self.send(404, {'message':'페이지가 없습니다.'})
        if TWIN_WEB not in target.parents or not target.is_file():
            return self.send(404, {'message':'페이지가 없습니다.'})
        encoding = None
        suffix = target.suffix
        if suffix in ('.gz', '.br'):
            encoding = 'gzip' if suffix == '.gz' else 'br'
            suffix = Path(target.stem).suffix
        stat = target.stat()
        modified = formatdate(int(stat.st_mtime), usegmt=True)
        since = self.headers.get('If-Modified-Since')
        try:
            if since and int(parsedate_to_datetime(since).timestamp()) >= int(stat.st_mtime):
                self.send_response(304)
                self.send_header('Last-Modified', modified)
                self.end_headers()
                return
        except (TypeError, ValueError):
            pass
        self.send_response(200)
        self.send_header('Content-Type', WEB_TYPES.get(suffix, 'application/octet-stream'))
        if encoding:
            self.send_header('Content-Encoding', encoding)
        self.send_header('Content-Length', str(stat.st_size))
        self.send_header('Last-Modified', modified)
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Content-Security-Policy', "frame-ancestors 'none'; base-uri 'none'")
        self.end_headers()
        with target.open('rb') as source:
            while chunk := source.read(1 << 20):
                self.wfile.write(chunk)

    def proxy(self, path, body=None):
        connection_type = http.client.HTTPSConnection if UPSTREAM.scheme == 'https' else http.client.HTTPConnection
        connection = connection_type(UPSTREAM.hostname, UPSTREAM.port, timeout=8)
        headers = {}
        if self.headers.get('Authorization'):
            headers['Authorization'] = self.headers['Authorization']
        if body is not None:
            headers['Content-Type'] = 'application/json'
        try:
            connection.request(self.command, path, body=body, headers=headers)
            response = connection.getresponse()
            payload = response.read()
            self.send(response.status, payload, response.getheader('Content-Type','application/json').split(';')[0])
        except (OSError, http.client.HTTPException):
            self.send(503, {'error':'ERP 서버에 연결할 수 없습니다.'})
        finally:
            connection.close()

    def proxy_assistant(self, path, body=None):
        connection = http.client.HTTPConnection('127.0.0.1', ASSISTANT_PORT, timeout=56 if body is not None else 4)
        headers = {}
        if authorization := self.headers.get('Authorization'):
            headers['Authorization'] = authorization
        if body is not None:
            headers['Content-Type'] = 'application/json'
        try:
            connection.request(self.command, path, body=body, headers=headers)
            response = connection.getresponse()
            return self.send(response.status, response.read(), response.getheader('Content-Type', 'application/json').split(';')[0])
        except (OSError, http.client.HTTPException):
            return self.send(503, {'detail': '로컬 챗봇에 연결할 수 없습니다.'})
        finally:
            connection.close()

    def equipment_account(self):
        authorization = self.headers.get('Authorization')
        if not authorization:
            self.send(401, {'error': 'SIWS에 로그인하세요.'})
            return None
        connection_type = http.client.HTTPSConnection if UPSTREAM.scheme == 'https' else http.client.HTTPConnection
        connection = connection_type(UPSTREAM.hostname, UPSTREAM.port, timeout=5)
        try:
            connection.request('GET', '/api/me', headers={'Authorization': authorization})
            response = connection.getresponse()
            if response.status == 200:
                return json.loads(response.read())
            self.send(401 if response.status in (401, 403) else 503,
                      {'error': 'ERP 세션을 확인할 수 없습니다.'})
        except (OSError, http.client.HTTPException, ValueError):
            self.send(503, {'error': 'ERP 세션을 확인할 수 없습니다.'})
        finally:
            connection.close()
        return None

    def refresh_rail_cranes(self):
        """The ERP crane plan (rail cranes, their boom data, assignments and lift points), read with the caller's
        session at most once a minute, so the collector records every rail crane as the ERP plan moves it; a session
        without those permissions leaves the plan as it is."""
        global RAILS_READ_AT
        if time.monotonic() - RAILS_READ_AT < 60:
            return
        connection_type = http.client.HTTPSConnection if UPSTREAM.scheme == 'https' else http.client.HTTPConnection
        connection = connection_type(UPSTREAM.hostname, UPSTREAM.port, timeout=8)
        try:
            headers = {'Authorization': self.headers.get('Authorization', '')}
            rows = {}
            for route in CRANE_ROUTES:
                connection.request('GET', '/api/' + route, headers=headers)
                response = connection.getresponse()
                body = response.read()
                if response.status != 200:
                    return
                rows[route] = json.loads(body)
            EQUIPMENT_FEED.set_crane_plan(rows)
            RAILS_READ_AT = time.monotonic()
        except (OSError, http.client.HTTPException, ValueError):
            pass
        finally:
            connection.close()

    def equipment_observations(self, kind):
        if kind not in EQUIPMENT_KINDS:
            return self.send(404, {'error': '장비 유형을 확인하세요.'})
        account = self.equipment_account()
        if account is None:
            return
        if kind == 'crane':
            self.refresh_rail_cranes()
        try:
            return self.send(200, {
                'observations': EQUIPMENT_FEED.recent(kind),
                'collecting': EQUIPMENT_FEED.collection_status(),
                'can_manage_collection': account.get('group_id') == 'ADMIN',
            })
        except (OSError, ValueError):
            return self.send(503, {'error': '관측 기록을 조회할 수 없습니다.'})

    def equipment_history(self, asset_id):
        if self.equipment_account() is None:
            return
        try:
            samples = EQUIPMENT_FEED.history(asset_id)
            return self.send(200, {'speed_unit': 'km/h', 'window_seconds': 600, 'observations': samples})
        except ValueError as error:
            status = 400 if str(error) == 'Invalid equipment ID' else 503
            return self.send(status, {'error': '장비 관측 이력을 조회할 수 없습니다.'})
        except OSError:
            return self.send(503, {'error': '장비 관측 이력을 조회할 수 없습니다.'})

    def equipment_collection(self, enabled):
        account = self.equipment_account()
        if account is None:
            return
        if account.get('group_id') != 'ADMIN':
            return self.send(403, {'error': '관리자만 수집을 관리할 수 있습니다.'})
        if enabled:
            self.refresh_rail_cranes()
        return self.send(200, {'collecting': EQUIPMENT_FEED.set_collection(enabled)})

    def do_GET(self):
        if not self.allowed_host():
            return self.send(403, {'message':'지정된 로컬 주소에서 접속하세요.'})
        if self.path in ('/assistant/health', '/assistant/widget.js'):
            return self.proxy_assistant(self.path)
        if self.path == '/':
            return self.send(200, (ROOT / 'index.html').read_bytes(), 'text/html')
        if self.path == '/shipyard-icon.png':
            return self.send(200, (ROOT / 'shipyard-icon.png').read_bytes(), 'image/png')
        if self.path == '/twin-preview.png':
            return self.send(200, (ROOT / 'twin-preview.png').read_bytes(), 'image/png')
        if self.path == '/apps.json':
            return self.send(200, (ROOT / 'apps.json').read_bytes())
        if self.path == '/yard-hero.jpg':
            return self.send(200, (ROOT / 'yard-hero.jpg').read_bytes(), 'image/jpeg')
        if self.path == '/production-control.jpg':
            return self.send(200, (ROOT / 'production-control.jpg').read_bytes(), 'image/jpeg')
        if self.path == '/equipment-monitoring.jpg':
            return self.send(200, (ROOT / 'equipment-monitoring.jpg').read_bytes(), 'image/jpeg')
        if self.path in ('/control', '/control/'):
            return self.send(200, (ROOT / 'control.html').read_bytes(), 'text/html')
        if urlsplit(self.path).path in ('/process-guide', '/process-guide/'):
            return self.send(200, (ROOT / 'process-guide.html').read_bytes(), 'text/html')
        if urlsplit(self.path).path in ('/equipment', '/equipment/'):
            return self.send(200, (ROOT / 'map.html').read_bytes(), 'text/html')
        if self.path in ('/map', '/map/'):
            return self.send(200, (ROOT / 'map.html').read_bytes(), 'text/html')
        if self.path == '/map/vehicle3d.js':
            return self.send(200, (ROOT / 'vehicle3d.js').read_bytes(), 'text/javascript')
        if self.path == '/map/config':
            return self.send(200, {})
        map_assets = {
            '/map/twin-satellite.json': ('twin-satellite.json','application/json'),
            '/map/twin-satellite.jpg': ('twin-satellite.jpg','image/jpeg'),
            '/map/twin-satellite-detail.jpg': ('twin-satellite-detail.jpg','image/jpeg'),
        }
        if self.path in map_assets:
            name, media_type = map_assets[self.path]
            asset = ROOT / 'map-assets' / name
            if not asset.is_file():
                return self.send(404, {'message': '로컬 위성 자료가 없습니다.'})
            return self.send(200, asset.read_bytes(), media_type)
        if self.path in ('/guide', '/guide/', '/docs', '/docs/', '/brand/hoon-icon.png', '/brand/yard-hero.jpg'):
            return self.proxy(self.path)
        if self.path == '/twin':
            self.send_response(301)
            self.send_header('Location', '/twin/')
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        if self.path.startswith('/twin/'):
            return self.twin_web()
        if self.path == '/erp/':
            return self.proxy('/')
        if self.path.startswith('/equipment/history/'):
            return self.equipment_history(self.path.rsplit('/', 1)[-1])
        if self.path.startswith('/equipment/observations/'):
            return self.equipment_observations(self.path.rsplit('/', 1)[-1])
        if self.path.startswith('/api/'):
            return self.proxy(self.path)
        if self.path == '/config':
            return self.send(200, {'erp':'/erp/', 'twin':TWIN_URL, 'installed':UNITY_APP.is_dir(), 'web':'/twin/' if (TWIN_WEB / 'index.html').is_file() else '', 'download':DOWNLOAD})
        self.send(404, {'message':'페이지가 없습니다.'})

    def do_POST(self):
        if not self.allowed_host() or self.headers.get('Origin') != f"http://{self.headers.get('Host')}":
            return self.send(403, {'message':'대시보드에서 요청하세요.'})
        if self.path in ('/equipment/collection/start', '/equipment/collection/stop'):
            return self.equipment_collection(self.path.endswith('/start'))
        if self.path == '/assistant/chat' or self.path.startswith('/api/'):
            try:
                length = int(self.headers.get('Content-Length', '0'))
            except ValueError:
                length = 0
            if not 0 < length <= 65536 or self.headers.get_content_type() != 'application/json':
                return self.send(400, {'error':'유효한 JSON 요청이 필요합니다.'})
            if self.path == '/assistant/chat':
                return self.proxy_assistant(self.path, self.rfile.read(length))
            return self.proxy(self.path,self.rfile.read(length))
        if self.path != '/launch':
            return self.send(404, {'message':'페이지가 없습니다.'})
        if not self.headers.get('Authorization'):
            return self.send(401, {'message':'로그인이 필요합니다.'})
        connection = http.client.HTTPConnection(UPSTREAM.hostname, UPSTREAM.port, timeout=5) if UPSTREAM.scheme == 'http' else http.client.HTTPSConnection(UPSTREAM.hostname, UPSTREAM.port, timeout=5)
        try:
            connection.request('GET','/api/me',headers={'Authorization':self.headers['Authorization']})
            response = connection.getresponse()
            if response.status != 200 or json.loads(response.read()).get('group_id') == 'LOCAL':
                return self.send(401, {'message':'ERP 세션을 확인할 수 없습니다.'})
        except (OSError, http.client.HTTPException, ValueError):
            return self.send(503, {'message':'ERP 서버에 연결할 수 없습니다.'})
        finally:
            connection.close()
        if not UNITY_APP.is_dir():
            return self.send(404, {'message':'Mac에서 디지털 트윈 앱을 찾지 못했습니다.'})
        try:
            subprocess.run(['/usr/bin/open', str(UNITY_APP)], check=True, timeout=8, capture_output=True)
        except (OSError, subprocess.SubprocessError):
            return self.send(503, {'message':'Mac에서 앱을 열 수 없습니다.'})
        self.send(200, {'message':'Mac에 디지털 트윈 앱 실행을 요청했습니다.'})


if __name__ == '__main__':
    servers = [ThreadingHTTPServer((host, PORT), Portal) for host in BIND_HOSTS]
    stop_collector = threading.Event()
    collector = threading.Thread(target=EQUIPMENT_FEED.run, args=(stop_collector,), daemon=True)
    try:
        collector.start()
        for server in servers:
            print(f'http://{server.server_address[0]}:{server.server_port}', flush=True)
        for server in servers[1:]:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        servers[0].serve_forever()
    finally:
        stop_collector.set()
        if collector.is_alive():
            collector.join(timeout=6)
        for server in servers:
            server.server_close()
