# Phase 03: 메인 루프 + 스레드 모델 + 이벤트 파이프라인

## 목표
Program.cs 진입점, Win32 메시지 루프, 3-스레드 모델, 이벤트 트리거 로직을 구현한다.
이 단계가 완료되면 앱이 실행되어 IME 상태 변경과 포커스 변경을 감지하고
커스텀 메시지로 메인 스레드에 통보할 수 있어야 한다.

## 선행 조건
- Phase 01 완료 (P/Invoke, Models, Utils, DefaultConfig)
- Phase 02 완료 (ImeStatus, CaretTracker, SystemFilter)

## 팀 ���성
- **이온-리드**: Program.cs 작성 (통합 작업이므로 리드가 직접 수행)
- mode: "plan" — 계획 제출 후 승인

## 병렬 실행 계획
- 이 단계는 모든 모듈을 통합하는 작업이므로 **순차 실행**.
- Program.cs 1개 파일에 집중.

---

## 구현 명세

### Program.cs — 앱 진입점 + 메시지 루프

#### 엔트리포인트

```csharp
// NativeAOT WinExe → async Main 불가
// 콘솔이 없으므로 Console.CancelKeyPress 사용하지 않음
static class Program
{
    // 전역 상태
    private static IntPtr _hwndMain;
    private static IntPtr _hwndOverlay;
    private static volatile AppConfig _config;
    private static volatile ImeState _lastImeState;
    private static IntPtr _hEventHook;
    private static Mutex? _mutex;
    private static bool _stopping;
    private static volatile bool _needCaretUpdate;   // 메인↔감지 스레드 간 공유
    private static volatile bool _indicatorVisible;   // 인디케이터 표시 상태 (메인 스레드에서 갱신)

    static void Main()
    {
        // 1. 이전 트레이 찌꺼기 정리
        CleanupPreviousTrayIcon();

        // 2. 다중 인스턴스 체크
        if (!TryAcquireMutex()) return;

        // 3. 설정 로드
        _config = Settings.Load();
        // 탐색 순서: %APPDATA%\KoEnVue\config.json → exe dir → DEFAULT_CONFIG

        // 4. I18n 텍스트 로드
        I18n.Load(_config.Language);

        // 5. 감지 엔진 초기화
        // ImeStatus, CaretTracker 모듈 준비 (상태 없는 static 클래스)

        // 6. UI 초기화
        RegisterWindowClass();
        _hwndMain = CreateMainWindow();
        _hwndOverlay = CreateOverlayWindow();
        InitializeTray();

        // 7. 스레드 시작
        StartDetectionThread();
        StartUiaThread();

        // 8. 이벤트 훅 등록
        RegisterImeHook();

        // 9. 핫키 등록
        if (_config.HotkeysEnabled) RegisterHotkeys();

        // 10. 종료 핸들러
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        // 11. 메인 메시지 루프
        RunMessageLoop();
    }
}
```

#### 다중 인스턴스 방지

```csharp
static bool TryAcquireMutex()
{
    _mutex = new Mutex(true, "KoEnVue_{A1B2C3D4-...}", out bool createdNew);
    if (!createdNew)
    {
        // 이미 실행 중 → 종료
        _mutex.Dispose();
        return false;
    }
    return true;
}
```

#### 크래시 복구 — 고정 GUID NIM_DELETE

```csharp
static void CleanupPreviousTrayIcon()
{
    // 앱 시작 시 가장 먼저 실행
    // 고정 GUID로 이전 비정상 종료 시 남은 아이콘 제거
    var nid = new NOTIFYICONDATAW { /* 고정 GUID 설정 */ };
    Shell32.Shell_NotifyIconW(0x02, ref nid);  // NIM_DELETE
    // 아이콘이 없으면 무시 → 안전
}
```

#### Win32 윈도우 클래스 등록 + 생성

```csharp
static void RegisterWindowClass()
{
    var wc = new WNDCLASSEXW
    {
        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
        lpfnWndProc = &WndProc,  // 함수 포인터 (NativeAOT 호환)
        lpszClassName = "KoEnVueMain",
        // ... 기타 필드
    };
    User32.RegisterClassExW(ref wc);
}

static IntPtr CreateMainWindow()
{
    // 메시지 전용 윈도우 (화면에 표시 안 됨)
    return User32.CreateWindowExW(0, "KoEnVueMain", "KoEnVue",
        0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
}

static IntPtr CreateOverlayWindow()
{
    // 오버레이 윈도우 — Phase 04에서 렌더링 연결
    return User32.CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST
            | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
        _config.Advanced.OverlayClassName,  // "KoEnVueOverlay"
        "",
        WS_POPUP,
        0, 0, 0, 0,
        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
}
```

#### 메인 메시지 루프

```csharp
static void RunMessageLoop()
{
    MSG msg;
    while (User32.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
    {
        User32.TranslateMessage(ref msg);
        User32.DispatchMessage(ref msg);
    }
}
```

#### WndProc — 메시지 처리

```csharp
[UnmanagedCallersOnly]
static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
{
    switch (msg)
    {
        // === 커스텀 메시지 (감지 스레드 → 메인 스레드) ===

        case AppMessages.WM_IME_STATE_CHANGED:
            HandleImeStateChanged((ImeState)(int)wParam);
            return IntPtr.Zero;

        case AppMessages.WM_FOCUS_CHANGED:
            HandleFocusChanged(wParam);  // wParam = 새 hwndFocus
            return IntPtr.Zero;

        case AppMessages.WM_CARET_UPDATED:
            HandleCaretUpdated((int)wParam, (int)lParam);  // x, y 스크린 좌표
            return IntPtr.Zero;

        case AppMessages.WM_HIDE_INDICATOR:
            HideOverlay();
            return IntPtr.Zero;

        case AppMessages.WM_CONFIG_CHANGED:
            HandleConfigChanged();
            return IntPtr.Zero;

        // === 트레이 ===

        case AppMessages.WM_TRAY_CALLBACK:
            HandleTrayCallback(lParam);
            return IntPtr.Zero;

        // === 타이머 (애니메이션) ===

        case 0x0113:  // WM_TIMER
            HandleTimer(wParam);  // wParam = timer ID
            return IntPtr.Zero;

        // === 핫키 ===

        case 0x0312:  // WM_HOTKEY
            HandleHotkey((int)wParam);  // wParam = hotkey ID
            return IntPtr.Zero;

        // === 시스템 메시지 ===

        case 0x0218:  // WM_POWERBROADCAST
            if ((int)wParam == 0x0007)  // PBT_APMRESUMESUSPEND
                HandlePowerResume();
            return IntPtr.Zero;

        case 0x007E:  // WM_DISPLAYCHANGE
            HandleDisplayChange();
            return IntPtr.Zero;

        case 0x001A:  // WM_SETTINGCHANGE
            HandleSettingChange();
            return IntPtr.Zero;

        case 0x02E0:  // WM_DPICHANGED (PerMonitorV2 → 모니터 간 이동 시 발화)
            HandleDpiChanged(wParam, lParam);
            return IntPtr.Zero;

        case 0x0111:  // WM_COMMAND
            HandleMenuCommand((int)wParam);
            return IntPtr.Zero;

        case 0x0002:  // WM_DESTROY
            User32.PostQuitMessage(0);
            return IntPtr.Zero;

        default:
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
```

---

### 3-스레드 모델

#### 감지 스레드 (Background)

```csharp
static void StartDetectionThread()
{
    var thread = new Thread(DetectionLoop)
    {
        IsBackground = true,  // 메인 종료 시 자동 종료
        Name = "KoEnVue-Detection"
    };
    thread.Start();
}

static void DetectionLoop()
{
    IntPtr lastHwndFocus = IntPtr.Zero;
    IntPtr lastHwndForeground = IntPtr.Zero;
    ImeState lastImeState = ImeState.English;
    int pollCount = 0;
    DateTime lastConfigMtime = DateTime.MinValue;
    // _needCaretUpdate 라이프사이클 (클래스 필드 — volatile은 로컬 변수에 사용 불가):
    //   true로 설정: 메인 스레드 핸들러(HandleImeStateChanged/HandleFocusChanged)에서
    //   false로 리셋: 감지 스레드에서 캐럿 좌표 획득 + PostMessage 완료 후

    while (!_stopping)
    {
        Thread.Sleep(_config.PollIntervalMs);  // 기본 80ms
        pollCount++;

        // 0. config.json 변경 감지 (약 5초마다 = 62폴링 x 80ms)
        if (pollCount % DefaultConfig.ConfigCheckIntervalPolls == 0)
        {
            CheckConfigFileChange(ref lastConfigMtime);
        }

        // 1. 포그라운드 윈도우 확인
        IntPtr hwndForeground = User32.GetForegroundWindow();

        // 자기 자신의 윈도우(인디케이터/메인) 무시 (PRD §2.1)
        if (hwndForeground == _hwndMain || hwndForeground == _hwndOverlay)
            continue;

        uint threadId = User32.GetWindowThreadProcessId(hwndForeground, out uint processId);

        // 2. 시스템 필터 체크 (포그라운드 윈도우가 변경된 경우에만 실행 — PRD §2.1 최적화)
        GUITHREADINFO gti = default;
        gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();
        User32.GetGUIThreadInfo(threadId, ref gti);
        IntPtr hwndFocus = gti.hwndFocus;

        if (hwndForeground != lastHwndForeground)
        {
            lastHwndForeground = hwndForeground;
            // 포그라운드 윈도우 변경 시에만 시스템 필터 실행 (PRD §2.1 최적화)
            if (SystemFilter.ShouldHide(hwndForeground, hwndFocus, _config))
            {
                if (_indicatorVisible)
                    User32.PostMessage(_hwndMain, AppMessages.WM_HIDE_INDICATOR, 0, 0);
                continue;
            }
        }
        // 포그라운드 동일 시 시스템 필터 스킵 → 단계 3(포커스 변경 감지)부터 실행 (PRD §2.1)

        // 3. 포커스 변경 감지
        if (hwndFocus != lastHwndFocus)
        {
            lastHwndFocus = hwndFocus;
            User32.PostMessage(_hwndMain, AppMessages.WM_FOCUS_CHANGED,
                hwndFocus, IntPtr.Zero);
        }

        // 4. IME 상태 감지
        ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId);
        if (currentIme != lastImeState)
        {
            lastImeState = currentIme;
            User32.PostMessage(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                (IntPtr)(int)currentIme, IntPtr.Zero);
        }

        // 5. 캐럿 위치 추적 (이벤트 발생 시에만 또는 always 모드)
        if (_needCaretUpdate)
        {
            string procName = GetProcessName(processId);
            var (x, y, w, h) = CaretTracker.GetCaretPosition(
                hwndFocus, threadId, procName, _config);

            // tier-1 재시도 로직: rcCaret==(0,0,0,0)이면 50ms 간격 최대 3회 재시도
            // CaretTracker.GetCaretPosition 내부에서 처리됨 (02_DETECTION.md 참조)

            // 좌표를 IntPtr로 인코딩하여 wParam/lParam에 전달
            User32.PostMessage(_hwndMain, AppMessages.WM_CARET_UPDATED,
                (IntPtr)x, (IntPtr)y);
            _needCaretUpdate = false;  // 리셋 — 다음 이벤트까지 캐럿 갱신 안 함
        }
    }
}

// config.json mtime 변경 감지 (감지 스레드에서 5초마다 호출)
static void CheckConfigFileChange(ref DateTime lastMtime)
{
    if (_configFilePath == null) return;
    try
    {
        DateTime mtime = File.GetLastWriteTimeUtc(_configFilePath);
        if (mtime != lastMtime)
        {
            lastMtime = mtime;
            User32.PostMessage(_hwndMain, AppMessages.WM_CONFIG_CHANGED, 0, 0);
        }
    }
    catch { /* 파일 접근 실패 시 무시 */ }
}
```

#### UIA 스레드 (Background)

```csharp
static void StartUiaThread()
{
    var thread = new Thread(UiaLoop)
    {
        IsBackground = true,
        Name = "KoEnVue-UIA"
    };
    thread.Start();
}

static void UiaLoop()
{
    // COM STA 초기화 (UI Automation은 STA 필수)
    Ole32.CoInitializeEx(IntPtr.Zero, Win32Constants.COINIT_APARTMENTTHREADED);

    // IUIAutomation 인스턴스 생성
    // CoCreateInstance(CLSID_CUIAutomation, IID_IUIAutomation)

    // 큐 기반 요청/응답 대기
    // Phase 07에서 상세 구현

    Ole32.CoUninitialize();
}
```

핵심 규칙:
- `COINIT_APARTMENTTHREADED` (STA) 사용 — UI Automation COM은 STA 필수
- `COINIT_MULTITHREADED` (MTA) 사용 금지
- 매 호출마다 new Thread 생성 금지

#### 스레드 간 통신 3대 규칙

```
규칙 1: 감지 스레드에서 직접 윈도우 조작 금지
  Win32 윈도우 조작(ShowWindow, MoveWindow, UpdateLayeredWindow 등)은
  반드시 메인 스레드에서만 수행.

규칙 2: PostMessage로 커스텀 메시지 전송
  감지 스레드 → 메인: PostMessage(hwndMain, WM_APP+N, wParam, lParam)
  훅 콜백 → 메인: PostMessage(hwndMain, WM_IME_STATE_CHANGED, newState, 0)
  두 채널의 업데이트가 메인 스레드 메시지 큐에서 직렬화됨.

규칙 3: volatile 상태 동기화
  상태 비교용 volatile 필드 사용.
  실제 업데이트는 메인 스레드 핸들러에서.
```

---

### 이벤트 트리거 로직

#### 트리거 조건 (on_event 모드 기준)

```
[트리거 1] 포커스 변경
  조건: 이전 hwndFocus != 현재 hwndFocus
  동작: 인디케이터 표시 (일반 페이드 시퀀스)
  config.EventTriggers.OnFocusChange가 true일 때만 발동
  참고: 감지 스레드는 항상 변경을 감지하여 PostMessage 전송.
        EventTriggers 가드는 메인 스레드 핸들러(HandleFocusChanged)에서 체크.

[트리거 2] IME 상태 변경
  조건: 이전 ImeState != 현재 ImeState
  동작: 인디케이터 표시 + 1.3배 확대 강조 효과
  config.EventTriggers.OnImeChange가 true일 때만 발동
  참고: 감지 스레드는 항상 변경을 감지하여 PostMessage 전송.
        EventTriggers 가드는 메인 스레드 핸들러(HandleImeStateChanged)에서 체크.
```

#### 표시 타이밍 시퀀스

```
기본 시퀀스:
  이벤트 → 페이드인(0→255, 150ms) → 유지(1500ms) → 페이드아웃(255→0, 400ms) → 숨김

IME 상태 변경 시 추가:
  → 1.3배 확대(즉시) → 원래 크기 복귀(300ms, 페이드인과 동시)

유지 중 새 이벤트:
  → 유지 타이머 리셋(1500ms 재시작)
  → 페이드인 생략(이미 표시 중)
  → 새 캐럿 위치로 재계산

핵심 원칙:
  인디케이터는 캐럿을 따라다니지 않는다.
  이벤트 발생 시점의 캐럿 위치에 표시, 그 위치에 고정.
  1.5초 후 자연 소멸.

캐럿 위치 재시도 로직:
  tier-1(GetGUIThreadInfo)이 이벤트 시점에 실패(rcCaret==0,0,0,0)하면
  config.CaretPollIntervalMs(기본 50ms) 간격으로 최대 3회 재시도.
  3회 모두 실패 시 다음 fallback tier로 진행.
```

#### 타이밍 상수 (DefaultConfig.cs에 정의됨)

```
FadeInDurationMs     = 150     페이드인 지속 시간
HoldDurationMs       = 1500    유지 시간
FadeOutDurationMs    = 400     페이드아웃 지속 시간
ScaleFactor          = 1.3     IME 전환 시 확대 배율
ScaleReturnMs        = 300     확대→원래 크기 복귀 시간
PollingIntervalMs    = 80      감지 폴링 간격
```

---

### 종료 처리

#### 정상 종료

```csharp
static void OnProcessExit(object? sender, EventArgs e)
{
    _stopping = true;

    // 1. 폴링 스레드 종료 신호 (IsBackground이므로 자동 종료되지만 명시적 플래그)
    // 2. UnhookWinEvent
    if (_hEventHook != IntPtr.Zero)
        User32.UnhookWinEvent(_hEventHook);

    // 3. 핫키 해제
    UnregisterHotkeys();

    // 4. 오버레이 윈도우 파괴
    if (_hwndOverlay != IntPtr.Zero)
        User32.DestroyWindow(_hwndOverlay);

    // 5. 트레이 아이콘 제거
    Tray.Remove();

    // 6. GDI 리소스 해제
    // SafeHandle Dispose 패턴

    // 7. Mutex 해제
    _mutex?.ReleaseMutex();
    _mutex?.Dispose();
}
```

#### 비정상 종료 대응
- `TerminateProcess`, 전원 차단 시 ProcessExit 미호출.
- 12.2절 시작 시 정리(CleanupPreviousTrayIcon)로 대응.
- `Console.CancelKeyPress` 사용하지 않음 — `OutputType: WinExe`에서 콘솔 없음.

---

### 시스템 메시지 핸들러 (스텁)

Phase 03에서는 핸들러 구조만 잡고, 실제 로직은 해당 Phase에서 구현.

```csharp
static void HandleImeStateChanged(ImeState newState)
{
    _lastImeState = newState;
    // Phase 04: Overlay 색상 변경 + 트리거 발동
    // Phase 05: Tray 아이콘/툴팁 갱신
}

static void HandleFocusChanged(IntPtr newHwndFocus)
{
    // Phase 04: 위치 재계산 + 트리거 발동
}

static void HandleCaretUpdated(int x, int y)
{
    // Phase 04: Overlay 위치 업데이트
}

static void HandleTimer(IntPtr timerId)
{
    // Phase 04: Animation 프레임 처리
}

static void HandlePowerResume()
{
    // Phase 07: IME 재감지, DPI/rcWork 재조회, HFONT 재생성 확인
}

static void HandleDisplayChange()
{
    // Phase 07: 모니터 DPI 재조회, rcWork 재계산, HFONT/DIB 재생성
}

static void HandleSettingChange()
{
    // Phase 07: 작업표시줄 변경 → rcWork 재계산
    // Phase 07: 고대비 모드 감지
}

static void HandleDpiChanged(IntPtr wParam, IntPtr lParam)
{
    // WM_DPICHANGED (0x02E0): PerMonitorV2 앱에서 모니터 간 이동 시
    // wParam: HIWORD=newDpiY, LOWORD=newDpiX
    // lParam: RECT* (새 DPI에 맞는 권장 윈도우 크기/위치)
    // Phase 04: HFONT 재생성 (새 DPI에 맞는 폰트 크기)
    // Phase 04: DIB 재생성 (새 DPI에 맞는 인디케이터 크기)
}
```

---

## 검증 기준

- [ ] 앱이 NativeAOT WinExe로 컴파일 가능
- [ ] 다중 인스턴스 실행 시 두 번째 프로세스가 자동 종료
- [ ] 시작 시 이전 트레이 찌꺼기 NIM_DELETE 실행
- [ ] 설정 로드 순서: %APPDATA% → exe dir → DEFAULT_CONFIG
- [ ] 감지 스레드가 80ms 간격으로 폴링
- [ ] IME 상태 변경 시 WM_IME_STATE_CHANGED PostMessage 전송
- [ ] 포커스 변경 시 WM_FOCUS_CHANGED PostMessage 전송
- [ ] UIA 스레드가 COINIT_APARTMENTTHREADED(STA)로 초기화
- [ ] 감지 스레드에서 config.json mtime 변경 감지 (5초마다, PRD §2.8.3)
- [ ] 캐럿 tier-1 실패 시 CaretPollIntervalMs(50ms) 간격 3회 재시도
- [ ] 감지 스레드에서 윈도우 조작 함수 직접 호출 없음
- [ ] ProcessExit 핸들러에서 정리 순서 준수
- [ ] WndProc에서 모든 커스텀 메시지 + 시스템 메시지 라우팅
- [ ] volatile 키워드로 스레드 간 공유 상태 선언

## 산출물
```
Program.cs    # 앱 진입점 + 메시지 루프 + 3-스레드 관리 + 이벤트 트리거
```
