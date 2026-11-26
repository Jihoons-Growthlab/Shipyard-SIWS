# Twin 야드 오브젝트 검색 UI (Claude → Codex 인수인계)

## 범위

- 기준: 공유 작업 트리(미커밋 변경 포함). Claude는 UI와 상호작용을 맡았다.
- Codex 담당 파일은 편집하지 않았다(`YardObjectSearch.cs`, OrbitCameraRig, HoverTarget, ObjectDetailView).
- Generated/, 씬·프리팹, Git, DB, WebGL 빌드·배포는 건드리지 않았다.

| 파일 (`apps/twin/unity/Assets/ShipyardTwin/` 기준) | 변경 |
| --- | --- |
| `Runtime/Dashboard/DashboardObjectSearch.cs` (신규) | 검색 서랍 UI. 140 ms 디바운스 질의, 아이콘 카드, 빈 검색 안내와 결과 없음, "화면에 없음" 상태, 탭과 드래그 구분, 키 처리, 2.5초 제자리 갱신, `-shipyard-search` 캡처 인수 |
| `Runtime/Dashboard/DashboardShellView.cs` | 세 번째 서랍 `search-panel` 추가. 열기 시 `SearchOpened`, `OpenSearch()`, `CollapseSearch()`, `IsSearchOpen`, `SearchFieldFocused`, `SearchControlsFocused`. 검색창의 Escape 처리, 닫힐 때 검색창 Blur, 포인터 차단 목록 추가 |
| `Runtime/Dashboard/DashboardIcon.cs` | 글리프 6종(`search`, `block`, `hull`, `vehicle`, `facility`, `building`)과 `Glyphs`, `GlyphFor(TargetCategory)` 추가 |
| `Authoring/Dashboard.uxml` | 레일 첫 메뉴 `nav-search`와 `search-panel`(검색 상자, 상태 줄, 스크롤 결과, 빈 상태) 추가 |
| `Authoring/Dashboard.uss` | `tw-search-*` 스타일: 카드, 분류 색, 눌림·흔들림·화면에 없음, compact·portrait |
| `Authoring/shipyard.build.json` | Panel 컴포넌트에 `DashboardObjectSearch` 추가 |
| `Tests/Editor/DashboardSearchTests.cs` (신규) | 5건: 강조와 이스케이프, 카드 문구와 건수와 자르기, 분류 아이콘, UXML·빌드 연결, 포커스 속성 기본값 |
| `apps/twin/docs/dashboard/object-search.md` | 검색 명세를 다시 썼다. |

## Codex 계약 사용

- **검색**: `YardObjectSearch.Find(ShipView.Model, query.Trim(), 40)`. 모델이 없으면 "야드 데이터를 불러오는 중"을 표시한다.
- **열기**: `YardObjectSearch.Open(result)`가 true면 `DashboardShellView.CollapseSearch()`로 서랍을 접는다. false면 카드를 "화면에 없음"으로 바꾸고 0.4초 뒤 다시 조회한다.
  - 예외는 경고 로그만 남기고 실패로 처리한다.
  - `IsAvailable == false`인 카드는 `Open`을 호출하지 않고 안내만 한다. 날짜와 재생은 건드리지 않는다.
- **`YardSearchResult`**: readonly struct이고 생성자가 internal이다. 그래서 null 비교를 하지 않는다. 테스트 헬퍼는 문자열과 분류 값을 직접 받는다.
- **카메라 단축키 차단**: `DashboardShellView.SearchControlsFocused`(서랍 안 아무 요소나 포커스)를 쓴다. `SearchFieldFocused`(검색창만)는 Escape 처리에 쓴다. 둘 다 읽을 때 `focusController.focusedElement`로 계산하고, 서랍이 닫히면 false다.

## Codex 리뷰 보완

- **입력 직후 키 입력**: 입력 후 140 ms 안에 Enter·↓를 누르면 `FlushPendingSearch()`가 예약 검색을 취소하고 현재 입력으로 바로 다시 조회한 뒤 키를 처리한다. 현재 입력과 마지막 질의가 같으면 다시 조회하지 않는다.
- **카드 포커스 중 카메라**: 서랍 전체를 보는 `SearchControlsFocused`를 추가했다.

## 검증

- `tools/test.sh` EditMode: 72/72 통과. 신규 `DashboardSearchTests` 5건도 통과했다.
  - 재실행 한 번은 Codex의 WebGL 빌드가 같은 프로젝트를 열고 있어 충돌했다(exit 1).
  - 그 빌드가 끝난 뒤 최종 코드(보조 정보 60자 포함)로 다시 돌린 결과: exit 0, 72/72 통과.
- `tools/build.sh` macOS 빌드 성공.
- macOS 앱 캡처
  - 1600×900에서 `-shipyard-search 241C`, `크레인`, `없는이름xyz`의 결과 카드, 일치 부분 강조, 건수와 화면에 없음 수, 결과 없음 상태를 확인했다.
  - 430×860 세로 화면에서 아래쪽 시트와 카드 배치를 확인했다.
- 하지 못한 것
  - 실제 클릭·탭으로 카드를 열어 카메라가 이동하고 서랍이 접히는지(입력 장치가 필요해서).
  - 보조 정보 길이를 60자로 줄인 뒤의 화면 캡처(테스트만 함).
  - WebGL과 모바일 키보드.

## Codex가 확인할 것

- WebGL에서 확인할 것:
  - iPhone Safari에서 TextField를 탭하면 모바일 키보드가 뜨는지, 한글 IME 조합 중 결과가 갱신되는지.
  - Enter·↓ 키 처리와 `SearchControlsFocused`에 따른 카메라 단축키 차단.
- 카드를 열면 카메라가 이동하고, 상세 팝업과 하이라이트가 열리고, 서랍이 접히고, 재생 날짜가 그대로인지 확인한다.
- 크레인 결과의 `Subtitle`에 호버 안내 문구("클릭하면 위험반경·반경별 정격하중")가 섞여 있다. 카드에서는 60자에서 잘리지만, 검색용 보조 정보는 짧게 주는 편이 낫다.
- 검색창에 포커스가 있을 때 Escape를 누르면 `CraneSafetyView`도 자체 Escape로 선택한 크레인을 해제한다(기존 동작이며 이번에 바꾸지 않았다).
- 검색 서랍 뒤로 WorldLabels 라벨이 흐리게 비치는 현상은 다른 서랍과 같은 기존 동작이다.

## Codex 통합 검증

- 포커스 검색은 등록된 씬 오브젝트와 ERP 계획을 함께 조회한다. 화면에서 보이지 않는 항목은 선택할 수 없고, 검색으로 재생 시점이나 호선 선택을 바꾸지 않는다.
- `tools/verify twin` EditMode 72개 통과. PlayMode의 `YardSearch_FramesAFacilityAndOpensItsDetailWithoutSeeking`에서 도크 결과 선택, 카메라 목표 변경, 상세 팝업 열기, 재생 날짜 불변을 검증했다.
- WebGL 빌드 성공. 본인용·친구용 HTTPS에서 새 WASM·데이터 파일을 해시 비교했다. Mac 앱의 데스크톱 및 세로 폭 캡처를 확인했고 아이폰 Safari 입력은 실기기 확인 전이다.
