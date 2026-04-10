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

    // 서브메뉴: 크기 배율
    // 정수 프리셋 — Nx → IDM_SIZE_BASE + N - ScaleIntegerMin. N ∈ [ScaleIntegerMin, ScaleIntegerMax].
    // IDM_SIZE_CUSTOM — "직접 지정" 대화상자 호출.
    private const int IDM_SIZE_BASE = 3201;
    private const int IDM_SIZE_CUSTOM = 3206;
    private const int ScaleIntegerMin = 1;
    private const int ScaleIntegerMax = 5;
    private const double ScaleMinValue = 1.0;
    private const double ScaleMaxValue = 5.0;
    private const double ScaleTolerance = 0.001;
    private const double ScaleStep = 0.1;

    // 메인 메뉴
    private const int IDM_STARTUP         = 4001;
    private const int IDM_CLEANUP         = 4003;
    private const int IDM_SNAP_TO_WINDOWS = 4004;
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

        // --- 서브메뉴: 크기 배율 ---
        // 정수 프리셋 5개 + 직접 지정(대화상자). 현재 배율이 비정수면
        // "직접 지정 (2.3배)" 형태로 값을 라벨에 노출하고 해당 항목에 라디오 체크.
        IntPtr hSizeMenu = User32.CreatePopupMenu();
        for (int n = ScaleIntegerMin; n <= ScaleIntegerMax; n++)
        {
            User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
                (nuint)(IDM_SIZE_BASE + n - ScaleIntegerMin), I18n.GetSizeLabel(n));
        }

        double currentScale = Math.Clamp(config.IndicatorScale, ScaleMinValue, ScaleMaxValue);
        bool isIntegerScale = IsIntegerScale(currentScale);
        string customLabel = isIntegerScale
            ? I18n.MenuSizeCustom
            : I18n.FormatCustomScaleLabel(currentScale);
        User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_SIZE_CUSTOM, customLabel);

        uint sizeCheckId;
        if (isIntegerScale)
        {
            int intScale = Math.Clamp((int)Math.Round(currentScale), ScaleIntegerMin, ScaleIntegerMax);
            sizeCheckId = (uint)(IDM_SIZE_BASE + intScale - ScaleIntegerMin);
        }
        else
        {
            sizeCheckId = (uint)IDM_SIZE_CUSTOM;
        }
        User32.CheckMenuRadioItem(hSizeMenu,
            (uint)IDM_SIZE_BASE,
            (uint)IDM_SIZE_CUSTOM,
            sizeCheckId,
            Win32Constants.MF_BYCOMMAND);

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
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hSizeMenu, I18n.MenuSize);
        uint snapFlags = config.SnapToWindows ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, snapFlags, (nuint)IDM_SNAP_TO_WINDOWS, I18n.MenuSnapToWindows);
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
        // --- 크기 배율 정수 프리셋 (동적 ID 범위 매칭) ---
        if (commandId >= IDM_SIZE_BASE && commandId < IDM_SIZE_BASE + (ScaleIntegerMax - ScaleIntegerMin + 1))
        {
            double newScale = ScaleIntegerMin + (commandId - IDM_SIZE_BASE);
            if (Math.Abs(newScale - config.IndicatorScale) > ScaleTolerance)
                updateConfig(config with { IndicatorScale = newScale });
            return;
        }

        // --- 크기 배율 직접 지정 대화상자 ---
        if (commandId == IDM_SIZE_CUSTOM)
        {
            double? typed = ShowScaleInputDialog(config.IndicatorScale);
            if (typed.HasValue)
            {
                double rounded = Math.Round(typed.Value, 1);
                if (Math.Abs(rounded - config.IndicatorScale) > ScaleTolerance)
                    updateConfig(config with { IndicatorScale = rounded });
            }
            return;
        }

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

            // --- 창에 자석처럼 붙이기 토글 ---
            case IDM_SNAP_TO_WINDOWS:
                updateConfig(config with { SnapToWindows = !config.SnapToWindows });
                Logger.Info($"SnapToWindows toggled: {!config.SnapToWindows}");
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

    // ================================================================
    // Private — 직접 지정 대화상자 (크기 배율 입력)
    // ================================================================

    // 레이아웃 상수 (96 DPI 기준)
    private const int ScaleDlgWidth = 320;
    private const int ScaleDlgPad = 16;
    private const int ScaleDlgLabelH = 20;
    private const int ScaleDlgEditH = 26;
    private const int ScaleDlgHintH = 18;
    private const int ScaleDlgBtnW = 80;
    private const int ScaleDlgBtnH = 28;
    private const int ScaleDlgGap = 8;

    // 컨트롤 ID
    private const int IDC_SCALE_EDIT = 6001;
    private const int IDC_SCALE_OK = 6002;
    private const int IDC_SCALE_CANCEL = 6003;

    // 다이얼로그 모달 상태
    private static bool _scaleDlgResult;
    private static bool _scaleDlgClosed;
    private static IntPtr _hwndScaleDlg;
    private static IntPtr _hwndScaleEdit;
    private static double _scaleDlgParsedValue;

    /// <summary>배율이 정수에 매우 가까운지 확인 (1e-3 tolerance).</summary>
    private static bool IsIntegerScale(double scale) =>
        Math.Abs(scale - Math.Round(scale)) < ScaleTolerance;

    /// <summary>
    /// 배율 직접 입력 대화상자. 커서 위치(서브메뉴 직전 클릭 좌표)에 모달로 표시.
    /// 확인 클릭 시 입력값 유효성 검사: 숫자 파싱 실패 또는 [1.0, 5.0] 범위 밖이면
    /// MessageBox로 안내 후 대화상자 유지. 취소/ESC → null.
    /// </summary>
    private static unsafe double? ShowScaleInputDialog(double initialValue)
    {
        _scaleDlgResult = false;
        _scaleDlgClosed = false;
        _scaleDlgParsedValue = 0;

        // 커서 위치 기준 모니터 DPI/work area
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);

        int pad = DpiHelper.Scale(ScaleDlgPad, dpiScale);
        int labelH = DpiHelper.Scale(ScaleDlgLabelH, dpiScale);
        int editH = DpiHelper.Scale(ScaleDlgEditH, dpiScale);
        int hintH = DpiHelper.Scale(ScaleDlgHintH, dpiScale);
        int btnW = DpiHelper.Scale(ScaleDlgBtnW, dpiScale);
        int btnH = DpiHelper.Scale(ScaleDlgBtnH, dpiScale);
        int gap = DpiHelper.Scale(ScaleDlgGap, dpiScale);
        int dlgWidth = DpiHelper.Scale(ScaleDlgWidth, dpiScale);

        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = User32.GetSystemMetricsForDpi(Win32Constants.SM_CYCAPTION, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CYFIXEDFRAME, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, rawDpi);

        int dlgHeight = nonClientH
            + pad
            + labelH + gap
            + editH + gap
            + hintH + pad
            + btnH
            + pad;

        // UI 폰트 (맑은 고딕 9pt, DPI 스케일)
        int fontHeight = -(int)Math.Round(9.0 * dpiY / 72.0);
        IntPtr hFont = Gdi32.CreateFontW(fontHeight, 0, 0, 0, Win32Constants.FW_NORMAL,
            0, 0, 0, Win32Constants.DEFAULT_CHARSET,
            Win32Constants.OUT_TT_PRECIS, Win32Constants.CLIP_DEFAULT_PRECIS,
            Win32Constants.CLEARTYPE_QUALITY, Win32Constants.DEFAULT_PITCH,
            "맑은 고딕");

        // 다이얼로그 클래스 등록 (중복 호출은 무시됨)
        string dlgClassName = "KoEnVueScaleDlg";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ScaleDlgProc,
            lpszClassName = dlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref wc);

        // 대화상자 좌표: 커서 위치 기준, 모니터 work area 안으로 클램프
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMon, ref mi);

        int cx = cursorPt.X;
        int cy = cursorPt.Y;
        if (cx + dlgWidth > mi.rcWork.Right) cx = mi.rcWork.Right - dlgWidth;
        if (cy + dlgHeight > mi.rcWork.Bottom) cy = mi.rcWork.Bottom - dlgHeight;
        if (cx < mi.rcWork.Left) cx = mi.rcWork.Left;
        if (cy < mi.rcWork.Top) cy = mi.rcWork.Top;

        _hwndScaleDlg = User32.CreateWindowExW(0, dlgClassName, I18n.ScaleDialogTitle,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            _hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndScaleDlg == IntPtr.Zero)
        {
            if (hFont != IntPtr.Zero) Gdi32.DeleteObject(hFont);
            return null;
        }

        int borderW = DpiHelper.Scale(16, dpiScale);
        int contentW = dlgWidth - pad * 2 - borderW;

        int y = pad;

        // 안내 레이블 ("배율 (1.0 ~ 5.0):")
        IntPtr hwndLabel = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogPrompt,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, labelH,
            _hwndScaleDlg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndLabel, hFont);
        y += labelH + gap;

        // EDIT 박스 — 현재 값으로 미리 채움
        string initialText = initialValue.ToString("0.#");
        _hwndScaleEdit = User32.CreateWindowExW(0, "EDIT", initialText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_BORDER
                | Win32Constants.WS_TABSTOP | Win32Constants.ES_LEFT | Win32Constants.ES_AUTOHSCROLL,
            pad, y, contentW, editH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_EDIT, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(_hwndScaleEdit, hFont);
        y += editH + gap;

        // 힌트 레이블
        IntPtr hwndHint = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogHint,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, hintH,
            _hwndScaleDlg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndHint, hFont);
        y += hintH + pad;

        // 버튼 — 오른쪽 정렬
        int btnAreaWidth = btnW * 2 + gap;
        int btnX = dlgWidth - borderW - pad - btnAreaWidth;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP
                | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, y, btnW, btnH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_OK, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndOk, hFont);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + gap, y, btnW, btnH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_CANCEL, IntPtr.Zero, IntPtr.Zero);
        ApplyFont(hwndCancel, hFont);

        // 모달 표시 + EDIT 포커스 + 텍스트 전체 선택
        User32.EnableWindow(_hwndMain, false);
        User32.ShowWindow(_hwndScaleDlg, Win32Constants.SW_SHOW);
        User32.SetForegroundWindow(_hwndScaleDlg);
        User32.SetFocus(_hwndScaleEdit);
        User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));

        // 모달 루프 — IsDialogMessageW로 Tab/Enter/ESC 기본 처리
        while (!_scaleDlgClosed)
        {
            int ret = User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break;
            if (!User32.IsDialogMessageW(_hwndScaleDlg, ref msg))
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }

        double? result = _scaleDlgResult ? _scaleDlgParsedValue : null;

        User32.EnableWindow(_hwndMain, true);
        User32.SetForegroundWindow(_hwndMain);
        User32.DestroyWindow(_hwndScaleDlg);
        _hwndScaleDlg = IntPtr.Zero;
        _hwndScaleEdit = IntPtr.Zero;
        if (hFont != IntPtr.Zero) Gdi32.DeleteObject(hFont);

        return result;
    }

    /// <summary>
    /// EDIT 박스 내용을 읽고 파싱. 성공 시 _scaleDlgParsedValue 설정 후 true,
    /// 실패 시 MessageBox로 에러 표시 후 false (대화상자 유지).
    /// </summary>
    private static bool TryCommitScaleInput()
    {
        if (_hwndScaleEdit == IntPtr.Zero) return false;

        char[] buf = new char[32];
        int len = User32.GetWindowTextW(_hwndScaleEdit, buf, buf.Length);
        string text = len > 0 ? new string(buf, 0, len).Trim() : "";

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogInvalidInput,
                I18n.ScaleDialogTitle, 0);
            User32.SetFocus(_hwndScaleEdit);
            User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return false;
        }

        if (value < ScaleMinValue - ScaleTolerance || value > ScaleMaxValue + ScaleTolerance)
        {
            User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogOutOfRange,
                I18n.ScaleDialogTitle, 0);
            User32.SetFocus(_hwndScaleEdit);
            User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return false;
        }

        _scaleDlgParsedValue = Math.Round(Math.Clamp(value, ScaleMinValue, ScaleMaxValue), 1);
        return true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr ScaleDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
                // IsDialogMessageW는 Enter → IDOK(1)/DEFPUSHBUTTON ID, Escape → IDCANCEL(2)로
                // 변환해 WM_COMMAND를 쏜다. 우리 OK는 BS_DEFPUSHBUTTON이라 Enter가 IDC_SCALE_OK로
                // 오지만 IDOK도 방어적으로 함께 수락.
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IDC_SCALE_OK || id == Win32Constants.IDOK)
                {
                    if (TryCommitScaleInput())
                    {
                        _scaleDlgResult = true;
                        _scaleDlgClosed = true;
                    }
                    return IntPtr.Zero;
                }
                if (id == IDC_SCALE_CANCEL || id == Win32Constants.IDCANCEL)
                {
                    _scaleDlgResult = false;
                    _scaleDlgClosed = true;
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case Win32Constants.WM_CLOSE:
                _scaleDlgResult = false;
                _scaleDlgClosed = true;
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
