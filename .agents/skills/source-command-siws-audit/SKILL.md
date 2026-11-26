---
name: "source-command-siws-audit"
description: "SIWS Codex 지침과 설정 중복 점검"
---

# source-command-siws-audit

Use this skill when the user asks to run the migrated source command `siws-audit`.

## Command Template

AGENTS.md, 하위 AGENTS.md, .Codex/settings.json과 명령 파일을 점검한다.
코드에서 바로 알 수 있는 반복 설명과 충돌하는 지시를 찾고, 빌드 명령·생성 파일 편집 규칙·데이터 원본·레거시 의존은 보존한다.
설정 키는 설치된 Codex --version과 공식 문서에 맞춰 확인한다.
사용자가 개선을 요청했으면 작은 변경부터 적용하고 차이와 확인 결과를 보고한다. 점검만 요청했으면 결과만 보고한다.
비밀 파일 내용은 출력하지 않는다. 다른 세션의 변경은 사용자 요청 범위와 연결되는지 확인한다.
