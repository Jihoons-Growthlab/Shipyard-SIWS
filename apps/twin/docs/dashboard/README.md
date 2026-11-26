# 대시보드 기능 명세

Unity 앱은 시작 시 조선소 화면과 왼쪽 아이콘 바만 표시한다. 탑재 패널은 닫힌 상태이고, 패널의 재생 버튼을 눌러야 시뮬레이션이 시작된다(`ShipyardTwinSettings.AutoPlay` 기본값 false). 진입점은 `ShipyardTwinBootstrap`이고, 아이콘 바·패널 열고 닫기·종료·수심도 토글은 `Runtime/Dashboard/DashboardShellView.cs`가 맡는다. 스타일은 `Authoring/Dashboard.uss`, 아이콘은 `Runtime/Dashboard/DashboardIcon.cs`(벡터)다.

| 작업 | 명세 |
| --- | --- |
| 화면 오른쪽 위 종료 | [exit.md](exit.md) |
| 화면 오른쪽 아래 야드 안내원(챗봇) | [chat.md](chat.md) |
| 좌측 내비게이션의 통합검색 | [object-search.md](object-search.md) |
| 아래 일정 바와 재생 중 배너 | [schedule-bar.md](schedule-bar.md) |
| 오브젝트 클릭 상세 팝업과 그래프 | [object-detail.md](object-detail.md) |
| 에디터 검증 및 MCP 연결 | [unity-tools.md](unity-tools.md) |

각 명세의 소스 경로는 `unity/Assets/ShipyardTwin/` 기준이다. 공통 레이아웃은 `Authoring/Dashboard.uxml`에서 수정한다. 기능별 로직은 필요한 경우 `Runtime/`에 별도 클래스로 두고 기존 대시보드 및 생성 흐름을 사용한다. 검색창과 챗봇에 입력하는 동안 카메라 단축키와 포인터 조작이 개입하지 않아야 한다. 화면 크기를 줄여도 각 컨트롤을 사용할 수 있어야 한다.

## 조선소 달력 재생 (호선 미선택 시 기본 화면)

- `ShipView.Calendar`(`YardCalendar`)가 모든 호선의 ERP 계획을 한 시각으로 재생한다. 범위는 첫 공장 착수·적치부터 마지막 인도 출항 3일 뒤까지이고, 끝나면 처음으로 돌아가 반복한다.
- 블록 위치 규칙:
  - 공장: ERP `block_stages`의 작업장 단계(절단·가공, 소조립, 중조립, 대조립)와 반출 대기. 공장 크기보다 블록이 많아 3D에는 그리지 않고, 공장 위 라벨(`YardClockView`)과 공장 상세 팝업의 배치로 보여 준다.
  - 적치: `block_stock_plans` 슬롯. 다음 슬롯 전날에는 트랜스포터로 옮긴다.
  - 탑재: 탑재일에 골리앗이 인양한다. 인양 중에는 달력이 0.15배로 감속한다.
- 선박 위치 규칙: 도크 예약 기간에는 도크, 그 뒤에는 `quay_shifts`가 가리키는 선석에 있다. 시운전과 인도 뒤에는 화면에서 사라진다.
- 하단 달력 바: 날짜, 일시정지·재생, 속도(×0.25~×8), 진행 막대(눌러서 날짜 이동), 진행 중인 작업 한 줄. 호선을 고르면 달력은 그 시각에 멈추고, 선택한 호선의 탑재 재생이 그 날짜부터 시작한다. '종료'를 누르면 달력이 멈춘 곳에서 이어진다.
