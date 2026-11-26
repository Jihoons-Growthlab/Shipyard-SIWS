"""Login accounts, user groups with per-menu read/write permissions, Redis bearer sessions and the audit trail.

Tokens are random 256-bit values handed to the client once; the store keeps only their SHA-256 hash, so a
dump of Redis cannot be replayed. Sessions slide: each authenticated request extends the expiry.
"""
import base64
import hashlib
import hmac
import json
import secrets

import cache
import menus

ADMIN_GROUP = 'ADMIN'
SESSION_TTL = 8 * 3600
LOCK_LIMIT = 5
LOCK_SECONDS = 15 * 60
ACCESS_TTL = 600
PBKDF2_ITERATIONS = 600_000  # OWASP 2023 guidance for PBKDF2-HMAC-SHA256
SECRET_KEYS = {'password', 'current_password', 'new_password'}
SCHEMA = '''
CREATE TABLE IF NOT EXISTS user_groups(id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT);
CREATE TABLE IF NOT EXISTS group_permissions(group_id TEXT NOT NULL REFERENCES user_groups(id), menu TEXT NOT NULL,
 can_read INTEGER NOT NULL, can_write INTEGER NOT NULL, PRIMARY KEY(group_id, menu));
CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY, name TEXT NOT NULL, group_id TEXT NOT NULL REFERENCES user_groups(id),
 password_hash TEXT NOT NULL, active INTEGER NOT NULL, created_at TEXT NOT NULL, password_changed_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS login_events(id TEXT PRIMARY KEY, user_id TEXT NOT NULL, success INTEGER NOT NULL,
 reason TEXT NOT NULL, client TEXT NOT NULL, recorded_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS audit_log(id TEXT PRIMARY KEY, user_id TEXT NOT NULL, action TEXT NOT NULL,
 document TEXT NOT NULL, payload TEXT NOT NULL, recorded_at TEXT NOT NULL);
'''
FIELDS = {
    'user_groups': ('id', 'name', 'description'),
    'accounts': ('id', 'name', 'group_id', 'password'),
    'account_update': ('id', 'name', 'group_id', 'status'),
    'account_password_reset': ('id', 'password'),
}
PREFIXES = {'audit_log': 'AUD', 'login_events': 'LGN'}
OPTIONAL = {'user_groups': {'description'}}
DATES = set()
NUMBERS = set()
CHOICES = {'status': ['사용', '중지']}
READS = {'accounts', 'user_groups', 'group_permissions', 'audit_log', 'login_events'}
# Default groups created on first start, mirroring the earlier fixed roles. Every group reads every menu
# except user administration; write access differs.
DEFAULT_GROUPS = {
    '계획': [menus.PLAN_MASTER, menus.PLAN_HULL, menus.PLAN_OUTFIT, menus.PLAN_SHORT, '자재 소요계획 (MRP)', '설계 도면',
           '형상·배치'],
    '생산': ['작업지시·실적', '블록 조립·공수', '의장관리', '블록 이동', '자재 출고'],
    '구매': ['구매·입고', '자재 출고', '강재 추적'],
    '품질': ['품질 검사'],
    '조회': [],
}
GROUP_IDS = {'계획': 'PLAN', '생산': 'PROD', '구매': 'PURCH', '품질': 'QA', '조회': 'VIEW'}


class AuthError(Exception):
    """Missing, invalid or expired session (HTTP 401)."""


class LockedError(Exception):
    """Too many failed logins (HTTP 429)."""


def _b64(raw):
    return base64.b64encode(raw).decode()


def hash_password(password, iterations=None):
    iterations = iterations or PBKDF2_ITERATIONS
    salt = secrets.token_bytes(16)
    digest = hashlib.pbkdf2_hmac('sha256', password.encode(), salt, iterations)
    return f'pbkdf2_sha256${iterations}${_b64(salt)}${_b64(digest)}'


def verify_password(password, stored):
    try:
        scheme, iterations, salt, digest = stored.split('$')
        if scheme != 'pbkdf2_sha256':
            return False
        actual = hashlib.pbkdf2_hmac('sha256', password.encode(), base64.b64decode(salt), int(iterations))
    except (ValueError, TypeError):
        return False
    return hmac.compare_digest(actual, base64.b64decode(digest))


_DUMMY_HASH = hash_password(secrets.token_hex(16))


def check_password(require, user_id, password):
    require(isinstance(password, str) and 8 <= len(password) <= 128, '비밀번호는 8~128자로 입력하세요.')
    require(password != user_id, '비밀번호는 아이디와 달라야 합니다.')
    require(any(c.isalpha() for c in password) and any(c.isdigit() for c in password),
            '비밀번호에는 문자와 숫자를 함께 사용하세요.')


def migrate_menus(db):
    """Carry group permissions from former menu names (menus.MENU_RENAMES) to the menus that replaced them.

    A new menu gets read/write if any former menu mapped to it had it; rows of retired names are removed.
    Idempotent: does nothing once no former name is left.
    """
    retired = [old for old in menus.MENU_RENAMES if old not in menus.MENUS]
    for row in [dict(r) for r in db.execute('SELECT * FROM group_permissions ORDER BY group_id, menu')]:
        for menu in menus.MENU_RENAMES.get(row['menu'], ()):
            current = db.execute('SELECT can_read, can_write FROM group_permissions WHERE group_id=? AND menu=?',
                                 (row['group_id'], menu)).fetchone()
            if current is None:
                db.execute('INSERT INTO group_permissions(group_id,menu,can_read,can_write) VALUES(?,?,?,?)',
                           (row['group_id'], menu, row['can_read'], row['can_write']))
            elif row['menu'] != menu:
                db.execute('UPDATE group_permissions SET can_read=?, can_write=? WHERE group_id=? AND menu=?',
                           (int(current['can_read'] or row['can_read']), int(current['can_write'] or row['can_write']),
                            row['group_id'], menu))
    for old in retired:
        db.execute('DELETE FROM group_permissions WHERE menu=?', (old,))


def ensure_groups(db, insert):
    """Create the administrator group and default groups once; carry permissions over renamed menus."""
    if db.execute('SELECT 1 FROM user_groups LIMIT 1').fetchone():
        migrate_menus(db)
        return
    insert(db, 'user_groups', id=ADMIN_GROUP, name='관리자', description='모든 메뉴 조회·등록 (변경 불가)')
    for name, writable in DEFAULT_GROUPS.items():
        group = GROUP_IDS[name]
        insert(db, 'user_groups', id=group, name=name, description='기본 그룹')
        for menu in menus.MENUS:
            if menu != menus.ADMIN_MENU:
                db.execute('INSERT INTO group_permissions(group_id,menu,can_read,can_write) VALUES(?,?,?,?)',
                           (group, menu, 1, int(menu in writable)))


def create_account(db, insert, now, user_id, name, group_id, password):
    insert(db, 'accounts', id=user_id, name=name, group_id=group_id, password_hash=hash_password(password),
           active=1, created_at=now, password_changed_at=now)


# --- permissions -------------------------------------------------------------------------------

def permissions(db, group_id):
    if group_id == ADMIN_GROUP:
        return {menu: {'read': True, 'write': True} for menu in menus.MENUS}
    rows = db.execute('SELECT * FROM group_permissions WHERE group_id=?', (group_id,))
    return {row['menu']: {'read': bool(row['can_read']), 'write': bool(row['can_write'])}
            for row in rows if row['menu'] in menus.MENUS}


def access(store, connect, group_id, menu_routes, menu_actions):
    """Readable routes and writable actions for a group, cached until its permissions change."""
    key = f'access:{group_id}'
    try:
        raw = store.get(key)
    except cache.STORE_ERRORS:
        raw = None
    if raw:
        return json.loads(raw)
    with connect() as db:
        granted = permissions(db, group_id)
    routes, actions = set(menus.COMMON), set()
    for menu, flags in granted.items():
        if flags['read'] or flags['write']:
            routes |= set(menu_routes[menu])
        if flags['write']:
            actions |= set(menu_actions[menu])
    result = {'menus': granted, 'routes': sorted(routes), 'actions': sorted(actions)}
    try:
        store.set(key, json.dumps(result, ensure_ascii=False), ACCESS_TTL)
    except cache.STORE_ERRORS:
        pass
    return result


# --- sessions ----------------------------------------------------------------------------------

def session_key(token):
    return 'session:' + hashlib.sha256(token.encode()).hexdigest()


def account(store, connect, user_id):
    key = f'account:{user_id}'
    raw = store.get(key)
    if raw:
        return json.loads(raw)
    with connect() as db:
        row = db.execute('SELECT id, name, group_id, active FROM accounts WHERE id=?', (user_id,)).fetchone()
    if not row:
        return None
    info = {'id': row['id'], 'name': row['name'], 'group_id': row['group_id'], 'active': bool(row['active'])}
    store.set(key, json.dumps(info, ensure_ascii=False), ACCESS_TTL)
    return info


def authenticate(store, connect, header):
    if not header or not header.startswith('Bearer '):
        raise AuthError('로그인이 필요합니다.')
    key = session_key(header[7:].strip())
    raw = store.get(key)
    if not raw:
        raise AuthError('로그인이 만료되었습니다. 다시 로그인하세요.')
    user = account(store, connect, json.loads(raw)['user'])
    if not user or not user['active']:
        store.delete(key)
        raise AuthError('사용이 중지된 계정입니다.')
    store.expire(key, SESSION_TTL)
    return {**user, 'session': key}


def open_session(store, user_id, now):
    token = secrets.token_urlsafe(32)
    key = session_key(token)
    store.set(key, json.dumps({'user': user_id, 'issued_at': now}), SESSION_TTL)
    store.sadd(f'user_sessions:{user_id}', key)
    store.expire(f'user_sessions:{user_id}', SESSION_TTL)
    return token


def revoke(store, user_id, keep=None):
    members = store.smembers(f'user_sessions:{user_id}')
    for key in members - {keep}:
        store.delete(key)
        store.srem(f'user_sessions:{user_id}', key)


def login(db, store, api, data, client):
    """Return (http_status, payload). Failed attempts are recorded, so the caller always commits."""
    user_id = str(data.get('id') or '').strip()[:100]
    password = data.get('password')
    lock = f'login_fail:{user_id}'

    def event(success, reason):
        api.insert(db, 'login_events', id=api.traceability.document_number(db, 'login_events'), user_id=user_id,
                   success=int(success), reason=reason, client=client[:100], recorded_at=api.now())

    if int(store.get(lock) or 0) >= LOCK_LIMIT:
        event(False, '잠금')
        return 429, {'error': f'로그인 실패가 {LOCK_LIMIT}회를 넘어 15분간 잠겼습니다.'}
    row = db.execute('SELECT * FROM accounts WHERE id=?', (user_id,)).fetchone()
    valid = verify_password(password if isinstance(password, str) else '', row['password_hash'] if row else _DUMMY_HASH)
    if not (row and valid and row['active']):
        if store.incr(lock) == 1:
            store.expire(lock, LOCK_SECONDS)
        event(False, '중지된 계정' if row and valid else '아이디 또는 비밀번호 불일치')
        return 401, {'error': '아이디 또는 비밀번호가 올바르지 않습니다.'}
    store.delete(lock)
    event(True, '로그인')
    token = open_session(store, user_id, api.now())
    return 200, {'token': token, 'expires_in': SESSION_TTL}


def change_password(db, store, api, user, data):
    row = api.lookup(db, 'accounts', user['id'])
    api.require(verify_password(str(data.get('current_password') or ''), row['password_hash']), '현재 비밀번호가 올바르지 않습니다.')
    check_password(api.require, user['id'], data.get('new_password'))
    db.execute('UPDATE accounts SET password_hash=?, password_changed_at=? WHERE id=?',
               (hash_password(data['new_password']), api.now(), user['id']))


def save_permissions(db, api, data):
    require = api.require
    group = str(data.get('group_id') or '')
    api.lookup(db, 'user_groups', group)
    require(group != ADMIN_GROUP, '관리자 그룹 권한은 변경할 수 없습니다.')
    granted = data.get('permissions')
    require(isinstance(granted, dict) and set(granted) <= set(menus.MENUS), '메뉴 권한 형식을 확인하세요.')
    db.execute('DELETE FROM group_permissions WHERE group_id=?', (group,))
    for menu, flags in granted.items():
        require(isinstance(flags, dict), '메뉴 권한 형식을 확인하세요.')
        write = bool(flags.get('write'))
        read = bool(flags.get('read')) or write
        if read:
            db.execute('INSERT INTO group_permissions(group_id,menu,can_read,can_write) VALUES(?,?,?,?)',
                       (group, menu, 1, int(write)))
    return group


# --- account administration (generic write actions) -------------------------------------------

def active_admins(db, excluding):
    return db.execute('SELECT COUNT(*) FROM accounts WHERE group_id=? AND active=1 AND id<>?',
                      (ADMIN_GROUP, excluding)).fetchone()[0]


def write(db, action, v, api, actor=None):
    require, lookup = api.require, api.lookup
    if action == 'user_groups':
        require(v['id'].isascii() and v['id'].replace('-', '').replace('_', '').isalnum(), '그룹 ID는 영문·숫자로 입력하세요.')
        return api.insert(db, action, **v)
    if action == 'accounts':
        require(v['id'].isascii() and v['id'].replace('.', '').replace('-', '').replace('_', '').isalnum(),
                '아이디는 영문·숫자·. - _ 로 입력하세요.')
        lookup(db, 'user_groups', v['group_id'])
        check_password(require, v['id'], v['password'])
        create_account(db, api.insert, api.now(), v['id'], v['name'], v['group_id'], v['password'])
        return v['id']
    if action == 'account_update':
        row = lookup(db, 'accounts', v['id'])
        lookup(db, 'user_groups', v['group_id'])
        require(v['status'] in CHOICES['status'], '사용 여부를 선택하세요.')
        active = int(v['status'] == '사용')
        if actor == v['id']:
            require(active and v['group_id'] == row['group_id'], '본인 계정의 그룹 변경과 사용 중지는 할 수 없습니다.')
        if row['group_id'] == ADMIN_GROUP and (not active or v['group_id'] != ADMIN_GROUP):
            require(active_admins(db, v['id']), '사용 중인 관리자가 최소 1명 있어야 합니다.')
        db.execute('UPDATE accounts SET name=?, group_id=?, active=? WHERE id=?', (v['name'], v['group_id'], active, v['id']))
        return v['id']
    if action == 'account_password_reset':
        lookup(db, 'accounts', v['id'])
        check_password(require, v['id'], v['password'])
        db.execute('UPDATE accounts SET password_hash=?, password_changed_at=? WHERE id=?',
                   (hash_password(v['password']), api.now(), v['id']))
        return v['id']
    raise ValueError('지원하지 않는 작업입니다.')


def after_commit(store, action, data, document):
    """Drop cached account/permission state and end sessions when credentials or status change."""
    if action in {'account_update', 'account_password_reset'}:
        store.delete(f'account:{document}')
        if action == 'account_password_reset' or data.get('status') == '중지':
            revoke(store, document)
    if action == 'group_permissions_bulk':
        store.delete(f'access:{document}')


def redact(data):
    return {key: ('***' if key in SECRET_KEYS else value) for key, value in data.items()} if isinstance(data, dict) else data


def audit(db, api, user_id, action, document, payload):
    api.insert(db, 'audit_log', id=api.traceability.document_number(db, 'audit_log'), user_id=user_id or '',
               action=action, document='' if document is None else str(document),
               payload=json.dumps(redact(payload), ensure_ascii=False)[:4000], recorded_at=api.now())


def read(db, table):
    if table == 'accounts':
        return [dict(row) for row in db.execute('''SELECT id, name, group_id, active, created_at, password_changed_at
            FROM accounts ORDER BY id''')]
    if table == 'group_permissions':
        rows = [{'group_id': ADMIN_GROUP, 'menu': menu, 'can_read': 1, 'can_write': 1} for menu in menus.MENUS]
        return rows + [dict(row) for row in db.execute('SELECT * FROM group_permissions ORDER BY group_id, menu')]
    if table in {'audit_log', 'login_events'}:
        return [dict(row) for row in db.execute(f'SELECT * FROM {table} ORDER BY recorded_at DESC, id DESC LIMIT 5000')]
    return None
