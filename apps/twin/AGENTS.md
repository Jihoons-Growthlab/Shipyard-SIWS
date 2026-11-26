# ShipyardTwin

조선소 디지털 트윈 프로젝트다. ShipYard-ERP의 호선 데이터로 블록 탑재 과정을 Unity에서 재생한다. 트윈 관련 작업은 모두 이 프로젝트에서 한다.

- 이웃 폴더 역할:
  - `../erp`: 데이터 원천(API).
  - `../../tools/migration/migrate.py`: ERP 업로드 도구.
  - `../../shared/unity-packages`: 재사용 모듈 라이브러리. Unity 프로젝트가 `file:`로 참조한다(core, data, network, ui, authoring). 사용법은 `../../shared/unity-packages/docs/MANUAL.md`와 `../../shared/unity-packages/docs/AUTHORING.md`를 참고한다. 트윈 도메인 코드나 데이터는 넣지 않는다. 범용 기능이 필요할 때만 모듈로 추가한다.
- 씬, 프리팹, UI는 `unity/Assets/ShipyardTwin/Authoring/`(shipyard.build.json, dock.csv, Dashboard.uxml)을 고친 뒤 생성한다. `Generated/`와 `.unity`/`.prefab`은 읽거나 고치지 않는다.
- 검증 명령:
  - `tools/test.sh`(EditMode)
  - `tools/build.sh`(macOS 앱)
  - `tools/build-web.sh`(WebGL, Portal `/twin/`에서 Mac·Windows·iPhone 브라우저로 실행)
  - `tools/capture.sh <일> <png> [overview|bow|stern|crane|top]`(화면 캡처)
  - 파이프라인은 README.md의 "데이터 갱신" 절차를 따른다.
- ERP 계정, 토큰 등 비밀은 파일이나 로그에 남기지 않는다. 환경변수로만 쓴다.

- 작업별 코드와 명세 위치는 [docs/project-map.md](docs/project-map.md)를 참고한다.
