# ShipyardTwin

조선소 디지털 트윈. ShipYard-ERP의 호선 데이터(블록 형상, 탑재 순서, 계획일, 도크 배치)를 받아 Unity에서 블록을 하나씩 도크에 탑재하는 과정을 재생한다.

```
ShipyardTwin/
├── pipeline/            Python (외부 패키지 없음, 3.9+)
│   ├── generate_yard.py     가상 조선소 배치도(부지 위경도·방위각, 도크·안벽·공장·야드·창고 크기) → ERP 입력 JSONL
│   ├── generate_scenery.py  조선소 소품: 시설 크기에 맞춘 공장·창고 건물, 사무동·경비실, 트랜스포터·트럭·지게차·승용차,
│   │                        안벽 레벨러핑 크레인, 조명탑, 컨테이너·강판·파이프 적치 + 도로·배치·운행 경로(scenery.json)
│   ├── generate_blocks.py   호선 제원 → 가상 블록 분할, 탑재 선후행·계획, 도크 배치 (ERP 입력 JSONL)
│   ├── generate_models.py   블록 JSONL → 도장(usemtl)된 블록별 OBJ + paint.mtl + 전체 선박 미리보기
│   ├── hullform.py          선형(구상선수, 선미 개구부, 빌지) — 블록 박스와 모델이 같이 사용
│   ├── export_twin.py       ERP API → data/twin/<hull>.twin.json, Unity Resources로 복사
│   ├── erp_client.py        ERP 로그인·조회
│   └── paths.py             기본 경로
├── unity/               Unity 6000.3 프로젝트 (Built-in 렌더 파이프라인)
│   ├── Packages/manifest.json   ../../shared/unity-packages 패키지를 file:로 참조
│   └── Assets/ShipyardTwin/
│       ├── Authoring/       shipyard.build.json, dock.csv, Dashboard.uxml ← 씬·UI는 여기만 고친다
│       ├── Runtime/         재생 코드 + Resources/ShipyardTwin (내보낸 데이터·모델)
│       ├── Editor/          ShipyardTwinBuild (씬 생성, macOS 빌드)
│       └── Tests/Editor/    EditMode 테스트
├── tools/               test.sh, build.sh, capture.sh
├── docs/TWIN_FORMAT.md  twin JSON 형식, 좌표계, 재생 규칙
└── data/                생성물 (gitignore): 블록 JSONL, models/, twin/
```

이웃 프로젝트:
- `../erp`: 데이터 원천.
- `../../tools/migration/migrate.py`: ERP 업로드.
- `../../shared/unity-packages`: 모듈 라이브러리. 참조만 한다.

## 데이터 갱신

ERP 접속 정보는 환경변수로 준다. 비밀번호를 파일에 쓰지 않는다.

```bash
export SHIPYARD_API_URL=https://erp.example.com   # 로컬은 http://127.0.0.1:8001
export SHIPYARD_API_USER=...  SHIPYARD_API_PASSWORD=...   # 또는 SHIPYARD_API_TOKEN

python3 pipeline/generate_yard.py                      # → data/yard.jsonl (부지 + 시설 14곳, 이미 있는 시설은 --existing ID,...)
python3 pipeline/generate_blocks.py                    # → data/SN2040-blocks.jsonl (도크는 generate_yard의 DT-DOCK)
python3 ../../tools/migration/migrate.py data/yard.jsonl --apply
python3 ../../tools/migration/migrate.py data/SN2040-blocks.jsonl --apply
python3 pipeline/generate_operations.py                # → data/operations.jsonl (예시 호선 10척, 크레인·선석·안벽 이동·의장 작업)
python3 ../../tools/migration/migrate.py data/operations.jsonl --apply   # 한 DB에 한 번만 (호선·일정 등은 추가 전용)
python3 pipeline/generate_models.py data/SN2040-blocks.jsonl   # → data/models/SN2040/*.obj
python3 pipeline/generate_scenery.py                   # → data/props/*.obj, data/scenery.json (+ Unity Resources 복사)
python3 pipeline/build_hq_assets.py                    # Blender: 상세 크레인·차량·건물 모델 → 같은 이름으로 덮어씀
python3 pipeline/fetch_satellite.py                    # 브이월드 위성영상 4km 사방 → 바다 투명 처리 → Unity Satellite/
/Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup -P pipeline/blender/build_textures.py -- data/textures \
  && cp data/textures/*.png unity/Assets/ShipyardTwin/Runtime/Resources/ShipyardTwin/Textures/   # 타일링 텍스처
python3 pipeline/export_twin.py SN2040                  # → data/twin/SN2040.twin.json + unity/.../Resources/ShipyardTwin
```

- 대상 ERP에는 `hull_geometry`, `block_geometry`, `location_geometry`, `hull_placements` 입력과 `erection_timeline` 조회가 있어야 한다(ShipYard-ERP `geometry.py`).
- 블록 모델은 생성한 예시 모델이며 설계 CAD가 아니다. 실제 CAD를 받으면 OBJ로 변환해 같은 `model_asset` 경로에 넣는다. 축과 원점은 docs/TWIN_FORMAT.md를 따른다.

### 오프라인 데모 호선 확장

Unity에 포함된 야드 스냅샷은 SN2040·SN2046과 SN2050~SN2059를 제공합니다. SN2050~SN2059의 모의 탑재 계획과 3D 모델을 ERP 원본 스냅샷에서 다시 만들려면 `apps/twin`에서 실행합니다.

```bash
python3 pipeline/expand_demo_fleet.py data/twin/yard.snapshot.json --models \
  -o unity/Assets/ShipyardTwin/Runtime/Resources/ShipyardTwin/yard.snapshot.json
```

확장 데이터는 기존 호선 제원과 도크 예약을 사용합니다. 기존 간이 블록은 데모 스냅샷에서 교체됩니다.

ERP에도 같은 블록 분할(모델 ID 동일)과 공정 실적을 넣으려면 `apps/erp/shipyard-postgres seed_hull_simulation.py`를 실행합니다. 이 스크립트는 ERP 쓰기 규칙을 그대로 거칩니다.

- 대상과 등록 내용: SN2050~2059의 간이 블록을 교체합니다. SN2040·2046에는 도크 예약, 주요 일정, 크레인, 공정 실적을 넣습니다.
- 진척: 오늘까지 조립 공수, 준비 항목 확인, 이동, 탑재 검사 실적을 기록합니다.
- 거부된 블록: ERP가 탑재를 거부한 블록(미결 NCR 등)은 사유와 함께 오늘 이후로 재계획합니다.

## 실행과 검증

```bash
tools/test.sh                          # EditMode 테스트
tools/test.sh PlayMode                 # Play Mode 테스트(생성된 씬에서 이름 표시·카메라 버튼 확인, 먼저 build.sh로 씬 갱신)
tools/build.sh                         # 씬 재생성 + Builds/ShipyardTwin.app
tools/capture.sh 31 /tmp/day31.png crane   # 31일차 정지 화면, 크레인 시점
```

- 에디터: 메뉴에서 **ShipyardTwin > Create Scene**을 누르고 Play한다.
- 앱 옵션:
  - `-shipyard-seek <일>`: 재생 위치로 이동
  - 웹(Portal `/twin/`): `?hull=<호선>&block=<블록>`이면 그 호선을 선택하고 탑재 패널을 연 뒤, 블록 인양 직전으로 재생 위치를 옮긴다(생산 관제에서 연결).
  - `-shipyard-pause`: 정지 상태로 시작(기본도 정지이며, `-shipyard-seek`와 함께 쓸 때 위치만 옮기고 멈춰 둔다)
  - `-shipyard-panel`: 탑재 패널을 연 상태로 시작(`tools/capture.sh`가 쓴다)
  - `-shipyard-depth`: 수심도를 켠 상태로 시작
  - `-shipyard-hover x,y,z`: 커서가 야드 좌표 (x, y, 높이 z)에 있는 것처럼 이름 표시를 띄운다(캡처용)
  - `-shipyard-camera x,y,yaw,pitch,거리`: 야드 좌표 (x, y) 둘레로 카메라를 둔다(점검·캡처용, 예: 안벽 접합부 `615,-150,270,28,85`)
  - `-growthlab-screenshot <png>`: 화면 캡처
- 화면 구성(`Authoring/Dashboard.uxml`, `Dashboard.uss`, `DashboardShellView`):
  - 앱을 열면 조선소 화면과 왼쪽 아이콘 바만 보이고, 시뮬레이션은 정지 상태다.
  - 아이콘 바: 블록 탑재(패널 열기/닫기), 수심도(켜기/끄기), 맨 아래 챗봇.
  - 패널 목록은 휠·드래그·터치로 스크롤하며 스크롤바는 표시하지 않는다.
  - 바다(`Resources/ShipyardTwin/Shaders/Water.shader`, `Runtime/Yard/SeaSurface.cs`)는 바람 방향 주위로 퍼진 진행파 8개(파장 4~90 m)로 움직인다.
    - 카메라 주변: 원형 격자(약 4,600 정점)가 카메라를 따라다니며 실제로 출렁인다.
    - 먼 바다: 평면 한 장이며 같은 파도로 음영만 낸다. 도크 자리는 두 메시 모두 비운다.
    - 표현: 물마루 거품, 해를 등진 물마루의 투과광, 거리에 따라 거칠어지는 하늘 반사가 있다.
    - 성능: 짧은 파도는 멀어질수록 약해져 깜빡임이 없다. 프래그먼트 비용은 해석식 파도 8개와 텍스처 읽기 2회다.
  - 작은 크레인(`Runtime/Yard/RailCraneView.cs`): ERP `crane_rails`의 지브·러핑 크레인을 레일과 함께 그리고, 장비 모니터링과 같은 규칙(실제 시각 기준 120초 주기)으로 레일 위를 움직인다. 마우스를 올리면 ERP 장비명, 정격, 배치 시설, 현재 상태가 나온다. ERP에 레일이 있으면 scenery의 고정 크레인은 그리지 않는다.
  - 탑재 패널은 왼쪽에서 밀려 나오며, 안의 **재생** 버튼을 눌러야 시뮬레이션이 시작된다. 챗봇 패널과 같은 자리를 쓰므로 한 번에 하나만 열린다. Esc로 닫는다.
  - 종료 버튼은 화면 오른쪽 위에 있다. 확인 창을 거쳐 앱에서는 앱을 닫고, 에디터에서는 Play Mode만 끝낸다.
  - 수심도: 국립해양조사원 실측 수심(150m 격자)을 바다 위에 2m 간격 색 단계와 등심선으로 겹친다. 오른쪽 아래 범례에 범위와 출처를 표시한다. 앱 옵션 `-shipyard-depth`로 켠 채 시작한다.
  - 패널이나 아이콘 바 위에서 시작한 드래그와 휠은 카메라를 움직이지 않는다.
  - 이름 표시(`HoverTooltipView`, `HoverTarget`): 블록(ID, 호선·구역·중량), 건물·시설(ERP 시설명, 없으면 ID), 선박(선명, 없으면 호선 번호. 용골 받침 위)에 마우스를 올리면 커서 옆에 표시한다. 선박 위 블록은 블록 정보가 우선이다. 매 프레임 레이캐스트 한 번으로 커서 아래 가장 가까운 대상만 고른다. 지형·포장면·바다·호안·골리앗 크레인은 가림막이라 그 뒤의 대상은 표시하지 않고, 아직 탑재되지 않은 블록은 콜라이더가 꺼진다. UI 위, 마우스 버튼을 누른 동안(카메라 드래그), 화면 밖에서는 숨긴다. 툴팁은 화면 안에 머물고 마우스 입력을 받지 않는다.
- 패널: 날짜, 탑재 단계, 상태(대기/재생 중/일시정지/완료), 블록 수와 중량 진척, 재생/속도/처음부터 버튼, 인양 중인 블록(구역, 중량, 크기, 선행 블록), 탑재 이력.
- 카메라: 왼쪽·가운데 드래그로 땅을 잡고 이동(커서 아래 지점이 커서를 따라감), 오른쪽 드래그나 Alt/Ctrl+왼쪽 드래그로 회전, 휠로 커서 쪽 확대, 더블클릭으로 그 지점을 중심에 둔다. WASD·화살표로 이동, Q/E로 회전, +/-로 확대한다. 패널·아이콘 바 위에서 시작한 드래그와 그 위의 휠은 카메라를 움직이지 않는다. 1 전체, 2 선수, 3 선미, 4 크레인, 5 위, 6 조선소, F 인양 블록 따라가기. 앱 옵션 `-shipyard-view <overview|bow|stern|crane|top|follow>`로 시작 시점을 정한다.
- 장면 구성:
  - `ShipView`: 도장된 블록. 인양 중인 블록은 주황빛으로 표시한다.
  - `GantryCraneView`: 골리앗 크레인. 고정 A프레임 다리와 유연 다리, 두 줄 박스 거더, 상부 트롤리(훅 2개, 슬링 4줄), 하부 트롤리, 바퀴 대차, 운전실이 있다.
  - `YardEnvironment`: 도크와 주변. 움푹 파인 도크, 킬·사이드 블록, 안벽, 도크 게이트, 바다, 조립 공장, 적치 블록, 그림자, 안개를 만든다.
  - `ShipPalette`: 도장 색. `paint.mtl`과 이름이 같다.

## 야드 배치 (2026-11-17 수정안)

크레인 계획, 안벽이동 계획, 의장작업계획을 담을 수 있도록 배치를 바꿨다. 기준은 `pipeline/generate_yard.py`의 `FACILITIES`다.

- **골리앗 크레인:** 도크마다 1대씩 둔다. 레일은 도크 안쪽 끝의 PE장(1·2 PE장, 95 × 100 m) 위에서 게이트 앞까지 깔린다. 탑재 중인 도크의 골리앗은 인양 블록을 따라가고, 다른 도크의 골리앗은 PE장 위에 대기한다.
- **지브 크레인:** 각 도크 바깥쪽 가장자리에 2대씩 둔다. 두 도크 사이에는 레일과 도로가 지나간다.
- **의장 피어:** 북서쪽 만으로 뻗은 640 × 40 m 피어 하나다. 양옆에 299 m 선박을 두 척씩 앞뒤로 대어 선석이 4개다. 러핑 크레인 6대가 좌우로 번갈아 서 있고, 2도크 게이트 출입로와는 약 60 m 떨어져 있다. 게이트 옆에 있던 의장안벽 2개는 없앴다. 선박보다 짧았고, 도크 쪽 면에 배를 대면 게이트 출입로를 막았기 때문이다.
- **안벽 의장 지원:** 피어 뒤 옛 PE장 자리에 안벽 의장공장과 의장 자재 적치장을 둔다.
- **카메라:** `pier` 시점으로 피어를 본다(`tools/capture.sh 33.3 out.png pier`).

## 운영 계획 예시 데이터

`pipeline/generate_operations.py`는 ERP의 크레인 계획, 안벽 계획, 의장작업 계획에 넣을 예시 데이터를 만든다(기준일 `--as-of`, 기본 오늘).

- **호선:** SN2040 외에 예시 호선 10척이 있다(SN2031~2035, SN2041~2045). LNG선 8척, VLGC 2척, 아프라막스 1척이다. 두 도크를 약 10주씩 번갈아 쓰고, 착공·기공·진수·시운전·인도 일정과 도크 점유 예약이 있다. SN2036~2039는 ERP 시드와 겹치지 않도록 쓰지 않는다.
- **크레인:**
  - 장비: 도크마다 골리앗 900 t 1대와 지브 50 t 2대, 피어에 러핑 40 t 6대, 트랜스포터 3대다.
  - 작업 배정: 2026-10 ~ 2027-04, 약 3,500건이다. 블록 탑재, PE 리프트, 턴오버, 의장품 인양, 안벽 의장 인양, 월간 정비를 넣었다.
  - 규칙: 같은 크레인의 작업은 07~23시 안에서 겹치지 않고, 인양 중량은 정격하중 이하다.
  - SN2040: 실제 탑재 계획 138블록을 골리앗 1호에 배정하고, 하루에 다 들지 못한 블록은 다음 작업일로 넘긴다.
- **선석과 이동:** 피어의 4개 선석을 겹치지 않게 자동 배정한다(안쪽 선석 우선).
  - 안쪽 선석이 비면 `선석 이동`을 넣는다.
  - 진수 후 이동, 시운전 출항·귀항, 인도 출항을 넣고, 기준일 이전 이동에는 실제 이동일을 기록한다.
- **의장 작업:** 호선마다 구역 7개 × 공종 × 단계로 약 90건, 모두 약 1,000건이다. 선후행 약 970건이 있고, 기준일 기준으로 진척률·완료·보류·지연이 섞여 있다.

## 지도 좌표 연동 (브이월드)

- **부지 기준점:** ERP의 `yard_sites`(위도, 경도, 야드 +x축 방위각)가 야드 미터 좌표를 위경도로 바꾼다. 시설 위경도와 모서리는 `GET /api/location_geo`로 받는다.
- **지도 화면:** ERP `/map`(상단 **조선소 지도** 버튼)에서 브이월드 지도 위에 시설과 도크의 선체를 표시한다. 십자 표시를 끌어 위치를 옮기고, 방위각으로 회전한 뒤 **부지 저장**을 누른다. 그다음 `export_twin.py`를 다시 실행하면 Unity 패널에 새 위경도가 나온다.
- **인증키:** 브이월드에서 직접 발급받아 ERP 서버 실행 환경변수 `VWORLD_API_KEY`에 넣는다. 파일에 저장하지 않는다. 지도 화면에서 이 브라우저에만 임시로 입력할 수도 있다.
- **기본 위치:** 거제·통영 부근(34.83N, 128.55E, 방위 180°)은 임시값이다. 실제 해안선에 맞는지는 지도에서 확인해 조정한다.
- **Unity:** 내보낸 `yard`(부지와 시설)로 도크 2기, 공장, PE장·적치장, 의장안벽, 안벽 지반, 바다를 만든다. 6번 키는 조선소 전체 시점이다.
- **소품과 도로:** `YardScenery`가 `scenery.json`으로 도로(차선), 소품, 운행 차량(블록 적재 트랜스포터, 순환 트랜스포터, 트럭, 지게차)을 만든다. 공장·창고는 시설 ID와 같은 이름의 생성 건물을 쓰고, 없으면 상자로 그린다. 배치를 바꾸면 `generate_yard.py` 다음에 `generate_scenery.py`를 다시 실행한다.
  - `generate_scenery.py`는 Unity와 같은 규칙으로 포장면 외곽선을 계산해 배치를 검사한다. 도로 표면(끝에서 폭의 절반만큼 더 나간 부분 포함), 차량 경로(루프의 마지막→첫 점 구간 포함), 가로등과 소품이 포장면이나 피어 밖에 있거나 도크·골리앗 레일·건물·야드와 겹치면 실패한다.
  - 도로망: 공장 열 뒤 간선과 공장·PE장 사이 간선, 도크 사이와 1도크 위 도로(게이트 쪽 포장면 안에서 끝남), 남쪽 도로 → 연결로 → 의장 피어 중앙 차도. 정문은 블록 적치장 남쪽, 포장면 서쪽 끝에 있다.
- **피어 접속:** 해안선에서 시작하는 안벽(의장 피어)은 뿌리 1m를 육지 시설로 보고 포장면 외곽선에 넣는다(`YardLayout.LandFootprints`). 그래서 포장면이 피어 상판까지 같은 높이로 이어진다. 접속부 양옆은 수직 안벽이 해수면 아래까지 내려가고, 안벽과 경사 호안이 만나는 모서리는 삼각면으로 막는다. 안벽 뒤 24m의 지형은 안벽 바닥보다 낮춰서 수면 위로 드러나지 않게 한다.

## 그래픽 품질

- **렌더링:** URP 17이 내장 패키지라 오프라인으로 쓴다. `Editor/ShipyardTwinRendering`이 빌드 때마다 아래를 만들고 굽는다.
  - 파이프라인 자산: 그림자 1.5km·4단계, 소프트 섀도, MSAA 4x, HDR
  - SSAO (반경 1.6m)
  - 후처리 볼륨: ACES, 블룸, 색보정, 비네트
  - 절차적 하늘, 환경광과 반사
- **상세 모델:** `pipeline/blender/build_assets.py`로 만든다. 골리앗 크레인(A프레임 고정 다리·트러스·유연 다리·계단·대차·워크웨이·상하 트롤리·훅 블록), 트랜스포터(축렬·서스펜션·휠림), 안벽 크레인(격자 지브), 공장(골강판·홈통·채광창·문틀·레일), 사무동, 차량이 들어 있다. 모서리를 둥글리고(bevel), 각도 기준으로 경계선을 나누고, 미터 단위 UV를 준다.
- **텍스처:** `pipeline/blender/build_textures.py`로 만든다. 콘크리트, 아스팔트, 도장 강판, 선체 외판(이음매·용접), 골강판, 수면이며 이음매 없이 반복된다. 도장 색 위에 곱해지고 미터 단위로 반복되도록 `ShipPalette.Details`에서 연결한다.
- **선체:** 블록 경계에 용접선을 넣는다. 선명은 선수 양현에, 선명과 선적항(GEOJE)은 선미에, 흘수 표시는 2m 간격으로 넣는다. 깊이를 검사하는 텍스트 셰이더(`Resources/ShipyardTwin/Shaders/Text3D`)를 쓴다.

## 수심도

- **생성:** `python3 pipeline/fetch_bathymetry.py` (위성 영역 기준이므로 `fetch_satellite.py` 다음에 실행한다).
  - 원천: 공공데이터포털 "해양수산부 국립해양조사원_자연과학용 수심정보 조회"(15142498). 호출 주소는 `https://apis.data.go.kr/1192136/waterDepth/GetWaterDepthApiService`이고, 응답 필드는 `lat`, `lot`, `dpwt`(m)다. 공공누리 1유형이라 출처를 표시해야 한다.
  - 인증키: 환경변수 `BATHYMETRY_SERVICE_KEY`나 `apps/twin/.env.bathymetry`에서만 읽고, 출력물에는 남기지 않는다.
  - 범위의 위경도는 소수 5자리까지만 받는다(6자리면 `INVALID_REQUEST_PARAMETER_ERROR`). 한 번에 300점씩 받는다.
  - 출력: `data/bathymetry/points.json`(원본 점, 캐시이며 `--refresh`로 다시 받음), 위성 격자에 맞춘 `bathymetry.bytes`·`bathymetry.json` → Unity `Resources/ShipyardTwin/Satellite/bathymetry_grid.bytes`, `bathymetry_meta.json`.
- **현재 자료:** 509점, -0.15 ~ 10.4 m. 위성 격자의 바다 칸 99%를 덮는다. 수심 기준면(약최저저조면/평균해면)은 API 문서에 없어 "미확인"으로 표시한다.
- **지형과의 차이:** Unity 해저(`terrain.bytes`)는 원본 표고에 수심이 없어 해안 거리로 만든 경사(최대 -75 m)라 실측보다 훨씬 깊다. 수심도는 해저 높이를 바꾸지 않고 바다 위에 겹쳐 그린다.

## 위성 바닥과 지형

- **생성:** `pipeline/fetch_satellite.py`를 실행한다. 중심은 내보낸 twin 파일의 부지(위경도·방위각)다.
  - 영상: 브이월드 위성 타일(zoom 16, 약 2m/화소, 12×12장, 약 6km 사방)을 `blender/stitch_satellite.py`가 한 장(JPEG)으로 잇는다.
  - 표고: Mapzen Terrain Tiles(AWS 공개 데이터, 약 30m, 해저 수심 포함)를 같은 영역의 385×385 격자로 만든다(`terrain.bytes`).
  - 해안선: 영상 기준으로 정한다. 도크 게이트 앞바다에서 이어진 물은 -3m 이하, 육지는 +0.5m 이상으로 맞춘다. 원본의 드문 오류값은 -60~800m로 자른다.
  - 인증키: 환경변수 `VWORLD_API_KEY`나 `../erp/.env`에서만 읽고, 출력물에는 남기지 않는다.
- **Unity 지형 (`TerrainModel`, `SatelliteGround`):** 영상의 각 화소를 웹 메르카토르 → 위경도 → 야드 좌표로 바꿔 실제 높이(해수면 0m = Unity 바다면)에 놓는다. 그래서 산이 솟고 해안은 바다로 경사져 들어간다.
  - 부지 둘레 140m: 안벽 높이로 매끄럽게 깎거나 쌓는다(정지 작업).
  - 부지 아래: 지형이 포장면 바로 밑에 놓인다.
  - 도크: 도크 자리만 비운다.
  - 조선소 앞바다: 물속으로 내린다.
- **상세 영상:** 조선소 주변 약 1.5km는 zoom 18(약 0.5m/화소) 영상을 따로 받아(`satellite_inner.jpg`) 더 촘촘한 지형 격자(약 4m)에 입힌다. 이 격자의 바깥 테두리가 큰 격자의 변과 맞물려 틈이 생기지 않는다.
- **해안 경사:** 30m 표고 자료에서는 언덕 높이가 물 바로 옆 칸에 놓여 절벽처럼 보인다. 그래서 물가에서 육지는 최대 1:2로만 오르고, 해저는 1:12~1:4로 완만하게 깊어지게 다듬는다.
- **조선소 앞바다 준설:** 피어 선석(피어 양옆 60m)부터 1도크 게이트 너머 10m까지는 모두 물속으로 내린다. 그 바깥(피어 쪽 150m, 1도크 쪽 60m)과 내륙 쪽 120m에서는 낮은 땅(해변·갯벌, 약 10m 이하)만 서서히 내리고, 언덕과 섬은 원래 모양을 지킨다. 1.5m 넘게 깎인 비탈은 위성 영상이 늘어나 보이지 않도록 흙·자갈 색 재질로 덮는다.
- **자연 해안:** 물가를 따라 반투명한 흰 파도 띠를 두르고(밀려오듯 움직임), 조선소 주변 물가에 바위를 흩어 놓는다.
- **부지 경계(육지 쪽):** 가상 야드가 실제 들판·산과 자연스럽게 이어지도록 처리한다.
  - 담장: 포장면 경계 3 m 안쪽에 조립식 콘크리트 담장(높이 2.4 m, 기둥 4 m 간격, 갓돌)을 두른다. 도로가 지나거나 나란히 달리는 곳(정문)과 크레인 레일 옆은 비운다.
  - 녹지 사면: 바깥 32 m, 그리고 정지로 2.5 m 넘게 깎거나 쌓은 땅(90 m까지)은 위성 사진 대신 풀 덮인 사면 재질로 칠한다.
  - 가로수: 9 m와 19 m 두 줄로 심는다.
  - 성능: 담장, 기둥, 줄기, 수관은 구역마다 메시 하나로 합쳐 그린다.
- **포장면과 호안:** 부지 윤곽(시설을 감싸는 볼록 다각형 + 28m, 해안선에서 자름)으로 포장면을 깐다.
  - 해안선(안벽·도크 게이트 쪽)은 수직 안벽이다.
  - 바깥이 바다인 다른 변은 1:1.5 사석 경사 호안이다. 경사면에는 바위를, 물가에는 테트라포드를 두 줄로 놓는다.
  - 바깥이 육지인 변은 지형을 깎거나 쌓아 잇는다.
  - 바다면은 도크 자리만 비운다.
- **카메라:** `coast` 시점(`tools/capture.sh 33.3 out.png coast`)으로 호안과 해안을 가까이 본다.
- **출처와 이용 조건:** 영상은 브이월드(국토교통부), 표고는 Mapzen Terrain Tiles(AWS Open Data)다. 외부에 공개하거나 배포하기 전에 각 이용 조건을 확인한다.
