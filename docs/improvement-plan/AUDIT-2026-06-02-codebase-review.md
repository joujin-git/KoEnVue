# Codebase Audit — 2026-06-02 전체 리뷰

**범위**: `App/` + `Core/` + `Program*.cs` 전체 (테스트·obj 제외).
**방법**: 4개 영역으로 분할해 explorer 서브에이전트 병렬 심층 리뷰 → 메인 세션에서 핵심 주장 교차검증(grep) → 종합·우선순위화.
**세 축**: ① 구조/안전성 개선 · ② 중복 → 공통 모듈화 · ③ 하드코딩 → const/enum/config.

> 이 문서는 **백로그**다. 단일 PR이 아니라 독립 항목들의 모음이며, 각 항목에 확신도·비용·정확한 `file:line`을 붙였다. 실행 시 아래 "권장 묶음" 단위로 PR(또는 의미 커밋)을 끊는 것을 권장.

---

## 0. 핵심 결론

이 코드베이스는 PR-00~22를 거치며 P1–P6이 이미 매우 엄격하게 강제되어 있다. **하드 규칙 위반은 0건**:

| 점검 | 결과 |
|------|------|
| `[DllImport]` (P1/AOT) | **0** — 전부 `[LibraryImport]` |
| bare `catch {}` (conventions §1) | **0** — 전부 narrowing 또는 정당화된 wide catch |
| Core → App enum 누출 (P6/Risk4) | **0** — `ImeState`/`NonKoreanImeMode`/`맑은 고딕` 등 |
| Core 내 `Logger.X` 직접 호출 (PR-09) | **0** — `LoggerSink` 정의부 외 전부 `LogProvider.Sink?.X` |
| `requireAdministrator` (P5) | **0** — 코드/매니페스트 |
| record struct 생성자 필드순서 취약성 | **0** — 전부 named argument |

따라서 가치 있는 발견은 전부 **invariant grep의 사각지대**에 있다: 식 인자(`* 72`, `!= 200`)·getter fallback·다이얼로그 레이아웃 좌표·셰이더 계수에 박힌 매직넘버, 그리고 grep으로는 안 보이는 의미론적 중복.

**총 발견**: 약 50건 (핵심 High 12 / Med 다수 / Low 다수). 그중 **삭제·dead 판정 등 비가역 주장 4건은 메인에서 grep으로 직접 교차검증 완료**(아래 ✓).

> **마커 범례**: `✓` = grep 교차검증 완료(착수 전 확인) · `✅` = 실행 완료 · `◐` = 부분 완료(범위 일부 보류/제외 — 셀에 사유 명기) · `🔒 보존` = 검토 후 제거 안 하기로 결정. **묶음 1 (dead code 삭제 + 매직넘버 const화) 은 2026-06-02 실행 완료** — IMP-1·HC-1·2·3·5·6·7·8·12·13 에 ✅, HC-17 은 🔒 보존 (CHANGELOG `[Unreleased]` 참조). **묶음 2 (설정 색상/문자열/배열 디폴트 단일화, DUP-1) 도 2026-06-02 실행 완료** — PR-17 numeric 축의 비-numeric 완성, 회귀 차단 invariant grep 동반 (§6 메모). **묶음 3 (다이얼로그 모듈화) 도 2026-06-02 실행 완료** — DUP-2 ✅ / DUP-5·DUP-6 ◐(각각 SettingsDialog commit 에러·`IDM_ADMIN_ELEVATION` 보류) + HC-9·10·11·14·15·16 ✅, `DefaultConfig.AppName` 신설, 동작 보존(값·문자열 불변).

**가장 임팩트 큰 3건**:
1. **DUP-1** — `AppConfig` ↔ `Settings.EnsureSubObjects`의 **string/array 디폴트 이중 관리** (PR-17이 numeric만 단일화하고 남긴 축). 수기 "양쪽 유지" 주석에 의존 중.
2. **HC 일괄** — `200`/`32`/`72`/`256*1024`/`4`/`255`/`0x8000`/`0xFFFF` 등 **P3 직접 위반 매직넘버** — 대부분 비용 S, 이미 같은 의미의 const가 다른 파일에 존재하는 경우 다수.
3. **DUP-2/DUP-3** — 스크롤바 셋업·DIB 픽셀 렌더 블록의 **글자 그대로 중복** → 이미 존재하는 helper 클래스에 메서드 추가만으로 해소. (DUP-2 는 묶음 3 `SetupVScrollbar`·DUP-3 은 묶음 4 `RenderDibPixels` 로 ✅ 완료)

---

## ⚠ 동작 이슈 (사용자 보고 — 코드 진단 완료)

코드 품질 리뷰와 별개로 사용자가 보고한 **커서 인디 강조**(IME 전환 스케일 팝, PR-21) 동작 2건. 코드를 직접 추적해 근본 원인을 확정했다.

### BEH-1 — 커서 강조가 `animation_enabled` 마스터를 무시 (공백, High)

**현상**: 트레이 "애니메이션 사용"(`animation_enabled`) OFF여도 커서 인디의 IME 전환 강조(팝)가 계속 작동.

**근본 원인**: PR-22가 **메인 인디**는 `Animation.BuildAnimationConfig`에서 `ChangeHighlight/SlideAnimation = AnimationEnabled && ...`로 마스터 게이팅했으나(`App/UI/Animation.cs:154,157`), **커서 인디는 별도 파사드**라 그 합성을 안 거친다. `App/UI/CursorOverlay.cs:209` `if (_config.CursorChangeHighlight) TriggerPop();`이 `AnimationEnabled`를 **전혀 보지 않음** → PR-22의 "전체 마스터" 의도(라벨·PRD·config-reference)에서 커서 팝이 누락된 사각지대.

**수정 방안** (메인 인디와 평행, 비용 S, 회귀 낮음):
```csharp
// CursorOverlay.SetImeState (현재 line 209)
if (_config.AnimationEnabled && _config.CursorChangeHighlight)
    TriggerPop();
else
    _engine.Render(_currentStyle);   // 마스터/개별 OFF면 색만 즉시 갱신 (강조 효과만 게이팅)
```
`HandleConfigChanged`의 `StopPop()`(CursorOverlay.cs:107)이 이미 있어 토글 OFF 순간 진행 중 팝도 정리됨. **PR-22 후속**으로 분류. (단위 테스트는 커서가 GDI 의존이라 어려움 — 수동 smoke 영역.)

### BEH-2 — 앱 포커싱 시 커서 강조 누락 (동일 IME 앱 전환 시) — 설계 결정 필요

**현상**: 앱 전환 시 커서 강조가 뜨는 경우와 안 뜨는 경우가 갈림. 명시적 한/영 전환은 정상.

**근본 원인**: `CursorOverlay.SetImeState`는 **IME 상태가 실제 바뀔 때만** 호출되고(`Program.cs:504-505`, `HandleImeStateChanged` 내부), 자체적으로 `CursorOverlay.cs:201` `if (_lastImeState == state) return;` early return을 둔다. 앱 포커스 변경 핸들러(`Program.cs:520 HandleFocusChanged`)는 **커서 인디를 전혀 건드리지 않는다**. 따라서:
- 한글앱 → 영문앱 (IME 바뀜): `HandleImeStateChanged` → `SetImeState` → 팝 ✅
- 한글앱 → 한글앱 (IME 동일): IME 미변경 → early return → 팝 없음 ❌
- 추가 게이트: 전환 순간 마우스 이동 중이면 `_isVisible=false`(`CursorOverlay.cs:205`)라 팝 없음

즉 사용자가 말한 "앱 포커싱 시 강조"는 실제로 **"앱 전환에 수반된 IME 변경 시 강조"**이고, 동일 IME 앱 사이 전환은 강조가 없다.

**이것은 현재 설계 의도와 일치**: PR-21 범위는 "커서 인디 **IME 전환** 스케일 팝"이고, 메인 인디도 동일하게 IME 변경 시에만 highlight(`Core/Animation/OverlayAnimator.cs:177` `willHighlight = highlightTrigger && ChangeHighlight`; focus change는 `Program.cs:530`에서 `imeChanged:false`). 즉 메인/커서 둘 다 "포커스 변경만으로는 강조 안 함"이 일관된 현 동작 — 버그라기보다 **기대치 차이**.

**두 방향 — 사용자 결정 필요**:
- **방향 A (현행 유지 + 문서화)**: IME 전환 시에만 강조 = 메인 인디와 일관된 의도. "동일 IME 앱 전환은 강조 없음"을 정상 동작으로 User_Guide/config-reference에 명문화. **코드 변경 0**.
- **방향 B (포커스 전환에도 강조 추가)**: `HandleFocusChanged`에서도 커서 팝 트리거(가시 상태 한정). 트레이드오프 — (a) 메인 인디는 focus 시 강조 안 하므로 메인↔커서 **비대칭** 발생(또는 메인까지 맞추면 변경 확대), (b) `EventTriggers.OnFocusChange` 연동 설계 필요, (c) 새 config 키(예: `cursor_highlight_on_focus`) 여부 결정. 비용 M+.

> BEH-1은 명확한 공백이라 수정 권장. BEH-2는 "현행이 의도된 동작"이라 방향 A(문서화)와 방향 B(기능 추가) 중 사용자 선호에 달림.

---

## 1. ② 중복 → 공통 모듈화

| ID | 확신 | 위치 | 무엇이 중복 | 모듈화 방안 | 비용 |
|----|------|------|-------------|-------------|------|
| **DUP-1** ★ ✅ | High | `App/Models/AppConfig.cs:34-49,83,85,194` ↔ `App/Config/Settings.cs:208,635-654` | 색상 6쌍(`#16A34A` 등)·`SystemHideClasses` 7배열·`SystemHideProcesses`·`OverlayClassName`·`FontFamily`/라벨(`맑은 고딕`/`한`/`En`/`EN`)이 init 디폴트와 null-폴백에 **각각 리터럴**. `AppConfig.cs:81-82,634` 주석이 "양쪽 동일 유지"를 수기 강제 중 | ✅ **완료 (묶음 2, 2026-06-02)**: 색상 7개(`DefaultHangulBg` 등 Bg/Fg 6 + `DefaultBorderColor`)·폰트/라벨 4개(`DefaultIndicatorFontFamily`=맑은 고딕 + 라벨 3)·`DefaultOverlayClassName` const + `DefaultSystemHideClasses`/`DefaultSystemHideProcesses` 배열 property 를 `DefaultConfig` 에 추출. 배열은 property(`=>`) 라 호출마다 새 배열(공유 변형 위험 0 — `TrayQuickOpacityPresets` 패턴). `AppConfig` 14 init + `Settings.EnsureSubObjects`/`ValidateAdvanced` 폴백 단일 참조, 수기 유지 주석 제거. 값 불변. 회귀 grep 신설(아래 메모) | M |
| **DUP-2** ✅ | High | `App/UI/Dialogs/SettingsDialog.Scroll.cs:30-42` ↔ `App/UI/Dialogs/CleanupDialog.cs:226-237` | `SCROLLINFO` 셋업(cbSize/fMask/nMin=0/nMax=total-1/nPage/nPos + SetScrollInfo)이 두 다이얼로그에 복제. `ScrollTo`/`ResolveVScrollPosition` 등은 이미 `ScrollableDialogHelper` 공유인데 셋업만 빠짐 | ✅ **완료 (묶음 3, 2026-06-02)**: `ScrollableDialogHelper.SetupVScrollbar(hwndViewport, totalContentHeight, viewportClientHeight)` 추가, 두 다이얼로그가 헬퍼 위임(nMax `Max(0,…)`/nPage `Max(1,…)` 방어 클램프 흡수). 동작 불변 | S |
| **DUP-3** ✅ | High | `Core/Windowing/LayeredOverlayBase.cs:228-259` ↔ `:467-490` | `PaintDib`와 `HandleDragDpiChange`가 "DIB clear → SelectObject(oldFont) → try{BuildMetrics+renderToDib}finally{복원} → ApplyPremultipliedAlpha → `_lastRenderedStyle=style`" ~25 LOC를 글자 그대로 중복 | ✅ **완료 (묶음 4, 2026-06-02)**: `private unsafe RenderDibPixels(style,w,h)` 추출, PaintDib·HandleDragDpiChange 위임 — 콜백 예외·폰트 복원·캐시 미갱신(재시도) 시맨틱 보존. 동작 불변 | S |
| **DUP-4** ◐ | Med | `Program.cs:785,1012,1047,1077,1098` (5곳) | "인디 가시 시 현재 위치로 재표시" 블록(`if(_indicatorVisible && _lastForegroundHwnd!=0){ (x,y)=GetAppPosition(); Animation.TriggerShow(...imeChanged:false) }`) 5중 반복 | ◐ **부분 완료 (묶음 5, 2026-06-02)**: `RefreshVisibleIndicator()` 추출 — `HandleConfigChanged`(추가 `!UserHidden` 가드 후 호출)·`HandleDisplayChange`·`HandleSettingChange` **3곳 적용**. 나머지 2곳(표시 전환·메뉴 else-if)은 가시 조건/후속 동작이 달라 **제외**. 동작 보존 | S |
| **DUP-5** ◐ | Med | `App/UI/Dialogs/ScaleInputDialog.cs:175-180,185-190` ↔ `SettingsDialog.cs:336-344` | 검증 실패 시 `RunExternal(MessageBoxW)+SetFocus+SendMessage(EM_SETSEL,-1)` 블록 3곳 복제 | ◐ **부분 완료 (묶음 3, 2026-06-02)**: `Win32DialogHelper.ShowFieldError(hwndOwner, hwndField, message, title)` 신설 — **`ScaleInputDialog` 2곳(invalid input + out of range)만 적용**. `SettingsDialog` commit 에러 경로는 ScrollIntoView 등 컨트롤별 선행 동작이 얽혀 **보류**(향후 합류 시 헬퍼 그대로 재사용) | S |
| **DUP-6** ◐ | Med | `App/UI/Tray.cs:316-318,550-551,606-607` | `MessageBoxW(_hwndMain, I18n.X, "KoEnVue", MB_OK)` 3곳 (2곳은 `RunExternal` 래핑까지 동형) | ◐ **부분 완료 (묶음 3, 2026-06-02)**: `Tray.ShowMessage(body)` 헬퍼(RunExternal 가드 + 타이틀 `DefaultConfig.AppName` 일관) 신설 — `ShowPositionError` / `CleanupPositions` empty / 위치 기록 empty 3 경로 위임. **`IDM_ADMIN_ELEVATION` 안내는 "확인 후 자동 종료" 흐름이라 헬퍼 미적용**(타이틀만 `"KoEnVue"`→`DefaultConfig.AppName` 으로 교체). HC-11(`DefaultConfig.AppName`) 동반 완료 | S |
| **DUP-7** 🔒 보류 | Med | `Program.cs:767-793` ↔ `:1026-1062` | 설정 적용 후처리 시퀀스(I18n.Load 분기→UpdateDetectionMethod→Overlay.HandleConfigChanged→ApplyCursorConfigChange→가시 시 TriggerShow→Tray.UpdateState)가 거의 동형 2회 | 🔒 **보류 (2026-06-02 정밀 재확인)**: `HandleConfigChanged`(풀 시퀀스: 재로드·로깅·I18n·detection·cursor·tray)와 `HandleSettingChange`(테마/강조색)의 공유는 **3개뿐**(ClearProfileCache + 조건부 Overlay.HandleConfigChanged + RefreshVisibleIndicator). **RefreshVisibleIndicator 는 이미 묶음 5에서 추출됨**. B는 A의 부분집합 아님 + 순서·UserHidden 가드 다름 → 통합 시 단계 선택 플래그 파라미터로 **오히려 복잡↑**. "거의 동형 2회" 진단은 라인 drift + 묶음 5 추출로 **이미 과장**됨 | M |
| **DUP-8** ✅ | Med | `App/UI/CursorOverlay.cs:143-147,165-169` | `_engine.Hide()+_isVisible=false+StopPop()+Logger.Debug` 묶음 반복 | ✅ **완료 (묶음 5, 2026-06-02)**: `HideCursor(string reason)` 헬퍼 추출 — 셸 UI 진입·이동 시작 2 경로 위임(멱등 — 이미 숨김이면 no-op). 동작 보존 | S |
| **DUP-9** ✅ | Med | `App/UI/Tray.cs:340-355,358-385` | DragModifier 4 case + PositionMode 2 case가 `if(config.X!=Y){updateConfig(...); Logger.Info(...)}` 동형 반복 | ✅ **완료 (묶음 5, 2026-06-02)**: `UpdateIfChanged(updateConfig, changed, newConfig, logMsg)` 헬퍼로 6 case 단일화 — `config with {…}` 합성은 호출자(changed=false 면 버려짐, record with 순수). 동작 보존 | M |
| **DUP-10** ✅ | Med | `Core/Logging/Logger.cs:107,132,197,273,280` | self-catch breadcrumb의 `[WARN] {DateTime.Now:yyyy.MM.dd HH:mm:ss.fff}` 접두 + 포맷 문자열 5회 반복 | ✅ **완료 (2026-06-02)**: `private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss.fff"` 신설 + `private static string FormatBreadcrumb(string)` 순수 헬퍼. 정상 경로 `Write()` 와 self-catch breadcrumb 4곳(pre-init 드롭 / 큐 상한 드롭 / drain join 타임아웃 ×2)이 동일 포맷 참조. 3 sink(`_logQueue.Enqueue`/`_fileWriter.WriteLine`/`Console.Error.WriteLine`) 차이는 유지, 출력 문자열 불변, drain 재귀 없음(NF-25) | S |
| **DUP-11** 🔒 보류 | Med | `App/UI/Dialogs/SettingsDialog.Scroll.cs:79-100` ↔ `CleanupDialog.cs:320-339` | `ViewportProc`(WM_VSCROLL/WM_MOUSEWHEEL/default)가 동형 — 둘 다 helper에 위임만 | 🔒 **보류 (2026-06-03 재평가 — 근거 정정)**: 스크롤 산술은 이미 `ScrollableDialogHelper` 로 단일화. 어제 "단일 공유 WndProc 으로 합치려면 hwnd→상태 매핑 인프라 신설 필요"는 **부정확** — `ScrollTo`/`ResolveVScrollPosition` 이 클래스 로컬 static 래퍼라 `&ScrollTo` 함수포인터 위임으로 **dispatch 만 헬퍼화하는 더 가벼운 길**이 존재(매핑 인프라 불필요). 그러나 **보류 유지**: dispatch ~10줄 제거 < 4-arg 함수포인터 헬퍼 복잡도 + `_lineHeight`/`_itemHeight` 분기 + 스크롤 자동검증 불가 → 이득 < 비용. 가치 재평가 시 가벼운 길로 선택 진행 가능 | M |
| **DUP-12** ✅ | Low | `App/UI/TrayIcon.cs:151-179` ↔ `:185-209` | `DrawCaretDot`/`DrawStrikeThrough`가 `CreateSolidBrush→SelectObject→try/finally 복원+DeleteObject` GDI 보일러 동일 | ✅ **완료 (2026-06-03 재평가)**: 어제 "ref struct using 가드 복잡도↑" 보류 근거는 **과장**이었음 — ref struct using 은 표준 RAII 패턴이고 힙 할당 0·람다 클로저 0(NativeAOT 적합). 신규 `private readonly ref struct SolidFillScope`(생성자=solid brush + NULL_PEN(stock) 셀렉트, `Dispose`=pen/brush 복원 + `DeleteObject`(brush))로 prologue 4줄 + finally 3줄 보일러 중복 제거 + GDI 핸들 누수 방지 표준화. 두 함수는 `using var _ = new SolidFillScope(hdc, fgColor);` 후 그리기만. 그리기 좌표/상수 불변이라 시각 회귀 위험 낮음 — reviewer 가 생성자/Dispose GDI 호출 순서 = 원본, NULL_PEN(stock) 미삭제, 좌표 불변 입증. 트레이 아이콘 시각 산출물은 자동검증 불가였으나 **육안검증 통과 ✅ (2026-06-03, 회귀 0)** — DUP-12 완결 | M |
| **DUP-13** ✅ | Low | `Core/Windowing/LayeredOverlayBase.cs:451-458,507-514` | DPI 변화 시 캐시 리셋(`_currentWidth=0;...;_lastRenderedStyle=null`)이 2곳 동일 (cursor는 필드집합 달라 비범위) | ✅ **완료 (묶음 4)**: `InvalidateDpiCaches()` 5필드 리셋 — HandleDragDpiChange·UpdateDpiFromPoint·HandleDpiChanged 3곳 공유(필드집합 정확). 동작 불변 | S |

---

## 2. ③ 하드코딩 → const/enum/config

> P3 직접 대상. ✓ = 메인에서 grep 교차검증 완료. **여러 건이 "같은 의미의 const가 이미 다른 파일에 존재"**하는 케이스.

| ID | 확신 | 위치 | 값·의미 | 방안 | 비용 |
|----|------|------|---------|------|------|
| **HC-1** ✅ | High | `Core/Http/HttpClientLite.cs:118` | `!= 200` HTTP OK | `const uint HttpStatusOk = 200;` | S |
| **HC-2** ✅ | High | `Core/Shell/UriLauncher.cs:42` | `(long)result <= 32` ShellExecute 성공 임계값 (`Shell32.cs:14,16`이 의미 2회 문서화) | ~~`Win32Constants.ShellExecuteSuccessThreshold = 32`~~ → 실제로는 `UriLauncher` 로컬 `private const long ShellExecuteSuccessThreshold = 32` (단일 사용처라 모듈 로컬) | S |
| **HC-3** ✓ ✅ | High | `Core/Windowing/LayeredOverlayBase.cs:550,685` | `MulDiv(fontSize, dpiY, 72)` points-per-inch. **`Win32DialogHelper.cs:27`에 이미 `PointsPerInch=72.0` 존재** | LayeredOverlayBase에 `const int PointsPerInch=72` (공용화 대신 엔진 로컬) | S |
| **HC-4** ✅ | High | `App/UI/CursorRenderer.cs:103,110,138,58,90,106,113,179-181` | 2×2 supersample 오프셋 `0.25`/`0.75`, 평균 `*0.25`, AA 여유 `+1.0`, 헤일로 흰색 `255` (셰이더 — grep 사각) | ✅ **완료 (묶음 4)**: `SubSampleLow/High`·`InvSubSampleCount`·`EdgeMarginPx`·`HaloWhiteComponent` const 5종. `avgAlpha*255.0` 정규화 스케일은 보존 | S |
| **HC-5** ✅ | Med | `Core/Http/HttpClientLite.cs:156` | `> 256 * 1024` 응답 상한 | `const long MaxResponseBytes = 256*1024;` | S |
| **HC-6** ✓ ✅ | Med | `Core/Windowing/LayeredOverlayBase.cs:231,470` · `LayeredCursorBase.cs:197` | `w*h*4`·`i*4` 32bpp BGRA. **`CursorRenderer.cs:29`에 이미 `BytesPerPixel=4` 존재 — Core만 누락** | `DibSectionFactory.BytesPerPixel=4` (DIB 생성처 = stride 진실원, 두 엔진 참조) | S |
| **HC-7** ✅ | Med | `LayeredOverlayBase.cs:730-732,723,724` · `LayeredCursorBase.cs:279-281,277` · `OverlayAnimator.cs:536` | premultiply/불투명 가드의 alpha 최대값 `255` | 곱셈·`a==255` 가드 → `byte.MaxValue` (BCL 상수 채택; `b*a/255` 나눗셈 수식은 의미 보존 위해 리터럴 유지) | S |
| **HC-8** ✓ ✅ | Med | `SettingsDialog.Scroll.cs:86` · `SettingsDialog.cs:364` · `ScaleInputDialog.cs:204` · `CleanupDialog.cs:283,327` (5곳) | 인라인 `& 0xFFFF` WM_COMMAND LOWORD. **`Win32Constants.LOWORD_MASK=0xFFFF`(Win32Types.cs:519) 이미 존재 — `Program.cs:953`만 사용** | 5곳을 const 참조로 통일 | S |
| **HC-9** ✅ | Med | `App/UI/Dialogs/SettingsDialog.cs:229,232` | 라벨 배치 `y+3`/`rowH-4` 수직 인셋·높이 보정 | ✅ **완료 (묶음 3)**: `LabelVInsetPx=3` / `LabelHeightTrimPx=4` const (`SettingsDialog.cs`). 값 불변 | S |
| **HC-10** ✅ | Med | `App/UI/Dialogs/SettingsDialog.Scroll.cs:67` | `ScrollTo(...+ _lineHeight*2)` "두 줄 여유" 마진 | ✅ **완료 (묶음 3)**: `ScrollIntoViewMarginLines=2` const (`SettingsDialog.Scroll.cs` 로컬 — Core 이동 안 함, ScrollIntoView 가 SettingsDialog 전용 동작이라). 값 불변 | S |
| **HC-11** ✅ | Med | `App/UI/Tray.cs:317,551,607` | MessageBox 타이틀 `"KoEnVue"` 리터럴 3곳 (앱 표시명 단일 진실원 부재; `UpdateRepoName`은 의미 다름) | ✅ **완료 (묶음 3, DUP-6과 함께)**: `DefaultConfig.AppName="KoEnVue"` const 신설 — `UpdateRepoName`(레포명)과 의미 분리. Tray 4 호출처(`ShowMessage` 경유 3 + `IDM_ADMIN_ELEVATION`)가 참조 | S |
| **HC-12** ✅ | Med | `Program.cs:571` ↔ `Core/Windowing/LayeredOverlayBase.cs:406` | GetAsyncKeyState 눌림 `0x8000`이 App(const)·Core(인라인) 각기 | `Win32Constants.KEY_PRESSED=0x8000` 단일 const (Core 배치 = P4 부합) | S |
| **HC-13** ✓ ✅ | Med | `App/Config/DefaultConfig.cs:75` | `HoldDurationMs=1500` **정의만, 사용처 0** (실 hold는 `EventDisplayDurationMs`) | dead const 제거 (단일 진실원 신뢰도) | S |
| **HC-14** ✅ | Low | `ScaleInputDialog.cs:168` · `SettingsDialog.Fields.cs:504` | EDIT 읽기 버퍼 `new char[32]`/`[len+2]` (영역3·4 중복 발견) | ✅ **완료 (묶음 3)**: `ScaleEditBufferChars=32`(`ScaleInputDialog.cs`) + `EditReadBufferSlackChars=2`(`SettingsDialog.Fields.cs`). 두 버퍼가 의미(고정 vs 길이+슬랙)가 달라 통합 대신 각 const 명명. 값 불변 | S |
| **HC-15** ✅ | Low | `SettingsDialog.Fields.cs:417,427` · `ScaleInputDialog.cs:125` | Double 표시/에러 포맷 `"0.###"`/`"0.##"`/`"0.#"` | ✅ **완료 (묶음 3)**: `ScaleDisplayFormat="0.#"`(`ScaleInputDialog`) · `DoubleFieldDisplayFormat="0.###"` · `DoubleRangeBoundFormat="0.##"`(`SettingsDialog.Fields`). 표시 의도가 자리수별로 달라 **단일 통일 안 함** — 3 const 분리 명명(원안 "통일"에서 조정). 값 불변 | S |
| **HC-16** ✅ | Low | `App/UI/Dialogs/CleanupDialog.cs:191-192` | 구분선 `-1`·두께 `2` 인라인 (`SettingsDialog.cs:218`은 `SectionSepH=2` const) | ✅ **완료 (묶음 3)**: `DlgSepThickness=2` const(`CleanupDialog.cs` 로컬). `SettingsDialog.SectionSepH` 와 값·의미는 같으나 각 다이얼로그 레이아웃 블록 소유 private const 라 **App→Core 공통화 안 함**(`-1` 인라인은 sepGap 계산 일부라 보존). 값 불변 | S |
| **HC-17** 🔒 보존 | Low | `Core/Native/WinHttp.cs:15,16,20,28` | 미사용 const 4개 (선언만, 사용처 0) | ~~제거~~ → **제거 안 함 (묶음 1 결정)**: 사용 중 상수의 짝/대안이라 참고용으로 의도적 보존 (dead 아님) | S |
| **HC-18** ✅ | Low | `SettingsDialog.Fields.cs` Combo setIdx 람다 **11곳**(실측 102/106/131/156/171/237/279/283/299/304/317; AUDIT 초안 89~158 5곳은 라인 drift) | `Math.Clamp(i,0,N)` enum 값 개수 매직넘버 N (Combo 팩토리가 이미 `labels.Length` 클램프 → 이중) | ✅ **완료 (2026-06-02 후속 재평가)**: explorer 실측 재수집으로 실제 11곳 확인(초안 5곳은 구버전). `Combo.Commit` 이 `CB_GETCURSEL` idx 를 `[0, labels.Length-1]` 로 클램프한 **직후** `setIdx(cfg, idx)` 호출 → setIdx 내 `Math.Clamp(i,0,N)` 는 검증된 인덱스의 순수 재클램프(이중). setIdx 가 Commit 클로저 전용(FieldDef 미저장·호출처 1)임을 확인하고 **방어를 Commit 단일 지점으로 통일**, setIdx 는 `(EnumType)i` 로 매직넘버 11개 제거 + Commit 에 계약 주석. reviewer 가 11곳 전부 `제거 N == labels.Length-1 == enum 최대 ordinal` 동등성 입증(회귀 0). 직전 "이중 안전망 보존(§4)" 판단을 뒤집음 — 이미 검증된 값의 재클램프는 방어가 아니라 중복이라 §4 와 무관 | M |
| **HC-19** ✅ | Low | `Core/Animation/OverlayAnimator.cs:558` | `*1000.0/Stopwatch.Frequency` 초→ms | ✅ **완료 (2026-06-02)**: `private const double MsPerSecond = 1000.0` 신설, `GetElapsedMs` 산식이 참조. 값 불변 | S |

---

## 3. ① 구조/안전성 개선

| ID | 확신 | 위치 | 현상 | 방안 | 비용 |
|----|------|------|------|------|------|
| **IMP-1** ✓ ✅ | High | `Core/Native/OleAut32.cs:1-26` (전체) | `SysFreeString`+SafeArray 5종 = **6 P/Invoke 전부 사용처 0** (주석의 UIA 코드 부재). grep 교차검증 완료 | 파일 통째 삭제 (미래 UIA 시 재도입). AOT marshalling stub 6개 제거 | S |
| **IMP-2** ✅ | High | `App/UI/Dialogs/SettingsDialog.cs:133-316` | `BuildChildren` ~184줄 단일 메서드 (스케일 21변수 + 행 루프 3-way switch + 스크롤바 + 버튼, 깊은 중첩) | ✅ **완료 (2026-06-02, 별도 세션)**: 보류 시 권장한 "레이아웃 메트릭 구조체화 + 육안검증" 경로로 실행. 신규 `SettingsDialog.Layout.cs` 의 `SettingsLayout` record struct(27필드) + `BuildLayout` 순수 팩토리로 메트릭을 분리하고, `BuildChildren` 은 ~17줄 오케스트레이터 + 윈도우 생성 헬퍼 5종으로 분해. `y` 누적 단일 결합점은 `LayoutRows` 가 콘텐츠 총높이를 **반환값**으로 노출해 박제(static 부작용 대입은 BuildChildren 집중). 순수 extract-method — 픽셀 동등 보존, 신규 매직넘버 0. `SettingsLayoutTests` 8 Fact 로 파생식(clamp/Max/뷰포트/DPI) 자동차단 + 나머지 수동 smoke. 설계 [dev-notes/2026-06-02-settings-buildchildren-decomposition.md](../dev-notes/2026-06-02-settings-buildchildren-decomposition.md) | M |
| **IMP-3** 보류 | Med | `App/UI/Tray.cs:257-449` | `HandleMenuCommand` ~190줄 (DUP-9 패턴 포함) | DUP-9 헬퍼 적용(✅ 묶음 5) + 분기 그룹 추출. **분해는 보류 (묶음 5, 2026-06-02)** — `UpdateIfChanged` 적용으로 6 case 는 축약했으나, switch 전체 분해는 case 흐름 명료성 유지 위해 미실행 | M |
| **IMP-4** 🔒 보류 | Low | `Core/Animation/OverlayAnimator.cs:171-246` | `TriggerShow` 4분기가 slide+highlight 호출집합 부분 반복 (분기마다 미묘하게 다름) | 🔒 **보류 (2026-06-02 검토)**: 4분기가 실제로 다 다름(분기1 SnapToTargetAlpha O / 분기3 SnapToTargetAlpha X + Fade Kill + `_forceHidden` 리셋 / 분기4 TryStartSlide X + AnimationEnabled 내부분기). 공통 꼬리는 `if(willHighlight) StartHighlight();` 1줄뿐 → 추출 이득 미미. **flip-flop 회귀 진앙**(주석 L264-269 에 race 박제: FadingIn 재진입 시 Fade 타이머 Kill 필요) + 애니 타이밍 자동검증 불가 | M |
| **IMP-5** ✅ | Low | `App/Detector/ImeStatus.cs:42-54` | `Detect(hwndFocus, threadId)` 2-arg 오버로드 외부 호출 0 (내부 default 분기만) | ✅ **완료 (2026-06-02)**: `public`→`private` 강등 (3-arg `Detect` 의 Auto 분기 전용, 외부 직접 호출 0). 공개 표면 축소, 동작 불변 | S |
| **IMP-6** ✅ | Low | `Program.cs:1105-1110` | `HandleDpiChanged`가 wParam/lParam 받지만 둘 다 무시 | ✅ **완료 (2026-06-03 재평가)**: 어제 보류 근거 2개가 **코드 사실과 어긋났음** — (1) "미래 per-monitor DPI hook 주석"은 코드에 **실재하지 않았음**(비트 레이아웃 설명 2줄뿐, "미래/per-monitor/hook" 문구 없음). (2) "WndProc dispatch 일관성 유지"는 **반대** — 원형 `(wParam,lParam)` 쌍을 받는 핸들러는 dispatch 전체에서 `HandleDpiChanged` 단 하나라 오히려 불일치였고, 인자 제거로 무인자 핸들러 그룹(`HandleConfigChanged` 등)과 일관성↑. `HandleDpiChanged(IntPtr,IntPtr)`→`HandleDpiChanged()` 로 dead param 정리 + WM_DPICHANGED case 호출부 수정. WM_DPICHANGED 페이로드는 현재 미사용(Overlay 가 자체 DPI 재조회) — 비트 레이아웃 주석은 "현재 미사용 + per-monitor DPI 정밀 대응 필요 시 dispatch 에서 wParam/lParam 재전달로 복원 가능"으로 갱신 보존. 자동검증 가능 영역(컴파일+동작 불변) | S |
| **IMP-7** ✅ | Low | `App/Config/Settings.cs:216-228` | `IsValidWindowClassName`이 ASCII 화이트리스트를 4중 범위 비교 | ✅ **완료 (2026-06-02)**: ASCII 4중 범위 비교 → `char.IsAsciiLetterOrDigit(c) \|\| c == '_'` (BCL 표준). 판정 집합 동일, 동작 불변 | S |

---

## 4. 검토했으나 제외 (의도적 결정 — 근거 확인됨)

- **LayeredCursorBase ↔ LayeredOverlayBase의 `ApplyPremultipliedAlpha` 분기** — `a==0` 처리가 의미 정반대(overlay=AA 엣지 보존 / cursor=외곽 잡티 제거). dev-notes 2026-05-28-pr-18 §결정1 + PR-18.md §3 + 인라인 코멘트 3중 박제. 공유화 시 회귀.
- **채널 비트연산** `<<16`/`<<8`/`&0xFF` (`ColorHelper`/`Dwmapi`/`User32`) — RGB↔COLORREF/ARGB 표준 레이아웃의 직접 표현. 위치 자체가 의미라 const화가 가독성 해침.
- **`CalculateNonClientHeight`의 `2*` 계수** — 상/하·좌/우 2변의 기하학적 상수. 주석 자명.
- **`Dwmapi.DwmGetWindowAttribute` 2 오버로드 / `User32.SystemParametersInfoHighContrast`** — PVOID를 `[LibraryImport]`가 generic marshalling 못해 타입별 시그니처 분기가 정석.
- **`Win32Constants`의 동일 비트값 별도 상수**(`WS_EX_LAYERED`=`WS_SYSMENU`=0x80000 등) — exStyle/style은 별개 비트 네임스페이스. 통합하면 의미 오염.
- **AppConfig nested record nullable / enum cast 디폴트** — PR-17 단일화 의도적 제외 대상.
- **DetectionLoop 지수 백오프 / flip-flop 히스테리시스 / AdminElevation 4-case** — dev-notes 정착, 회귀 민감. 재설계 금지.
- **I18n enum+_table+property 3축, `MenuAdminElevationExternal` 영문 mix** — 명시적 의도.
- **AppMessages WM_APP+N / Timer ID / 메뉴 ID 블록** — 전부 const + 의미 주석. 순차 정수 ID는 도메인 관용.

---

## 5. 실행 권장 묶음

> 의존·회귀 위험·비용으로 묶음. 각 묶음 = 1 PR(또는 의미 커밋). 매 묶음 후 verifier(build+AOT+test) + reviewer(P1–P6) 게이트.

| 묶음 | 포함 | 성격 | 비용 | 위험 |
|------|------|------|------|------|
| **묶음 0 — 동작(커서 마스터)** | BEH-1 (PR-22 후속 — 커서 팝 `AnimationEnabled &&` 게이팅) | 사용자 보고 공백 | S | 낮음 |
| **묶음 0b — 동작(포커스 강조)** | BEH-2 (방향 A 문서화 / B 기능추가 — 결정 후) | 사용자 결정 대기 | 0~M+ | — |
| **묶음 1 — dead/명백 const** ✅ **완료 (2026-06-02)** | IMP-1, HC-13 (삭제) + HC-1·2·3·5·6·7·8·12 (P3 일괄, 이미 const 존재분 우선). **HC-17 은 보존 결정 (🔒, 제거 안 함)** — WinHttp 미사용 const 4개는 사용 중 상수의 짝/대안 | 즉시·거의 무위험 | S | 낮음 |
| **묶음 2 — 설정 단일화** ★ ✅ **완료 (2026-06-02)** | DUP-1 | PR-17의 비-numeric 완성. 회귀 grep 동반 (양쪽 값 일치 박제) | M | 중 (디폴트 변경 표면) |
| **묶음 3 — 다이얼로그 모듈화** ✅ **완료 (2026-06-02)** | DUP-2(✅) + DUP-5(◐ ScaleInputDialog만, SettingsDialog 보류) + DUP-6(◐ IDM_ADMIN_ELEVATION 제외) + HC-9·10·11·14·15·16(✅) | helper 추가(`SetupVScrollbar`/`ShowFieldError`) + `DefaultConfig.AppName` + 레이아웃 const. **동작 보존(값·문자열 불변)** | S–M | 낮음 |
| **묶음 4 — Core 렌더 모듈화** ✅ **완료 (2026-06-02)** | DUP-3, DUP-13 + HC-4(셰이더 const) | 픽셀 렌더 단일화 | S | 낮음 (단위테스트 영역 밖 — 수동 smoke) |
| **묶음 5 — Program/Tray 정리** ◐ **완료 (2026-06-02)** | DUP-4(◐ 3곳 적용·2곳 제외) + DUP-8(✅) + DUP-9(✅) + IMP-3(보류 — DUP-9 헬퍼만 적용, switch 분해 미실행) | 반복 추출 + 클래스 내부 private 헬퍼(`RefreshVisibleIndicator`/`HideCursor`/`UpdateIfChanged`). **동작 보존(로직·로그 불변), 신규 모듈/config 키 0** | S–M | 낮음 |
| **묶음 6 — 큰 분해 (선택)** ◐ **부분 (2026-06-02~03)** | IMP-2(✅), DUP-7, DUP-11, IMP-4(🔒) | 180/190줄 분해·동형 시퀀스 통합 | M | 중 (회귀 민감 — 신중) — **IMP-2 ✅ 완료** (별도 세션 + 레이아웃 메트릭 struct 화 `SettingsLayout`/`BuildLayout` + `SettingsLayoutTests` 8 Fact 로 파생식 박제 + 수동 육안검증). 나머지 DUP-7·DUP-11·IMP-4 는 픽셀/애니 타이밍/dispatch 정적상태가 얽혀 자동검증 불가 + 이득 미미로 **🔒 보류 유지** (2026-06-03 재평가 — DUP-11 근거 정정[가벼운 함수포인터 길 존재하나 이득<비용], DUP-7·IMP-4 보류 유지·강화. 각 셀 근거 참조) |
| **잔여 Low** ✅ **완료 (2026-06-02~03)** | DUP-10·12, HC-18·19, IMP-5·6·7 | 기회 될 때 | S | 낮음 — **DUP-10·HC-18·HC-19·IMP-5·IMP-7 ✅ 완료 (2026-06-02)** + **DUP-12·IMP-6 ✅ 완료 (2026-06-03 재평가 — 어제 보류 근거가 과장/거짓이라 진행)**. **HC-18 은 2026-06-02 후속 재평가로 진행**(Commit 단일 클램프 통일). DUP-12 는 트레이 GDI 시각 산출물 → 육안검증 통과 ✅(2026-06-03, 회귀 0) |

**권장 순서**: 묶음 1(빠른 승리) → 묶음 2(최대 임팩트) → 3·4·5(병렬 가능) → 6은 별도 세션(IMP-2 ✅ 별도 세션 완료, 나머지 DUP-7·DUP-11·IMP-4 보류 유지). 전부 P3/P4 정신 강화이며 **동작 변경 0**(묶음 2의 디폴트 단일화 포함 — 값 자체는 불변)이 목표.

---

## 6. 메모

- 모든 `file:line`은 리뷰 시점(HEAD `d11241a` 이후) 기준. 착수 전 해당 줄 재확인 필수(세션 노트 라인번호 불신 원칙).
- ✅ 묶음 2 **회귀 차단 invariant 신설 완료** (2026-06-02): `AppConfig` 디폴트와 `Settings.EnsureSubObjects`/`ValidateAdvanced` 폴백이 같은 `DefaultConfig` 심볼을 참조하는지 — 리터럴 잔존 0 으로 가드. `git grep '"#16A34A"\|"맑은 고딕"\|"KoEnVueOverlay"\|"Progman"\|"ShellExperienceHost"' App/Models/AppConfig.cs App/Config/Settings.cs` → **0 매치** (정의는 `DefaultConfig.cs` 만). PR-17 numeric init invariant(`\}\s*=\s*리터럴`)와 형제 가드 — [conventions.md § P4 sub-rule](../conventions.md) 에 등재.
- 묶음 4는 `OverlayAnimatorTests` 류 콜백 spy 단위테스트로 일부 박제 가능하나, DIB 픽셀 경로는 수동 smoke 영역.
- **2026-06-02 후속 재평가** (사용자 지시 — IMP-2 분해 + 보류 6건 재평가): explorer 가 6건 실제 코드를 재수집(라인 drift 보정) 후 항목별 재판단 → **IMP-2·HC-18 진행 / DUP-7·DUP-11·DUP-12·IMP-4·IMP-6 보류 재확인**.
  - **IMP-2 ✅**: 레이아웃 메트릭 struct 화로 실행(별도 커밋). **HC-18 ✅**: `Combo.Commit` 단일 클램프로 방어 통일 → setIdx 매직넘버 11개 제거. "이중 안전망 보존" 직전 판단을 뒤집음(검증된 인덱스의 재클램프는 방어가 아니라 중복).
  - **보류 5건 라인 drift 보정**: DUP-7 = `Program.cs` `HandleConfigChanged`(766-789) ↔ `HandleSettingChange`(1088-1103) ↔ `HandleMenuCommand` 람다(1017-1060) — AUDIT 가 후자로 본 1026-1062 는 **HandleSettingChange 아닌 메뉴 람다**(3경로 부분집합 관계 재확인). DUP-11 = `SettingsDialog.Scroll.cs:72-93` ↔ `CleanupDialog.cs:314-333`. DUP-12(`TrayIcon.cs:151-179`↔`185-209`)·IMP-4(`OverlayAnimator.cs:171-246`)·IMP-6(`Program.cs:1105-1110`) 라인 불변.
  - **보류 재확인 근거**: DUP-7(3경로 부분집합·self-bump 우회 → 통합 시 단계선택 플래그로 복잡↑) · DUP-11(`[UnmanagedCallersOnly]` static 이 `_lineHeight`/`_itemHeight` 클래스별 필드에 바인딩 — 콜백 시그니처에 갇혀 공유 불가) · DUP-12(공통 보일러 9줄 추출은 가능하나 트레이 아이콘 GDI 렌더가 자동검증 불가 — IMP-2 로 이미 발생한 육안검증 부담에 시각 회귀 위험 누적 부적절) · IMP-4(4분기 실상이 — KillTimer Hold/Fade·Snap·slide·highlight 위치 + flip-flop race 박제 주석) · IMP-6(미사용 wParam/lParam 이나 future per-monitor DPI hook 시그니처 보존 — 멀티모니터 대응 중).
  - **2026-06-03 육안검증 통과**: 사용자가 IMP-2(설정 다이얼로그 14섹션 레이아웃·라벨/입력 정렬·스크롤·버튼) + HC-18(Combo 선택→저장→config.json enum 직렬화) 을 실 exe 로 확인 → **이상 없음(회귀 0)**. 자동검증 불가 영역이던 두 변경의 마지막 게이트가 닫혀 IMP-2·HC-18 **완결**.
- **2026-06-03 재평가** (사용자 지시 — 잔여 보류 5건 ultrathink 재평가): explorer 가 5건(DUP-7·DUP-11·DUP-12·IMP-4·IMP-6)의 실제 코드를 explorer 실측으로 재수집해 어제 "보류 재확인"을 코드 사실로 재검증. 결과 — **DUP-7·DUP-11·IMP-4 보류 유지 / DUP-12·IMP-6 진행**.
  - **DUP-12 ✅ 진행**: 어제 "ref struct using 가드 복잡도↑" 보류 근거는 **과장**. ref struct using 은 표준 RAII(힙 할당 0·클로저 0, NativeAOT 적합) — `SolidFillScope` 추출로 prologue 4 + finally 3줄 보일러 제거 + GDI 핸들 누수 표준화, 그리기 좌표/상수 불변. reviewer 가 생성자/Dispose 호출 순서=원본·NULL_PEN(stock) 미삭제·좌표 불변 입증. 트레이 시각 산출물은 자동검증 불가 → **육안검증 통과 ✅(2026-06-03, 회귀 0)** (셀 참조).
  - **IMP-6 ✅ 진행**: 어제 보류 근거 2개가 **코드 사실과 어긋남** — (1) "미래 per-monitor DPI hook 주석"은 코드에 실재하지 않았음(비트 레이아웃 2줄뿐, "미래/per-monitor/hook" 문구 없음). (2) "WndProc dispatch 일관성 유지"는 **반대** — `(wParam,lParam)` 쌍 핸들러는 dispatch 에 `HandleDpiChanged` 단 하나라 오히려 불일치, 인자 제거로 무인자 핸들러 그룹과 일관성↑. dead param 정리, 미래 per-monitor 는 dispatch 재전달로 1줄 복원(주석 명시). 자동검증 가능(컴파일+동작 불변) (셀 참조).
  - **DUP-7 보류 유지**: 3경로(`HandleConfigChanged` 풀 시퀀스 ↔ `HandleSettingChange` ↔ `HandleMenuCommand` 람다)가 단계·순서·UserHidden 가드가 다름. 통합 시 단계선택 플래그로 복잡↑ + ①③ UpdateColor 폴백 차이로 동작변경 위험. 최대 공통부 `RefreshVisibleIndicator` 는 이미 묶음 5 에서 추출 → 보류 정당.
  - **DUP-11 보류 유지 (근거 정정)**: 어제 "hwnd→상태 매핑 인프라 신설 필요"는 부정확 — `&ScrollTo` 함수포인터 위임으로 dispatch 만 헬퍼화하는 가벼운 길이 존재(매핑 인프라 불필요). 그러나 dispatch ~10줄 제거 < 4-arg 함수포인터 헬퍼 + `_lineHeight`/`_itemHeight` 분기 복잡도 + 스크롤 자동검증 불가 → 이득<비용으로 보류 유지(셀 참조).
  - **IMP-4 보류 강화**: 4분기 전부 다름(Snap/Slide/`_forceHidden`/AnimationEnabled 분기). 공통 꼬리 2줄조차 StartHighlight↔SnapToTargetAlpha 순서 때문에 못 뺌. flip-flop race 박제 진앙이라 보류 강화.
  - **2026-06-03 DUP-12 육안검증 통과**: 사용자가 트레이 아이콘(한/En/EN 캐럿+점 + UserHidden 취소선)을 실 exe 로 확인 → **이상 없음(회귀 0)**. `SolidFillScope` 추출의 자동검증 불가 게이트가 닫혀 DUP-12 **완결**. IMP-6 은 자동검증(컴파일+동작 불변)으로 이미 닫힘 → 잔여 보류 5건 재평가 산출 2건(DUP-12·IMP-6) 모두 완결.
