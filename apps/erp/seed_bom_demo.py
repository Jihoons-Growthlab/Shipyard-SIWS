"""EBOM → MBOM → requirement example for DT-DEMO-01 (run after seed_twin_workflow.py).

B01 (already erected): structural EBOM on the existing STR R01 drawing — stiffeners and brackets nested from
12 mm plates plus FCAW wire for the panel welding. Its activities are finished, so publishing only links the
revision to the completed work order as history; no new requirement, purchase or issue is created.

B05 (waiting in the PE yard): purchased outfitting on a new OFT R01 drawing, installed by a new PE-stage
outfitting activity before erection. The manhole covers are short, so MRP shows the shortage that leads to a
purchase request.

Every step goes through the normal ERP write rules and is skipped when it already exists, so re-running adds
nothing. Facility coordinates, block moves and telemetry from the twin seed are not touched.
"""
import os
from contextlib import closing
from datetime import date, timedelta

import app
import storage

HULL = 'DT-DEMO-01'
B01, B05 = f'{HULL}-B01', f'{HULL}-B05'
WAREHOUSE, CENTER, SUPPLIER, PE_YARD = 'DTW-ST', 'DTW-WC-PE', 'DTW-SUP-OF', 'DTW-YARD'
# id: (name, unit, opening stock, lead days)
MATERIALS = {
    'DTW-AH36-12': ('AH36 후판 원판 12t 2,500×12,000', '장', 0, 14),
    'DTW-FCW-12': ('FCAW 용접 와이어 1.2mm (E71T-1)', 'kg', 0, 7),
    'DTW-VLV-150': ('버터플라이 밸브 DN150 PN10', 'EA', 4, 21),
    'DTW-MHC-600': ('맨홀 커버 600×400 볼트 체결형', 'EA', 2, 14),
    'DTW-BOLT-M20': ('플랜지 볼트·너트 세트 M20', 'SET', 10, 7),
}
PLATE_BASIS = ('네스팅 NP-B01-01 · 원판 2,500×12,000×12t = 2,826kg/장. 1번 원판: FB 폭 255mm(절단여유 5mm 포함) 9열 × 길이 2본 = 18EA. '
               '2번 원판: FB 7열×2본 = 14EA, 남는 폭 715mm에 브래킷 600×600 8EA. 부재 순중량 4,755kg ÷ 원판 5,652kg = 수율 84%, '
               '잔재 1건은 반납 처리 → 원판 2장')
WIRE_BASIS = ('필릿 용접장 = FB 32EA × 5.95m × 양면 380.8m + 브래킷 8EA × 2.4m 19.2m = 400m. '
              '각장 6mm FCAW 0.13kg/m × 400m = 52kg')
B01_ITEMS = [  # item_no, parent, level, name, spec, unit, qty, supply, material, net kg
    ('S100', None, '조립품', '선측 외판 보강 패널', '소조립 · 2,400×6,000', 'EA', 2, '제작', None, None),
    ('S110', 'S100', '부품', '종보강재 FB 250×12', 'AH36 · L=5,950', 'EA', 32, '제작', None, 140.1),
    ('S120', 'S100', '부품', '트리핑 브래킷 PL12 600×600', 'AH36', 'EA', 8, '제작', None, 33.9),
]
B05_ITEMS = [
    ('O100', None, '조립품', '밸러스트 배관 의장 유닛', 'DN150 · PE 단계 설치', 'EA', 1, '제작', None, None),
    ('O110', 'O100', '부품', '버터플라이 밸브 DN150 PN10', '구매사양 VS-150-10', 'EA', 4, '구매', 'DTW-VLV-150', None),
    ('O120', None, '부품', '맨홀 커버 600×400', '볼트 체결형 · 가스켓 포함', 'EA', 6, '구매', 'DTW-MHC-600', None),
]


def exists(db, sql, *params):
    return db.execute(sql, params).fetchone()


def add(db, action, **values):
    return app.write(db, action, values)


def ensure_master(db):
    if not exists(db, 'SELECT 1 FROM locations WHERE id=?', WAREHOUSE):
        add(db, 'locations', id=WAREHOUSE, name='트윈 자재창고', kind='창고')
    if not exists(db, 'SELECT 1 FROM suppliers WHERE id=?', SUPPLIER):
        add(db, 'suppliers', id=SUPPLIER, name='가상 의장품 공급사')
    if not exists(db, 'SELECT 1 FROM centers WHERE id=?', CENTER):
        add(db, 'centers', id=CENTER, name='트윈 PE 의장반', hours=16)
    if not exists(db, 'SELECT 1 FROM center_locations WHERE center=?', CENTER):
        add(db, 'center_locations', center=CENTER, location=PE_YARD)
    for key, (name, unit, stock, lead) in MATERIALS.items():
        if not exists(db, 'SELECT 1 FROM materials WHERE id=?', key):
            add(db, 'materials', id=key, name=name, unit=unit, stock=stock, lead_days=lead)
            if stock:
                add(db, 'warehouse_stock', id='WS-' + key, location=WAREHOUSE, material=key, qty=stock)


def activity(db, block, stage):
    row = exists(db, 'SELECT id FROM activities WHERE block=? AND stage=? ORDER BY id LIMIT 1', block, stage)
    return row['id'] if row else None


def ensure_pe_outfitting(db):
    """B05's PE-stage outfitting activity, its constraint and weekly work order."""
    key = activity(db, B05, '선탑재·탑재')
    if key:
        return key
    erection = date.fromisoformat(app.lookup(db, 'erection_plans', exists(
        db, 'SELECT id FROM erection_plans WHERE block=?', B05)['id'])['forecast'])
    work_day = erection - timedelta(days=3)
    key = add(db, 'activities', hull=HULL, block=B05, stage='선탑재·탑재', baseline=work_day.isoformat(),
              forecast=work_day.isoformat(), reason='')
    add(db, 'operations', activity=key, center=CENTER, duration=16,
        earliest=(work_day - timedelta(days=2)).isoformat())
    monday = date.today() - timedelta(days=date.today().weekday())
    plan = add(db, 'plans', start=monday.isoformat())
    week = work_day - timedelta(days=work_day.weekday())
    add(db, 'orders', activity=key, cadence='주간', day=week.isoformat(), plan_id=str(int(plan.split('-')[-1])),
        instruction=f'{B05} PE 단계 의장품 설치 (밸브·맨홀 커버)')
    return key


def ensure_drawing(db, block, drawing_no, description):
    row = exists(db, "SELECT id FROM drawing_revisions WHERE drawing_no=? AND revision='R01'", drawing_no)
    if row:
        return row['id']
    key = add(db, 'drawing_revisions', drawing_no=drawing_no, block=block, revision='R01',
              issued_day=(date.today() - timedelta(days=14)).isoformat(), change_type='최초 출도', description=description)
    # The designer confirms the new drawing so the block readiness reflects the current revision.
    add(db, 'block_gate_checks', block=block, gate='도면 출도', verdict='확인', evidence=f'{drawing_no} R01 출도 확인')
    return key


def ensure_ebom(db, drawing, items, description):
    row = exists(db, 'SELECT id, status FROM ebom_revisions WHERE drawing_revision=?', drawing)
    key = row['id'] if row else add(db, 'ebom_revisions', drawing_revision=drawing, description=description)
    if not row or row['status'] == '작성 중':
        for no, parent, level, name, spec, unit, qty, supply, material, weight in items:
            add(db, 'ebom_items', ebom=key, item_no=no, parent_no=parent or '', item_level=level, name=name, spec=spec,
                unit=unit, design_qty=qty, supply=supply, material=material or '', net_weight_kg=weight or '')
        add(db, 'ebom_publish', id=key)
    return key


def ensure_mbom(db, block, ebom, description, ops, inputs, assign):
    """ops: [(seq, process, activity)], inputs: [(seq, type, material, qty, basis, [item_no])], assign: {item_no: seq}"""
    row = exists(db, "SELECT id, status FROM mbom_revisions WHERE block=? AND revision='R01'", block)
    key = row['id'] if row else add(db, 'mbom_revisions', ebom=ebom, revision='R01', description=description)
    if not row or row['status'] == '작성 중':
        op_ids = {seq: add(db, 'mbom_ops', mbom=key, seq=seq, process=process, activity=work) for seq, process, work in ops}
        items = {r['item_no']: r['id'] for r in db.execute('SELECT id, item_no FROM ebom_items WHERE ebom=?', (ebom,))}
        for no, seq in assign.items():
            add(db, 'mbom_items', op=op_ids[seq], ebom_item=items[no])
        for seq, kind, material, qty, basis, linked in inputs:
            line = add(db, 'mbom_inputs', op=op_ids[seq], input_type=kind, material=material, qty=qty, basis=basis)
            for no in linked:
                add(db, 'mbom_links', input=line, ebom_item=items[no])
    # Publishing an already published revision re-processes it; the reconciliation adds nothing twice.
    # Once a later revision (design change) is published, R01 stays as history and is left alone.
    latest = app.bom.latest_issued(db, block)
    if not latest or latest['id'] == key:
        add(db, 'mbom_publish', id=key)
    return key


def seed(db):
    app.require(exists(db, 'SELECT 1 FROM hulls WHERE id=?', HULL), f'{HULL}이 없습니다. seed_twin_workflow.py를 먼저 실행하세요.')
    before = db.execute('SELECT COUNT(*) FROM requirements').fetchone()[0]
    ensure_master(db)
    pe = ensure_pe_outfitting(db)
    fab = activity(db, B01, '가공·조립')
    str_drawing = exists(db, "SELECT id FROM drawing_revisions WHERE drawing_no=? AND revision='R01'", f'{B01}-STR')['id']
    ebom1 = ensure_ebom(db, str_drawing, B01_ITEMS, 'B01 구조 EBOM (외판 보강 패널)')
    ensure_mbom(db, B01, ebom1, 'B01 제조 BOM: 절단·가공 → 소조립 (탑재 완료 블록 이력)',
                [(10, '절단·가공', fab), (20, '소조립', fab)],
                [(10, '원판', 'DTW-AH36-12', 2, PLATE_BASIS, ['S110', 'S120']),
                 (20, '용접재', 'DTW-FCW-12', 52, WIRE_BASIS, [])],
                {'S110': 10, 'S120': 10, 'S100': 20})
    oft = ensure_drawing(db, B05, f'{B05}-OFT', '트윈 실습 B05 밸러스트 배관 의장도')
    ebom5 = ensure_ebom(db, oft, B05_ITEMS, 'B05 의장 EBOM (PE 단계 구매 의장품)')
    ensure_mbom(db, B05, ebom5, 'B05 제조 BOM: PE 단계 의장품 설치',
                [(10, '선행탑재(PE)', pe)],
                [(10, '구매품', 'DTW-VLV-150', 4, '', ['O110']),
                 (10, '구매품', 'DTW-MHC-600', 6, '', ['O120']),
                 (10, '소모품', 'DTW-BOLT-M20', 8, '밸브 4EA × 플랜지 2면 = 8세트', [])],
                {'O100': 10, 'O110': 10, 'O120': 10})
    return db.execute('SELECT COUNT(*) FROM requirements').fetchone()[0] - before


if __name__ == '__main__':
    app.DATABASE_URL = os.environ['SHIPYARD_DATABASE_URL']
    with closing(app.connect()) as db, db:
        storage.init_schema(db, app.SCHEMA)
        storage.begin_write(db)
        added = seed(db)
    app.bump_external_cache()
    print(f'{HULL} BOM 예시 반영 완료 · 새 자재 소요 {added}건')
