---
description: 변경 앱에 맞는 SIWS 검사 실행과 결과 보고
---
현재 git diff와 사용자 요청으로 검사 범위를 정한다. $ARGUMENTS가 있으면 해당 범위를 우선한다.
1. docs/ai-workflow.md의 공통 검증 기준에 따라 저장소 루트에서 tools/verify에 변경 범위(erp, portal, twin, nginx)를 전달한다.
2. 화면 변경은 실행 중인 해당 화면에서 동작을 확인한다. 테스트 수집을 시작했다면 원래 수집 상태로 돌린다.
3. Unity 변경은 apps/twin/CLAUDE.md와 apps/twin/docs/project-map.md를 읽는다. WebGL 배포가 요청 범위면 apps/twin/tools/build-web.sh로 빌드하고 포털 /twin/에서 확인한다.
4. 문서만 수정했다면 변경분 공백, 상대 링크와 미디어 존재 여부를 확인한다.
실행 명령, 성공/실패, 실제로 확인하지 못한 항목을 보고한다. 실패를 수정한 뒤 관련 검사만 다시 실행한다.
