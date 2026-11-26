# 오브젝트 상세 팝업

3D 화면에서 오브젝트를 클릭(터치는 탭)하면 오른쪽에 공용 상세 팝업이 열린다. 작은 화면에서는 아래쪽 시트로 열린다. 팝업은 오른쪽에서 밀려 들어오며 나타나고, 섹션이 차례로 올라온 뒤 그래프가 그려진다. 선택한 모델에는 외곽선과 은은한 윤곽 발광이 표시된다(`SelectionHighlight`). 모델의 재질, 그림자, 콜라이더는 바꾸지 않는다. 지게차, 트랜스포터 같은 중장비(`HoverTarget.IsHeavyEquipment`)를 선택하면 바닥에 모델을 따라가는 "모의 위험반경" 링과 라벨을 그리고(`EquipmentZone`), 팝업에 추정값이라는 안내를 띄운다. 이 반경은 ERP 검증값이 아니며 모델의 바닥 점유 범위로 추정한 시각용 값이다. 크레인은 기존 `CraneSafetyView` 위험반경을 쓰고, 승용차와 트럭에는 링이 없다. 닫기는 X 버튼, Esc, 또는 같은 오브젝트를 다시 클릭한다. 드래그, 대시보드에서 시작한 클릭, 이름 없는 곳 클릭으로는 팝업이 바뀌지 않는다.

| 오브젝트 | 상태·요약 | 그래프 |
| --- | --- | --- |
| 호선(선체·킬블록) | 공정 상태, 현재 위치, 다음 안벽 이동, 이동 지연, 인도 예정, 탑재 합격 | 도크·선석·안벽 이동·마일스톤 타임라인, 의장 진척 도넛과 구역별 공수 막대, 의장 작업 타임라인(진척 표시), 블록 탑재 도넛과 누적 S-커브 |
| 블록 | 탑재 상태, 중량·순서, 계획·예정·실적일, 현재 위치·현재 작업장(단계·종료일), 선행 블록 | 조립 진척 도넛과 단계별 진척률, 단계별 계획·실적 공수, 조립 단계·적치·탑재 타임라인 |
| 크레인(레일 지브·골리앗) | 오늘 작업, 정격하중, 레일·붐 제원, 다음 작업 | 주간 가동률(85% 기준선), 작업 배정 타임라인, 인양 중량과 정격하중선 |
| 운반 장비 | 이동 중 여부, 이동 실적, 미확정 이동 | 최근 블록 이동 타임라인, 주간 이동 건수 |
| 도크·안벽 | 현재 호선·접안, 다음 입거 | 향후 90일 점유 도넛, 점유·선석 배정 타임라인, 안벽 입출항 |
| 작업장 | 작업 중 블록, 단계 지연, 반출 대기 | 현재 블록 배치(단계별 레인: 절단·가공 / 소조립 / 중조립 / 대조립 / 반출 대기, 블록 칩에 남은 일수(D-n, D-day)·지연·반출 대기 일수, 여러 호선이면 호선), −1주~+4주 작업장 블록 수 |
| 창고·야드 | 현재 적치 블록 수, 이번 주 크레인 작업 | 주별 적치 블록 수, 적치 일정 |
| 배경 차량·지게차·건물 | ERP 미등록 배경 요소 안내 | 없음 |

## 데이터

- 야드 스냅샷 또는 ERP 시작 조회(`YardDataSource`)에 있는 데이터로 즉시 그린다. 기준일은 재생 중인 날짜(`ShipView.CurrentDate`, 호선 미선택 시 야드 달력)이며, 날짜가 바뀌면 열린 팝업을 애니메이션 없이 다시 그린다.
- 작업장 배치와 블록의 현재 작업장은 `YardModel.ShopBlocks`, `BlockPlan.ShopAt`(스냅샷 `block_stages`, `center_locations`)에서 계산한다. ERP `block_stages`를 받기 전이나 오프라인에서는 블록 조립 진척도 스냅샷 단계 계획으로 그린다(공수 제외).
- 스냅샷에 없는 조회는 팝업을 열 때 ERP에서 읽는다. 호선은 `outfit_works`, `outfit_progress`, `hull_milestones`, 블록은 `block_stages`, `block_progress`, 크레인은 `crane_load`를 읽는다(`Runtime/Detail/ErpDetailCache.cs`). 결과는 1분 동안 재사용한다. 응답 전에는 해당 섹션을 "불러오는 중"으로 표시하고, 권한이 없거나 ERP가 연결되지 않았으면 그 사유를 표시한다.
- 오브젝트 식별자는 `HoverTarget.Identify(분류, ERP ID, 호선, 위치)`로 붙인다. 새 오브젝트를 클릭 대상으로 만들 때는 `HoverTarget.Attach(...).Identify(...)`를 호출하고, 필요하면 `ObjectDetailBuilder`에 분류별 빌더를 추가한다.

## 코드

| 역할 | 경로(`unity/Assets/ShipyardTwin/` 기준) |
| --- | --- |
| 클릭 처리, 팝업 렌더링·애니메이션 | `Runtime/Dashboard/ObjectDetailView.cs` |
| 선택 모델 외곽선, 중장비 모의 위험반경 | `Runtime/Interaction/SelectionHighlight.cs`, `EquipmentZone.cs`, 셰이더 `Runtime/Resources/ShipyardTwin/Shaders/Selection{Fill,Outline,Zone}.shader` |
| 오브젝트별 내용(순수 데이터) | `Runtime/Detail/ObjectDetailBuilder.cs`, `ObjectDetail.cs` |
| ERP 추가 조회 | `Runtime/Detail/ErpDetailCache.cs` |
| 레이아웃·스타일 | `Authoring/Dashboard.uxml`(`object-detail`, `detail-zone`), `Authoring/Dashboard.uss` |
| 그래프 | `shared/unity-packages/com.growthlab.ui/Runtime/Charts/` |
| 테스트 | `Tests/Editor/ObjectDetailTests.cs`, `SelectionHighlightTests.cs`, 공용 그래프는 `com.growthlab.ui/Tests/Editor/ChartTests.cs` |

크레인 안전 패널(`CraneSafetyView`, `crane-panel`)이 같은 오른쪽 위에 열려 있으면 상세 팝업은 그 왼쪽으로 비켜 선다. 캡처용 명령행 인수는 `-shipyard-select <ID 또는 x,y,z>`다.
