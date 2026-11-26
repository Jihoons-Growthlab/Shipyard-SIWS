#!/bin/sh
# Temporarily starts the loopback Nginx proxy and performs read-only checks.
set -eu

here="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
nginx_bin="${NGINX_BIN:-nginx}"
runtime="$(mktemp -d "${TMPDIR:-/tmp}/siws-nginx.XXXXXXXX")"
preview_pid=

cleanup() {
    if [ -n "$preview_pid" ]; then
        kill -QUIT "$preview_pid" 2>/dev/null || :
        wait "$preview_pid" 2>/dev/null || :
    fi
    rm -rf "$runtime"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir "$runtime/temp"
"$nginx_bin" -t -p "$runtime/" -c "$here/nginx.conf"
"$nginx_bin" -p "$runtime/" -c "$here/nginx.conf" -g 'daemon off;' &
preview_pid=$!

python3 - <<'PY'
from http.client import HTTPConnection
import time


def get(port, path, host="127.0.0.1:8080"):
    connection = HTTPConnection("127.0.0.1", port, timeout=10)
    try:
        # Target a different TCP port while keeping a Host the current Portal allows.
        connection.request("GET", path, headers={"Host": host})
        with connection.getresponse() as response:
            return (
                response.status,
                [response.getheader(key) for key in ("Content-Type", "Content-Encoding", "Cache-Control")],
                response.read(),
            )
    finally:
        connection.close()


for _ in range(30):
    try:
        if get(18080, "/config")[0] == 200:
            break
    except OSError:
        time.sleep(0.1)
else:
    raise SystemExit("Nginx preview did not reach the existing Portal.")

paths = {
    "/": "text/html",
    "/control": "text/html",
    "/equipment/": "text/html",
    "/process-guide/": "text/html",
    "/erp/": "text/html",
    "/api/meta": "application/json",
    "/twin/": "text/html",
    "/twin/Build/WebGL.loader.js": "application/javascript",
    "/twin/Build/WebGL.data.gz": "application/octet-stream",
    "/twin/Build/WebGL.wasm.gz": "application/wasm",
}
for path, expected_media in paths.items():
    upstream = get(8080, path)
    proxy = get(18080, path)
    status, headers, body = proxy
    if status != 200 or not body or not headers[0].startswith(expected_media) or proxy != upstream:
        raise SystemExit(f"Unexpected proxy response: {path}, status {status}, headers {headers}")
    if path.endswith(".gz") and headers[1] != "gzip":
        raise SystemExit(f"Missing WebGL gzip header: {path}")
    print(f"OK {path}")

for path in ("/api/me", "/equipment/observations/crane"):
    if get(18080, path)[0] != 401:
        raise SystemExit(f"Expected authentication challenge: {path}")
    print(f"OK {path} (authentication required)")
if get(18080, "/", host="unlisted.invalid:8080")[0] != 403:
    raise SystemExit("Expected the Portal to reject the unlisted Host.")
print("OK unlisted Host rejected")
PY
