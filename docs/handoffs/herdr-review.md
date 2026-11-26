# Herdr 교차 리뷰 인수인계 — 크레인 위험반경·반경별 정격하중

- 요청: Twin에서 크레인을 선택하면 위험반경과 반경별 정격하중을 표시한다. ERP에 데이터가 없으면 ERP에 추가한다.
- 비교 기준: 세션 시작 시점의 HEAD `bcc2d8a`와 당시 미커밋 작업 트리. 공유 폴더라 아래 파일에는 다른 세션의 변경이 섞여 있다. 본인 변경은 아래에 적은 함수와 블록뿐이다.
- 세션 siws-1a가 같은 UI 파일(Dashboard.uxml/.uss, shipyard.build.json, DashboardShellView.cs, RailCraneView.cs의 `.Identify`)에 ObjectDetailView를 추가했다. 그 부분은 본인 작업이 아니다.
- ERP `geometry.py`
  - `crane_kinematics.danger_margin_m`(기본 5)를 추가했다.
  - 새 테이블 `crane_load_charts(equipment, radius_m, capacity_t)`와 `upgrade_crane_kinematics`, `reach`, `lift_radius`, `chart_points`, `chart_capacity`를 추가했다.
  - write 검증: 반경은 붐 도달 범위 안이어야 하고, 하중은 장비 정격 이하이며 반경이 커질수록 같거나 줄어야 한다.
- ERP 연결
  - `yardplan.py`: `check_chart`를 추가하고 `crane_lifts`와 `crane_assignments`에서 정격 초과를 거부한다.
  - `yardsim.py` `jib_state`: `danger_radius_m`, `rated_t`, `overload`, `load_chart`를 추가했다.
  - `app.py`는 upgrade 호출과 route를, `menus.py`는 "크레인 정격하중표" 메뉴를 추가했다.
  - `seed_yard_cranes.py`: `chart_for`와 `assign_load_charts`를 추가했다. `assign_lifts`는 정격을 넘는 인양 중량을 낮춘다.
- Twin
  - Data: `YardDataSource.cs`와 `ShipyardTwinData.cs`에 `CraneLoadChartRow`와 `danger_margin_m`을 추가했다.
  - `RailCraneView.cs`: `RatedAt`, `DangerRadius`, 공개 `Crane`, `Find`, `Rebuilt` 이벤트를 추가했다.
  - 새 `Runtime/Yard/CraneSafetyView.cs`: 선택한 크레인 둘레에 링, 라벨, 패널을 그린다.
  - Authoring: Dashboard.uxml에 `crane-panel`, Dashboard.uss에 `.tw-crane*`, shipyard.build.json에 `CraneSafetyView`를 추가했다.
  - `pipeline/export_yard.py`에 `crane_load_charts` route를 추가했다.
- 문서·테스트: `shared/contracts/README.md`에 규칙을 적었다. `test_yardsim.py`와 `Tests/Editor/ShipyardTwinTests.cs`에 테스트를 1개씩 추가했다.
- 테스트 결과
  - ERP 단위 테스트 69/69 통과.
  - Unity EditMode는 siws-1a 변경까지 합쳐 `tools/test.sh` 54/54 통과.
  - `tools/build.sh` macOS 빌드 성공.
  - `tools/capture.sh`로 링, 라벨, 패널을 확인했다. 확인할 때만 임시로 차트를 snapshot에 주입했고, 끝난 뒤 원본으로 되돌렸다(cmp 동일).
- 남은 검증
  - 실DB에서 `seed_yard_cranes.py`를 아직 돌리지 않았다. 일부 계획 인양 중량이 낮아지므로 먼저 `--dry-run`으로 확인해야 한다.
  - snapshot 재-export도 하지 않았다. 그래서 오프라인 앱에는 아직 차트가 없다.
  - 골리앗 크레인은 대상에서 빠졌다.
  - 모바일·세로 레이아웃은 실기기로 확인하지 않았다.

## 수정 및 검증

- 범위: `apps/erp/seed_yard_cranes.py`(모듈 설명, `assign_load_charts`, `assign_lifts`, `main`, 새 `prepare`/`finish`)와 `apps/erp/test_yardsim.py`(새 `FakePostgres`, `SeedYardCranesTest` 3건, import 추가)만 바꿨다. 다른 파일과 다른 세션의 변경은 건드리지 않았다.
- 1. dry-run 트랜잭션
  - `prepare`: PostgreSQL에서 `--dry-run`이면 `init_schema`가 잡은 advisory lock과 DDL(CREATE/ALTER)을 커밋하지 않고 같은 트랜잭션에 둔다. 마지막 `finish`의 rollback이 스키마 준비와 미리보기 데이터를 함께 되돌린다.
  - 실제 적용 경로는 기존 순서를 유지한다. 스키마를 commit한 뒤 `begin_write`, 마지막에 commit한다.
- 2. 정격하중표 보존: `assign_load_charts`는 DELETE를 하지 않는다. `chart_points`가 비어 있는 장비에만 데모 표를 채우고 (신규, 보존) 대수를 반환한다.
- 3. 계획 중량 보존
  - `assign_lifts`는 더 이상 `crane_assignments.lift_t`를 쓰지 않는다.
  - 표를 초과하는 추정 지점은 `crane_lifts`에 등록하지 않고 보류 건수로 센다. `main`이 "정격하중 초과로 등록 보류 N건"을 출력한다.
  - 정격 확인과 DB 기록에 같은 값(소수 2자리로 반올림한 pick/drop x·y)을 쓴다.
  - 남은 한계: 이전 실행이 남긴 해당 작업의 기존 '추정' 행은 삭제하지 않고 그대로 둔다.
- 격리 테스트: 임시 SQLite fixture와 서버 없는 `FakePostgres`만 쓴다.
  - `test_dry_run_rolls_back_schema_and_preview_in_one_transaction`
  - `test_rerun_keeps_an_existing_load_chart`
  - `test_over_chart_lift_is_held_and_planned_weight_kept`: 반올림 전 20.006 m는 통과하지만 저장값 20.01 m는 초과하는 경계 사례를 포함한다.
- 회귀 확인
  - 수정 전 코드의 백업을 별도 프로세스로 불러와 같은 3건을 돌렸다. 3건 모두 실패했다(FAIL 2, ERROR 1). ERROR는 반올림 불일치로 ERP write가 거부한 경우다.
  - 수정 후에는 3/3 통과.
- `tools/verify erp`: exit 0, 74 tests OK.
  - 샌드박스가 `apps/erp/.env.example` 읽기를 막는다는 "Operation not permitted" 경고가 한 줄 출력됐다. 스크립트는 멈추지 않았다.
  - 변경 파일 `git diff --check`도 통과했다.
- 실행하지 않은 것: 실제 DB 대상 시드와 `--dry-run`. 운영 DB, 기존 PostgreSQL·Redis, 서버, Git도 변경하지 않았다.
- 위 "남은 검증"의 "일부 계획 인양 중량이 낮아지므로"는 이번 수정으로 더 이상 맞지 않는다. 이제 중량을 낮추지 않고 등록을 보류한다.

## 독립 재검증

- Codex가 Claude 수정분을 다시 읽고 `tools/verify erp` 74개 통과와 `tools/verify twin` 62개 통과를 확인했다.
- 별도 일회용 PostgreSQL 17에서 구버전 붐 제원 테이블을 만들어 검증했다. 미리보기 트랜잭션에서 추가한 컬럼, 정격하중표 테이블, 임시 데이터가 모두 롤백됐고, 일반 적용 경로는 변경 사항을 커밋했다. 검증 후 임시 컨테이너를 정리했다.
- 운영 DB의 기존 계획과 등록된 추정 지점은 그대로다. 실제 DB에서의 시드 적용, 오프라인 스냅샷 갱신, 아이폰 화면 확인은 별도 작업이다.
