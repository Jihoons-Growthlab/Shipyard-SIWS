# Claude Code 프로젝트 설정 매뉴얼

2026-11-19에 설치된 Claude Code 2.1.327과 공식 문서를 기준으로 작성했다.

## SIWS에 적용한 구성

| 파일 | 역할 |
| --- | --- |
| ../../CLAUDE.md | 짧은 프로젝트 진입 안내와 데이터 연동 기준 |
| ../../.claude/settings.json | 비밀 파일 읽기 거부, Bash 샌드박스, 검사 훅 |
| ../../.claude/hooks/check.py | Python·JSON·Shell 문법 검사와 변경분 공백 오류 검사 |
| ../../.claude/commands/siws-check.md | 변경 앱별 검증 절차 |
| ../../.claude/commands/siws-audit.md | 프로젝트 지침과 설정 점검 |

프로젝트 루트에서 Claude Code를 새로 시작한 뒤 /hooks에서 등록된 훅을 확인한다. /siws-check로 변경 범위 검사를 요청하고 /siws-audit로 지침 중복을 점검한다. 실행한 명령과 실제 결과를 함께 보고하도록 구성했다.

기존 루트 CLAUDE.md는 짧아서 필요한 프로젝트 규칙을 유지했다. Unity의 생성 파일 규칙과 공용 패키지 안내는 하위 CLAUDE.md에 유지한다.

## 검사 동작과 범위

PostToolUse는 Edit 또는 Write가 끝난 뒤 해당 Python 파일을 AST로 파싱하고, JSON을 파싱하며, .sh는 bash -n으로 검사한다. 파일을 다시 포맷하거나 실행하지 않는다. 실패 시 stderr와 종료 코드 2로 Claude에 피드백한다. 이미 수행된 편집은 남아 있으므로 Claude가 후속 수정해야 한다.

Stop은 git diff --check와 git diff --cached --check를 실행한다. 실패 시 종료 코드 2를 반환한다. stop_hook_active가 참이면 반복 차단을 피한다. 이 검사는 단위 테스트 성공을 의미하지 않는다.

전체 ERP 테스트나 Unity 빌드는 /siws-check에서 변경 범위에 맞게 실행한다. Unity 빌드는 시간이 길고 실행 환경에 의존하므로 매 응답마다 자동 실행하는 훅에 넣지 않았다. Bash·MCP로 수정한 파일은 Edit/Write 훅의 대상에서 빠질 수 있으므로 완료 전에 범위 검사를 수행한다.

## 비밀과 샌드박스

Read 거부 규칙은 프로젝트 하위의 .env, .env.*, secrets 및 사용자 SSH·AWS 파일을 대상으로 한다. .env.example도 패턴에 포함된다. 변수 이름은 개발 가이드와 소스에서 확인하고 실제 값을 문서에 복사하지 않는다.

샌드박스는 enabled로 설정했고 autoAllowBashIfSandboxed는 false다. SSH·AWS 읽기를 제한했다. 네트워크 허용 도메인은 실제 사용하는 의존성·ERP·Docker·Unity 경로를 확인한 뒤 /sandbox에서 정한다. 이 구성은 네트워크 도메인 목록을 별도로 고정하지 않는다. 파일 도구의 권한 규칙과 Bash 샌드박스는 적용 범위가 다르다. MCP와 훅 자체의 동작도 별도로 확인해야 한다.

현재 사용자의 ~/.claude/settings.json은 기존 개인 설정을 유지한다. auto 기본 모드와 autoMode 신뢰 범위는 모든 프로젝트에 영향을 주므로 프로젝트 설정에 섞지 않았다. 필요하면 공식 자동 모드 설정을 확인하고 사용자 파일에 기존 키를 보존해 병합한다. JSON 전체를 덮어쓰지 않는다.

## 다음 프로젝트에 적용하는 순서

1. 프로젝트 루트에서 claude --version을 확인한다. 기존 CLAUDE.md, .claude/settings.json, settings.local.json의 존재와 적용 범위를 확인한다. 비밀 값을 출력하지 않는다.
2. 코드로 파악하기 어려운 규칙, 빌드·테스트 명령, 생성 파일 처리만 CLAUDE.md에 짧게 적는다. 모노레포의 앱별 규칙은 해당 폴더의 CLAUDE.md에 둔다.
3. 기존 설정에 permissions.deny와 hooks를 병합한다. SIWS settings.json을 통째로 복사하면 기존 권한·훅이 사라질 수 있다.
4. .claude/hooks/check.py를 복사하고 언어에 맞는 검사를 고른다. 이 스크립트는 Python 3.9 이상과 git, .sh 검사 시 bash가 필요하다. Node 프로젝트라면 설치된 린터와 package.json의 실제 명령을 사용한다.
5. .claude/commands에 프로젝트 이름의 check.md와 audit.md를 만든다. SIWS의 경로·ERP 테스트·Unity 명령을 새 프로젝트의 실제 진입점으로 교체한다.
6. 새 세션의 /hooks에서 적용 여부를 확인한다. 임시 파일로 정상 문법은 성공하고 잘못된 문법은 종료 코드 2가 되는지 검사한다.
7. 단위 테스트와 화면 동작을 실제로 실행한다. DB·서버·클라우드를 변경하는 명령은 프로젝트의 작업 범위에 맞춘다.
8. 검증한 프로젝트 설정을 Git에 커밋한다. settings.local.json과 실제 비밀 파일의 Git 제외 여부를 확인한다.

설치 버전에 /doctor prompt-audit가 제공되면 활용할 수 있다. 이 매뉴얼은 해당 명령을 실행해 감사 결과를 받은 것으로 취급하지 않는다. 로컬 /siws-audit는 이 저장소에 추가한 명령이다. 강연에 나온 모델명·성능 수치·권한 기본값은 설치 검증의 근거로 사용하지 않았다.

## 다음 프로젝트에서 사용할 요청문

> 이 저장소의 기존 Claude 설정과 빌드·테스트 명령부터 확인해 줘. 공식 문서와 설치 버전에 맞춰 CLAUDE.md를 간결하게 정리하고, 비밀 파일 읽기 거부와 프로젝트에 맞는 문법 검사 훅을 기존 설정에 병합해 줘. 변경 범위에 맞는 검사 명령과 설정 점검 명령을 추가하고, 정상 입력과 실패 입력으로 훅을 검증해 줘. 개인 설정을 변경할 필요가 있으면 프로젝트 설정과 적용 범위를 구분해서 설명하고, 실제 검증 결과와 남은 항목을 보고해 줘.

## 공식 문서

- [설정 파일과 우선순위](https://code.claude.com/docs/en/settings)
- [권한 규칙](https://code.claude.com/docs/en/permissions)
- [훅 레퍼런스](https://code.claude.com/docs/en/hooks)
- [Bash 샌드박스](https://code.claude.com/docs/en/sandboxing)
- [사용자 명령과 스킬](https://code.claude.com/docs/en/skills)
