# Shipyard

- ERP·Portal·Unity는 각각 `apps/erp`, `apps/portal`, `apps/twin`에 있다. 요청에 해당하는 앱만 열고, Unity 작업은 `apps/twin/AGENTS.md`와 그 작업별 경로 안내부터 읽는다.
- 포트와 실행 명령은 [개발 가이드](docs/development.md)에 있다. ERP와 Twin 사이의 데이터 변경은 [연동 기준](shared/contracts/README.md)을 확인한다.
- 비밀과 로컬 DB는 Git에 올리지 않는다. 실행 중인 서버와 DB는 요청 범위에 따라 조작한다.
- Claude·Codex 간 작업 분리, 코드 리뷰, 검증과 인수인계는 [공통 작업 절차](docs/ai-workflow.md)를 따른다.
