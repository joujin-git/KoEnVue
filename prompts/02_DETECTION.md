# Phase 02: 핵심 감지 엔진

## 목표
IME 한/영 상태 감지, 캐럿 위치 추적, 시스템 필터링 — 3개 감지 모듈을 구현한다.
이 단계가 완료되면 콘솔 로그로 IME 상태와 캐럿 좌표를 확인할 수 있어야 한다.

## 선행 조건
- Phase 01 완료 (Native/*.cs, Models/*.cs, Utils/*.cs, DefaultConfig.cs)

## 팀 구성
- **이온-감지**: ImeStatus.cs → CaretTracker.cs → SystemFilter.cs (순차 구현)
- mode: "plan" — 계획 제출 후 리드 승인 받아 구현 시작

## 병렬 실행 계획
- ImeStatus.cs와 SystemFilter.cs는 서로 독립적이라 병렬 가능하지만,
  CaretTracker.cs는 ImeStatus의 결과(hwndFocus 획득 패턴)를 참조하므로 ImeStatus 이후 작성 권장.
- 실질적으로는 한 팀원(이온-감지)이 순차 작성.

---

## 구현 명세

### Detector/ImeStatus.cs — IME 상태 감지

#### ImeState 결과 (3가지)
```csharp
// Models/ImeState.cs에 이미 정의됨 (Phase 01)
enum ImeState { Hangul, English, NonKorean }
```

#### 3-tier fallback 체인

```
public static ImeState Detect(IntPtr hwndFocus, uint threadId)
{
    // detection_method 설정에 따라:
    //   "auto" → 아래 3-tier 순차 시도
    //   "ime_default" → tier 1만
    //   "ime_context" → tier 2만
    //   "keyboard_layout" → tier 3만

    // [1순위] ImmGetDefaultIMEWnd + SendMessageTimeout
    IntPtr hIMEWnd = Imm32.ImmGetDefaultIMEWnd(hwndFocus);
    if (hIMEWnd != IntPtr.Zero)
    {
        nint result;
        IntPtr ret = User32.SendMessageTimeoutW(
            hIMEWnd,
            0x0283,          // WM_IME_CONTROL
            (IntPtr)0x0005,  // IMC_GETOPENSTATUS
            IntPtr.Zero,
            0x0002,          // SMTO_ABORTIFHUNG
            100,             // 100ms 타임아웃
            out result);
        if (ret != IntPtr.Zero)
        {
            // result != 0 이면 IME 열림 → 한글 모드
            // result == 0 이면 IME 닫힘 → 영문 모드
            // 추가: conversion 비트로 한/영 세부 판별
            return DetermineState(result);
        }
    }

    // [2순위] GetGUIThreadInfo() → ImmGetContext + ImmGetConversionStatus
    // (GetGUIThreadInfo는 호출부에서 수행되어 hwndFocus가 이미 해석된 상태)
    IntPtr hIMC = Imm32.ImmGetContext(hwndFocus);
    if (hIMC != IntPtr.Zero)
    {
        try
        {
            uint conversion, sentence;
            if (Imm32.ImmGetConversionStatus(hIMC, out conversion, out sentence))
            {
                // conversion 비트 마스크로 한/영 판별
                // IME_CMODE_HANGUL (0x01) 비트 확인
                return (conversion & 0x01) != 0
                    ? ImeState.Hangul
                    : ImeState.English;
            }
        }
        finally
        {
            Imm32.ImmReleaseContext(hwndFocus, hIMC);  // 반드시 해제!
        }
    }

    // [3순위] GetKeyboardLayout (언어 코드만)
    IntPtr hkl = User32.GetKeyboardLayout(threadId);
    ushort langId = (ushort)((long)hkl & 0xFFFF);
    if (langId == 0x0412)  // 한국어
        return ImeState.English;  // 한국어 IME이지만 한/영 구분 불가
    return ImeState.NonKorean;
}
```

핵심 규칙:
- `SendMessage` 금지 — 반드시 `SendMessageTimeoutW` + `SMTO_ABORTIFHUNG` 사용 (hang 방지)
- `ImmReleaseContext` 반드시 호출 (finally 블록)
- 3순위는 한국어 IME의 한/영 세부 구분이 불가능 — NonKorean 판별용

#### SetWinEventHook 하이브리드

```
메인 스레드에서 등록:
  _hEventHook = User32.SetWinEventHook(
      EVENT_OBJECT_IME_CHANGE,   // 0x8029 (eventMin)
      EVENT_OBJECT_IME_CHANGE,   // 0x8029 (eventMax)
      IntPtr.Zero,               // hmodWinEventProc (null = out-of-process)
      _imeChangeCallback,        // delegate
      0,                         // idProcess (0 = all)
      0,                         // idThread (0 = all)
      WINEVENT_OUTOFCONTEXT);    // 0x0000

콜백 실행 스레드:
  WINEVENT_OUTOFCONTEXT → 콜백은 SetWinEventHook를 호출한 스레드(= 메인 스레드)의
  메시지 루프에서 발화한다.
  콜백 내에서 직접 상태 갱신도 가능하지만, 감지 스레드와의 일관성을 위해
  PostMessage 패턴을 통일한다.

콜백 구현:
  void OnImeChange(IntPtr hWinEventHook, uint eventType,
                   IntPtr hwnd, int idObject, int idChild,
                   uint idEventThread, uint dwmsEventTime)
  {
      ImeState newState = Detect(GetForegroundWindow(), ...);
      if (newState != _lastState)
      {
          _lastState = newState;
          User32.PostMessage(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                            (IntPtr)newState, IntPtr.Zero);
      }
  }

종료 시:
  User32.UnhookWinEvent(_hEventHook);
```

하이브리드 동작 원리:
- **훅**: 빠른 반응. 한/영 키 연타 시 짝수번 전환 누락 방지.
- **폴링 (80ms)**: 훅이 발화하지 않는 앱에 대한 안전망.
- 두 채널 모두 동일��� 상태 비교 로직 → 중복 표시 없음.

---

### Detector/CaretTracker.cs — 캐럿 위치 추적

#### 4-tier fallback 체인

```
public static (int x, int y, int w, int h, string method)? GetCaretPosition(
    IntPtr hwndFocus, uint threadId, string processName, AppConfig config)
// 반환값: nullable — tier 4(GetCursorPos)까지 모두 실패 시 null
// method: 사용된 감지 방법 이름 ("gui_thread"/"uia"/"window_rect"/"mouse") — 디버그 오버레이(F-47)용
{
    // caret_method 설정에 따라:
    //   "auto" → 캐시 확인 후 4-tier 순차 시도
    //   "gui_thread" → tier 1만
    //   "uia" → tier 2만
    //   "mouse" → tier 4만

    // 앱별 캐시 확인 (auto 모드)
    if (config.CaretMethod == "auto" && _methodCache.TryGetValue(processName, out int cached))
    {
        var result = TryMethod(cached, hwndFocus, threadId, config);
        if (result.HasValue) return result.Value;
        // 캐시 실패 → 1순위부터 재시도
    }

    // [1순위] GetGUIThreadInfo → rcCaret → ClientToScreen
    GUITHREADINFO gti = default;
    gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();
    if (User32.GetGUIThreadInfo(threadId, ref gti))
    {
        // 유효 조건: rcCaret가 (0,0,0,0)이 아닐 것
        if (gti.rcCaret.Right != 0 || gti.rcCaret.Bottom != 0
            || gti.rcCaret.Left != 0 || gti.rcCaret.Top != 0)
        {
            POINT pt = new(gti.rcCaret.Left, gti.rcCaret.Top);
            // 핵심: hwndCaret 사용! (hwndFocus 아님!)
            User32.ClientToScreen(gti.hwndCaret, ref pt);
            int w = gti.rcCaret.Right - gti.rcCaret.Left;
            int h = gti.rcCaret.Bottom - gti.rcCaret.Top;
            CacheMethod(processName, 1);
            return (pt.X, pt.Y, w, h);
        }
    }

    // [2순위] UI Automation — Phase 07에서 구현
    // UiaClient.GetCaretBounds(hwndFocus) 호출
    // 타임아웃: config.Advanced.UiaTimeoutMs (기본 200ms)
    // 이 단계에서는 placeholder (null 반환)

    // [3순위] 포커스 윈도우 영역 기반 fallback
    RECT rect;
    if (User32.GetWindowRect(hwndFocus, out rect))
    {
        // DPI 스케일링은 Overlay.CalculateIndicatorPosition에서 적용 —
        // 여기서는 raw 좌표만 반환 (PRD §2.5: 캐럿 획득 단계에서는 스케일링 없음)
        CacheMethod(processName, 3);
        return (rect.Left, rect.Bottom + DefaultConfig.FocusWindowGap, 0, 0);
    }

    // [4순위] 마우스 커서 위치 (최종 fallback — 항상 성공)
    POINT cursor;
    User32.GetCursorPos(out cursor);
    CacheMethod(processName, 4);
    return (cursor.X, cursor.Y, 0, 0);
}
```

#### 앱별 방식 캐싱 (LRU)

```
private static readonly Dictionary<string, int> _methodCache = new();
private static readonly LinkedList<string> _lruOrder = new();
private const int MaxCacheSize = 50;  // config.AppMethodCacheSize

static void CacheMethod(string processName, int method)
{
    if (_methodCache.ContainsKey(processName))
    {
        _lruOrder.Remove(processName);
    }
    else if (_methodCache.Count >= MaxCacheSize)
    {
        string oldest = _lruOrder.Last!.Value;
        _lruOrder.RemoveLast();
        _methodCache.Remove(oldest);
    }
    _methodCache[processName] = method;
    _lruOrder.AddFirst(processName);
}
```

- "성공" 정의: fallback 체인에서 유효한 좌표를 반환한 방식
  - Tier 1: rcCaret != (0,0,0,0)
  - Tier 2: UIA bounds != empty
  - Tier 3/4: 항상 성공
- 캐시 미스 → 1순위부터 fallback
- 설정 리로드 시 캐시 전체 무효화

핵심 주의사항:
- **ClientToScreen에 반드시 `gti.hwndCaret` 전달** (hwndFocus 아님!)
- 매 호출마다 new Thread 생성 금지 (UIA는 전용 스레드, Phase 07)
- DPI 스케일링은 DpiHelper.Scale 사용

---

### Detector/SystemFilter.cs — 시스템 필터

#### 필터링 판정 순서 (단락 평가)

```
// PRD의 ShouldShowIndicator() 역할 (boolean 반전: true=숨김)
// monitorRect는 IsFullscreenExclusive 내부에서 획득 (PRD §2.2 시그니처와 차이)
public static bool ShouldHide(IntPtr hwnd, IntPtr hwndFocus, AppConfig config)
{
    // 하나라도 true면 인디케이터 숨김

    // 1. 보안 데스크톱
    if (hwnd == IntPtr.Zero) return true;

    // 2. 보이지 않거나 최소화됨
    if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;

    // 3. 현재 가상 데스크톱이 아님
    if (!IsOnCurrentVirtualDesktop(hwnd)) return true;

    // 4. 클래스명 블랙리스트
    string className = GetClassName(hwnd);
    // 성능 최적화 고려: 설정 로드 시 사전 병합하여 매 폴링마다 Concat 방지
    var hideClasses = config.SystemHideClasses
        .Concat(config.SystemHideClassesUser);
    if (hideClasses.Any(c => c.Equals(className, StringComparison.OrdinalIgnoreCase)))
        return true;

    // 5. 키보드 포커스 없음
    if (hwndFocus == IntPtr.Zero && config.HideWhenNoFocus) return true;

    // 6. 전체화면 독점
    if (config.HideInFullscreen && IsFullscreenExclusive(hwnd)) return true;

    // 7. 드래그 중 (마우스 좌클릭 누름) — PRD §5.3 드래그 감지를 ShouldShowIndicator에 통합
    if (User32.GetAsyncKeyState(Win32Constants.VK_LBUTTON) < 0) return true;

    // 8. 앱 필터 (블랙/화이트리스트)
    if (!PassesAppFilter(hwnd, config)) return true;

    return false;
}
```

#### IsFullscreenExclusive 구현

```
static bool IsFullscreenExclusive(IntPtr hwnd)
{
    RECT rect;
    User32.GetWindowRect(hwnd, out rect);

    IntPtr hMonitor = User32.MonitorFromPoint(
        new POINT(rect.Left, rect.Top), Win32Constants.MONITOR_DEFAULTTONEAREST);
    MONITORINFOEXW mi = default;
    mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
    User32.GetMonitorInfoW(hMonitor, ref mi);

    // 윈도우가 모니터를 완전히 덮는가?
    bool coversMonitor = rect.Left <= mi.rcMonitor.Left
        && rect.Top <= mi.rcMonitor.Top
        && rect.Right >= mi.rcMonitor.Right
        && rect.Bottom >= mi.rcMonitor.Bottom;

    if (!coversMonitor) return false;

    // WS_CAPTION 체크: 캡션 없으면 전체화면 독점
    int style = User32.GetWindowLongW(hwnd, -16);  // GWL_STYLE
    return (style & 0x00C00000) != 0x00C00000;  // WS_CAPTION = 0x00C00000
}
```

핵심: WS_CAPTION 체크로 **최대화 윈도우**(타이틀바 있음)와 **전체화면 독점**을 구분.

#### IsOnCurrentVirtualDesktop 구현

```
// IVirtualDesktopManager COM 인터페이스 사용
[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out bool onCurrentDesktop);
}

static bool IsOnCurrentVirtualDesktop(IntPtr hwnd)
{
    try
    {
        // CoCreateInstance로 VirtualDesktopManager 인스턴스 획득
        // IsWindowOnCurrentVirtualDesktop(hwnd, out bool result)
        // 실패 시 true 반환 (숨기지 않음 = 안전한 기본값)
        return _vdm?.IsWindowOnCurrentVirtualDesktop(hwnd, out bool result) == 0
            ? result : true;
    }
    catch { return true; }
}
```

#### PassesAppFilter 구현

```
static bool PassesAppFilter(IntPtr hwnd, AppConfig config)
{
    if (config.AppFilterList.Length == 0) return true;

    string processName = GetProcessName(hwnd);
    bool inList = config.AppFilterList.Contains(processName, StringComparer.OrdinalIgnoreCase);

    return config.AppFilterMode switch
    {
        "blacklist" => !inList,   // 리스트에 있으면 숨김
        "whitelist" => inList,    // 리스트에 없으면 숨김
        _ => true
    };
}
```

---

## 검증 기준

- [ ] ImeStatus.Detect()가 메모장에서 한/영 전환 시 정확한 상태 반환
- [ ] SendMessageTimeoutW 사용 (SendMessage 아님)
- [ ] ImmReleaseContext가 finally 블록에서 호출됨
- [ ] SetWinEventHook이 메인 스레드에서 등록됨
- [ ] CaretTracker가 GetGUIThreadInfo → hwndCaret로 ClientToScreen 호출
- [ ] rcCaret == (0,0,0,0) 체크로 유효성 판정
- [ ] LRU 캐시 크기 제한 (기본 50)
- [ ] SystemFilter 8개 조건이 단락 평가 순서대로 구현됨
- [ ] IsFullscreenExclusive에서 WS_CAPTION 체크
- [ ] GetAsyncKeyState(VK_LBUTTON) 드래그 감지
- [ ] 모든 P/Invoke는 Native/*.cs의 선언만 사용 (중복 선언 없음)
- [ ] DpiHelper.Scale 사용 (직접 계산 없음)

## 산출물
```
Detector/
├── ImeStatus.cs       # IME 3-tier fallback + SetWinEventHook 하이브리드
├── CaretTracker.cs    # 캐럿 4-tier fallback + LRU 캐싱
└── SystemFilter.cs    # 8-조건 시스템 필터
```
