# PR-29: 커서 이동 중 시인성 저하 — 3원 공통 가우시안 안개

> 상태: **✅ main (`401be8e`)** — 2026-07-23. 반복 튜닝 후 최종 = **하드 코어 없는 가우시안 안개**. 코드·단위테스트·docs 동기화 완료.
>
> 목적: 커서 인디가 이동 중 **덜 거슬리게**(attention↓). **지연 가림이 아님** — 그 축은 [PR-28](PR-28-cursor-lag-perceptual-masking.md)에서 기각·박제.
>
> 선행: [PR-28](PR-28-cursor-lag-perceptual-masking.md) 0안(`CursorAlwaysPollMs`/`AnimationFrameMs` 16→15) 적용·사용자 체감 “별 차이 없음·머뭇거림” → **뒤처짐 수용** 후 본 PR로 전환.

## 동기

항상 표시 모드(`cursor_always_show=true`, 디폴트)에서 링이 커서를 따라다니며 **시야를 잡아끈다.**
사용자는 지연을 줄이는 것보다 **이동 중 존재감을 낮추는** 쪽을 원함. PR-28의 “흐리게 해서 어긋남을
못 보게”와 겉모습은 비슷하나 **성공 지표가 다르다** — 본 PR은 주관적 “덜 거슬림”만 판정하고,
추적 오차·겉보기 지연은 비목표·비측정.

## 사용자 확정 결정

| # | 항목 | 결정 |
|---|---|---|
| U1 | 디폴트 | **켜둠** (`cursor_motion_dim_enabled = true`) |
| U2 | 프로토 범위 | 알파+소프트 **한 번에** (분할 안 함) |
| U3 | 이동 중 한/영 | 초안은 알파 바닥 ≥0.50이었으나 **최종 안개**에서는 α≈0.22 + 색+흰색 혼합으로 시인성 유지(클램프 하한 0.04) |
| U4 | UI | Settings「이동 중 옅게 / 안개 농도 / 안개 강도」+ 트레이 on/off |
| U5 | 체감 | 2026-07-23 **이 정도 만족** → 세션 마무리(머지 대기) |

### planner 권고 vs 채택

| 항목 | planner | 채택 |
|---|---|---|
| 디폴트 | opt-in `false` | **`true`** (U1) |
| 프로토 | 알파만 → soft | **동시** (U2) |
| Settings/트레이 | 프로토는 config만 | **UI까지** (U4) |
| CPU 가우시안 | 보류 | **채택**(셰이더 distance-field 가우시안 — OS blur/DComp **금지** 유지) |
| `_lastAlpha` 정리 3건 | 선행 분리 커밋 | **Commit0으로 포함** |

## PR-28과의 축 분리

| PR-28 근거 | 본 PR(distraction↓) |
|---|---|
| 알파 → vernier/지연 가림 무효 + Hess | 지연 가림으론 **여전히 무효**. distraction↓ 지표로는 **유효** |
| 등방 블러 → 센트로이드 불변 | 지연 가림으론 **무효**. 본 PR은 **엣지·채도 자극 완화** |
| σ 10–22px “형체 소실” 금지 | PR-28 지연 가림 맥락. 본 PR은 distraction↓ 용 **의도적 안개**(σ≈halo×14 ≈14px @디폴트) — 성공 지표가 다름 |
| B안 9Hz 리미트 사이클 | **그대로 fatal** — enter/exit 이질 물리량 금지 |
| 금지 API 5종 (DComp/DWM blur 등) | **절대 금지** 유지 — 셰이더만 |

성공 기준: “이동 중 덜 거슬림?” Y/N. **비목표**: 링이 커서에 더 붙음 / 머뭇거림 감소.

## 최종 동작 (구현)

### 적용 조건

- `CursorIndicatorEnabled && CursorAlwaysShow && CursorMotionDimEnabled`
- `CursorAlwaysShow=false`(정지 검출·이동 중 Hide) → **no-op**

### 모션 상태 (B안 함정 회피)

- **단일 물리량**: 틱당 맨해튼 `|dx|+|dy|` vs `CursorMotionThresholdPx`
- **enter**: 임계 초과 → 즉시 딤
- **exit**: 임계 미만이 **연속 `CursorMotionDimSettlePolls`(=3)** 틱일 때만 Full  
- 창 `SourceConstantAlpha` 는 항상 Full(`_displayAlpha`) — 딤은 **픽셀 셰이더만**

### 3원 공통 가우시안 안개

- CAPS ON = Inner+Middle+Outer(3), CAPS OFF = Inner+Middle(2) — Outer는 기존처럼 CAPS 전용
- `MotionSoftness`&gt;0:
  - **하드 코어 없음**
  - σ = `max(baseHalo, 0.5) × (1 + soft × (FogSigmaMul−1))` — soft=1 → ≈`baseHalo×14`
  - 링 색 = IME/CAPS 색 + 흰색 혼합(whiteness↑ with soft)
  - `RingAlpha*` = `cursor_motion_alpha` × (1.00 / 0.97 / 0.94)
- soft=0: 기존 코어+헤일로 AA 경로
- `MotionFogPadLogicalPx = 28` — soft&gt;0 일 때 bbox에 더해 σ 잘림 방지
- IME 팝(PR-21) 중: soft=0·원별 α=1 (가독 우선)

### Config (3키) — P4 4-축

| 키 | 형 | 디폴트 | clamp | UI |
|---|---|---|---|---|
| `cursor_motion_dim_enabled` | bool | **`true`** | — | Settings Bool + 트레이 |
| `cursor_motion_alpha` | double | **`0.22`** | **`[0.04, 1.0]`** | Settings「안개 농도」 |
| `cursor_motion_softness` | double | **`1.0`** | `[0.0, 1.0]` | Settings「안개 강도」 |

내부 const(키 아님): `CursorMotionFogSigmaMul=14`, `CursorMotionFogPadLogicalPx=28`, ring factors 1.00/0.97/0.94, `CursorMotionDimSettlePolls=3`.

트레이: on/off만 (`IDM_CURSOR_MOTION_DIM`). I18n: `MenuCursorMotionDim` = "커서 이동 중 옅게".

## 구현 체크리스트 (반영됨)

**Commit 0**

- [x] `LayeredCursorBase` `_lastAlpha` → `_displayAlpha` (Hide/Prepare가 의도 알파를 오염시키지 않음)
- [x] `HandleConfigChanged` 가시 시 블리트 1회
- [x] `CursorOverlay.Initialize`에서 `_lastCursorX/_lastCursorY` 시드

**본 기능**

- [x] `DefaultConfig` / `AppConfig` / `Settings.Validate` / SettingsDialog 3필드
- [x] `App/UI/CursorMotionDim.cs` — settle / EffectiveSoftness / RingAlphas
- [x] `CursorOverlay` — 모션 딤 상태 + FogPad 배선 + 팝 억제
- [x] `CursorRenderer` — soft&gt;0 가우시안 안개 경로
- [x] `CursorStyle` — `MotionSoftness` / `MotionFogPadLogicalPx` / `RingAlpha*` (bbox에 FogPad 반영)
- [x] Tray + I18n
- [x] `tests/.../CursorMotionDimTests.cs`
- [x] docs: CHANGELOG · config-reference · implementation-notes · architecture · User_Guide · INDEX · 본 문서

## 기각·보류

| 안 | 판정 | 이유 |
|---|---|---|
| 지연 가림용 흐림 재시도 | 기각 | PR-28 |
| B안식 enter/exit 이질 척도 | 기각 | 9Hz |
| OS DComp/DWM blur API | 기각 | 금지 API |
| C안 속도 외삽 | 범위 외 | distraction 아님 |
| 디폴트 OFF | 사용자 기각 | U1 |
| 초안 α=0.55 / soft=0.35 / “가짜 소프트만” | **튜닝으로 대체** | 최종 가우시안 안개(본 문서) |

## P1–P6

| 규칙 | 영향 |
|---|---|
| P1 | NuGet 0 · 금지 blur API 0 |
| P2 | UI/메뉴 한국어 · config 키·로그 영어 |
| P3 | 알파·settle·σ·FogPad const |
| P4 | 모션/딤 App(`CursorMotionDim`/`CursorOverlay`/`CursorRenderer`); 블리트 Core |
| P5 | manifest 무변경 |
| P6 | `Core/` → `App/` 참조 0 |

## 위험 / 되돌리기 / 테스트

**위험**

- Commit 0 없이 딤만 → `_displayAlpha` 침묵 invisible 재발
- settle 없거나 enter≠exit 척도 → 깜빡임
- FogPad 없이 σ↑ → DIB 클리핑
- 디폴트 ON → 기존 사용자 체감 즉시 변경 — CHANGELOG 명시

**되돌리기**

- 사용자: `cursor_motion_dim_enabled=false`, 또는 α=1.0·soft=0
- 개발: 본 기능 revert (Commit 0은 유지 권장)

**Tier**

1. build + AOT publish + invariant  
2. `dotnet test tests\KoEnVue.Tests\KoEnVue.Tests.csproj`  
3. 수동: AlwaysShow 고속/정지 / AlwaysShow OFF / IME 팝 / 셸 UI / 트레이·Settings

## 구현 순서

1. [x] 초안 승인 → Commit0 → 딤+config+UI+테스트
2. [x] 반복 튜닝 → **최종 가우시안 안개** (사용자 만족)
3. [x] docs-keeper 동기화 (본 문서·INDEX 🚧 유지)
4. [ ] 머지 후 INDEX/본 문서 → ✅ + (선택) 릴리스 노트

## 참고

- [PR-28](PR-28-cursor-lag-perceptual-masking.md) — 지연 가림 기각 · 금지 API
- [dev-notes/2026-07-22-settimer-tick-quantization.md](../dev-notes/2026-07-22-settimer-tick-quantization.md)
- [PR-21](PR-21-cursor-pop-animation.md) — 커서 팝 · Settings/트레이 패턴
- [dev-notes/2026-05-27-cursor-indicator.md](../dev-notes/2026-05-27-cursor-indicator.md)
