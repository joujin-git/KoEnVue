# 05 — Stage 4: Core 기반 추출

← Previous: [04 — Stage 3](04-stage3-narrowing.md) | → Next: [06 — Stage 5](06-stage5-verification.md)

## 목표

사용자가 확정한 "깊은 분리". Core 측에 KoEnVue-specific 의존성 없이 독립 컴파일되는 재사용 기반을 완성한다. 4개의 서브태스크(A/B/C/D)는 편집 파일이 서로 다르므로 병렬 실행 가능.

## 에이전트 구성

- **구성**: parallel, 2x Plan + 4x general-purpose
- **Plan 에이전트 2대 병렬**로 먼저 4개 추출의 공개 API 설계를 문서화:
  - Plan A: `LayeredOverlayBase` + `OverlayStyle` + `OverlayAnimator` 공개 API
    - **선행 필수**: 아래 "사전 서브태스크 A0 — RenderIndicator 필드 감사"를 먼저 완료하고 결과를 `docs/reorg/stage4-style-audit.md`로 제출 → 이후 API 설계
    - 감사 완료 전에는 `OverlayStyle` 필드 확정 불가능 (starter set은 출발점일 뿐)
  - Plan B: `JsonSettingsManager<T>` + `NotifyIconManager` 공개 API
- **general-purpose 4대 병렬**로 실제 추출. 작업 A는 Plan A의 감사 결과를 입력으로 받음

## 작업 A — LayeredOverlayBase + OverlayStyle (컴포지션)

### 편집 파일
- 새 파일: `Core/Windowing/LayeredOverlayBase.cs`, `Core/Windowing/OverlayStyle.cs`
- 편집: `App/UI/Overlay.cs`

### ⚠️ 상속 대신 컴포지션

현재 `App/UI/Overlay.cs`는 **완전 정적 클래스**이며 [Program.cs](../../Program.cs)(17 호출부) / [Tray.cs:372](../../UI/Tray.cs#L372) (1 호출부, `ComputeAnchorFromCurrentPosition`) / [Animation.cs](../../UI/Animation.cs)(약 25개 호출부 — `UpdateColor`/`UpdateAlpha`/`UpdatePosition`/`UpdateScaledSize`/`Show`/`Hide`/`GetLastPosition`/`GetBaseSize`/`ForceTopmost` 등)가 `Overlay.X()` 형태로 직접 호출한다 (총 약 43 호출부). 인스턴스 상속으로 전환하면 모든 호출부가 `Overlay.Instance.X()`로 바뀌어야 해서 diff가 수백 라인 부풀고 Stage 3의 `_config` 내부 필드 도입과 충돌한다. 대신 `LayeredOverlayBase`를 **인스턴스 엔진 클래스**로 만들고 `Overlay`는 정적 파사드로 유지:

### ⭐ 사전 서브태스크 A0 — RenderIndicator 필드 감사 (Plan 에이전트 A 선행 작업)

Plan 에이전트 A가 `LayeredOverlayBase` 설계를 확정하기 **전에** 반드시 수행:

1. `App/UI/Overlay.cs`의 `EnsureResources`/`EnsureFont`/`CalculateFixedLabelWidth`/`RenderIndicator`/`GetLabelText` 5개 메서드를 모두 읽고, **각각이 `AppConfig`/`ImeState`에서 직접 읽는 필드를 전수 나열**한다. 예시: `config.FontFamily`/`config.FontSize`/`config.IndicatorScale`/`config.HangulLabel`/`state == ImeState.Hangul ? … : …` 등.
2. 나열된 필드를 다음 3개 버킷으로 분류:
   - **OverlayStyle 필드로 승격** (엔진이 읽어야 할 값)
   - **파사드에서 사전 계산해 OverlayStyle에 합성** (예: `IndicatorScale * FontSize` → `int FontSizePx`)
   - **상태 라우팅** (예: `ImeState` → `string LabelText` + 색상 3종)
3. 결과를 Stage 4 작업 시작 전 `docs/reorg/stage4-style-audit.md`에 임시 기록하고 Plan A 결과물에 첨부. (Stage 5 스모크 이후 삭제)

**이 서브태스크를 건너뛰면** 아래 OverlayStyle 필드 리스트는 **starter set**에 불과해 엔진이 실제 렌더 시 필요한 값을 못 받는 런타임 결함으로 이어짐.

### Core/Windowing/LayeredOverlayBase.cs
- 인스턴스 클래스
- 생성자: `IntPtr hwnd` + 콜백 `Func<HDC, OverlayStyle, (int w, int h)> renderToDib`
- Public 메서드: `Render(OverlayStyle style)`, `BeginDrag(bool snapToWindows)`, `EndDrag()`, `HandleMoving(ref RECT, bool snapToWindows, int snapThresholdPx)`, `Show(int x, int y)`, `Hide()`, `HandleDpiChanged()`, `UpdateAlpha(byte)`, `UpdatePosition(int, int)`, `UpdateScaledSize(int x, int y, int w, int h, byte alpha)`, `ForceTopmost()`, `(int w, int h) GetBaseSize()`, `(int x, int y) GetLastPosition()`, `Dispose()`

### 상태 소유권 분할 표 (Overlay → LayeredOverlayBase vs facade 잔존)

현재 `App/UI/Overlay.cs`([Overlay.cs:19-67](../../UI/Overlay.cs#L19-L67))의 private static 필드를 하나도 빠짐없이 아래 둘 중 하나로 매핑:

| 필드 | 이동 대상 | 비고 |
|---|---|---|
| `_hwndOverlay` | 엔진 | 생성자 인자 → 엔진 필드 |
| `_memDC` | 엔진 | GDI 리소스 |
| `_currentBitmap` (SafeBitmapHandle) | 엔진 | GDI 리소스 |
| `_currentFont` (SafeFontHandle) | 엔진 | GDI 리소스 |
| `_ppvBits` | 엔진 | DIB 포인터 |
| `_nullPen` | 엔진 | GDI 리소스 |
| `_currentDpiScale`, `_currentDpiY` | 엔진 | DPI 캐시 |
| `_currentWidth`, `_currentHeight` | 엔진 | DIB 크기 캐시 |
| `_baseWidth`, `_baseHeight` | 엔진 | DPI 미적용 크기 |
| `_fixedLabelWidth` | 엔진 | 라벨 고정 너비 (flip-flop fix 캐시) |
| `_cachedFontFamily`/`_cachedFontSize`/`_cachedFontWeight`/`_cachedFontDpiScale` | 엔진 | 폰트 캐시 키 |
| `_isVisible` | 엔진 | UpdateLayeredWindow 상태 |
| `_lastX`, `_lastY`, `_lastAlpha` | 엔진 | 위치/알파 캐시 |
| `_lastRenderedState` (ImeState?) | **엔진 — 타입 변경** | `ImeState?` → `OverlayStyle?` 로 변경. flip-flop 가드는 이전 스타일과 값 동등성 비교로 대체. **ImeState 누출 금지 조건 충족**. |
| `_isDragging`, `_dragStartX`, `_dragStartY`, `_dragHotPointX`, `_dragHotPointY` | 엔진 | 드래그 상태 |
| `_snapRects` | 엔진 | 스냅 후보 리스트 |
| `_config` (Stage 3-A 도입) | **파사드 잔존** | AppConfig는 Core에 등장 금지 |
| `_engine` (Stage 4-A 신규) | 파사드 | LayeredOverlayBase 인스턴스 참조 |

파사드에 남는 건 `_config` + `_engine` **단 2개 필드**. 나머지 15+개 필드는 모두 엔진 소유로 이동.

### App/UI/Overlay.cs
- 기존 static 유지
- 내부에 `private static LayeredOverlayBase? _engine` 필드 + Stage 3-A에서 도입된 `private static AppConfig _config` 유지
- `Initialize(hwnd, config)`가 `_engine = new LayeredOverlayBase(hwnd, Overlay.OnRenderToDib)` 수행
- 기존 public static 메서드는 `_engine` 메서드로 포워딩하면서 필요 시 `BuildStyle(_config, state)`로 스타일 합성 후 `_engine.Render(style)` 호출
- `private static OverlayStyle BuildStyle(AppConfig config, ImeState state)`: 라벨 문자열 라우팅(`GetLabelText` 로직 흡수) + 색상 3종 선택 + `IndicatorScale` 사전 곱셈
- `private static (int w, int h) OnRenderToDib(IntPtr hdc, OverlayStyle style)`가 실제 GDI 그리기 담당 — 엔진이 이 콜백을 호출할 때 파사드가 `DrawTextW` + `RoundRect`로 렌더

### OverlayStyle record (Core) — starter set

> ⚠️ 최종 필드 리스트는 사전 서브태스크 A0의 `stage4-style-audit.md` 결과로 확정. 아래는 출발점일 뿐.

```csharp
public readonly record struct OverlayStyle(
    string FontFamily,
    int FontSizePx,         // IndicatorScale + DPI 사전 적용 완료
    FontWeight Weight,
    int BorderRadiusPx,     // 사전 적용
    int BorderWidthPx,      // 사전 적용
    int PaddingXPx,         // 사전 적용
    string BgHex,
    string FgHex,
    string BorderHex,
    double Opacity,
    string LabelText        // 파사드가 ImeState → 문자열 라우팅
);
```
- 엔진은 `LabelText`와 폰트 메트릭에서 width/height를 내부 계산 (`GetTextExtentPoint32W` 등)
- `IndicatorScale`은 파사드에서 사전 곱셈 후 엔진에 전달 → 엔진이 scale 개념을 몰라도 됨
- `record struct`로 선언해 GC 압력 없이 값 동등성 비교 (flip-flop 가드용)

**ImeState 누출 금지**: LayeredOverlayBase / OverlayStyle / `_lastRenderedStyle` 어디에도 `ImeState`가 등장하면 안 됨 (Stage 7 grep 조건). 변환 지점은 `BuildStyle(config, state)` **단 1곳**이며 파사드 내부.

**호출부 diff 최소화**: Program.cs/Tray.cs/Animation.cs의 `Overlay.X(...)` 호출 표현은 **0건 변경** (static 파사드 유지). Stage 3 A의 시그니처 좁힘이 유일한 호출부 변경.

## 작업 B — OverlayAnimator

### 편집 파일
- 새 파일: `Core/Animation/OverlayAnimator.cs`, `Core/Animation/AnimationConfig.cs`
- 편집: `App/UI/Animation.cs`

### 추출 대상
- Hidden/FadingIn/Holding/FadingOut/Idle 페이즈 스테이트 머신
- 이즈-아웃 큐빅 `1-(1-t)^3`
- 슬라이드 보간
- TOPMOST 재적용 타이머

### 타이머 ID 소유권 규칙

현재 [Native/AppMessages.cs:48-52](../../Native/AppMessages.cs#L48-L52)에 5개의 `TIMER_ID_*` 상수(`FADE=1`/`HOLD=2`/`HIGHLIGHT=3`/`TOPMOST=4`/`SLIDE=5`)가 하드코딩되어 있고 `WM_TIMER` 라우팅은 메인 윈도우 프로시저(Program.cs)에서 `switch`로 분기한다. Stage 2에서 `AppMessages.cs`는 `App/UI/`로 이동하므로 Core에서는 참조 불가.

**해결**: `Core/Animation/OverlayAnimator`는 자기가 사용할 타이머 ID를 **생성자에 주입**받는다. 앱이 ID 할당을 소유:

```csharp
public sealed class OverlayAnimator
{
    public OverlayAnimator(
        IntPtr hwndTimer,           // 타이머 타깃(= _hwndMain)
        AnimationTimerIds timerIds, // 앱이 넘기는 ID 묶음
        AnimationConfig config,
        Action<byte> onAlphaChange,
        Action<int,int> onPositionOffset);

    public void OnWmTimer(nuint timerId); // 앱 WndProc가 WM_TIMER를 받으면 이 메서드로 위임
}

public readonly record struct AnimationTimerIds(
    nuint Fade, nuint Hold, nuint Highlight, nuint Topmost, nuint Slide);
```

앱 측 `App/UI/Animation.cs`는:
- `AppMessages.TIMER_ID_*` 상수를 그대로 유지 (App 네임스페이스이므로 합법)
- `OverlayAnimator`를 `new OverlayAnimator(hwndMain, new AnimationTimerIds(Fade: AppMessages.TIMER_ID_FADE, …), animConfig, …)`로 생성
- Program.cs의 `WM_TIMER` 핸들러가 `Animation.HandleTimer(nuint id)` → 내부에서 `_animator.OnWmTimer(id)`로 포워드
- 타이머 ID 비교는 엔진 내부 필드와 수행되므로 ID 값 자체는 앱 정의 상수 그대로 재사용 가능 (중복 재정의 없음)

**Core에 상수를 새로 정의하지 않는** 이유: Core가 `TIMER_ID_FADE = 1`을 박아두면 같은 hwnd에 붙는 다른 Core 모듈(향후 추가)과 ID 충돌이 발생할 수 있음. 앱이 모든 WM_TIMER ID를 단일 소스로 할당해야 충돌 관리가 가능.

### 공개 API
```
OverlayAnimator(
    IntPtr hwndTimer,
    AnimationTimerIds timerIds,
    AnimationConfig config,
    Action<byte> onAlphaChange,
    Action<int,int> onPositionOffset)

void OnWmTimer(nuint timerId)
void TriggerShow(int prevX, int prevY, int newX, int newY, bool imeChanged)
void TriggerHide(bool forceHidden)
void TriggerHighlight()
void Dispose()
```

### AnimationConfig record
```csharp
public readonly record struct AnimationConfig(
    bool AnimationEnabled,
    int FadeInDurationMs,
    int FadeOutDurationMs,
    int HoldDurationMs,
    bool ChangeHighlight,
    double HighlightScale,
    int HighlightDurationMs,
    bool SlideAnimation,
    int SlideDurationMs,
    int ForceTopmostIntervalMs,
    int AnimationFrameMs,
    double DimOpacityFactor);
```

### App/UI/Animation.cs
OverlayAnimator를 소비하면서 IME 분기(`NonKoreanImeMode.Hide` 등)만 앱 레벨에서 처리. `NonKoreanImeMode`는 `AnimationConfig`에 포함시키지 않고 App 레벨 `TriggerShow` 래퍼에서 필터링해 `_forceHidden` 경로로 라우팅 (ImeState 관련 개념이 Core에 들어가지 않도록).

## 작업 C — JsonSettingsManager\<T\>

### 편집 파일
- 새 파일: `Core/Config/JsonSettingsFile.cs`, `Core/Config/JsonSettingsManager.cs`
- 편집: `App/Config/Settings.cs`

### 추출 대상
- `MergeWithDefaults → Deserialize → EnsureSubObjects → Migrate → Validate → Apply` 파이프라인
- 파일 mtime 폴링(`CheckConfigFileChange`)
- 삭제-안전 hot-reload 가드
- 손상 파일 스팸 방지

### 공개 API
- `JsonSettingsManager<T>` 제네릭 클래스
- `T Load()`, `void Save(T)`, `bool CheckReload(out T)`
- virtual 훅: `T Migrate(T)`, `T Validate(T)`, `T ApplyTheme(T)`

### App/Config/Settings.cs
`JsonSettingsManager<AppConfig>` 하위 클래스 생성, Migrate/Validate/ThemePresets.Apply 훅만 구현.

### NativeAOT 대응
`[JsonSerializable(typeof(AppConfig))]` 컨텍스트를 App 측에 유지하고 제네릭 관리자에 `JsonTypeInfo<T>`를 주입. 구체 시그니처는 Plan 에이전트 B가 Stage 4 설계 단계에서 확정(`JsonSettingsManager<T>` + ctor `JsonTypeInfo<T>` vs `JsonSettingsManager<T, TContext> where TContext : JsonSerializerContext`).

## 작업 D — NotifyIconManager + ModalDialogLoop

### 편집 파일
- 새 파일: `Core/Tray/NotifyIconManager.cs`, `Core/Windowing/ModalDialogLoop.cs`
- 편집: `App/UI/Tray.cs`, `App/UI/Dialogs/CleanupDialog.cs`, `App/UI/Dialogs/ScaleInputDialog.cs`, `App/UI/Dialogs/SettingsDialog.cs`

### 추출 대상 — NotifyIconManager
- `Shell_NotifyIconW` 래퍼
- `NIM_SETVERSION`
- `NIF_SHOWTIP` (툴팁 표시 보존)
- `WM_CONTEXTMENU` 전경 활성화 처리
- 아이콘 교체, 툴팁 갱신

### 추출 대상 — ModalDialogLoop
- `EnableWindow(main, false)` + 중첩 `GetMessageW` + `IsDialogMessageW` + 종료 시 `EnableWindow(main, true)` 패턴
- 3개 다이얼로그가 모두 `ModalDialogLoop.Run(hwndDlg, hwndMain)` 호출로 귀결

### Tray.cs 처리
NotifyIconManager에 메뉴 문자열 콜백과 아이콘 렌더러 콜백을 주입. 아이콘 렌더링은 TrayIcon.cs에 그대로 남김 (ImeState-specific).

## 검증 게이트

- `dotnet build` + `dotnet publish -r win-x64 -c Release` 성공, 경고 증가 없음
- **A0 산출물 존재 확인**: `docs/reorg/stage4-style-audit.md`가 존재하고 `EnsureResources`/`EnsureFont`/`CalculateFixedLabelWidth`/`RenderIndicator`/`GetLabelText` 5개 메서드의 필드 참조 표가 기록되어 있음 (Stage 5 스모크 완료 후 삭제)
- **Core 독립 컴파일 확인**: `git grep "KoEnVue\.App" Core/` → 0건
- **ImeState 누출 가드**: `git grep "ImeState" Core/` → 0건 (LayeredOverlayBase/OverlayStyle/OverlayAnimator 내부에 절대 등장 금지)
- **published exe 크기 증분 ≤ +100KB** (기준선 대비) — 제네릭 인스턴스화 테이블 때문에 약간 증가는 허용
- 전체 런타임 스모크:
  - 오버레이 드래그, Shift 축 고정, 창 에지 스냅, 모니터 간 DPI 전환
  - 애니메이션 페이드 인/아웃, 하이라이트 스케일, 슬라이드
  - `Settings.Load()` 최초 실행 시 `config.json` 자동 생성
  - 핫-리로드(Opacity 변경, 5초 내 반영)
  - 손상된 `config.json` 투입 시 경고 1건만 기록 후 스팸 없음
  - 트레이 아이콘 툴팁 hover 표시(`NIF_SHOWTIP` 회귀 없음)
  - 트레이 메뉴 열림, 모든 토글 체크 상태 정상
  - 3개 다이얼로그 모두 정상 개폐 + Tab/ESC 동작

## 커밋 출력

| # | 작업 | 커밋 제목 |
|---|---|---|
| C9 | 4-A | `feat: extract LayeredOverlayBase and OverlayStyle to Core` |
| C10 | 4-B | `feat: extract OverlayAnimator state machine to Core` |
| C11 | 4-C | `feat: extract JsonSettingsManager<T> to Core` |
| C12 | 4-D | `feat: extract NotifyIconManager and ModalDialogLoop to Core` |

C9~C12는 서로 독립적이므로 Stage 4 안에서도 부분 revert 가능 (예: LayeredOverlayBase가 문제 있으면 C9만 revert).

---

← Back to [README](README.md)
