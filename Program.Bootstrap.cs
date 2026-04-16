using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.UI;
using KoEnVue.Core.Native;
using KoEnVue.Core.Logging;

namespace KoEnVue;

/// <summary>
/// Program 의 부트스트랩/종료 분할.
/// MainImpl 의 보조 단계(다중 인스턴스 가드, 트레이 잔재 청소, 윈도우 클래스/핸들 생성, 핫키 등록·해제·파싱)
/// 와 ProcessExit 정리 시퀀스를 모아둔다.
/// 메시지 루프/이벤트 핸들러/감지 스레드는 <c>Program.cs</c> 본체에 그대로 둔다.
/// </summary>
internal static partial class Program
{
    // ================================================================
    // 부트스트랩 전용 상태
    // ================================================================

    private static Mutex? _mutex;

    // 핫키 ID (P3: 매직 넘버 금지)
    private const int HOTKEY_TOGGLE_VISIBILITY = 1;
    private const int HOTKEY_COUNT = 1;

    // 핫키 파싱 상수 (P3: 매직 스트링 금지)
    private const string ModCtrl = "Ctrl";
    private const string ModAlt = "Alt";
    private const string ModShift = "Shift";
    private const string ModWin = "Win";
    private const string FKeyPrefix = "F";
    private const int FKeyMin = 1;
    private const int FKeyMax = 12;

    // ================================================================
    // 다중 인스턴스 방지
    // ================================================================

    private static bool TryAcquireMutex()
    {
        _mutex = new Mutex(true, DefaultConfig.MutexName, out bool createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance already running, exiting");
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
    }

    // ================================================================
    // 크래시 복구 — 고정 GUID NIM_DELETE
    // ================================================================

    private static unsafe void CleanupPreviousTrayIcon()
    {
        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.uFlags = Win32Constants.NIF_GUID;
        nid.guidItem = DefaultConfig.AppGuid;
        Shell32.Shell_NotifyIconW(Win32Constants.NIM_DELETE, ref nid);
    }

    // ================================================================
    // 윈도우 클래스 등록 + 윈도우 생성
    // ================================================================

    private static unsafe void RegisterWindowClasses()
    {
        // 메인 윈도우 클래스
        Logger.Debug("Registering main window class");
        var mainClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = MainClassName,
        };
        ushort mainAtom = User32.RegisterClassExW(ref mainClass);
        if (mainAtom == 0)
            Logger.Error($"RegisterClassExW failed for main class: error={Marshal.GetLastPInvokeError()}");
        else
            Logger.Debug($"Main window class registered: atom={mainAtom}");

        // 오버레이 윈도우 클래스
        Logger.Debug($"Registering overlay window class: {_config.Advanced.OverlayClassName}");
        var overlayClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = _config.Advanced.OverlayClassName,
        };
        ushort overlayAtom = User32.RegisterClassExW(ref overlayClass);
        if (overlayAtom == 0)
            Logger.Error($"RegisterClassExW failed for overlay class: error={Marshal.GetLastPInvokeError()}");
        else
            Logger.Debug($"Overlay window class registered: atom={overlayAtom}");
    }

    private static IntPtr CreateMainWindow()
    {
        IntPtr hwnd = User32.CreateWindowExW(
            0, MainClassName, "KoEnVue",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            Logger.Error("Failed to create main window");

        return hwnd;
    }

    private static IntPtr CreateOverlayWindow()
    {
        IntPtr hwnd = User32.CreateWindowExW(
            Win32Constants.WS_EX_LAYERED
                | Win32Constants.WS_EX_TOPMOST | Win32Constants.WS_EX_TOOLWINDOW
                | Win32Constants.WS_EX_NOACTIVATE,
            _config.Advanced.OverlayClassName, "",
            Win32Constants.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            Logger.Error("Failed to create overlay window");

        return hwnd;
    }

    // ================================================================
    // 핫키 등록/해제
    // ================================================================

    private static void RegisterHotkeys()
    {
        RegisterSingleHotkey(HOTKEY_TOGGLE_VISIBILITY, _config.HotkeyToggleVisibility);
    }

    private static void RegisterSingleHotkey(int id, string hotkeyString)
    {
        if (!ParseHotkey(hotkeyString, out uint modifiers, out uint vk))
        {
            Logger.Warning($"Invalid hotkey format: {hotkeyString}");
            return;
        }

        if (!User32.RegisterHotKey(_hwndMain, id, modifiers | Win32Constants.MOD_NOREPEAT, vk))
            Logger.Warning($"Failed to register hotkey: {hotkeyString}");
    }

    private static void UnregisterHotkeys()
    {
        for (int id = 1; id <= HOTKEY_COUNT; id++)
            User32.UnregisterHotKey(_hwndMain, id);
    }

    private static bool ParseHotkey(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrEmpty(hotkey)) return false;

        string[] parts = hotkey.Split('+');
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string mod = parts[i].Trim();
            if (mod.Equals(ModCtrl, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_CONTROL;
            else if (mod.Equals(ModAlt, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_ALT;
            else if (mod.Equals(ModShift, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_SHIFT;
            else if (mod.Equals(ModWin, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_WIN;
            else
                return false;
        }

        string keyStr = parts[^1].Trim();
        if (keyStr.Length == 1)
        {
            char c = char.ToUpperInvariant(keyStr[0]);
            if (c is >= 'A' and <= 'Z')
                vk = (uint)c;          // A-Z → 0x41-0x5A
            else if (c is >= '0' and <= '9')
                vk = (uint)c;          // 0-9 → 0x30-0x39
            else
                return false;
        }
        else if (keyStr.StartsWith(FKeyPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(keyStr.AsSpan(1), out int fNum)
            && fNum >= FKeyMin && fNum <= FKeyMax)
        {
            vk = (uint)(Win32Constants.VK_F1 + fNum - 1);
        }
        else
        {
            return false;
        }

        return vk != 0;
    }

    // ================================================================
    // 종료 처리
    // ================================================================

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _stopping = true;

        // 1. IME 훅 해제
        ImeStatus.UnregisterHook();

        // 2. 핫키 해제
        UnregisterHotkeys();

        // 3. CAPS LOCK 폴링 타이머 명시적 해제
        if (_hwndMain != IntPtr.Zero)
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CAPS);

        // 4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();

        // 5. 오버레이 + 메인 윈도우 파괴
        if (_hwndOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndOverlay);
        if (_hwndMain != IntPtr.Zero)
            User32.DestroyWindow(_hwndMain);

        // 6. 트레이 아이콘 제거
        Tray.Remove();

        // 7. Mutex 해제 (Dispose만 — 프로세스 종료 시 OS가 자동 해제.
        //    ReleaseMutex는 소유 스레드에서만 호출 가능하나 ProcessExit는 다른 스레드일 수 있음)
        _mutex?.Dispose();

        // 8. COM 해제
        Ole32.CoUninitialize();

        // 9. 로거 종료 (Shutdown 전에 최종 로그 기록)
        Logger.Info("KoEnVue stopped");
        Logger.Shutdown();
    }
}
