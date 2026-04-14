# Phase 2/5 — Task A: Overlay 분리 (C9)

> 이 파일은 **단일 세션에서 붙여 넣어 실행**하기 위한 self-contained 프롬프트다.
> Phase 1이 끝나고 C9-0 커밋이 있는 상태에서 시작한다.

---

## Task

Stage 4 Task A를 수행한다. `App/UI/Overlay.cs`의 **렌더링·DIB·레이어드 윈도우** 로직을 Core/Overlay/LayeredOverlayBase로 분리하고, 기존 `App/UI/Overlay.cs`는 static facade로 남긴 채 내부에서 Core 기반 인스턴스를 보유하도록 리팩터링한다. 외부 호출자(Animation/Tray/Program.cs)가 쓰는 public API는 **변경하지 않는다** — 정적 메서드 시그니처 그대로. 커밋 C9를 만든다.

## Context

- 저장소: `e:\dev\KoEnVue`
- 브랜치: `master`
- 직전 상태: Phase 1 C9-0 skeleton 완료. `Core/Overlay/LayeredOverlayBase.cs`, `Core/Overlay/OverlayStyle.cs`가 존재하지만 구현은 비어 있음. Release exe 4,891,136 바이트.
- 관련 문서:
  - `docs/reorg/05-stage4-core-extraction.md` Task A 섹션
  - `docs/reorg/09-risks-and-reuse.md` Risk 4 (ImeState leak), Risk 6 (UnmanagedCallersOnly entry point 충돌)
  - `CLAUDE.md` Indicator Rendering / Key Implementation Decisions 섹션

## Hard constraints

- **P1~P6 유지.** 특히 P6: `git grep "KoEnVue\.App" Core/` → 0.
- Core/Overlay는 `ImeState`, `AppConfig`, `DefaultConfig`를 **직접 참조하지 않는다** (Risk 4).
  - 색상 선택(한/영/비한 → bg/fg)은 App/UI/Overlay에서 수행. Core는 이미 결정된 `Color bg, Color fg`만 받는다.
- Core/Overlay는 **`OverlayStyle`** 값 객체만 받는다. 스케일(double)·DPI(int)·config bool은 OverlayStyle의 프로퍼티로 흡수한다.
- `App/UI/Overlay.cs`의 public static API는 **시그니처 변경 금지**:
  - `Initialize(IntPtr hwnd, AppConfig config)`, `HandleConfigChanged(AppConfig config)` — AppConfig 주입점
  - `Show(int x, int y, ImeState state)`, `UpdateColor(ImeState state)`, `HandleDpiChanged()`, `GetDefaultPosition(IntPtr hwndForeground, string processName)` — Stage 3 좁힘 형태 그대로
  - `BeginDrag(bool snapToWindows)`, `HandleMoving(ref RECT, ImeState, bool snapToWindows, int snapThresholdPx)`, `EndDrag()` — Stage 3 primitive-injected 형태 그대로
  - `ComputeAnchorFromCurrentPosition()`, `Hide()` 등 기존 멤버
- `[UnmanagedCallersOnly]` entry point 이름 충돌 주의 (Risk 6). Core/Overlay에 WndProc이 생긴다면 `EntryPoint = "Core_OverlayWndProc"` 등 명시적으로 고유화.
- Release 퍼블리시 exe 크기는 Phase 1 기준 4,891,136 바이트에서 **±수 KB 편차** 허용. 큰 증가(+50KB 이상)는 ILC tree-shaking이 제대로 동작하지 않는 신호이므로 원인 조사.

## 계획 재검토 원칙

**구현 중 계획 재검토가 필요하면 먼저 알려줘.** 에이전트가 `App/UI/Overlay.cs`를 실제로 읽어본 결과, 이 프롬프트의 상태 소유권 분할(facade 잔존 필드 vs engine 이동 필드), `OverlayStyle` 필드 리스트, public static facade 시그니처 유지 원칙, 드래그/스냅 로직의 App 잔존 결정, Risk 4(ImeState/AppConfig/DefaultConfig 누출 금지) 중 하나라도 실제 코드와 중요한 불일치가 있다고 판단되면, **구현을 시작하기 전에** 메인 세션에 보고하고 사용자 판단을 받는다. 특히 facade의 public static 시그니처를 변경해야 한다고 판단되거나 Animation.cs/Program.cs/Tray.cs를 함께 수정해야 한다고 판단되면 즉시 중단한다. 에이전트는 독자 판단으로 프롬프트의 계약을 바꾸지 않고, 보고 후 승인을 받을 때까지 기존 가정을 그대로 따른다.

## 0. Sanity check

- `git status` clean.
- `git log --oneline -3`에 C9-0 커밋 존재 확인.
- `git grep "KoEnVue\.App" Core/` → 0.
- `git grep "AppConfig" Core/` → 0.
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0.
- `dotnet build` 성공.

## 1. 작업 에이전트 구성

작업량이 크므로 **1개의 general-purpose 에이전트**에게 위임하고, 메인 세션은 결과를 리뷰·수정하는 역할만 수행한다. 에이전트는 연구가 아니라 **실제 구현**을 한다.

> **주의 — 토큰 부담 경고**
> Phase 2는 Stage 4 Task 중 가장 큰 단일 작업이다 (Overlay.cs ~1000줄 + 새 Core 클래스 2~3개). 에이전트 응답이 길어질 수 있으니, 결과 보고는 반드시 **≤400단어 요약 + 변경된 파일 목록 + 놓친 부분 질문** 형식으로 제한한다. 실제 diff는 커밋 후 `git diff`로 확인한다.

### Agent — Task A 구현

프롬프트 지시:

- 목적: `App/UI/Overlay.cs`에서 렌더링·DIB·layered window·font 관리 로직을 `Core/Overlay/LayeredOverlayBase.cs`로 옮기고, App 쪽은 정적 facade + 내부 instance 보유 형태로 리팩터링.
- 필수 읽기:
  - `App/UI/Overlay.cs` 전체
  - `Core/Overlay/LayeredOverlayBase.cs` + `Core/Overlay/OverlayStyle.cs` (Phase 1 skeleton)
  - `Core/Native/Win32Types.cs`, `Core/Native/SafeGdiHandles.cs`
  - `Core/Dpi/DpiHelper.cs`, `Core/Color/ColorHelper.cs`
  - `docs/reorg/05-stage4-core-extraction.md` Task A 상세
  - `docs/reorg/09-risks-and-reuse.md` Risk 4/6
- 구현 지침:
  1. `LayeredOverlayBase` (abstract class)에 넣을 것:
     - DIB 생성/갱신/파기 (CreateDIBSection, SafeBitmapHandle 필드)
     - font cache (SafeFontHandle 필드, EnsureFont 로직 — 단, AppConfig.FontFamily 대신 OverlayStyle.FontFamily를 받음)
     - UpdateLayeredWindow 호출 래퍼
     - RenderToDib(Graphics/HDC) 추상 메서드 — 구체 그리기는 App 파생 클래스
     - Dispose / Finalize 체인 (SafeGdiHandles 기반)
     - Protected 프로퍼티: 현재 DPI, 현재 OverlayStyle (nullable, Initialize 시 주입)
  2. `OverlayStyle` record:
     - LabelWidth, LabelHeight, LabelBorderRadius, BorderWidth, BorderColorHex, FontFamily, FontSize, FontWeight, IndicatorScale. (**AppConfig.Theme, HangulBg/Fg 등은 포함하지 않음** — 그건 App 파생 클래스가 해석.)
  3. `App/UI/Overlay.cs` (static facade) 리팩터링:
     - 정적 필드 `_engine` (`OverlayEngine : LayeredOverlayBase` or inline subclass) 보유.
     - 기존 static public 메서드는 내부 `_engine.*`로 위임.
     - `_config` 정적 필드는 그대로 유지(AppConfig 주입점). `HandleConfigChanged`에서 `_engine`에 `OverlayStyle` 주입.
     - 색상 선택 로직(`UpdateColor(ImeState)` → bg/fg)은 App에서 수행하여 Core의 `_engine.SetColors(bg, fg)`에 전달.
  4. **Risk 4 준수**: Core/Overlay 어떤 파일도 `using KoEnVue.App.*`를 포함하지 않아야 한다. `ImeState`, `AppConfig`, `DefaultConfig` 이름이 Core/Overlay에 등장하면 에러.
  5. **Risk 6 준수**: 기존 `[UnmanagedCallersOnly]` entry point가 Core/Overlay로 옮겨지는 경우 없으면 OK. 옮긴다면 `EntryPoint = "Core_OverlayXxx"`로 명명.
  6. 드래그/스냅 관련 `HandleMoving`은 복잡하고 App 상태(`_snapRects`, `_dragHotPoint*`)에 깊게 엮여 있다. **이번 Phase에서는 App/UI/Overlay에 남긴다.** Core로 옮기는 것은 향후 Task A-2로 미루고, Stage 4 Task A 범위에는 포함하지 않는다.
  7. 모든 public static 시그니처는 유지. 호출자(`App/UI/Animation.cs`, `Program.cs`, `App/UI/Tray.cs`) 수정 없음.
- 보고 형식: ≤400단어 요약 + 파일 목록 + 의사결정 포인트 + 미결 질문.

## 2. 본 세션 작업 — 에이전트 결과 검토

에이전트 결과를 받으면 다음을 직접 확인한다.

1. `Read`로 변경된 파일(`App/UI/Overlay.cs`, `Core/Overlay/LayeredOverlayBase.cs`, `Core/Overlay/OverlayStyle.cs`) 전체를 일독.
2. `git grep "KoEnVue\.App" Core/` → 0 확인.
3. `git grep "using KoEnVue\.App" Core/Overlay/` → 0 확인.
4. `git grep "ImeState\|AppConfig\|DefaultConfig" Core/Overlay/` → 0 확인 (Risk 4).
5. `Animation.cs`, `Program.cs`, `Tray.cs`가 **수정되지 않았는지** 확인 (`git diff --name-only`). 수정되었다면 facade 시그니처가 변경된 것이므로 원상복구 요청.

## 3. 빌드·스모크

- `dotnet build` 성공.
- `dotnet publish -r win-x64 -c Release` 성공.
  - exe 크기 확인 — Phase 1 기준 ±수 KB 편차만 허용.
- Release 퍼블리시 exe 실행:
  - 켜지자마자 인디케이터가 보이는지.
  - 한/영 전환 시 색상이 바뀌는지.
  - 다른 앱으로 포커스 이동 시 위치가 따라가는지.
  - DPI가 다른 모니터로 드래그 시 크기가 재계산되는지.
  - Shift-drag 축 고정이 동작하는지.
  - 스냅이 동작하는지.
  - `koenvue.log`에 에러/경고가 없는지 (WARN은 corrupted-config 케이스 외 없어야 함).

## 4. 버그 시 대응

빌드가 깨지거나 스모크에서 실패하면:

- 에이전트에게 수정 지시 (1~2회 재시도 가능).
- 2회 재시도 후에도 실패 시 `git restore .`로 작업 폐기 후 원인을 다시 분석한다. 에이전트 프롬프트를 더 구체적으로 다시 작성 후 재시도.

## 5. 검증

다음 모두 통과해야 커밋한다. 하나라도 실패하면 `git restore .`로 폐기 후 재시도.

1. `git grep "KoEnVue\.App" Core/` → 0.
2. `git grep "using KoEnVue\.App" Core/Overlay/` → 0.
3. `git grep "ImeState\|AppConfig\|DefaultConfig" Core/Overlay/` → 0.
4. `git grep "AppConfig" Core/` → 0.
5. `dotnet build` / `dotnet publish -r win-x64 -c Release` 성공.
6. publish exe 크기 변화가 +50KB 미만.
7. `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5).
8. 스모크 매트릭스 (3번 섹션) 통과.

## 6. 커밋

- 커밋 메시지: `refactor(reorg): Stage 4 Task A — Overlay rendering → Core/Overlay/LayeredOverlayBase (C9)`
- 본문:
  - LayeredOverlayBase + OverlayStyle 신설 요약
  - App/UI/Overlay.cs가 static facade로 유지되었음
  - 드래그/스냅은 App에 잔존 (Task A-2로 연기)
  - Risk 4/6 준수 확인
  - exe 크기 변화 수치

## 7. Phase 종료 시 사용자에게 보고

- ≤200단어.
- 분리된 파일 목록(`Core/Overlay/*`, `App/UI/Overlay.cs` 변경 요약).
- exe 크기 수치 (Phase 1 대비 증감).
- Risk 4/5/6 grep 결과.
- 스모크 체크리스트 통과 여부.
- 다음 Phase에서 Task B(Animation)를 수행한다는 안내.
