using System.Diagnostics;
using KoEnVue.Config;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.UI;

/// <summary>
/// Shell_NotifyIconW 기반 시스템 트레이 아이콘 관리 + 팝업 메뉴 + 시작등록 + 설정파일 열기.
/// WinForms NotifyIcon 사용 금지 (P1).
/// </summary>
internal static class Tray
{
    // ================================================================
    // 메뉴 항목 ID (P3: 매직 넘버 금지)
    // ================================================================

    // 서브메뉴: 인디케이터 스타일
    private const int IDM_STYLE_DOT       = 1001;
    private const int IDM_STYLE_SQUARE    = 1002;
    private const int IDM_STYLE_UNDERLINE = 1003;
    private const int IDM_STYLE_VBAR      = 1004;
    private const int IDM_STYLE_LABEL     = 1005;

    // 서브메뉴: 표시 모드
    private const int IDM_DISPLAY_EVENT   = 2001;
    private const int IDM_DISPLAY_ALWAYS  = 2002;

    // 서브메뉴: 투명도
    private const int IDM_OPACITY_HIGH    = 3001;
    private const int IDM_OPACITY_NORMAL  = 3002;
    private const int IDM_OPACITY_LOW     = 3003;

    // 메인 메뉴
    private const int IDM_STARTUP         = 4001;
    private const int IDM_OPEN_SETTINGS   = 4002;
    private const int IDM_EXIT            = 4003;

    // schtasks 작업 이름
    private const string TaskName = "KoEnVue";

    // P3: 매직 넘버 금지
    private const double OpacityTolerance = 0.001;
    private const int TooltipMaxLength = 127; // szTip[128], null 종료 고려
    private const int SchtasksQueryTimeoutMs = 3000;
    private const int SchtasksCommandTimeoutMs = 5000;

    // ================================================================
    // 내부 상태
    // ================================================================

    private static bool _initialized;
    private static IntPtr _hwndMain;
    private static SafeIconHandle? _currentIcon;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 트레이 아이콘 등록 (NIM_ADD + NIM_SETVERSION).
    /// config.TrayEnabled == false 이면 건너뛴다.
    /// </summary>
    internal static unsafe void Initialize(IntPtr hwndMain, ImeState initialState, AppConfig config)
    {
        _hwndMain = hwndMain;

        if (!config.TrayEnabled)
        {
            _initialized = false;
            return;
        }

        _currentIcon = TrayIcon.CreateIcon(initialState, config);

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = hwndMain;
        nid.uFlags = Win32Constants.NIF_MESSAGE | Win32Constants.NIF_ICON
                   | Win32Constants.NIF_TIP | Win32Constants.NIF_GUID;
        nid.uCallbackMessage = AppMessages.WM_TRAY_CALLBACK;
        nid.hIcon = _currentIcon.DangerousGetHandle();
        nid.guidItem = DefaultConfig.AppGuid;

        // szTip 복사 (unsafe fixed char 버퍼)
        CopyTooltip(ref nid, initialState, config);

        Shell32.Shell_NotifyIconW(Win32Constants.NIM_ADD, ref nid);

        // NOTIFYICON_VERSION_4 활성화
        nid.uVersion = Win32Constants.NOTIFYICON_VERSION_4;
        Shell32.Shell_NotifyIconW(Win32Constants.NIM_SETVERSION, ref nid);

        _initialized = true;
        Logger.Info("Tray icon initialized");
    }

    /// <summary>
    /// IME 상태 변경 시 아이콘 + 툴팁 갱신 (NIM_MODIFY).
    /// </summary>
    internal static unsafe void UpdateState(ImeState state, AppConfig config)
    {
        if (!_initialized) return;

        var newIcon = TrayIcon.CreateIcon(state, config);

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwndMain;
        nid.uFlags = Win32Constants.NIF_ICON | Win32Constants.NIF_TIP | Win32Constants.NIF_GUID;
        nid.hIcon = newIcon.DangerousGetHandle();
        nid.guidItem = DefaultConfig.AppGuid;

        CopyTooltip(ref nid, state, config);

        Shell32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref nid);

        // 이전 아이콘 해제 후 교체
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
    }

    /// <summary>
    /// 트레이 아이콘 제거 (NIM_DELETE). 앱 종료 시 호출.
    /// </summary>
    internal static unsafe void Remove()
    {
        if (!_initialized) return;

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.uFlags = Win32Constants.NIF_GUID;
        nid.guidItem = DefaultConfig.AppGuid;
        Shell32.Shell_NotifyIconW(Win32Constants.NIM_DELETE, ref nid);

        _currentIcon?.Dispose();
        _currentIcon = null;
        _initialized = false;

        Logger.Info("Tray icon removed");
    }

    /// <summary>
    /// 트레이 우클릭 팝업 메뉴 표시.
    /// </summary>
    internal static void ShowMenu(IntPtr hwndMain, AppConfig config)
    {
        if (!_initialized) return;

        // --- 서브메뉴 1: 인디케이터 스타일 ---
        IntPtr hStyleMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hStyleMenu, Win32Constants.MF_STRING, (nuint)IDM_STYLE_DOT, I18n.StyleDot);
        User32.AppendMenuW(hStyleMenu, Win32Constants.MF_STRING, (nuint)IDM_STYLE_SQUARE, I18n.StyleSquare);
        User32.AppendMenuW(hStyleMenu, Win32Constants.MF_STRING, (nuint)IDM_STYLE_UNDERLINE, I18n.StyleUnderline);
        User32.AppendMenuW(hStyleMenu, Win32Constants.MF_STRING, (nuint)IDM_STYLE_VBAR, I18n.StyleVbar);
        User32.AppendMenuW(hStyleMenu, Win32Constants.MF_STRING, (nuint)IDM_STYLE_LABEL, I18n.StyleLabel);
        uint currentStyleId = config.IndicatorStyle switch
        {
            IndicatorStyle.CaretDot => (uint)IDM_STYLE_DOT,
            IndicatorStyle.CaretSquare => (uint)IDM_STYLE_SQUARE,
            IndicatorStyle.CaretUnderline => (uint)IDM_STYLE_UNDERLINE,
            IndicatorStyle.CaretVbar => (uint)IDM_STYLE_VBAR,
            IndicatorStyle.Label => (uint)IDM_STYLE_LABEL,
            _ => (uint)IDM_STYLE_DOT,
        };
        User32.CheckMenuRadioItem(hStyleMenu, (uint)IDM_STYLE_DOT, (uint)IDM_STYLE_LABEL,
            currentStyleId, Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴 2: 표시 모드 ---
        IntPtr hDisplayMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDisplayMenu, Win32Constants.MF_STRING, (nuint)IDM_DISPLAY_EVENT, I18n.DisplayEvent);
        User32.AppendMenuW(hDisplayMenu, Win32Constants.MF_STRING, (nuint)IDM_DISPLAY_ALWAYS, I18n.DisplayAlways);
        uint currentDisplayId = config.DisplayMode == DisplayMode.OnEvent
            ? (uint)IDM_DISPLAY_EVENT : (uint)IDM_DISPLAY_ALWAYS;
        User32.CheckMenuRadioItem(hDisplayMenu, (uint)IDM_DISPLAY_EVENT, (uint)IDM_DISPLAY_ALWAYS,
            currentDisplayId, Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴 3: 투명도 ---
        IntPtr hOpacityMenu = User32.CreatePopupMenu();
        double[] presets = config.TrayQuickOpacityPresets;
        if (presets.Length >= 3)
        {
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_HIGH,
                $"{I18n.OpacityHigh} {presets[0]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_NORMAL,
                $"{I18n.OpacityNormal} {presets[1]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_LOW,
                $"{I18n.OpacityLow} {presets[2]}");

            // 현재 opacity와 매칭되는 프리셋에 라디오 체크
            uint opacityCheckId = 0;
            if (Math.Abs(config.Opacity - presets[0]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_HIGH;
            else if (Math.Abs(config.Opacity - presets[1]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_NORMAL;
            else if (Math.Abs(config.Opacity - presets[2]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_LOW;

            if (opacityCheckId != 0)
                User32.CheckMenuRadioItem(hOpacityMenu, (uint)IDM_OPACITY_HIGH, (uint)IDM_OPACITY_LOW,
                    opacityCheckId, Win32Constants.MF_BYCOMMAND);
        }

        // --- 메인 메뉴 ---
        IntPtr hMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hStyleMenu, I18n.MenuIndicatorStyle);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hDisplayMenu, I18n.MenuDisplayMode);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hOpacityMenu, I18n.MenuOpacity);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        bool isStartup = IsStartupRegistered();
        User32.AppendMenuW(hMenu, isStartup ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED,
            (nuint)IDM_STARTUP, I18n.MenuStartup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_OPEN_SETTINGS, I18n.MenuOpenSettings);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_EXIT, I18n.MenuExit);

        // --- 표시 (워크어라운드 적용) ---
        User32.GetCursorPos(out POINT pt);
        User32.SetForegroundWindow(hwndMain);
        User32.TrackPopupMenu(hMenu, Win32Constants.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, hwndMain, IntPtr.Zero);
        User32.PostMessage(hwndMain, Win32Constants.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        // --- 정리 (DestroyMenu은 서브메뉴도 자동 파괴) ---
        User32.DestroyMenu(hMenu);
    }

    /// <summary>
    /// WM_COMMAND 메뉴 명령 처리.
    /// config 변경이 필요한 항목은 updateConfig 콜백으로 Program.cs에 위임.
    /// </summary>
    internal static void HandleMenuCommand(int commandId, AppConfig config, IntPtr hwndMain,
        Action<AppConfig> updateConfig)
    {
        switch (commandId)
        {
            // --- 인디케이터 스타일 ---
            case IDM_STYLE_DOT:
                updateConfig(config with { IndicatorStyle = IndicatorStyle.CaretDot });
                break;
            case IDM_STYLE_SQUARE:
                updateConfig(config with { IndicatorStyle = IndicatorStyle.CaretSquare });
                break;
            case IDM_STYLE_UNDERLINE:
                updateConfig(config with { IndicatorStyle = IndicatorStyle.CaretUnderline });
                break;
            case IDM_STYLE_VBAR:
                updateConfig(config with { IndicatorStyle = IndicatorStyle.CaretVbar });
                break;
            case IDM_STYLE_LABEL:
                updateConfig(config with { IndicatorStyle = IndicatorStyle.Label });
                break;

            // --- 표시 모드 ---
            case IDM_DISPLAY_EVENT:
                updateConfig(config with { DisplayMode = DisplayMode.OnEvent });
                break;
            case IDM_DISPLAY_ALWAYS:
                updateConfig(config with { DisplayMode = DisplayMode.Always });
                break;

            // --- 투명도 ---
            case IDM_OPACITY_HIGH:
                if (config.TrayQuickOpacityPresets.Length >= 1)
                    updateConfig(config with { Opacity = config.TrayQuickOpacityPresets[0] });
                break;
            case IDM_OPACITY_NORMAL:
                if (config.TrayQuickOpacityPresets.Length >= 2)
                    updateConfig(config with { Opacity = config.TrayQuickOpacityPresets[1] });
                break;
            case IDM_OPACITY_LOW:
                if (config.TrayQuickOpacityPresets.Length >= 3)
                    updateConfig(config with { Opacity = config.TrayQuickOpacityPresets[2] });
                break;

            // --- 시작 프로그램 등록 ---
            case IDM_STARTUP:
                ToggleStartupRegistration();
                break;

            // --- 설정 파일 열기 ---
            case IDM_OPEN_SETTINGS:
                Settings.OpenSettingsFile();
                break;

            // --- 종료 ---
            case IDM_EXIT:
                User32.PostQuitMessage(0);
                break;
        }
    }

    // ================================================================
    // Private — 툴팁
    // ================================================================

    /// <summary>
    /// szTip fixed char 버퍼에 툴팁 텍스트를 복사한다.
    /// </summary>
    private static unsafe void CopyTooltip(ref NOTIFYICONDATAW nid, ImeState state, AppConfig config)
    {
        if (!config.TrayTooltip) return; // 빈 문자열 = 툴팁 숨김

        string text = I18n.GetTrayTooltip(state);
        ReadOnlySpan<char> tip = text.AsSpan();
        int len = Math.Min(tip.Length, TooltipMaxLength);
        fixed (char* pTip = nid.szTip)
        {
            tip[..len].CopyTo(new Span<char>(pTip, TooltipMaxLength + 1));
        }
    }

    // ================================================================
    // Private — 시작 프로그램 등록 (schtasks)
    // ================================================================

    private static bool IsStartupRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(SchtasksQueryTimeoutMs);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ToggleStartupRegistration()
    {
        try
        {
            if (IsStartupRegistered())
            {
                // 삭제
                RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                Logger.Info("Startup registration removed");
            }
            else
            {
                // 등록
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\"\" /sc ONLOGON /rl HIGHEST /f");
                Logger.Info("Startup registration created");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to toggle startup registration: {ex.Message}");
        }
    }

    private static void RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(SchtasksCommandTimeoutMs);
    }

}
