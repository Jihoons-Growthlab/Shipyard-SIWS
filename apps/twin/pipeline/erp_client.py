"""Minimal ShipYard-ERP API client (read side) for the twin pipeline.

Environment: SHIPYARD_API_URL (HTTPS, or HTTP on localhost) and SHIPYARD_API_TOKEN, or
SHIPYARD_API_USER + SHIPYARD_API_PASSWORD. Uploading generated data uses tools/migration/migrate.py.
"""

import json
import os
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request(base, route, payload=None, token=None):
    headers = {"Accept": "application/json"}
    body = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        body = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode()
    if token:
        headers["Authorization"] = f"Bearer {token}"
    try:
        with build_opener(NoRedirect()).open(Request(f"{base}/api/{route}", data=body, headers=headers), timeout=30) as response:
            return json.load(response)
    except HTTPError as exc:
        raise RuntimeError(f"{route}: HTTP {exc.code}") from exc


def base_url(parser):
    base = os.environ.get("SHIPYARD_API_URL", "").rstrip("/")
    url = urlsplit(base)
    if (not url.hostname or url.username or url.password or url.query or url.fragment
            or (url.scheme != "https" and not (url.scheme == "http" and url.hostname in {"localhost", "127.0.0.1", "::1"}))):
        parser.error("SHIPYARD_API_URL에 HTTPS 또는 로컬 HTTP 주소를 설정하세요.")
    return base


def login(base, parser):
    token = os.environ.get("SHIPYARD_API_TOKEN")
    if not token:
        user, password = os.environ.get("SHIPYARD_API_USER"), os.environ.get("SHIPYARD_API_PASSWORD")
        if not user or not password:
            parser.error("API 토큰 또는 사용자 ID와 비밀번호를 설정하세요.")
        token = request(base, "login", {"id": user, "password": password})["token"]
    return token
