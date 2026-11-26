# Twin 선택 하이라이트·중장비 모의 위험반경 (Claude → Codex 인수인계)

## 범위

- 기준: 공유 작업 트리(미커밋 변경 포함).
- Codex 담당 파일은 편집하지 않았다(HoverTarget.cs, YardScenery.cs, ShipView.cs, YardSimSync.cs, ERP, Portal). `IsHeavyEquipment`는 Codex가 추가한 값을 그대로 읽는다.
- Generated/, 씬·프리팹, Git, WebGL 빌드, 배포는 건드리지 않았다.

| 파일 (`apps/twin/unity/Assets/ShipyardTwin/` 기준) | 변경 |
| --- | --- |
| `Runtime/Interaction/SelectionHighlight.cs` (신규) | 선택 모델 외곽선과 윤곽 발광 |
| `Runtime/Interaction/EquipmentZone.cs` (신규) | 중장비 모의 위험반경 바닥 링과 라벨 |
| `Runtime/Resources/ShipyardTwin/Shaders/SelectionFill.shader`, `SelectionOutline.shader`, `SelectionZone.shader` (신규) | URP 셰이더(WebGL 2 대상). Resources 아래에 두어 `Shader.Find`로 찾는다. |
| `Runtime/Dashboard/ObjectDetailView.cs` | `select-mark` 사각 마커 코드(Show/Hide/PlaceMark, 렌더러·콜라이더 목록)를 제거했다. 선택, 교체, 닫기, 비활성화 시점에 하이라이트와 링을 연결했다. 기존 선택 흐름과 팝업은 그대로다. |
| `Authoring/Dashboard.uxml`, `Dashboard.uss` | `select-mark`와 `.tw-select*`를 삭제했다. 팝업 머리에 `detail-zone` 추정 안내를 추가하고 `.tw-detail-zone`, `.tw-zone-label` 스타일을 추가했다. |
| `Tests/Editor/SelectionHighlightTests.cs` (신규) | 테스트 4개 |
| `apps/twin/docs/dashboard/object-detail.md` | 선택 표시 설명과 코드 표를 갱신했다. |

## 구현

- **외곽선**
  - 모델 메시를 `Graphics.RenderMesh`로 한 번 더 그린다.
    - Fill 패스: 프레넬 rim 발광을 additive로 더하고 stencil 128을 기록한다.
    - Outline 패스: 뒷면을 smoothed normal 방향으로 화면 2.6px만큼 밀어 그리며, stencil이 기록된 곳은 건너뛴다(NotEqual).
  - 모델에는 GameObject, Renderer, 재질, MaterialPropertyBlock, Collider를 추가하거나 바꾸지 않는다. 그래서 재질, 투명도, 인양 색상, 그림자, 피킹이 그대로 유지된다. 하이라이트 그리기 자체는 그림자, 프로브, 모션벡터를 끈다.
  - 서브메시는 개수만큼 그린다. 열릴 때 0.22초 동안 페이드인한다. 각진 블록에서도 선이 끊기지 않도록 같은 위치의 정점 normal을 평균한 smoothed 사본을 쓴다(선택당 정점 40만 개 한도).
  - stencil이 없는 GPU에서도 불투명 모델은 깊이 테스트로 내부가 가려진다.
- **정리**
  - 선택 해제(Close, OnDisable), 대상 교체(Show), 숨김(그 프레임부터 그리지 않음), 삭제(Unity null이면 Clear)에 바로 반응한다.
  - 블록 숨김 시 팝업이 닫히는 기존 동작은 유지된다. 호선 전환으로 대상이 사라지면 팝업은 남기고 하이라이트만 정리한다.
- **중장비 링**
  - 대상 조건: `IsHeavyEquipment && Category != Crane`. 크레인은 기존 `CraneSafetyView`를 쓰고, 승용차와 트럭에는 링이 없다.
  - 반경: 모델 바닥 점유 범위(heading 좌표계, 메시 bounds)의 반대각선에 여유를 더하고 0.5m 단위로 올린다. 여유는 반대각선의 35%이며 1.5~5m로 제한한다.
    - 예: 지게차 약 3.5~4m, 25m 트랜스포터 17.5m.
  - 링은 매 프레임 모델 위치와 바닥 높이를 따라간다. `RingMesh(0,1)` 원판을 셰이더로 꾸민다(가장자리선, 도는 점선, 퍼지는 펄스).
  - 라벨에는 "모의 위험반경 ≈ N m / 장비 크기 기반 추정 · ERP 미검증"이라고 쓴다. 팝업 `detail-zone`에는 "실제 작업 반경·안전거리로 쓰지 마세요"라고 안내한다.

## Codex 리뷰 후속(3건) 처리

1. **Mesh 지연 도착·교체**
   - 이제 매 프레임 `MeshFilter.sharedMesh`를 확인한다. 선택 시점에 mesh가 없던 렌더러도 목록에 넣어 두었다가, mesh가 들어오거나 바뀐 프레임부터 새 mesh의 외곽선을 그린다.
   - 선택 뒤에 추가된 렌더러는 0.5초마다 다시 찾는다.
   - smoothed 사본 캐시는 정적 캐시에서 인스턴스·선택 범위로 옮겼다. 같은 선택 안에서는 재사용하고, 원본이 파괴되면 해당 사본을 바로 파괴하며, Clear/Show 때 모두 파괴한다.
   - 회귀 테스트: `Highlight_TakesLateAndSwappedMeshesAndFreesCopiesWithTheSelection`.
2. **라벨 가시성**: WorldLabels는 `style.display`만 쓴다(WorldLabels.cs:93에서 확인). 그래서 EquipmentZone은 `style.visibility`로만 라벨을 숨긴다.
3. **셰이더 pow**
   - SelectionZone의 `pow(x, 2.0)`를 `x * x`로 바꿨다.
   - SelectionFill의 `pow(1 - facing, 2.5)`는 그대로 뒀다. 밑이 `saturate`로 0~1에 묶이고 지수가 양수라 GLSL에서도 정의되기 때문이다.

## 검증 결과

- `tools/test.sh` EditMode: 66/66 통과. 신규 4건도 통과했다(노멀 평활, 숨김·교체·삭제 정리, 지연·교체 mesh와 캐시 해제, 중장비 대상·반경 추정).
- `tools/build.sh` macOS 빌드 성공(0 errors). 셰이더 관련 경고는 기존 Terrain 셰이더 것뿐이다.
- macOS 앱 캡처
  - `-shipyard-select GY-FL-ASSY -shipyard-camera 40,350,180,55,40`에서 지게차 외곽선 발광, 주황 링(점선, 가장자리), "모의 위험반경 ≈ 4 m" 라벨, 팝업 안내를 확인했다.
  - 블록(`SN2040-241C1`)은 선택과 팝업까지 확인했다. 다만 캡처 화면에서 해당 블록을 특정하지 못해, 큰 모델의 외곽선 굵기·품질은 눈으로 확인하지 못했다.

## Codex가 확인할 것

- WebGL(`tools/build-web.sh`)에서 확인할 것:
  - 세 셰이더가 GLES3로 컴파일되는지.
  - stencil 버퍼가 있는지(없으면 불투명 모델은 정상이고, 반투명 모델에는 뒷면 hull이 비쳐 보인다).
  - iPhone Safari에서 외곽선 두께가 적절한지(px 기준이라 HiDPI에서 얇아 보일 수 있다).
- 호선과 블록처럼 렌더러가 많은 대상: 프레임 비용을 확인한다. 서브메시마다 draw 2회가 추가되고, 처음 선택할 때 smoothed 사본을 만드는 비용이 있다.
- `YardScenery.RefreshYardEquipment`는 데이터가 갱신되면 지게차를 파괴하고 다시 만든다. 이때 하이라이트와 링은 정리되지만, 팝업은 남은 채 새 객체로 다시 선택되지는 않는다. 재선택(ID로 다시 찾기)을 원하면 Codex 쪽 식별 로직과 맞춰야 한다.
- 중장비 링 반경은 선택 시점의 바닥 점유 범위로 계산한다. 선택할 때 mesh가 아직 없는 중장비는 링이 뜨지 않는다(현재 지게차와 트랜스포터 프롭은 즉시 로드된다).
- 승용차와 트럭에 링이 없는지 확인하려면 `IsHeavyEquipment` 값이 맞게 설정되어야 한다(Codex 담당).

## Codex 통합 검증

- ERP 지게차와 블록 운반 트랜스포터, 배경 지게차·트랜스포터를 `IsHeavyEquipment`에 연결했다. 승용차·트럭은 기존 표시를 유지한다.
- 해제된 Unity 메시가 null로 비교될 때 이전 윤곽이 남을 수 있는 경계 사례를 수정하고 회귀 테스트에 포함했다. 최종 `tools/verify twin`은 66개가 통과했다.
- Mac 앱 지게차 캡처에서 모델 외곽선과 주황색 링을 확인했다. WebGL 빌드는 성공했고 본인용·시연용 HTTPS에서 새 WASM과 데이터 파일이 제공되는 것을 확인했다. 실제 아이폰 Safari에서의 렌더링은 확인 전이다.
