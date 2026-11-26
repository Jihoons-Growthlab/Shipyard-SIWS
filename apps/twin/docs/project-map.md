# ShipyardTwin 작업별 경로

아래 Unity 경로는 `unity/Assets/ShipyardTwin/` 기준이다. 작업에 맞는 행의 파일과 명세부터 읽는다.

| 작업 | 시작 위치 |
| --- | --- |
| 앱 초기화 | `Runtime/Bootstrap/ShipyardTwinBootstrap.cs` |
| ERP 조회와 블록·야드 데이터 | `Runtime/Data/YardDataSource.cs`, `YardModel.cs`, `ShipyardTwinData.cs` |
| 호선별 탑재 재생과 크레인 | `Runtime/Erection/`의 `ShipView.cs`, `ErectionSchedule.cs`, `BlockMotion.cs`, `GantryCraneView.cs` |
| 도로, 안벽, 시설과 지형 | `Runtime/Yard/`, [도로·안벽 명세](yard-road-quay-fix.md) |
| 카메라와 오브젝트 이름 | `Runtime/Interaction/`, [호버·카메라 명세](yard-hover-camera.md) |
| 오브젝트 클릭 상세 팝업과 그래프 | `Runtime/Dashboard/ObjectDetailView.cs`, `Runtime/Detail/`, [상세 팝업 명세](dashboard/object-detail.md) |
| 패널, 아이콘과 수심도 UI | `Runtime/Dashboard/`, `Authoring/Dashboard.uxml`, [대시보드 명세](dashboard/README.md) |
| 공용 메시, 재질과 좌표 변환 | `Runtime/Rendering/` |
| 렌더링 제한과 성능 측정 | `Runtime/Performance/`, [성능 인수인계](performance-handoff.md) |
| 원본 데이터 내보내기와 소품 생성 | 프로젝트 루트 `pipeline/`, [Twin 데이터 형식](TWIN_FORMAT.md) |
| 씬 생성과 검증 | `Authoring/`, `Editor/`, `Tests/`, 프로젝트 루트 `tools/` |

런타임 기능 폴더는 하나의 `Runtime/ShipyardTwin.asmdef`를 공유한다. `Runtime/Resources/`는 Unity 리소스 경로를 유지한다. 스크립트 파일을 이동할 때는 대응하는 `.meta`를 함께 이동한다.
