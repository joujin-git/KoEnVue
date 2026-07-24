# PR-21: 커서 헤일로 스케일 팝 + config 신설 + SettingsDialog 대분류 재배치

## 동기

커서 추종 인디(동심원 3개)는 IME 전환 시 색상만 즉시 교체(`CursorOverlay.SetImeState` → `Render`)되고
시각적 피드백 강도가 약하다. 플로팅 배지는 IME 전환 시 Highlight 스케일 팝(1.3→1.0)으로 주의를
끄는데, 커서 헤일로에는 그 대응물이 없다. 본 PR 은 (1) 커서 동심원에 메인과 동형의 스케일 팝을
추가하고, (2) 메인과 평행하되 독립 조정 가능한 커서 전용 config 3키를 신설하며, (3) 트레이 팝
on/off 토글을 메인 ChangeHighlight 과 동형으로 노출하고, (4) SettingsDialog 13섹션을 일반/메인
인디/커서 헤일로 대분류로 재배치한다.

## 사용자 확정 결정 (변경 불가)

1. 효과 = 스케일 팝(메인 동일). 동심원 3개가 시작배율(기본 1.3)→1.0 선형 ratio 보간.
2. config 키 = 커서 전용 독립 신설: `CursorChangeHighlight`(bool) / `CursorHighlightScale`(double) /
   `CursorHighlightDurationMs`(int).
3. 트레이 = 커서 팝 on/off 토글을 메인 ChangeHighlight 토글과 동형 노출.
4. SettingsDialog = 섹션 재정렬 + 대분류 명칭 (단일 스크롤 구조 유지, 탭/구조변경 없음).
5. `CursorChangeHighlight` 디폴트 = **true** (메인 ChangeHighlight 와 평행 — 메인도 기본 켜짐. 커서
   인디가 이미 기본 표시되므로 신규 사용자도 즉시 팝 경험. 끄려면 트레이/config).

### planner 권장안 채택 결정 (D1~D4)

| # | 결정 | 채택안 |
|---|------|--------|
| D1 | DIB bbox 고정 기준 배율 | `MaxCursorHighlightScale`(2.0) 고정 — 어떤 사용자 scale 이든 팝 중 DIB 재생성 0 |
| D2 | 팝 타이머 방식 | 전용 신규 타이머 `TIMER_ID_CURSOR_POP`(=9) 16ms — 폴링과 독립, race 최소 |
| D3 | 팝 중 커서 이동 | 정지 검출 모드: 이동 시 즉시 Hide → 팝 중단(기존 이동=숨김 정책 우선). 항상 표시 모드: 팝 진행 + 위치 추종 |
| D4 | SettingsDialog 노출 범위 | scale + duration **2개만** 다이얼로그. on/off 는 트레이 토글(메인 ChangeHighlight 동일 정책) |

## 변경 범위

신규 파일 0. 모든 변경은 기존 파일 수정.

- `App/Config/DefaultConfig.cs` — 커서 팝 const 3 + Min/Max 4 신설
- `App/Models/AppConfig.cs` — 커서 팝 init 프로퍼티 3 신설
- `App/Config/Settings.cs` — Validate clamp 2 신설 (scale/duration)
- `Core/Windowing/CursorStyle.cs` — `HighlightScale` 필드 추가 + `BoundingBoxLogicalPx` 가 최대배율
  기준 고정
- `App/UI/CursorRenderer.cs` — 셰이더 반지름에 `style.HighlightScale` 곱 적용
- `App/UI/CursorOverlay.cs` — 팝 상태머신 + `TriggerPop()` + `HandleCursorPopTimer()` + IME 전환 배선
- `App/UI/AppMessages.cs` — `TIMER_ID_CURSOR_POP = 9` 신설
- `Program.cs` — WM_TIMER 분기에 팝 타이머 + EnableCursorOverlay hwndTimer 전달
- `App/UI/Tray.cs` — `IDM_CURSOR_HIGHLIGHT` const + 디스패치 case
- `App/UI/Tray.Menu.cs` — 커서 팝 토글 메뉴 항목 (커서 숨김 토글 옆)
- `App/Localization/I18n.cs` — `MenuCursorHighlight` 키 1
- `App/UI/Dialogs/SettingsDialog.Fields.cs` — 13섹션 재배치 + 섹션명 변경 + 커서 전환효과 필드 2

문서: CHANGELOG / architecture.md / config-reference.md / conventions(invariant) / dev-notes / INDEX 갱신.

## 1. 애니메이션 구동 아키텍처

### P4 판정 — 커서 전용 경량 구현 (메인 OverlayAnimator 재사용 안 함)

| 후보 | 판정 | 근거 |
|------|------|------|
| (a) 메인 `OverlayAnimator` 재사용 | **거부** | OverlayAnimator.HandleHighlightTimer 는 `_onScaledSize(x,y,w,h,alpha)` 로 **윈도우 자체를 stretch**(다른 size 로 UpdateLayeredWindow)하는 전제. 커서는 `LayeredWindowBlit.Blit` 가 1:1, 셰이더 반지름에 배율을 곱해 또렷하게 확대 — 메커니즘이 본질적으로 다름. OverlayAnimator 는 fade/hold/slide/topmost 5트랙 + ImeState 무지 설계라 커서 폴링 모델과 결합 불가. dev-notes/2026-05-27 의 P4 재검토 조건 미충족 |
| (b) Core 공유 이징 헬퍼 | **거부 (과설계)** | 공유 로직이 `ratio=clamp(elapsed/dur); scale=start+(1-start)*ratio` 2줄. Core 헬퍼 추출은 파일 신설 비용이 2줄 절약보다 큼. 커서 팝은 stretch 가 아니라 공유 분모 없음 |
| (c) **커서 전용 경량 팝 상태머신** | **채택** | 커서는 이미 P4 예외 영역(별도 엔진 LayeredCursorBase, dev-notes/2026-05-27). 팝 상태는 CursorOverlay 정적 필드 ~4개 + 16ms 타이머 1개. **플로팅 배지 코드 변경 0** |

### 매 프레임 처리 — `CursorOverlay.HandleCursorPopTimer()` (16ms WM_TIMER)

1. `elapsed = TickCount64 - _popStartTick`
2. `ratio = Math.Clamp((double)elapsed / _config.CursorHighlightDurationMs, 0.0, 1.0)` (dur=0 가드: ratio=1.0)
3. `scale = _config.CursorHighlightScale + (1.0 - _config.CursorHighlightScale) * ratio`
   (메인 HandleHighlightTimer 와 동일 선형식)
4. `_currentStyle = _currentStyle with { HighlightScale = scale }`
5. `_engine.Render(_currentStyle)` — 셰이더가 반지름 × scale 로 동심원 확대 렌더
6. `ratio >= 1.0` → KillTimer + `_popActive=false` + `_currentStyle = ... with { HighlightScale = 1.0 }`
   + 최종 Render (원래 크기 복원)

### DIB 재생성 회피 (핵심)

**문제**: 셰이더 반지름에 배율을 곱하면 동심원이 bbox 밖으로 나갈 수 있고, `BoundingBoxLogicalPx`
가 커지면 `EnsureDib` 가 매 프레임 DIB 재생성(CreateDIBSection) → 성능 + flip-flop.

**해결**: `CursorStyle.BoundingBoxLogicalPx` 를 **최대 배율(`MaxCursorHighlightScale`=2.0) 기준 고정**.
- bbox = `(maxRadius × MaxScale + outsideMargin) × 2`
- `HighlightScale` 필드는 bbox 계산에서 **제외** (record 동등성 비교엔 포함)
- 셰이더는 `effectiveRadius = baseRadius × HighlightScale` 로 그리되 항상 고정 bbox 안에 들어옴
- 결과: 팝 시작~종료 DIB 크기 불변 → 재생성 0. `targetSize == _currentWidth` 스킵 경로로 검증

디폴트 메모리 영향: maxRadius(45)×2.0 기준 ≈ 184px 정사각 DIB ≈ ~135KB (기존 ~36KB 대비 +99KB).
단일 윈도우라 무시 가능.

### flip-flop 가드 주의 (회귀 위험 — 명시)

`LayeredCursorBase.Render`: `if (_lastRenderedStyle is CursorStyle prev && prev == style)` 면 DIB
재생성 스킵 + blit. record 동등성에 `HighlightScale` 포함 시:
- 팝 중 매 프레임 scale 이 달라 `prev != style` → PaintDib 정상 (의도대로)
- PaintDib 의 `EnsureDib` 가 `BoundingBoxLogicalPx`(HighlightScale 무관 고정) 기반이라
  `targetSize == _currentWidth` → DIB 재생성 스킵(셰이더만 재계산). **정확히 의도한 동작**

→ `HighlightScale` 은 record 멤버로 포함(동등성 대상)하되 `BoundingBoxLogicalPx` getter 계산에서만
배제. **이 분리가 설계의 핵심.**

### 중심 유지

커서 팝은 윈도우 위치/크기 불변(bbox 고정), 셰이더 내부 `cx=w*0.5, cy=h*0.5` 중심 기준 반지름만
확대 → 중심 자동 유지. 메인처럼 `newX = lastX - (newW-baseW)/2` 위치 보정 **불필요**. stretch 방식
대비 커서 방식의 장점.

## 2. 타이머 전략

- 신규 `AppMessages.TIMER_ID_CURSOR_POP = 9`. 주석: "16ms, 커서 IME 전환 스케일 팝".
- **시작(IME 전환)**: `CursorOverlay.SetImeState` 내부에서 색상이 실제 바뀐 경우(`_lastImeState != state`)
  + `_config.CursorChangeHighlight` + `_isVisible` 이면 `TriggerPop()`.
  - `TriggerPop()`: `_popActive=true`, `_popStartTick=TickCount64`,
    `SetTimer(_hwndTimer, TIMER_ID_CURSOR_POP, AnimationFrameMs(16), IntPtr.Zero)`.
  - **타이머 hwnd**: CursorOverlay.Initialize 에 `IntPtr hwndTimer` 파라미터 추가(현재 hwnd 외).
    EnableCursorOverlay 가 `_hwndMain` 전달. CursorOverlay 가 자체 SetTimer/KillTimer (플로팅 배지
    OverlayAnimator 가 hwndTimer 받는 패턴과 동형). Program WM_TIMER 분기는 `HandleCursorPopTimer()` 호출만 추가.
- **완료 판정**: `HandleCursorPopTimer` 내 `ratio >= 1.0` → KillTimer + 복원.
- **정리**:
  - `CursorOverlay.Hide()` 진입 시 팝 진행 중이면 KillTimer + `_popActive=false`.
  - `CursorOverlay.Dispose()` (ON→OFF 토글) — 팝 타이머 KillTimer.

### Program.cs WM_TIMER 통합

```
else if ((nuint)(nint)wParam == AppMessages.TIMER_ID_CURSOR_MOTION)
    CursorOverlay.HandleCursorMotionTimer();
else if ((nuint)(nint)wParam == AppMessages.TIMER_ID_CURSOR_POP)   // 신규
    CursorOverlay.HandleCursorPopTimer();                           // 신규
```

## 3. config 4축 매핑

### 축1 — AppConfig init (App/Models/AppConfig.cs, [커서 헤일로] 블록 끝)
```csharp
public bool   CursorChangeHighlight     { get; init; } = DefaultConfig.CursorChangeHighlight;
public double CursorHighlightScale       { get; init; } = DefaultConfig.CursorHighlightScale;
public int    CursorHighlightDurationMs  { get; init; } = DefaultConfig.CursorHighlightDurationMs;
```

### 축2 — DefaultConfig const (App/Config/DefaultConfig.cs, 커서 헤일로 블록)
```csharp
public const bool   CursorChangeHighlight        = true;   // D5: 메인 ChangeHighlight 와 평행 (기본 켜짐)
public const double CursorHighlightScale          = 1.3;   // 메인 HighlightScale 과 평행 디폴트
public const int    CursorHighlightDurationMs     = 300;   // 메인 HighlightDurationMs 과 평행 디폴트

public const double MinCursorHighlightScale       = 1.0;
public const double MaxCursorHighlightScale       = 2.0;   // bbox 고정 기준 (DIB 재생성 회피)
public const int    MinCursorHighlightDurationMs  = 0;
public const int    MaxCursorHighlightDurationMs  = 2000;  // 메인 MaxFadeMs 와 동일 범위
```

### 축3 — Settings.Validate clamp (App/Config/Settings.cs, 커서 clamp 블록)
```csharp
CursorHighlightScale = Math.Clamp(config.CursorHighlightScale,
    DefaultConfig.MinCursorHighlightScale, DefaultConfig.MaxCursorHighlightScale),
CursorHighlightDurationMs = Math.Clamp(config.CursorHighlightDurationMs,
    DefaultConfig.MinCursorHighlightDurationMs, DefaultConfig.MaxCursorHighlightDurationMs),
```
(`CursorChangeHighlight` 는 bool → clamp 불요. 메인 ChangeHighlight 도 Validate 에 없음 — 일관.)

### 축4 — SettingsDialog.Fields min/max (§5 재배치에서 함께)

### JSON snake_case 키명 (STJ SnakeCaseLower 자동)
- `cursor_change_highlight` / `cursor_highlight_scale` / `cursor_highlight_duration_ms`
- AppConfigJsonContext 소스 생성기가 AppConfig 전체 자동 처리 — 별도 등록 불요.

## 4. 트레이 토글

### 위치
`Tray.Menu.cs` 의 `IDM_CURSOR_TOGGLE`(커서 헤일로 숨김) 다음에 커서 팝 토글 추가 — 커서 의미 그룹 유지.
체크 의미 = ON (메인 ChangeHighlight 동일).

### const / 디스패치 (Tray.cs)
```csharp
private const int IDM_CURSOR_HIGHLIGHT = <기존 커서 IDM 다음 번호>;
// 디스패치:
case IDM_CURSOR_HIGHLIGHT:
    updateConfig(config with { CursorChangeHighlight = !config.CursorChangeHighlight });
    break;
```
(메인 IDM_CHANGE_HIGHLIGHT 와 동형. updateConfig 람다가 CursorOverlay.HandleConfigChanged 로 `_config`
갱신 반영. 팝 on/off 는 다음 IME 전환부터 적용 — 즉시 시각 효과 없음, 정상.)

### I18n (I18n.cs)
- enum: `MenuCursorHighlight`
- table: `("커서 변경 시 강조", "Cursor highlight on change")`
- public surface property

## 5. SettingsDialog 재배치 매핑

현 13섹션을 **일반 / 플로팅 배지 / 커서 헤일로** 대분류로 재정렬. 단일 스크롤 구조 유지, 섹션명에 대분류
prefix 반영.

| 새 순서 | 대분류 | 기존 섹션 | 새 섹션명 ko / en |
|--------|-------|----------|------------------|
| 1 | 일반 | (10) 시스템 | 일반 / General |
| 2 | 일반 | (9) 트레이 | 일반 — 트레이 / General — Tray |
| 3 | 플로팅 배지 | (1) 표시 모드 | 플로팅 배지 — 표시 모드 / Floating badge — Display Mode |
| 4 | 플로팅 배지 | (2) 외관-크기·테두리 | 플로팅 배지 — 크기·테두리 / Floating badge — Size & Border |
| 5 | 플로팅 배지 | (3) 외관-색상·투명도 | 플로팅 배지 — 색상·투명도 / Floating badge — Colors & Opacity |
| 6 | 플로팅 배지 | (4) 외관-텍스트 | 플로팅 배지 — 텍스트 / Floating badge — Text |
| 7 | 플로팅 배지 | (5) 외관-테마 | 플로팅 배지 — 테마 / Floating badge — Theme |
| 8 | 플로팅 배지 | (6) 애니메이션 | 플로팅 배지 — 애니메이션 / Floating badge — Animation |
| 9 | 플로팅 배지 | (7) 감지 및 숨김 | 플로팅 배지 — 감지·숨김 / Floating badge — Detection & Hiding |
| 10 | 플로팅 배지 | (8) 앱별 프로필 | 플로팅 배지 — 앱별 프로필 / Floating badge — App Profiles |
| 11 | 플로팅 배지 | (11) 인디케이터 조작 | 플로팅 배지 — 조작 / Floating badge — Interaction |
| 12 | 커서 헤일로 | (12) 커서 헤일로 | 커서 헤일로 — 동심원 / Cursor halo — Rings |
| 13 | 커서 헤일로 | **신규** 커서 전환효과 | 커서 헤일로 — 전환 효과 / Cursor halo — Transition |
| 14 | 일반 | (13) 고급 | 고급 / Advanced |

### 재배치 근거
- **일반(시스템+트레이)을 맨 앞**: 인디 종류 무관 앱 전역 설정. 사용자가 먼저 만나는 게 자연.
- **고급(13)은 맨 끝 유지**: 고급은 대분류 어디에도 안 넣고 독립 말미 섹션 (기존 관례 보존).
- **커서 전환효과(신규)를 커서 동심원 바로 뒤**: 커서 의미 그룹 인접. on/off 제외(D4)라 scale/duration 2필드.
- 단일 스크롤 y-증가 구조(BuildChildren)는 `BuildRowDefs` 의 Sec/Add 호출 순서만 바꾸면 자동 반영 —
  Scroll.cs / .cs 변경 0.

### 신규 섹션 13 "커서 헤일로 — 전환 효과" 필드 (Fields.cs)
```csharp
Sec("커서 헤일로 — 전환 효과", "Cursor halo — Transition");
// CursorChangeHighlight on/off 는 트레이 메뉴 토글이라 여기서 제외 (메인 ChangeHighlight 패턴 동일).
Add(Dbl("전환 강조 배율", "Highlight scale",
    DefaultConfig.MinCursorHighlightScale, DefaultConfig.MaxCursorHighlightScale,
    c => c.CursorHighlightScale, (c, v) => c with { CursorHighlightScale = v }));
Add(Int("전환 강조 지속 시간 (ms)", "Highlight duration (ms)",
    DefaultConfig.MinCursorHighlightDurationMs, DefaultConfig.MaxCursorHighlightDurationMs,
    c => c.CursorHighlightDurationMs, (c, v) => c with { CursorHighlightDurationMs = v }));
```

### 구현 방식
13개 `Sec(...)`+`Add(...)` 블록을 위 표 순서로 물리적 이동(잘라 붙이기). 각 블록 내부 필드 불변,
섹션명 문자열만 표대로 수정. 신규 섹션 13 추가. `BuildRowDefs(13 섹션)` docstring → `(14 섹션)`,
SettingsDialog.cs "70개" → "72개" 갱신. Add 호출 순서가 `_fields`=`_fieldInputs`=Commit 순서를 결정
하나 각 FieldDef 람다는 독립이라 순서 무관 정상 동작(필드↔컨트롤 인덱스 짝 자동 보장).

## 6. P1–P6 판정

| 규칙 | 영향 | 검증 |
|------|------|------|
| P1 (zero NuGet) | 영향 없음 — BCL + 기존 Win32. SetTimer/KillTimer 기존 LibraryImport 재사용 | csproj Reference 변화 0 |
| P2 (UI 한국어 / 로그 영어) | UI: "커서 변경 시 강조"/"전환 강조 배율" 한국어. 로그: 영어. 섹션명 ko/en 쌍 | grep |
| P3 (const/enum) | 매직넘버 0 — scale/dur const, 타이머 ID const(=9), IDM const, 16ms=`AnimationFrameMs` 재사용 | grep |
| P4 (단일 구현) | 커서 팝은 커서 전용 경량(§1 c). 메인 OverlayAnimator 미재사용 — dev-notes/2026-05-27 P4 예외 영역 | `git grep HandleCursorPopTimer` 1곳. 플로팅 배지 파일 변경 0 |
| P5 (manifest asInvoker) | 영향 없음 | manifest 무변경 |
| P6 (App→Core 단방향) | `CursorStyle.HighlightScale` 는 Core record 필드(순수 double, ImeState/AppConfig 무지). App 이 BuildStyle 에서 주입 | `git grep "ImeState\|AppConfig" Core/Windowing/CursorStyle.cs` 0 |

## 7. 검증 invariant

```bash
# 빌드 (항상 둘 다)
dotnet build
dotnet publish -r win-x64 -c Release

# P1/P6 invariant
git grep "KoEnVue\.App"     Core/
git grep "ImeState"         Core/
git grep "NonKoreanImeMode" Core/
git grep "DllImport" -- '*.cs'

# 본 PR 가드
git grep "TIMER_ID_CURSOR_POP"          # AppMessages + Program + CursorOverlay
git grep "CursorChangeHighlight"        # DefaultConfig + AppConfig + Tray + Tray.Menu + CursorOverlay
git grep "ImeState\|AppConfig" Core/Windowing/CursorStyle.cs  # 0 (P6)
# 플로팅 배지 무변경 보장:
git diff --stat Core/Animation/OverlayAnimator.cs App/UI/Overlay.cs App/UI/Animation.cs  # 0 lines
git grep -c "Sec(" App/UI/Dialogs/SettingsDialog.Fields.cs  # 14
```

## 8. 위험과 완화

| 위험 | 심각도 | 완화 |
|------|-------|------|
| **플로팅 배지 회귀** (최우선 보장) | High | 플로팅 배지 파일(OverlayAnimator/Overlay/Animation/OverlayStyle) **변경 0** — `git diff --stat` 강제. 커서 팝은 별도 타이머 + 별도 상태머신 + CursorOverlay 정적 필드만 |
| **DIB 재생성으로 팝 끊김** | Medium | bbox 를 MaxCursorHighlightScale 기준 고정 → 팝 중 DIB 재생성 0. EnsureDib `targetSize==_currentWidth` 스킵으로 검증 |
| **커서 DIB race** (dev-notes 알파 race) | Medium | 팝은 `_isVisible` 일 때만 트리거 + Hide 진입 시 KillTimer. 팝은 alpha 무관(scale 만) → 알파 race 영역 미접촉 |
| **팝 중 커서 이동** | Low | D3: 정지검출 모드는 이동 시 Hide → 팝 KillTimer 중단. 항상표시 모드는 모션 타이머=위치, 팝 타이머=scale 책임 분리 — 마지막 Render 가 최신 둘 다 반영 |
| **디폴트 true → 신규/기존 사용자 즉시 팝** | Low | D5 사용자 확정(원하는 효과). 끄려면 트레이 "커서 변경 시 강조" 해제 또는 `cursor_change_highlight: false` |

### 비상 스위치
- `cursor_change_highlight: false` → 팝 완전 비활성.
- 트레이 "커서 변경 시 강조" 해제 → 즉시 off (다음 IME 전환부터).
- `cursor_highlight_duration_ms: 0` → ratio 즉시 1.0 → 팝 1프레임 종료(사실상 무효과).

## 9. 테스트 영향

- `SettingsValidateTests` — 커서 팝 clamp 2종 신규 케이스 권장(scale 0.5→1.0, 3.0→2.0 / duration -100→0,
  5000→2000). 기존 케이스 영향 0.
- AppConfig 직렬화 — 새 3키 JSON roundtrip(기존 cursor 키 패턴 동일, STJ 소스 생성기 자동).
- SettingsDialog 필드 카운트 — 재배치는 순서만, 신규 2필드 commit 검증.
- 신규 테스트 파일 불요 — 기존 `SettingsValidateTests` 확장으로 충분.

## 10. 롤백

단일 PR/브랜치. `git revert` 또는 미머지. config 키는 unknown-key-ignore(STJ)라 구버전 exe 가 새
키 config.json 읽어도 무시 — 다운그레이드 안전.

## 11. 단계별 실행 (구현 commit 분할)

1. **config 4축** — DefaultConfig const+Min/Max + AppConfig init + Settings.Validate clamp. build+test.
2. **Core 셰이더** — CursorStyle.HighlightScale + bbox 고정 + CursorRenderer 반지름 배율 (scale=1.0
   디폴트로 기존 동작 무변경). build+publish.
3. **팝 상태머신** — AppMessages 타이머 ID + CursorOverlay TriggerPop/HandleCursorPopTimer + Program
   WM_TIMER 분기 + SetImeState 트리거 + Initialize hwndTimer.
4. **트레이 토글** — Tray IDM + 디스패치 + Menu + I18n.
5. **SettingsDialog 재배치** — Fields.cs 13섹션 재정렬 + 섹션명 + 신규 섹션/필드.
6. **문서** — CHANGELOG / architecture / config-reference / conventions / dev-notes + INDEX.
