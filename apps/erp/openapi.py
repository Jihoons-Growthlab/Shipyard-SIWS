"""OpenAPI 3 document generated from the server's own route, field and menu definitions, plus a Swagger UI page.

GET /api/openapi.json  -> this document
GET /docs              -> Swagger UI (loads swagger-ui-dist from the jsDelivr CDN)
"""
import menus
import search

SWAGGER_VERSION = '5.17.14'
PAGED = {'type': 'object', 'properties': {
    'rows': {'type': 'array', 'items': {'$ref': '#/components/schemas/Row'}},
    'total': {'type': 'integer'}, 'page': {'type': 'integer'}, 'size': {'type': 'integer'}, 'pages': {'type': 'integer'}}}
ERROR = {'description': '', 'content': {'application/json': {'schema': {'$ref': '#/components/schemas/Error'}}}}
SEARCH_PARAMS = [
    ('q', 'string', '모든 열에서 부분 일치 검색 (자재명 포함)'),
    ('hull', 'string', '호선 ID로 범위 제한 (블록·공정·이동 등 연결 관계로 판단)'),
    ('material', 'string', '자재 ID 일치'),
    ('location', 'string', 'location 또는 source 시설 ID 일치'),
    ('date_key', 'string', 'from/until을 적용할 날짜 열 이름'),
    ('from', 'date', 'date_key 시작일 (YYYY-MM-DD, 포함)'),
    ('until', 'date', 'date_key 종료일 (YYYY-MM-DD, 포함)'),
    ('sort', 'string', '정렬 열 이름'),
    ('dir', 'string', 'asc | desc'),
    ('page', 'integer', '0부터 시작하는 페이지 번호'),
    ('size', 'integer', f'페이지 크기 1~{search.MAX_SIZE} (기본 20)'),
]
# Column filters are open-ended (`f.<column>`), so they are documented in the operation description.
COLUMN_FILTER_NOTE = ('열 필터: `f.<열이름>=값` (예: `f.status=관측`, `f.x=>=10`). 부분 일치, 숫자 비교(>=, <=, >, <, =) 또는 날짜 범위(`2026-11-01~2026-11-30`), '
                      f'최대 {search.MAX_COLUMN_FILTERS}개.')


def labels():
    """route/action -> Korean label taken from the menu catalogue."""
    names = {}
    for menu, views in menus.VIEWS.items():
        for route, action, label in views:
            names.setdefault(route, (menu, label))
            if action:
                names.setdefault(action, (menu, label))
    return names


def tags_for(name, mapping):
    found = [menu for menu, items in mapping.items() if name in items]
    return found[:1] or ['공통']


def field_schema(app, key):
    fixed = {'stage': app.STAGES, 'milestone': app.shipbuilding.MILESTONES, 'gate': app.shipbuilding.GATES,
             'verdict': ['확인', '보류', '해당 없음'], 'cadence': ['주간', '일일'], 'inspection': ['합격', '불합격'],
             'kind': ['야드', '작업장', '도크', '안벽', '창고'], **app.CHOICES}
    if key in fixed:
        return {'type': 'string', 'enum': list(fixed[key])}
    if key in {'actual_start_at', 'actual_end_at'}:
        return {'type': 'string', 'description': '한국시간 YYYY-MM-DDTHH:mm', 'example': '2026-11-16T08:30'}
    if key in app.DATE_KEYS:
        return {'type': 'string', 'format': 'date', 'example': '2026-11-16'}
    if key in app.planning.MEASURES or key in app.FLOAT_KEYS:
        return {'type': 'number'}
    if key in app.planning.NUMBERS:
        return {'type': 'integer', 'minimum': 0 if key in app.planning.NONNEGATIVE else 1}
    if key in app.auth.SECRET_KEYS:
        return {'type': 'string', 'format': 'password', 'minLength': 8, 'maxLength': 128}
    schema = {'type': 'string', 'maxLength': 1000}
    if key in menus.REFS:
        schema['description'] = f'{menus.REFS[key]} 목록의 ID'
    return schema


def build(app):
    names = labels()
    auth_fail = {'401': {**ERROR, 'description': '로그인 필요 또는 토큰 만료'},
                 '403': {**ERROR, 'description': '그룹 메뉴 권한 없음'}}
    paths = {}

    def op(path, method, body):
        paths.setdefault(path, {})[method] = body

    op('/api/login', 'post', {
        'tags': ['인증'], 'summary': '로그인 (토큰 발급)', 'security': [],
        'description': 'Swagger UI에서 실행하면 받은 토큰이 Authorize에 자동으로 설정됩니다. 5회 실패 시 15분 잠금.',
        'requestBody': {'required': True, 'content': {'application/json': {'schema': {
            'type': 'object', 'required': ['id', 'password'],
            'properties': {'id': {'type': 'string', 'example': 'admin'}, 'password': {'type': 'string', 'format': 'password'}}}}}},
        'responses': {'200': {'description': '로그인 성공', 'content': {'application/json': {'schema': {
            'type': 'object', 'properties': {'token': {'type': 'string'}, 'expires_in': {'type': 'integer', 'example': 28800}}}}}},
            '401': {**ERROR, 'description': '아이디 또는 비밀번호 불일치'}, '429': {**ERROR, 'description': '로그인 잠금'}}})
    op('/api/logout', 'post', {'tags': ['인증'], 'summary': '로그아웃 (현재 토큰 폐기)',
                               'requestBody': {'content': {'application/json': {'schema': {'type': 'object'}, 'example': {}}}},
                               'responses': {'200': {'description': '폐기됨'}, **auth_fail}})
    op('/api/change_password', 'post', {
        'tags': ['인증'], 'summary': '내 비밀번호 변경 (다른 세션 종료)',
        'requestBody': {'required': True, 'content': {'application/json': {'schema': {
            'type': 'object', 'required': ['current_password', 'new_password'],
            'properties': {'current_password': field_schema(app, 'current_password'),
                           'new_password': field_schema(app, 'new_password')}}}}},
        'responses': {'201': {'description': '변경됨'}, '400': {**ERROR, 'description': '현재 비밀번호 불일치·정책 위반'}, **auth_fail}})
    op('/api/me', 'get', {'tags': ['인증'], 'summary': '내 정보와 그룹 권한 (메뉴, 조회 가능 경로, 저장 가능 작업)',
                          'responses': {'200': {'description': '', 'content': {'application/json': {'schema': {
                              'type': 'object', 'properties': {
                                  'id': {'type': 'string'}, 'name': {'type': 'string'}, 'group_id': {'type': 'string'},
                                  'menus': {'type': 'object', 'additionalProperties': {'type': 'object', 'properties': {
                                      'read': {'type': 'boolean'}, 'write': {'type': 'boolean'}}}},
                                  'routes': {'type': 'array', 'items': {'type': 'string'}},
                                  'actions': {'type': 'array', 'items': {'type': 'string'}}}}}}}, **auth_fail}})
    op('/api/meta', 'get', {'tags': ['공통'], 'summary': '화면 구성 메타데이터 (메뉴, 입력 필드, 선택값)', 'security': [],
                            'responses': {'200': {'description': '', 'content': {'application/json': {'schema': {'type': 'object'}}}}}})
    op('/api/group_permissions_bulk', 'post', {
        'tags': [menus.ADMIN_MENU], 'summary': '그룹의 메뉴별 조회/등록 권한 일괄 저장',
        'description': '보내지 않은 메뉴는 권한이 없어집니다. write=true면 read도 부여됩니다. 관리자 그룹은 변경할 수 없습니다.',
        'requestBody': {'required': True, 'content': {'application/json': {'schema': {
            'type': 'object', 'required': ['group_id', 'permissions'], 'properties': {
                'group_id': {'type': 'string', 'example': 'PROD'},
                'permissions': {'type': 'object', 'additionalProperties': {'type': 'object', 'properties': {
                    'read': {'type': 'boolean'}, 'write': {'type': 'boolean'}}},
                    'example': {'블록 조립·공수': {'read': True, 'write': True}, '물량·공수 진척': {'read': True}}}}}}}},
        'responses': {'201': {'description': '저장됨', 'content': {'application/json': {'schema': {'$ref': '#/components/schemas/Saved'}}}},
                      '400': {**ERROR, 'description': '형식 오류'}, **auth_fail}})

    read_tags = {menu: routes for menu, routes in app.MENU_ROUTES.items()}
    for route in sorted(app.READS):
        menu, label = names.get(route, (None, route))
        paged = route in menus.SERVER_PAGED
        op(f'/api/{route}', 'get', {
            'tags': [menu] if menu else tags_for(route, read_tags),
            'summary': f'{label} 조회' + (' (긴 이력: 페이지 조회 권장)' if paged else ''),
            'description': '조건 없이 호출하면 전체 배열, 검색 조건을 하나라도 주면 페이지 객체를 반환합니다. '
                           '결과는 Redis에 데이터 버전별로 캐시되며 응답 헤더 X-Cache(hit|miss)로 확인할 수 있습니다. '
                           + COLUMN_FILTER_NOTE,
            'parameters': [{'$ref': f'#/components/parameters/{name}'} for name, _, _ in SEARCH_PARAMS],
            'responses': {'200': {'description': '', 'headers': {'X-Cache': {'schema': {'type': 'string', 'enum': ['hit', 'miss']}}},
                                  'content': {'application/json': {'schema': {'oneOf': [
                                      {'type': 'array', 'items': {'$ref': '#/components/schemas/Row'}}, PAGED]}}}},
                          '400': {**ERROR, 'description': '잘못된 검색 조건'}, **auth_fail}})

    for action, fields in sorted(app.FIELDS.items()):
        menu, label = names.get(action, (None, action))
        optional = app.OPTIONAL | app.ACTION_OPTIONAL.get(action, set())
        numbered = action in app.traceability.PREFIXES
        required = [key for key in fields if key not in optional and not (key == 'id' and numbered)
                    and not (action == 'milestone_update' and key == 'actual')]
        properties = {key: field_schema(app, key) for key in fields}
        if numbered and 'id' in properties:
            properties['id']['description'] = f'비워 두면 {app.traceability.PREFIXES[action]}-000001 형식으로 자동 채번'
        summary = {'forecast': '공정 예정일 갱신', 'confirm': '블록 이동 확정', 'milestone_update': '주요 일정 변경·실적',
                   'ncr_close': 'NCR 종결', 'punch_close': '펀치 종결', 'results': '작업 실적·검사 등록',
                   'account_update': '사용자 정보 변경', 'account_password_reset': '비밀번호 초기화'}.get(action, f'{label} 등록')
        op(f'/api/{action}', 'post', {
            'tags': tags_for(action, app.MENU_ACTIONS) if not menu else [menu],
            'summary': summary,
            'description': '해당 메뉴의 등록 권한이 필요합니다. 저장되면 감사 이력이 남고 조회 캐시 버전이 올라갑니다.',
            'requestBody': {'required': True, 'content': {'application/json': {'schema': {
                'type': 'object', 'additionalProperties': False, 'required': required, 'properties': properties}}}},
            'responses': {'201': {'description': '저장됨', 'content': {'application/json': {'schema': {'$ref': '#/components/schemas/Saved'}}}},
                          '400': {**ERROR, 'description': '입력 검증·업무 규칙 위반'},
                          '409': {**ERROR, 'description': 'ID 중복 또는 참조 무결성 위반'}, **auth_fail}})

    tag_order = ['인증', '공통'] + menus.MENUS
    used = {tag for item in paths.values() for body in item.values() for tag in body['tags']}
    return {
        'openapi': '3.0.3',
        'info': {'title': 'ShipYard ERP API', 'version': '1.0.0',
                 'description': '조선소 생산관리 ERP API. 먼저 **인증 › POST /api/login**을 실행하면 토큰이 자동으로 설정됩니다 '
                                '(또는 Authorize에 토큰 입력). 권한은 로그인한 사용자 그룹의 메뉴별 조회/등록 권한을 따릅니다.'},
        'servers': [{'url': '/'}],
        'tags': [{'name': tag} for tag in tag_order if tag in used],
        'security': [{'bearerAuth': []}],
        'paths': paths,
        'components': {
            'securitySchemes': {'bearerAuth': {'type': 'http', 'scheme': 'bearer',
                                               'description': 'POST /api/login 응답의 token (8시간 슬라이딩 만료, Redis 관리)'}},
            'parameters': {name: {'name': name, 'in': 'query', 'required': False, 'description': text,
                                  'schema': {'type': 'string', 'format': 'date'} if kind == 'date' else
                                  {'type': 'string', 'enum': ['asc', 'desc']} if name == 'dir' else {'type': kind}}
                           for name, kind, text in SEARCH_PARAMS},
            'schemas': {'Row': {'type': 'object', 'additionalProperties': True},
                        'Saved': {'type': 'object', 'properties': {'id': {'type': 'string'}}},
                        'Error': {'type': 'object', 'properties': {'error': {'type': 'string'}}}},
        },
    }


DOCS_HTML = f'''<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ShipYard API 문서</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@{SWAGGER_VERSION}/swagger-ui.css">
<style>body{{margin:0;background:#fff}}</style>
</head>
<body>
<div id="swagger-ui"></div>
<script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@{SWAGGER_VERSION}/swagger-ui-bundle.js"></script>
<script>
const ui = SwaggerUIBundle({{
  url: '/api/openapi.json',
  dom_id: '#swagger-ui',
  deepLinking: true,
  persistAuthorization: true,
  docExpansion: 'none',
  filter: true,
  tryItOutEnabled: true,
  displayRequestDuration: true,
  // A successful login in Swagger sets the bearer token for every later call.
  responseInterceptor: response => {{
    if (response.url.endsWith('/api/login') && response.ok && response.body && response.body.token) {{
      ui.preauthorizeApiKey('bearerAuth', response.body.token);
    }}
    return response;
  }}
}});
</script>
</body>
</html>
'''
