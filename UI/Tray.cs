using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // 서브메뉴: 투명도
    private const int IDM_OPACITY_HIGH    = 3001;
    private const int IDM_OPACITY_NORMAL  = 3002;
    private const int IDM_OPACITY_LOW     = 3003;

    // 서브메뉴: 기본 위치
    private const int IDM_DEFAULT_POS_SET_CURRENT = 3101;
    private const int IDM_DEFAULT_POS_RESET       = 3102;

    // 메인 메뉴
    private const int IDM_STARTUP         = 4001;
    private const int IDM_CLEANUP         = 4003;
    private const int IDM_EXIT            = 4002;

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
            Logger.Debug("Tray disabled by config");
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

        // --- 서브메뉴: 투명도 ---
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

        // --- 서브메뉴: 기본 위치 ---
        IntPtr hDefaultPosMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDefaultPosMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DEFAULT_POS_SET_CURRENT, I18n.MenuDefaultPosSetCurrent);
        uint resetFlags = Win32Constants.MF_STRING
            | (config.DefaultIndicatorPosition is null ? Win32Constants.MF_GRAYED : 0);
        User32.AppendMenuW(hDefaultPosMenu, resetFlags,
            (nuint)IDM_DEFAULT_POS_RESET, I18n.MenuDefaultPosReset);

        // --- 메인 메뉴 ---
        IntPtr hMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hOpacityMenu, I18n.MenuOpacity);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        bool isStartup = IsStartupRegistered();
        User32.AppendMenuW(hMenu, isStartup ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED,
            (nuint)IDM_STARTUP, I18n.MenuStartup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDefaultPosMenu, I18n.MenuDefaultPosition);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_CLEANUP, I18n.MenuCleanup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_EXIT, I18n.MenuExit);

        // --- 표시 (워크어라운드 적용) ---
        User32.GetCursorPos(out POINT pt);
        User32.SetForegroundWindow(hwndMain);
        User32.TrackPopupMenu(hMenu, Win32Constants.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, hwndMain, IntPtr.Zero);
        User32.PostMessageW(hwndMain, Win32Constants.WM_NULL, IntPtr.Zero, IntPtr.Zero);

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

            // --- 기본 위치: 현재 위치로 설정 ---
            case IDM_DEFAULT_POS_SET_CURRENT:
                SetDefaultPositionToCurrent(config, updateConfig);
                break;

            // --- 기본 위치: 초기화 ---
            case IDM_DEFAULT_POS_RESET:
                updateConfig(config with { DefaultIndicatorPosition = null });
                Logger.Info("Default indicator position reset to hardcoded fallback");
                break;

            // --- 미사용 위치 데이터 정리 ---
            case IDM_CLEANUP:
                CleanupUnusedPositions(config, updateConfig);
                break;

            // --- 종료 ---
            case IDM_EXIT:
                User32.PostQuitMessage(0);
                break;
        }
    }

    // ================================================================
    // Private — 기본 위치 설정
    // ================================================================

    /// <summary>
    /// 현재 인디케이터 위치를 가장 가까운 work area 모서리 기준으로 환산하여
    /// config.DefaultIndicatorPosition에 저장한다. 인디가 한 번도 표시된 적이 없으면 경고.
    /// </summary>
    private static void SetDefaultPositionToCurrent(AppConfig config, Action<AppConfig> updateConfig)
    {
        DefaultPositionConfig? anchor = Overlay.ComputeAnchorFromCurrentPosition();
        if (anchor is null)
        {
            User32.MessageBoxW(_hwndMain,
                I18n.IsKorean
                    ? "인디케이터 위치를 확인할 수 없습니다. 잠시 후 다시 시도하세요."
                    : "Cannot determine current indicator position. Please try again shortly.",
                "KoEnVue", 0);
            return;
        }

        updateConfig(config with { DefaultIndicatorPosition = anchor });
        Logger.Info($"Default indicator position saved: corner={anchor.Corner}, "
                  + $"delta=({anchor.DeltaX}, {anchor.DeltaY})");
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

        string modePrefix = Settings.IsPortableMode ? I18n.PortableLabel : I18n.InstalledLabel;
        string text = $"KoEnVue {modePrefix} - {I18n.GetTrayTooltip(state)}";
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

    // ================================================================
    // Private — 미사용 위치 데이터 정리
    // ================================================================

    /// <summary>
    /// 현재 실행 중이 아닌 프로세스의 indicator_positions 항목을 체크박스 다이얼로그로 선택 삭제한다.
    /// </summary>
    private static void CleanupUnusedPositions(AppConfig config, Action<AppConfig> updateConfig)
    {
        if (config.IndicatorPositions.Count == 0) return;

        // 실행 중인 프로세스 이름 수집
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try { running.Add(proc.ProcessName); }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { return; }

        // 실행 중이 아닌 항목 찾기
        var unused = new List<string>();
        foreach (string name in config.IndicatorPositions.Keys)
        {
            if (!running.Contains(name))
                unused.Add(name);
        }

        if (unused.Count == 0)
        {
            User32.MessageBoxW(_hwndMain,
                I18n.IsKorean ? "정리할 항목이 없습니다." : "Nothing to clean up.",
                "KoEnVue", 0);
            return;
        }

        // 체크박스 다이얼로그 표시
        List<string>? selected = ShowCleanupDialog(unused);
        if (selected is null || selected.Count == 0) return;

        // 삭제
        var cleaned = new Dictionary<string, int[]>(config.IndicatorPositions);
        foreach (string name in selected)
            cleaned.Remove(name);

        updateConfig(config with { IndicatorPositions = cleaned });
        Logger.Info($"Cleaned {selected.Count} position(s): {string.Join(", ", selected)}");
    }

    // ================================================================
    // Private — 정리 다이얼로그 (체크박스 선택)
    // ================================================================

    // 다이얼로그 레이아웃 상수 (96 DPI 기준, DPI 스케일 적용)
    private const int DlgPadding = 16;
    private const int DlgCheckHeight = 22;
    private const int DlgCheckGap = 4;
    private const int DlgButtonWidth = 90;
    private const int DlgButtonHeight = 30;
    private const int DlgMinWidth = 340;
    private const int DlgMaxVisibleItems = 15;
    private const int DlgDescHeight = 20;
    private const int DlgSepGap = 12;
    private const int DlgItemIndent = 20;

    // 다이얼로그 컨트롤 ID
    private const int IDC_CHECK_BASE = 5000;
    private const int IDC_SELECT_ALL = 5500;
    private const int IDC_BTN_OK = 5501;
    private const int IDC_BTN_CANCEL = 5502;

    // 다이얼로그 상태 (모달 루프용)
    private static bool _dlgResult;
    private static bool _dlgClosed;
    private static IntPtr _hwndDialog;
    private static readonly List<IntPtr> _checkboxHandles = [];
    private static bool _selectAllState = true;

    /// <summary>
    /// 체크박스 선택 다이얼로그를 표시하고 선택된 항목을 반환한다. 취소 시 null.
    /// </summary>
    private static unsafe List<string>? ShowCleanupDialog(List<string> items)
    {
        _dlgResult = false;
        _dlgClosed = false;
        _checkboxHandles.Clear();
        _selectAllState = true;

        // DPI 스케일링
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);

        int pad = DpiHelper.Scale(DlgPadding, dpiScale);
        int checkH = DpiHelper.Scale(DlgCheckHeight, dpiScale);
        int checkGap = DpiHelper.Scale(DlgCheckGap, dpiScale);
        int btnW = DpiHelper.Scale(DlgButtonWidth, dpiScale);
        int btnH = DpiHelper.Scale(DlgButtonHeight, dpiScale);
        int descH = DpiHelper.Scale(DlgDescHeight, dpiScale);
        int sepGap = DpiHelper.Scale(DlgSepGap, dpiScale);
        int itemIndent = DpiHelper.Scale(DlgItemIndent, dpiScale);

        int visibleCount = Math.Min(items.Count, DlgMaxVisibleItems);
        int checkAreaHeight = visibleCount * (checkH + checkGap);
        int dlgWidth = DpiHelper.Scale(DlgMinWidth, dpiScale);
        // 비클라이언트 영역 높이 (타이틀바 + 프레임)
        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = User32.GetSystemMetricsForDpi(Win32Constants.SM_CYCAPTION, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CYFIXEDFRAME, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, rawDpi);

        int dlgHeight = nonClientH               // non-client (title bar + borders)
            + pad                                // top padding
            + descH + checkGap                   // description label
            + checkH + checkGap                  // "전체 선택"
            + sepGap                             // separator
            + checkAreaHeight                    // items
            + pad                                // gap before buttons
            + btnH                               // buttons
            + pad;                               // bottom padding

        // UI 폰트 (맑은 고딕 9pt, DPI 스케일)
        int fontHeight = -(int)Math.Round(9.0 * dpiY / 72.0);
        IntPtr hFont = Gdi32.CreateFontW(fontHeight, 0, 0, 0, Win32Constants.FW_NORMAL,
            0, 0, 0, Win32Constants.DEFAULT_CHARSET,
            Win32Constants.OUT_TT_PRECIS, Win32Constants.CLIP_DEFAULT_PRECIS,
            Win32Constants.CLEARTYPE_QUALITY, Win32Constants.DEFAULT_PITCH,
            "맑은 고딕");

        // 다이얼로그 윈도우 클래스 등록 (한 번만)
        string dlgClassName = "KoEnVueCleanupDlg";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CleanupDlgProc,
            lpszClassName = dlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref wc); // 중복 등록은 무시됨

        // 화면 중앙 좌표
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMon, ref mi);
        int cx = (mi.rcWork.Left + mi.rcWork.Right - dlgWidth) / 2;
        int cy = (mi.rcWork.Top + mi.rcWork.Bottom - dlgHeight) / 2;

        string title = I18n.IsKorean ? "미사용 위치 데이터 정리" : "Clean unused position data";
        _hwndDialog = User32.CreateWindowExW(0, dlgClassName, title,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            _hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndDialog == IntPtr.Zero)
        {
            if (hFont != IntPtr.Zero) Gdi32.DeleteObject(hFont);
            return null;
        }

        // 클라이언트 영역 너비 (비클라이언트 보더 제외)
        int borderW = DpiHelper.Scale(16, dpiScale);
        int contentW = dlgWidth - pad * 2 - borderW;

        // 컨트롤 생성
        int y = pad;

        // 설명 라벨
        string descText = I18n.IsKorean
            ? "삭제할 위치 데이터를 선택하세요."
            : "Select position data to delete.";
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", descText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, descH,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndDesc, hFont);
        y += descH + checkGap;

        // "전체 선택" 체크박스
        string selectAllText = I18n.IsKorean ? "전체 선택" : "Select All";
        IntPtr hwndSelectAll = User32.CreateWindowExW(0, "BUTTON", selectAllText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.BS_AUTOCHECKBOX,
            pad, y, contentW, checkH,
            _hwndDialog, (IntPtr)IDC_SELECT_ALL, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndSelectAll, hFont);
        User32.SendMessageW(hwndSelectAll, Win32Constants.BM_SETCHECK,
            (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
        y += checkH + checkGap;

        // 구분선 (etched horizontal)
        User32.CreateWindowExW(0, "STATIC", "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.SS_ETCHEDHORZ,
            pad, y + sepGap / 2 - 1, contentW, 2,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        y += sepGap;

        // 항목 체크박스
        for (int i = 0; i < items.Count; i++)
        {
            IntPtr hwndCheck = User32.CreateWindowExW(0, "BUTTON", items[i],
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.BS_AUTOCHECKBOX,
                pad + itemIndent, y, contentW - itemIndent, checkH,
                _hwndDialog, (IntPtr)(IDC_CHECK_BASE + i), IntPtr.Zero, IntPtr.Zero);
            ApplyFont(hwndCheck, hFont);
            User32.SendMessageW(hwndCheck, Win32Constants.BM_SETCHECK,
                (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
            _checkboxHandles.Add(hwndCheck);
            y += checkH + checkGap;
        }

        // 버튼 영역
        y += pad;
        int btnAreaWidth = btnW * 2 + pad;
        int btnX = (dlgWidth - btnAreaWidth) / 2;

        string okText = I18n.IsKorean ? "삭제" : "Delete";
        string cancelText = I18n.IsKorean ? "취소" : "Cancel";
        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", okText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX, y, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndOk, hFont);
        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", cancelText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + pad, y, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_CANCEL, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndCancel, hFont);

        // 모달 표시
        User32.EnableWindow(_hwndMain, false);
        User32.ShowWindow(_hwndDialog, Win32Constants.SW_SHOW);

        // 모달 메시지 루프
        while (!_dlgClosed)
        {
            int ret = User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break;
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }

        // 결과 수집
        List<string>? result = null;
        if (_dlgResult)
        {
            result = [];
            for (int i = 0; i < items.Count; i++)
            {
                if (i < _checkboxHandles.Count)
                {
                    IntPtr state = User32.SendMessageW(_checkboxHandles[i],
                        Win32Constants.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    if (state == (IntPtr)Win32Constants.BST_CHECKED)
                        result.Add(items[i]);
                }
            }
        }

        // 정리
        User32.EnableWindow(_hwndMain, true);
        User32.SetForegroundWindow(_hwndMain);
        User32.DestroyWindow(_hwndDialog);
        _hwndDialog = IntPtr.Zero;
        _checkboxHandles.Clear();
        if (hFont != IntPtr.Zero) Gdi32.DeleteObject(hFont);

        return result;
    }

    private static void ApplyFont(IntPtr hwnd, IntPtr hFont)
    {
        User32.SendMessageW(hwnd, Win32Constants.WM_SETFONT, hFont, (IntPtr)1);
    }

    [UnmanagedCallersOnly]
    private static IntPtr CleanupDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IDC_BTN_OK)
                {
                    _dlgResult = true;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
                if (id == IDC_BTN_CANCEL)
                {
                    _dlgResult = false;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
                if (id == IDC_SELECT_ALL)
                {
                    // 전체 선택/해제 토글
                    _selectAllState = !_selectAllState;
                    IntPtr checkState = (IntPtr)(_selectAllState
                        ? Win32Constants.BST_CHECKED : Win32Constants.BST_UNCHECKED);
                    foreach (IntPtr h in _checkboxHandles)
                        User32.SendMessageW(h, Win32Constants.BM_SETCHECK, checkState, IntPtr.Zero);
                }
                return IntPtr.Zero;

            case Win32Constants.WM_CLOSE:
                _dlgResult = false;
                _dlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
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
