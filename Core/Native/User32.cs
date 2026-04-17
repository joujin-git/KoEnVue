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

    // === 좌표 변환 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight,
        [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

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

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial IntPtr SendMessageTimeoutTitleBarInfo(
        IntPtr hWnd, uint Msg, IntPtr wParam, ref TITLEBARINFOEX lParam,
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

    // === 핫키 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // === 윈도우 배치/시스템 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(
        uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int nIndex);

    // === 콜백 대리자 ===

    internal delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // WndProc 대리자 (NativeAOT에서는 [UnmanagedCallersOnly] + 함수 포인터 방식 권장)
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect,
        [MarshalAs(UnmanagedType.Bool)] bool bErase);

    // ScrollWindowEx: dx/dy 픽셀만큼 클라이언트를 BitBlt 복사 후 자식까지 자동 이동(SW_SCROLLCHILDREN)
    // 하고 노출 영역만 무효화(SW_INVALIDATE|SW_ERASE) 한다. 다이얼로그 스크롤에서 수십~수백개의
    // 자식 SetWindowPos 루프 + 전체 InvalidateRect 를 대체하기 위해 사용.
    [LibraryImport("user32.dll")]
    internal static partial int ScrollWindowEx(IntPtr hWnd, int dx, int dy,
        IntPtr prcScroll, IntPtr prcClip,
        IntPtr hrgnUpdate, IntPtr prcUpdate, uint flags);
}
