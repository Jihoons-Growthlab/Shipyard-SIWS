# SIWS Nginx 접속 구성

```text
본인용 HTTPS  :8443 → Tailscale Serve → Nginx 127.0.0.1:18082 → Portal :8080 → ERP :8001
친구용 HTTPS   :443 → Tailscale Serve → Nginx 127.0.0.1:18081 → Portal :8080 → ERP :8001
Mac 내부 테스트                      → Nginx 127.0.0.1:18080 → Portal :8080 → ERP :8001
```

Nginx는 Mac에서 실행한다. PostgreSQL·Redis·InfluxDB는 기존 Docker의 로컬 포트와 볼륨을 사용한다. WebGL 파일과 장비 API는 기존 Portal에서 제공한다. 개발 중에는 기존 Portal·ERP 접속 주소를 함께 사용할 수 있다.

## 기존 서비스에 영향 없이 검증

저장소 루트에서 `./ops/nginx/check-preview.sh`를 실행한다. `nginx`가 PATH에 없으면 `NGINX_BIN=/opt/homebrew/bin/nginx`를 지정한다. 이 명령은 **Mac 내부의 18080만** 잠시 열고, 정상 종료 또는 오류 시 자신이 시작한 Nginx를 정리한다.

Homebrew 기본 Nginx 설정의 8080 포트는 기존 Portal과 겹친다. 이 디렉터리의 전용 설정으로 실행한다.

포털·ERP 메타데이터·관제·장비·공정 가이드·WebGL 압축 파일을 기존 8080 응답과 비교한다. 로그인 없이 인증이 필요한 조회가 거부되는 것도 확인한다. 현재 Portal은 내부 포트와 브라우저 주소를 같은 기준으로 검사하므로, 미리보기 검증은 기존에 허용된 `Host`로 읽기 요청을 보낸다.

## 본인용 전체 SIWS

현재 Mac의 전체 SIWS는 `https://macbookair.taildb2f9e.ts.net:8443/`에서 연다. Tailscale이 확인한 접속 계정이 Mac의 Tailscale 계정과 일치해야 하고, 사이트 안에서는 기존 SIWS 로그인과 메뉴 권한을 사용한다. 공유 사용자에게는 기존 Tailscale 정책에서 443 포트만 허용한다.

설치하거나 저장소의 설정을 다시 적용하려면 아래 명령을 실행한다. 첫 명령은 Mac의 현재 MagicDNS 이름과 계정을 사용해 설정을 생성하고 macOS 로그인 시 실행할 Nginx 서비스를 등록한다.

```sh
python3 ops/nginx/install-owner.py
tailscale serve --bg --https=8443 http://127.0.0.1:18082
tailscale serve status
```

본인용 Nginx는 루프백에서만 수신한다. HTTPS 호스트와 POST 출처를 확인한 뒤 기존 Portal이 허용하는 로컬 Host·Origin으로 전달한다. 요청 경로, 쿼리, SIWS 세션과 WebGL 인코딩은 그대로 전달한다. 실행 설정과 로그는 `~/Library/Application Support/SIWS/nginx-owner`에 있다. Nginx의 자동 시작은 Portal·ERP·Docker의 시작과 별도다.

본인용 HTTPS만 내리려면 `tailscale serve --https=8443 off`를 실행한다. Nginx도 내리려면 `launchctl bootout "gui/$(id -u)/local.siws.nginx-owner"`를 실행한다. 다시 로그인할 때도 자동으로 시작하지 않도록 하려면 등록에 사용한 `~/Library/LaunchAgents/local.siws.nginx-owner.plist`를 이동한다.

## 친구 시연용 웹 접속

`demo.conf`는 기존 사용자용 Portal과 분리된 시연 경로다. Mac 내부 `18081`에서 포털·생산 관제·트윈·공정 가이드와 관련 조회만 전달한다. 로그인과 로그아웃을 제외한 등록 요청과 공유 야드 재생 변경은 거부한다. 기존 Portal은 `8080`에서 계속 실행한다.

기기 공유를 하기 전에 Tailscale 액세스 정책에서 외부 공유 사용자에게 이 Mac의 HTTPS 포트만 허용되는지 확인한다. 다른 허용 규칙의 효과도 함께 확인한다. Tailscale Serve를 `127.0.0.1:18081`로 연결한 뒤, 시연 계정으로 로그인하고 등록 요청이 거부되는지 검증한다. 그다음 인원별로 일회용 초대 링크와 시연 계정을 발급한다.
