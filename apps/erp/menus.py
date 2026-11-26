"""Menu catalogue shared by the server (permission enforcement) and the UI (navigation, lazy loading).

Each menu lists its views as [route, register_action, label]. A group's read permission on a menu
allows the menu's data routes (views, their dependencies and form reference lists); write permission
allows the menu's register actions plus EXTRA_ACTIONS.
"""

HOME = '처리할 업무'
ADMIN_MENU = '사용자·권한'
# Planning follows the shipyard hierarchy: 대일정 (hull milestones, dock and berth allocation) → 중일정
# (blocks/erection and outfitting/quay, weeks to months) → 소일정 (weekly/daily work, APS, crane jobs); material
# planning follows 중일정. Lower levels are checked against the windows set above them (yardplan.py).
PLAN_MASTER, PLAN_HULL, PLAN_OUTFIT, PLAN_SHORT = '대일정', '중일정 · 선체', '중일정 · 의장·안벽', '소일정 (주간·일일)'
# Design BOM lives with the drawings; the manufacturing BOM is part of production planning and feeds MRP.
MBOM_MENU = '제조 BOM (MBOM)'
MODULES = {
    '업무 홈': [HOME],
    '계획': [PLAN_MASTER, PLAN_HULL, PLAN_OUTFIT, PLAN_SHORT, MBOM_MENU, '자재 소요계획 (MRP)'],
    '실행': ['구매·입고', '자재 출고', '강재 추적', '작업지시·실적', '블록 조립·공수', '블록 이동'],
    '의장': ['의장관리'],
    '진척·품질': ['생산 진척', '물량·공수 진척', '품질 검사'],
    '관리': ['기준정보', '설계 도면', '형상·배치', ADMIN_MENU],
}
# Former menu names → the menus that took over their views (group permissions are carried over on start).
MENU_RENAMES = {
    '의장품': ['의장관리'],
    '호선·블록 계획': [PLAN_MASTER, PLAN_HULL],
    '12주 계획': [PLAN_SHORT],
    '제약 일정 (APS)': [PLAN_SHORT],
    '도크·크레인 계획': [PLAN_MASTER, PLAN_HULL, PLAN_SHORT, '형상·배치'],
    '크레인 계획': [PLAN_SHORT],
    '안벽 계획': [PLAN_MASTER, PLAN_OUTFIT],
    '의장작업 계획': [PLAN_OUTFIT],
    '설계 도면': ['설계 도면', '형상·배치'],
}
MASTER = [['hulls', 'hulls', '호선'], ['vessel_details', 'vessel_details', '호선 상세'],
          ['blocks', 'blocks', '블록과 마지막 확인 위치'], ['block_details', 'block_details', '블록 상세'],
          ['locations', 'locations', '작업장·도크·안벽'], ['centers', 'centers', '작업장 가용시간'],
          ['center_locations', 'center_locations', '워크센터별 생산 시설'], ['equipment', 'equipment', '장비'],
          ['materials', 'materials', '자재·현재고'], ['suppliers', 'suppliers', '공급업체'],
          ['contractors', 'contractors', '사내협력사'], ['warehouse_stock', 'warehouse_stock', '창고별 가용 재고'],
          ['equipment_specs', 'equipment_specs', '장비 능력(크레인 정격하중)'], ['holidays', 'holidays', '휴일 달력'],
          ['berths', 'berths', '선석']]
VIEWS = {
    PLAN_MASTER: [['hull_milestones', 'hull_milestones', '호선 주요 일정'], ['hull_overview', None, '배치·위험 점검'],
                  ['dock_bookings', 'dock_bookings', '도크 점유 예약'], ['berth_assignments', 'berth_assignments', '선석 배정'],
                  ['milestone_history', None, '일정 변경 이력'],
                  ['shift_point_positions', 'shift_point_positions', '이동 지점 좌표']],
    PLAN_HULL: [['erection_plans', 'erection_plans', '탑재 계획'], ['erection_links', 'erection_links', '탑재 선후행'],
                ['block_stock_plans', 'block_stock_plans', '블록 적치 계획'],
                ['block_structure', 'block_structure', '블록 구성'], ['erection_schedule', None, '탑재 일정 제안'],
                ['crane_load', None, '주간 크레인 부하'], ['block_readiness', None, '블록 준비 현황']],
    PLAN_OUTFIT: [['outfit_progress', None, '구역별 의장 진척'], ['outfit_works', 'outfit_works', '의장 작업'],
                  ['outfit_links', 'outfit_links', '작업 선후행'], ['quay_shifts', 'quay_shifts', '안벽 이동 계획·실적'],
                  ['berth_occupancy', None, '선석 점유 현황']],
    PLAN_SHORT: [['plans', 'plans', '12주 롤링·주간 발행'], ['activities', 'activities', '공정 목록'],
                 ['aps', None, '제약 일정 제안'], ['operations', 'operations', '작업 제약'],
                 ['operation_links', 'operation_links', '추가 선행 공정'], ['crane_assignments', 'crane_assignments', '크레인 작업 배정']],
    MBOM_MENU: [['mbom_revisions', 'mbom_revisions', '① MBOM 리비전·발행'], ['mbom_ops', 'mbom_ops', '② 공정 편성'],
                ['mbom_items', 'mbom_items', '③ 설계품목 공정 배정'], ['mbom_inputs', 'mbom_inputs', '④ 제조 투입품'],
                ['mbom_links', 'mbom_links', '⑤ 설계품목↔투입품 연결'], ['mbom_changes', None, '⑥ 발행 전 소요 비교'],
                ['order_boms', None, '작업지시 적용 리비전']],
    '자재 소요계획 (MRP)': [['mrp', None, '자재 부족'], ['requirements', 'requirements', '공정 자재 소요'],
                       ['bom_trace', None, '소요 근거 (EBOM→MBOM)'], ['bom_adjustments', 'bom_adjustment_close', '설계 변경 조정'],
                       ['receipts', 'receipts', '수동 입고 예정']],
    '구매·입고': [['purchase_requests', 'purchase_requests', '구매요청'], ['purchase_orders', 'purchase_orders', '발주'],
              ['goods_receipts', 'goods_receipts', '입고·검수']],
    '자재 출고': [['issues', 'issues', '자재 출고'], ['inventory_ledger', None, '수불부'], ['warehouse_stock', None, '창고 재고']],
    '강재 추적': [['receipt_lots', 'receipt_lots', '입고 로트·히트번호'], ['issue_lots', 'issue_lots', '출고 로트 배정'],
              ['material_returns', 'material_returns', '반납·잔재·폐기'], ['block_traceability', None, '블록별 강재 추적'],
              ['material_rollup', None, '블록 트리 자재 소요']],
    '작업지시·실적': [['orders', 'orders', '작업지시'], ['production_results', 'results', '실적·검사']],
    '블록 조립·공수': [['block_stages', 'block_stages', '조립 단계'], ['work_logs', 'work_logs', '공수·진척 실적'],
                  ['erection_records', 'erection_records', '탑재 검사 실적']],
    '의장관리': [['outfit_works', 'outfit_works', '작업 패키지'], ['outfit_orders', 'outfit_orders', '작업지시'],
               ['outfit_installations', 'outfit_installations', '설치 실적'], ['outfit_inspections', 'outfit_inspections', '검사·재검사'],
               ['outfit_progress', None, '구역별 진척'], ['outfitting_items', 'outfitting_items', '의장품'],
               ['outfitting_events', 'outfitting_events', '의장품 이력'],
               ['outfit_item_links', 'outfit_item_links', '의장품·작업 연결']],
    '블록 이동': [['blocks', None, '블록 위치'], ['moves', 'moves', '이동 이력'], ['telemetry', 'telemetry', '장비 관측']],
    '생산 진척': [['progress', None, '공정별 계획·실적'], ['readiness', None, '작업 착수 준비']],
    '물량·공수 진척': [['hull_progress', None, '호선 물량 진척'], ['block_progress', None, '블록 물량·공수'],
                  ['contractor_summary', None, '협력사 공수']],
    '품질 검사': [['quality_orders', 'results', '작업 검사'], ['quality_receipts', None, '입고 검사'],
              ['inspection_points', 'inspection_points', '검사 계획(ITP)'],
              ['inspection_records', 'inspection_records', '입회 검사 실적'], ['ncrs', 'ncrs', '부적합(NCR)'],
              ['punch_items', 'punch_items', '펀치리스트']],
    '크레인 계획': [['crane_assignments', 'crane_assignments', '크레인 작업 배정'], ['crane_load', None, '주간 크레인 부하'],
               ['equipment_specs', 'equipment_specs', '장비 능력']],
    '안벽 계획': [['berth_occupancy', None, '선석 점유 현황'], ['berth_assignments', 'berth_assignments', '선석 배정'],
              ['quay_shifts', 'quay_shifts', '안벽 이동 계획·실적'], ['berths', 'berths', '선석'],
              ['dock_bookings', 'dock_bookings', '도크 점유 예약']],
    '의장작업 계획': [['outfit_progress', None, '구역별 의장 진척'], ['outfit_works', 'outfit_works', '의장 작업'],
                ['outfit_links', 'outfit_links', '작업 선후행']],
    '기준정보': MASTER,
    '설계 도면': [['drawing_revisions', 'drawing_revisions', '도면 리비전·설계 변경'],
              ['ebom_revisions', 'ebom_revisions', '설계 BOM (EBOM) 리비전'], ['ebom_items', 'ebom_items', 'EBOM 품목 구조'],
              ['hull_geometry', 'hull_geometry', '호선 형상 기준'], ['block_geometry', 'block_geometry', '블록 형상·위치']],
    '형상·배치': [['yard_sites', 'yard_sites', '조선소 부지(위경도)'], ['location_geometry', 'location_geometry', '시설 좌표·크기'],
              ['location_geo', None, '시설 위경도'], ['hull_placements', 'hull_placements', '도크 선체 배치'],
              ['erection_timeline', None, '탑재 순서(트윈)'], ['crane_rails', 'crane_rails', '크레인 레일'],
              ['crane_kinematics', 'crane_kinematics', '크레인 붐 제원'], ['crane_load_charts', 'crane_load_charts', '크레인 정격하중표'],
              ['crane_lifts', 'crane_lifts', '크레인 인양 지점']],
    ADMIN_MENU: [['accounts', 'accounts', '사용자'], ['user_groups', 'user_groups', '그룹'],
                 ['group_permissions', None, '메뉴 권한'], ['audit_log', None, '감사 이력'],
                 ['login_events', None, '로그인 이력']],
}
EXTRA_ACTIONS = {
    PLAN_SHORT: ['forecast'],
    PLAN_MASTER: ['milestone_update'],
    PLAN_HULL: ['block_gate_checks'],
    MBOM_MENU: ['mbom_publish'],
    '의장관리': ['outfit_conditions', 'outfit_materials'],
    '설계 도면': ['ebom_publish'],
    '블록 이동': ['confirm'],
    '품질 검사': ['ncr_close', 'punch_close'],
    ADMIN_MENU: ['account_update', 'account_password_reset', 'group_permissions_bulk'],
}
# Reference lists every signed-in user can read: they label rows and drive the hull filter.
COMMON = ['hulls', 'blocks', 'locations', 'equipment', 'materials', 'centers', 'activities']
# Views computed in the browser from other routes.
DERIVED = {
    'progress': ['activities'],
    'readiness': ['orders', 'requirements', 'issues', 'operations', 'center_locations'],
    'quality_orders': ['orders', 'production_results'],
    'quality_receipts': ['goods_receipts', 'purchase_orders', 'requirements'],
}
# Extra routes a view needs for hull scoping, row status or trace links.
ROUTE_DEPS = {
    'purchase_requests': ['requirements', 'purchase_orders'],
    'purchase_orders': ['requirements'],
    'goods_receipts': ['purchase_orders', 'requirements'],
    'issues': ['requirements', 'orders'],
    'production_results': ['orders'],
    'milestone_history': ['hull_milestones'],
    'work_logs': ['block_stages'],
    'outfitting_events': ['outfitting_items'],
    'inspection_records': ['inspection_points'],
    'telemetry': ['moves'],
    'inventory_ledger': ['goods_receipts', 'issues', 'requirements'],
    'orders': ['plans'],
    'mrp': ['requirements', 'purchase_requests', 'purchase_orders', 'bom_trace'],
    'mbom_revisions': ['mbom_changes', 'ebom_revisions'],
    'mbom_changes': ['mbom_revisions'],
    'ebom_revisions': ['drawing_revisions'],
    'ebom_items': ['ebom_revisions'],
    'mbom_ops': ['mbom_revisions'],
    'mbom_items': ['mbom_ops', 'ebom_items'],
    'mbom_inputs': ['mbom_ops'],
    'mbom_links': ['mbom_inputs', 'ebom_items'],
    'drawing_revisions': ['ebom_revisions'],
    'outfit_links': ['outfit_works'],
    'outfit_orders': ['outfit_conditions', 'outfit_materials'],
    'berth_occupancy': ['berths'],
}
# Form field -> route that supplies its select options.
REFS = {'hull': 'hulls', 'block': 'blocks', 'location': 'locations', 'destination': 'locations',
        'activity': 'activities', 'parent': 'orders', 'equipment': 'equipment', 'move': 'moves',
        'material': 'materials', 'center': 'centers', 'predecessor': 'operations', 'requirement': 'requirements',
        'request': 'purchase_requests', 'supplier': 'suppliers', 'purchase_order': 'purchase_orders',
        'work_order': 'orders', 'source': 'locations', 'plan_id': 'plans', 'parent_block': 'blocks',
        'child_block': 'blocks', 'predecessor_block': 'blocks', 'successor_block': 'blocks',
        'block_stage': 'block_stages', 'contractor': 'contractors', 'item': 'outfitting_items',
        'point': 'inspection_points', 'lot': 'receipt_lots', 'issue': 'issues', 'receipt': 'goods_receipts',
        'dock': 'locations', 'warehouse': 'locations', 'station': 'locations', 'activity_ref': 'activities',
        'group_id': 'user_groups', 'work_area': 'locations', 'berth': 'berths', 'quay': 'locations',
        'from_point': 'shift_points', 'to_point': 'shift_points', 'work': 'outfit_works', 'predecessor_work': 'outfit_works',
        'drawing_revision': 'drawing_revisions', 'base_ebom': 'ebom_revisions', 'ebom': 'ebom_revisions',
        'base_mbom': 'mbom_revisions', 'mbom': 'mbom_revisions', 'op': 'mbom_ops', 'ebom_item': 'ebom_items',
        'input': 'mbom_inputs', 'outfit_order': 'outfit_orders', 'outfit_installation': 'outfit_installations'}
# Actions whose `id` field picks an existing document.
ID_SOURCES = {'outfit_conditions': 'outfit_works', 'outfit_item_links': 'outfitting_items', 'shift_point_positions': 'shift_points', 'forecast': 'activities', 'results': 'orders', 'confirm': 'moves', 'milestone_update': 'hull_milestones',
              'ncr_close': 'ncrs', 'punch_close': 'punch_items', 'account_update': 'accounts',
              'account_password_reset': 'accounts', 'ebom_publish': 'ebom_revisions', 'mbom_publish': 'mbom_revisions',
              'bom_adjustment_close': 'bom_adjustments'}
HOME_ROUTES = ['mrp', 'purchase_requests', 'purchase_orders', 'orders', 'moves', 'ncrs', 'punch_items',
               'erection_schedule', 'requirements', 'hull_overview', 'outfit_works', 'ebom_revisions', 'mbom_revisions',
               'bom_adjustments', 'drawing_revisions']
TRACE_ROUTES = ['requirements', 'orders', 'plans', 'purchase_requests', 'purchase_orders', 'goods_receipts', 'issues',
                'inventory_ledger', 'production_results', 'moves', 'bom_trace', 'order_boms']
# Long histories are searched and paged on the server instead of being sent whole to the browser.
SERVER_PAGED = {'telemetry': 'recorded_at', 'audit_log': 'recorded_at', 'login_events': 'recorded_at',
                'inventory_ledger': 'day', 'production_results': 'actual', 'work_logs': 'work_day'}
MENUS = [menu for menus in MODULES.values() for menu in menus if menu != HOME]


def _closure(routes):
    pending, seen = list(routes), set()
    while pending:
        route = pending.pop()
        if route in seen:
            continue
        seen.add(route)
        pending.extend(DERIVED.get(route, ()))
        pending.extend(ROUTE_DEPS.get(route, ()))
    return {route for route in seen if route not in DERIVED}


def build(fields):
    """Return ({menu: data routes}, {menu: write actions}) for the given action field catalogue."""
    routes, actions = {}, {}
    for menu in MENUS:
        views = VIEWS[menu]
        menu_actions = {action for _, action, _ in views if action} | set(EXTRA_ACTIONS.get(menu, ()))
        wanted = set(COMMON) | {route for route, _, _ in views}
        for action in menu_actions:
            wanted |= {REFS[key] for key in fields.get(action, ()) if key in REFS}
            if action in ID_SOURCES:
                wanted.add(ID_SOURCES[action])
        routes[menu] = sorted(_closure(wanted))
        actions[menu] = sorted(menu_actions)
    return routes, actions
