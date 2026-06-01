using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class User32
{
    // === 포그라운드/포커스 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);
    // char[] 배열로 선언하여 unsafe 의존 제거. 호출 시: var buf = new char[256]; GetClassNameW(hwnd, buf, 256);

    // WindowFromPoint — 지정 좌표의 최상위 visible 윈도우 핸들. WS_EX_TRANSPARENT 윈도우(커서 인디)는
    // 통과해 그 아래 윈도우를 반환하므로 자기 감지 없음 (cursor 인디의 셸 UI 호버 판정에 사용).
    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(POINT pt);

    // GetAncestor(GA_ROOT) — 자식 윈도우에서 최상위 루트로 상승 (작업 표시줄 자식 버튼 → Shell_TrayWnd).
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    // === 윈도우 상태 조회 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    // EnumWindowsProc: BOOL CALLBACK (HWND, LPARAM). 4바이트 BOOL이므로 int 리턴.
    // NativeAOT 권장 패턴: delegate* unmanaged + [UnmanagedCallersOnly] 콜백.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumWindows(
        delegate* unmanaged<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumChildWindows(
        IntPtr hWndParent, delegate* unmanaged<IntPtr, IntPtr, int> lpEnumFunc, IntPtr lParam);

    // === 모니터 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // === 마우스 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    // LoadCursorW: hInstance=NULL + lpCursorName=MAKEINTRESOURCE(IDC_*) 로 시스템 표준 커서 핸들 획득.
    // lpCursorName 은 LPCWSTR 시그니처지만 IDC_* 는 정수 리소스 ID 라 IntPtr 로 받아 마샬링 우회.
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    // === 윈도우 클래스/생성/파괴 ===

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    // === 메시지 루프 ===

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    // 반환: >0 메시지 있음, 0 WM_QUIT, -1 에러. bool이 아닌 int로 선언해야 함.

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int nExitCode);

    // === 타이머 ===

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    // === 이벤트 훅 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    // === 메시지 전송 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // === 포커스/메뉴 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(
        IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CheckMenuRadioItem(
        IntPtr hMenu, uint first, uint last, uint check, uint flags);

    // SetMenuDefaultItem: 메뉴의 default 항목을 명시 지정. default 항목은 시스템이 굵게 렌더링.
    // AppendMenu(MF_DEFAULT) 가 내부 default 비트는 세팅하지만 실제 볼드 렌더링은 본 함수의 명시
    // 호출이 있어야 보장됨 (MSDN: ModifyMenu 페이지 "use SetMenuDefaultItem instead",
    // SetMenuDefaultItem 페이지 "displayed in bold type"). fByPos = 0 → uItem 을 command ID 로,
    // 0 이외 → position 으로 해석. 메뉴당 1개만 default 가능.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr hMenu);

    // === 입력 상태 ===

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    internal static partial short GetKeyState(int vKey);

    // === 키보드 레이아웃 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetKeyboardLayout(uint idThread);

    // === 윈도우 배치/시스템 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int nIndex);

    // === 접근성 / 시스템 파라미터 ===

    /// <summary>
    /// SystemParametersInfoW 의 SPI_GETHIGHCONTRAST 전용 시그니처 — pvParam 이 HIGHCONTRAST 구조체.
    /// <para>
    /// PVOID 인자라 동일 함수가 uiAction 별로 다른 구조체를 받는데, [LibraryImport] 는 generic
    /// PVOID 마샬링을 직접 지원하지 않으므로 uiAction 별 EntryPoint=SystemParametersInfoW 오버로드를
    /// 둔다. 추가 SPI_*을 다룰 때 같은 패턴으로 별개 partial 메서드를 선언한다.
    /// </para>
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoHighContrast(
        uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    /// <summary>
    /// 접근성 → 고대비 모드 활성 여부. SPI_GETHIGHCONTRAST (0x0042) 를 통해 HCF_HIGHCONTRASTON
    /// 비트를 읽는다. ThemePresets.ApplySystemTheme 에서 contrast-safe 팔레트 분기 게이트로 사용.
    /// 호출 실패 시 false 로 폴백 — 정상 동작에 영향 없음.
    /// </summary>
    internal static bool IsHighContrastEnabled()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        if (!SystemParametersInfoHighContrast(Win32Constants.SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0))
            return false;
        return (hc.dwFlags & Win32Constants.HCF_HIGHCONTRASTON) != 0;
    }

    // === 콜백 대리자 ===

    internal delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // === 윈도우 관계 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    // === 윈도우 탐색 / 메시지 등록 ===

    // FindWindowW: 클래스명 + 타이틀(선택)로 최상위 윈도우 핸들 조회.
    // 다중 인스턴스 중복 실행 시 기존 인스턴스에 활성화 신호를 보내기 위해 사용.
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    // RegisterWindowMessageW: 시스템 전역 유일 메시지 ID를 등록.
    // Explorer 재시작 시 셸이 브로드캐스트하는 "TaskbarCreated" 메시지 수신에 사용.
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string lpString);

    // ChangeWindowMessageFilterEx: UIPI(User Interface Privilege Isolation) 메시지 필터 수정.
    // requireAdministrator 로 High IL 에서 실행되는 앱은 Medium IL 인 explorer 의
    // TaskbarCreated 브로드캐스트를 UIPI 기본 정책으로 차단당해 수신 못 함.
    // 본 API 로 특정 메시지를 화이트리스트에 올려 낮은 IL → 높은 IL 전달을 허용한다.
    // pChangeFilterStruct 은 optional (IntPtr.Zero 로 전달).
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message,
        uint action, IntPtr pChangeFilterStruct);

    // === 윈도우 텍스트 ===

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextLengthW(IntPtr hWnd);

    // === 메시지 박스 ===

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    // === 윈도우 활성화/메시지 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsDialogMessageW(IntPtr hDlg, ref MSG lpMsg);

    // === 아이콘 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    // DrawTextW는 user32.dll 소속 (gdi32.dll 아님!)
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    // === 스크롤바 ===

    [LibraryImport("user32.dll")]
    internal static partial int SetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi);

    // ScrollWindowEx: dx/dy 픽셀만큼 클라이언트를 BitBlt 복사 후 자식까지 자동 이동(SW_SCROLLCHILDREN)
    // 하고 노출 영역만 무효화(SW_INVALIDATE|SW_ERASE) 한다. 다이얼로그 스크롤에서 수십~수백개의
    // 자식 SetWindowPos 루프 + 전체 InvalidateRect 를 대체하기 위해 사용.
    [LibraryImport("user32.dll")]
    internal static partial int ScrollWindowEx(IntPtr hWnd, int dx, int dy,
        IntPtr prcScroll, IntPtr prcClip,
        IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);
}
