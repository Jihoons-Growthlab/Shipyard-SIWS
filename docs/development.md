# 로컬 개발

## 포트

| 서비스 | 주소 | 쓰임 |
| --- | --- | --- |
| Portal | http://127.0.0.1:8080 | 로컬 진입 화면 |
| ERP | http://127.0.0.1:8001 | 웹과 API |
| SIWS 챗봇·MCP | http://127.0.0.1:8787 | FastAPI, MCP는 `/mcp/` |
| Ollama | http://127.0.0.1:11434 | 로컬 모델 추론 |
| PostgreSQL | 127.0.0.1:55432 | Docker 내부 5432, VS Code DB 접속 |
| Redis | 127.0.0.1:56379 | Docker 내부 6379, ERP 세션과 캐시 |
| Unity Twin | macOS 앱 또는 Unity Play Mode | ERP 8001에 접속 |
| 실험용 ERP | 8031 | 이전 벤치마크용 포트, 일반 실행은 8001 |

## 최초 한 번

저장소 루트에서 실행한다.

```sh
python3 -m venv apps/erp/.venv
apps/erp/.venv/bin/pip install -r apps/erp/requirements.txt
cp apps/erp/.env.example apps/erp/.env
```

`apps/erp/.env`에 로컬 비밀번호를 입력한다. 기존 Docker DB를 계속 쓸 경우 기존 ERP의 로컬 비밀번호를 동일하게 설정한다. Compose 프로젝트 이름을 `shipyard`로 유지하므로 기존 PostgreSQL·Redis 볼륨을 사용한다. 기존 ERP와 통합 ERP를 같은 포트에서 함께 실행할 수 없다.

```sh
tools/dev db-up
```

새 DB에만 첫 관리자를 생성한다.

```sh
apps/erp/shipyard-postgres create_admin.py --id admin --name 관리자
```

## 실행·종료

각 터미널에서 `tools/dev erp`, `tools/dev portal`을 실행한다. 각 서버는 해당 터미널에서 Ctrl+C로 종료한다. DB 상태는 `tools/dev db-status`, DB 컨테이너 중지는 `tools/dev db-stop`이다. 같은 Compose 프로젝트를 사용하는 기존 ERP도 DB 중지의 영향을 받는다.

Tailscale로 다른 기기에서 접속하려면 `tools/dev erp tailnet`, `tools/dev portal tailnet`으로 실행한다. 서버가 `127.0.0.1`과 이 Mac의 Tailscale IPv4 주소에서 함께 열리고, Portal은 MagicDNS 이름(`<기기>.<tailnet>.ts.net:8080`)으로 들어온 요청도 받는다. 같은 와이파이의 다른 기기에는 열리지 않는다. 직접 실행할 때는 `SHIPYARD_BIND_HOSTS=127.0.0.1,<Tailscale IP>`를 지정하고, Portal에 다른 호스트 이름을 허용하려면 `SHIPYARD_ALLOWED_HOSTS`에 쉼표로 구분해 추가한다.

Unity Hub에서 `apps/twin/unity`를 연다. 터미널에서는 `tools/dev twin-test`, `tools/dev twin-build`, `tools/dev twin-run`을 사용한다. 브라우저용 트윈은 `apps/twin/tools/build-web.sh`로 `apps/twin/Builds/WebGL`에 빌드하며, 빌드가 있으면 Portal이 `/twin/`에서 제공하고 Portal의 디지털 트윈 카드가 그 페이지를 연다. Mac·Windows·iPhone 브라우저에서 같은 화면이 열리고, Portal에 로그인한 탭의 토큰으로 ERP 데이터를 조회한다. `tools/dev twin-run`은 기본으로 로컬 ERP(8001)를 조회한다. 터미널에서 실행할 때 인증 정보가 없으면 ERP 로그인을 묻고, 로그인 정보는 파일에 저장하지 않는다. 다른 ERP 주소나 기존 토큰은 `SHIPYARD_API_URL`과 `SHIPYARD_API_TOKEN`으로 지정한다. `tools/dev twin-run offline`은 앱에 포함된 스냅샷을 사용한다. Finder에서 앱을 직접 열면 실행 환경에 ERP 주소가 설정된 경우에만 ERP를 조회한다. Unity Editor는 같은 환경변수를 가진 터미널에서 열어야 한다. 위성 사진이 필요하면 배포 조건을 확인한 뒤 `apps/twin/pipeline/fetch_satellite.py`로 로컬에서 생성한다.

## ERP·디지털 트윈 챗봇

Ollama를 실행한 상태에서 저장소 루트에서 준비한다. 챗봇 서버에는 Python 3.11 이상을 사용한다.

```sh
ollama pull qwen3:4b
python3 -m venv apps/assistant/.venv
apps/assistant/.venv/bin/pip install -r apps/assistant/requirements.txt
tools/dev assistant
```

ERP와 포털도 실행한 뒤 포털에서 로그인한다. `/erp/`의 우측 하단 도우미 버튼이나 `/twin/` 오른쪽 아래의 야드 안내원 캐릭터를 연다. 포털은 웹 챗봇 요청을 FastAPI로 전달한다. 챗봇을 켠 포털은 이 Mac의 루프백 주소 또는 소유자 HTTPS 주소로 접속한다.

MCP 클라이언트는 Streamable HTTP 주소 `http://127.0.0.1:8787/mcp/`와 `Authorization: Bearer <ERP 세션 토큰>` 헤더를 사용한다. 호선 진행, 블록 위치·탑재, 탑재 계획·검사 실적, 도크·안벽 계획, 지브 크레인 계획 모의, 공용 트윈 재생 상태를 해당 세션의 ERP 조회 권한으로 읽는다. `/twin/`의 챗봇은 야드 안내 모드로 열려, 사용자가 명시적으로 요청한 대상으로 카메라를 옮긴다(통합검색 프레이밍 재사용, 재생 일자 유지). 명세는 `apps/twin/docs/dashboard/chat.md`에 있다. Ollama로 전송하는 내용은 질문, 최근 대화, 조회 결과이며 ERP 토큰은 ERP 인증에만 사용한다.

모델과 내부 서버 주소는 각각 `SIWS_ASSISTANT_MODEL`, `SIWS_ASSISTANT_ERP`, `SIWS_ASSISTANT_OLLAMA`로 설정한다. 포트를 바꿀 때는 포털과 챗봇에 같은 `SIWS_ASSISTANT_PORT`를 설정한다.

## Claude Code와 Git

저장소 루트에서 `claude`를 실행한다. 대시보드 전용 `/shipyard-dashboard` 스킬을 사용할 때는 `apps/twin`에서 Claude Code를 시작한다.

- ERP 작업: “`apps/erp`에서 블록 적치 조회를 수정하고 ERP 테스트를 실행해줘.”
- Twin 작업: “`apps/twin/CLAUDE.md`의 경로 안내에서 야드 배치 관련 파일만 읽고 수정해줘.”
- 연동 작업: “`shared/contracts/README.md`를 참고해서 ERP 응답과 Unity 파싱을 함께 수정하고 앱별로 검증해줘.”

원격 `origin`은 기존 ERP 저장소다. 통합 브랜치에서 검토한 뒤 올린다. 빌드 결과, Unity Library, 로컬 비밀번호와 DB는 Git에서 제외한다. 위성 사진은 배포 권한을 확인할 때까지 로컬 파일로 관리한다.

현재 통합 변경은 로컬 `integration/monorepo` 브랜치의 작업 트리에 있다. 변경을 검토한 뒤 아래 순서로 올린다.

```sh
git status --short
git add -A
git diff --cached --stat
git commit -m "Organize Shipyard apps in one repository"
git push -u origin integration/monorepo
```


## 화면 간 연결 주소

화면 사이에는 호선·블록 ID를 주소로 넘긴다. 권한 확인은 각 화면이 ERP 세션으로 한다.

| 주소 | 열리는 화면 |
|---|---|
| `/control#hull=SN2052&block=SN2052-213C3` | 생산 관제. `&week=YYYY-MM-DD`를 붙이면 그 주를 연다. |
| `/map#block=<블록>&hull=<호선>` | 조선소 지도. 블록의 마지막 확정 위치(시설 단위)와 계획 적치를 구분해 표시한다. |
| `/equipment/?asset=<장비>` | 장비 모니터링. 해당 장비를 선택한다. 위치는 계획 모의다. 기존 `/map#equipment&asset=<장비>`도 열린다. |
| `/erp/#open=<조회>&q=<검색어>` | ERP에서 그 조회가 있는 첫 화면을 검색어로 연다. 권한이 없으면 열지 않는다. |
| `/twin/?hull=<호선>&block=<블록>` | 웹 트윈. 해당 호선을 선택하고 그 블록 인양 직전으로 재생 위치를 옮긴다. |

## 장비 관측 저장소

포털은 크레인·중장비·차량의 서버 모의 관측값을 InfluxDB의 `equipment` 버킷에 초당 한 번 저장한다. 보관 기간은 7일이다. 포털 시작 시 수집은 중지 상태다. 관리자 계정으로 장비 모니터링의 로그 영역에서 수집을 시작하거나 중지한다. 수집을 시작하면 브라우저를 닫아도 포털 프로세스가 실행 중인 동안 계속 저장한다. 사용자 로그인은 기존 ERP 세션을 사용한다.

처음 구성할 때 `apps/portal/.env.example`을 `apps/portal/.env`로 복사한 뒤 비밀번호와 토큰을 각각 강한 임의 값으로 바꾼다. `tools/dev equipment-db-up`으로 InfluxDB를 시작하고 `tools/dev portal`로 포털을 실행한다. InfluxDB는 이 Mac의 127.0.0.1:8086으로만 열리고 Docker 볼륨에 데이터를 유지한다. 비밀번호와 토큰은 초기 볼륨 생성 시 적용된다.

야드의 작은 크레인은 `apps/erp/shipyard-postgres seed_yard_cranes.py`로 ERP에 장비·정격·배치 시설·레일(`crane_rails`)과 작업 배정을 등록한다.
- 도크: 바깥 레일에 지브 크레인을 둔다.
- 피어: 약 105 m마다 러핑 크레인을 둔다.
- 수집: 포털 수집기는 크레인 관측을 조회한 세션으로 ERP 레일 목록을 읽어, 등록된 크레인 전체의 모의 계측을 함께 기록한다.
- 이동 규칙: 포털 지도, 수집기, Twin이 같은 규칙을 쓴다([연동 기준](../shared/contracts/README.md)의 레일 주행 크레인).

포털은 `GET /equipment/observations/{crane|heavy|vehicle}`에 로그인 토큰을 받아 최신 관측값을 반환한다. 관측값의 `source=simulation`은 서버 모의 데이터임을 뜻한다.
