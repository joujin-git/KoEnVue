# 04 — Stage 3: AppConfig 시그니처 좁히기

← Previous: [03 — Stage 2](03-stage2-relocation.md) | → Next: [05 — Stage 4](05-stage4-core-extraction.md)

## 목표

god-object 관례 제거. 재사용 가능 Core 모듈이 AppConfig 타입 자체를 알지 못하게 만드는 준비 단계. Stage 4의 Core 추출이 가능하도록 public 시그니처에서 `AppConfig` 파라미터를 걷어낸다.

## 에이전트 구성

- **구성**: mixed, 2x general-purpose (A 완료 후 B 직렬)
- ⚠️ **Program.cs 소유권 분할**: A가 Program.cs 6곳을 건드리고 B가 1곳만 건드리지만 동일 파일이므로 **직렬 실행**. 커밋 순서는 C7 → C8.

## 작업 A — 오버레이/애니메이션/로거 (내부 `_config` 접근)

### 편집 파일
- `App/UI/Overlay.cs`
- `App/UI/Animation.cs`
- `Core/Logging/Logger.cs`
- `Program.cs` (호출부)

### 만지지 말 것
- `App/UI/Tray.cs`, 3개 다이얼로그, Detector, Settings (Stage 3 B 및 Stage 4 영역)

### 접근 방식

현재 Overlay는 모든 메서드가 AppConfig를 외부에서 받지만 내부 `RenderIndicator`/`EnsureResources`/`EnsureFont`/`CalculateFixedLabelWidth`가 50개 config 필드를 직접 읽는다. `UpdateColor(state, OverlayColors)`로 좁히면 RenderIndicator도 같이 refactor해야 해서 Stage 4와 섞인다. 대신 **내부 `private static AppConfig _config` 필드 도입**:

- `Overlay.Initialize(hwndOverlay, config)` → 유지 (최초 주입 시점)
- `Overlay.Show(x, y, state, config)` → `Overlay.Show(x, y, state)`
- `Overlay.UpdateColor(state, config)` → `Overlay.UpdateColor(state)`
- `Overlay.HandleDpiChanged(config)` → `Overlay.HandleDpiChanged()`
- `Overlay.HandleConfigChanged(config)` → 파라미터 유지 (교체 시점이므로 여기만 필요. 내부적으로 `_config = config`)
- `Overlay.BeginDrag(config)` → `Overlay.BeginDrag(bool snapToWindows)` — primitive 주입
- `Overlay.HandleMoving(ref RECT, state, config)` → `Overlay.HandleMoving(ref RECT, state, bool snapToWindows, int snapThresholdPx)`
- `Overlay.GetDefaultPosition(hwndForeground, processName, config)` → `Overlay.GetDefaultPosition(hwndForeground, processName)`

private 메서드(`EnsureResources`, `EnsureFont`, `CalculateFixedLabelWidth`, `RenderIndicator`, `HandleDragDpiChange`)는 Stage 3에서 **건드리지 않음** — Stage 4 A에서 `LayeredOverlayBase` + `OverlayStyle` record로 함께 refactor.

### Animation 측
- `Animation.Initialize(hwndMain, hwndOverlay, config)` 유지 (Stage 4 B에서 `OverlayAnimator` 소비 시 변경)
- 내부적으로 Overlay 호출부만 새 시그니처로 업데이트: `Overlay.UpdateColor(state, _config)` → `Overlay.UpdateColor(state)` (Animation.cs:115/138/162)

### Logger
- 현재 `Logger.Initialize(AppConfig config)`([Logger.cs:40](../../Utils/Logger.cs#L40))은 3개 필드를 읽는다: `config.LogToFile`([Logger.cs:45](../../Utils/Logger.cs#L45)), `config.LogFilePath`([Logger.cs:47](../../Utils/Logger.cs#L47)), `config.LogMaxSizeMb`([Logger.cs:50](../../Utils/Logger.cs#L50)). `LogToFile=false`면 drain 스레드 종료·즉시 반환 분기가 있으므로 파라미터로 보존 필요.
- 좁힘 후 시그니처: `Logger.Initialize(bool enabled, string? logFilePath, int maxSizeMb)`. `enabled=false`면 나머지 두 인수 무시. `logFilePath`가 `null`/빈 문자열이면 기존 폴백 로직(`Path.Combine(AppContext.BaseDirectory, "koenvue.log")`)을 Logger 내부에서 유지.
- 로그 레벨은 이미 별도 API([Logger.cs:34](../../Utils/Logger.cs#L34) `Logger.SetLevel`)로 설정되므로 Initialize 시그니처에 포함하지 않는다. 현재 Program.cs 흐름([Program.cs:80-81](../../Program.cs#L80-L81))이 이미 `SetLevel(_config.LogLevel)` + `Initialize(_config)` 2단계이므로 호출 패턴은 유지.
- `LogLevel`이 Core/Logging으로 이동됐으므로 Core가 App.Models.LogLevel을 참조하는 자연스럽지 않은 의존이 해소됨 (Stage 2 이동과 Stage 3 시그니처 좁힘이 결합되어 Core 독립성 완성).
- Program.cs:81과 479 두 군데 업데이트 (duplicate call인지 Stage 3에서 확인 후 통합 가능하면 통합)
- Stage 2 → Stage 3-A 전이 구간(C6 ~ C7 사이)에서 `Core/Logging/Logger.cs`가 `KoEnVue.App.Models.AppConfig`를 참조하는 일시 레이어 위반이 존재한다. C7이 완료되는 즉시 해소되며 Stage 7 최종 gate에는 노출되지 않음. 03-stage2-relocation.md의 "알려진 일시 위반" 주의 박스 참조.

## 작업 B — 디텍터/Settings 내부 헬퍼

### 편집 파일
- `App/Detector/ImeStatus.cs`
- `App/Config/Settings.cs`
- `Program.cs` (호출부 단 1줄)

### 만지지 말 것
- Overlay/Animation/Logger (Stage 3 A 영역), 모든 UI/Dialogs, Core/*

### 작업 내역
- `ImeStatus.Detect(IntPtr hwnd, uint threadId, AppConfig)` → `Detect(IntPtr hwnd, uint threadId, DetectionMethod method)`
- 실제로 읽는 건 `config.DetectionMethod` 하나뿐 (`NonKoreanImeMode`는 Animation의 `GetTargetAlpha` 전용이므로 Detector 좁힘에는 무관)
- ImeStatus에 이미 존재하는 `Detect(hwnd, threadId)` 오버로드는 Auto 모드 내부 폴백용이므로 유지
- Animation의 `NonKoreanImeMode` 사용은 Stage 3 A가 아닌 **Stage 4 B (OverlayAnimator)** 에서 `AnimationConfig` record로 함께 처리 — 두 축을 섞지 말 것
- `Settings.cs` 내부 private 헬퍼 중 AppConfig 전체를 받으면서 3개 이하 필드만 사용하는 것을 찾아 좁힘
- Program.cs 호출부는 단 1줄(Program.cs:713)만 업데이트

## 검증 게이트

- `dotnet build` + `dotnet publish -r win-x64 -c Release` 성공
- 런타임 스모크:
  - IME 한/영 토글 → 오버레이 업데이트
  - Shift 드래그 축 고정
  - SnapToWindows 활성 상태에서 창 경계 스냅
  - `config.json`의 `LogLevel`을 `Debug`로 변경 후 핫-리로드 → 로그 파일에 Debug 라인 생성 확인
  - `NonKoreanImeMode`를 `Dim`으로 두고 일본어/중국어 IME 전환 → 오버레이 디밍 반영
  - 3개 배경/전경 색상을 `config.json`에서 변경 후 핫-리로드 → 오버레이 색상 반영

## Stage 4 A와의 공존성

Stage 3 A가 `Overlay`에 추가하는 `private static AppConfig _config`와 Stage 4 A가 추가할 `private static LayeredOverlayBase? _engine`은 둘 다 동일 static 클래스의 private static 필드라 충돌 없음. 실행 순서 C7 → C9로 차례로 쌓이며, `_engine.Render()`가 호출될 때 `OnRenderToDib` 콜백이 `_config`를 읽어 OverlayStyle을 구성해 Core로 전달하는 흐름이 자연스럽게 연결된다.

## 커밋 출력

| # | 작업 | 커밋 제목 |
|---|---|---|
| C7 | 3-A | `refactor: narrow Overlay/Animation/Logger signatures off AppConfig` |
| C8 | 3-B | `refactor: narrow ImeStatus.Detect signature to DetectionMethod` |

---

← Back to [README](README.md)
