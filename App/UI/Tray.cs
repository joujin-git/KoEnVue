using System.Diagnostics;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Tray;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI;

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
    // IDM_SIZE_CUSTOM — "직접 지정" 대화상자 호출. 범위/허용오차는 ScaleInputDialog에 정의.
    private const int IDM_SIZE_BASE = 3201;
    private const int IDM_SIZE_CUSTOM = 3206;
    private const int ScaleIntegerMin = 1;
    private const int ScaleIntegerMax = 5;

    // 메인 메뉴
    private const int IDM_STARTUP            = 4001;
    private const int IDM_CLEANUP            = 4003;
    private const int IDM_SNAP_TO_WINDOWS    = 4004;
    private const int IDM_SETTINGS           = 4005;
    private const int IDM_ANIMATION_ENABLED  = 4006;
    private const int IDM_CHANGE_HIGHLIGHT   = 4007;
    private const int IDM_EXIT               = 4002;

    // schtasks 작업 이름
    private const string TaskName = "KoEnVue";

    // P3: 매직 넘버 금지
    private const double OpacityTolerance = 0.001;
    private const int SchtasksQueryTimeoutMs = 3000;
    private const int SchtasksCommandTimeoutMs = 5000;

    // ================================================================
    // 내부 상태
    // ================================================================

    private static bool _initialized;
    private static IntPtr _hwndMain;
    private static SafeIconHandle? _currentIcon;
    private static NotifyIconManager? _notifyIcon;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 트레이 아이콘 등록 (NIM_ADD + NIM_SETVERSION).
    /// config.TrayEnabled == false 이면 건너뛴다.
    /// </summary>
    internal static void Initialize(IntPtr hwndMain, ImeState initialState, AppConfig config)
    {
        _hwndMain = hwndMain;

        if (!config.TrayEnabled)
        {
            _initialized = false;
            Logger.Debug("Tray disabled by config");
            return;
        }

        _currentIcon = TrayIcon.CreateIcon(initialState, config);

        _notifyIcon = new NotifyIconManager(hwndMain, AppMessages.WM_TRAY_CALLBACK, DefaultConfig.AppGuid);
        _notifyIcon.Add(_currentIcon.DangerousGetHandle(), BuildTooltip(initialState, config));

        _initialized = true;
        Logger.Info("Tray icon initialized");
    }

    /// <summary>
    /// IME 상태 변경 시 아이콘 + 툴팁 갱신 (NIM_MODIFY).
    /// </summary>
    internal static void UpdateState(ImeState state, AppConfig config)
    {
        if (!_initialized) return;

        var newIcon = TrayIcon.CreateIcon(state, config);
        _notifyIcon?.UpdateIconAndTooltip(newIcon.DangerousGetHandle(), BuildTooltip(state, config));

        // 이전 아이콘 해제 후 교체 — 소유권은 Tray.cs 측에 남는다 (NotifyIconManager 는 해제 금지).
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
    }

    /// <summary>
    /// 트레이 아이콘 제거 (NIM_DELETE). 앱 종료 시 호출.
    /// </summary>
    internal static void Remove()
    {
        if (!_initialized) return;

        bool removed = _notifyIcon?.Remove() ?? true;
        if (!removed)
            Logger.Warning("Failed to remove tray icon on shutdown");

        _currentIcon?.Dispose();
        _currentIcon = null;
        _notifyIcon = null;
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

        double currentScale = Math.Clamp(config.IndicatorScale,
            ScaleInputDialog.ScaleMinValue, ScaleInputDialog.ScaleMaxValue);
        bool isIntegerScale = ScaleInputDialog.IsIntegerScale(currentScale);
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
        uint animationFlags = config.AnimationEnabled ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, animationFlags, (nuint)IDM_ANIMATION_ENABLED, I18n.MenuAnimation);
        uint highlightFlags = config.ChangeHighlight ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, highlightFlags, (nuint)IDM_CHANGE_HIGHLIGHT, I18n.MenuChangeHighlight);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        bool isStartup = IsStartupRegistered();
        User32.AppendMenuW(hMenu, isStartup ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED,
            (nuint)IDM_STARTUP, I18n.MenuStartup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDefaultPosMenu, I18n.MenuDefaultPosition);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_CLEANUP, I18n.MenuCleanup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_SETTINGS, I18n.MenuSettings);
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
            if (Math.Abs(newScale - config.IndicatorScale) > ScaleInputDialog.ScaleTolerance)
                updateConfig(config with { IndicatorScale = newScale });
            return;
        }

        // --- 크기 배율 직접 지정 대화상자 ---
        if (commandId == IDM_SIZE_CUSTOM)
        {
            double? typed = ScaleInputDialog.Show(_hwndMain, config.IndicatorScale);
            if (typed.HasValue)
            {
                double rounded = Math.Round(typed.Value, 1);
                if (Math.Abs(rounded - config.IndicatorScale) > ScaleInputDialog.ScaleTolerance)
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

            // --- 애니메이션 사용 토글 ---
            case IDM_ANIMATION_ENABLED:
                updateConfig(config with { AnimationEnabled = !config.AnimationEnabled });
                Logger.Info($"AnimationEnabled toggled: {!config.AnimationEnabled}");
                break;

            // --- 변경 시 강조 토글 ---
            case IDM_CHANGE_HIGHLIGHT:
                updateConfig(config with { ChangeHighlight = !config.ChangeHighlight });
                Logger.Info($"ChangeHighlight toggled: {!config.ChangeHighlight}");
                break;

            // --- 미사용 위치 데이터 정리 ---
            case IDM_CLEANUP:
                CleanupUnusedPositions(config, updateConfig);
                break;

            // --- 상세 설정 ---
            case IDM_SETTINGS:
                SettingsDialog.Show(hwndMain, config, updateConfig);
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
    /// 현재 IME 상태 기반 툴팁 문자열을 반환한다. <c>config.TrayTooltip</c> 이 false 이면 null
    /// (shell 은 빈 툴팁으로 취급하여 호버 표시를 생략).
    /// </summary>
    private static string? BuildTooltip(ImeState state, AppConfig config)
    {
        if (!config.TrayTooltip) return null;
        return $"KoEnVue - {I18n.GetTrayTooltip(state)}";
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

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 exe 경로가 현재 실행 파일 경로와 다르면 재등록한다.
    /// 포터블 모드에서 exe를 다른 폴더로 옮겼을 때 태스크 스케줄러가 오래된 절대 경로를 가리키는 문제를 해결.
    /// schtasks 호출 지연(~100~300ms)을 main 스레드에서 분리하기 위해 백그라운드 스레드에서 실행.
    /// </summary>
    internal static void SyncStartupPathAsync()
    {
        var thread = new Thread(SyncStartupPathCore)
        {
            IsBackground = true,
            Name = "StartupPathSync",
        };
        thread.Start();
    }

    private static void SyncStartupPathCore()
    {
        try
        {
            string? registeredPath = QueryRegisteredTaskCommand();
            if (registeredPath is null)
            {
                // 등록 안 돼 있거나 쿼리 실패 — 정상 케이스 (대부분 사용자는 startup 안 씀)
                return;
            }

            string? currentPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentPath)) return;

            if (PathsEqual(registeredPath, currentPath))
            {
                Logger.Debug("Startup task path already in sync");
                return;
            }

            Logger.Info($"Startup task path moved: '{registeredPath}' -> '{currentPath}', re-registering");
            RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{currentPath}\\\"\" /sc ONLOGON /rl HIGHEST /f");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to sync startup task path: {ex.Message}");
        }
    }

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 실행 명령 경로를 반환. 미등록 또는 실패 시 null.
    /// </summary>
    private static string? QueryRegisteredTaskCommand()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /xml ONE")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string xml = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(SchtasksQueryTimeoutMs);
            if (proc.ExitCode != 0) return null;
            return ExtractCommandFromXml(xml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// schtasks XML 출력에서 &lt;Command&gt;...&lt;/Command&gt; 내용을 추출하고 XML 엔티티를 복원한다.
    /// </summary>
    private static string? ExtractCommandFromXml(string xml)
    {
        const string openTag = "<Command>";
        const string closeTag = "</Command>";
        int start = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += openTag.Length;
        int end = xml.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (end < 0) return null;

        return xml[start..end]
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Trim();
    }

    /// <summary>
    /// Windows 경로 동일성 비교 (대소문자 무시 + 정규화).
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
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
                catch (Exception ex) when (ex is InvalidOperationException
                                             or System.ComponentModel.Win32Exception)
                {
                    // InvalidOperationException: ProcessName 게터가 이미 종료된 프로세스를 참조
                    // Win32Exception: 시스템/서비스 프로세스 접근 권한 부족
                    Logger.Debug($"CleanupDialog: failed to read process name: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            // 외부 루프 전체가 실패하면 "실행 중 프로세스 0개"로 오인되어 unused 목록이 과도 확장될
            // 위험이 있는 드문 치명 경로. 내부에서 던질 예외 타입을 특정하기 어려워 wide catch 유지.
            Logger.Warning($"CleanupDialog: Process.GetProcesses enumeration failed: {ex.Message}");
            return;
        }

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
        List<string>? selected = CleanupDialog.Show(_hwndMain, unused);
        if (selected is null || selected.Count == 0) return;

        // 삭제
        var cleaned = new Dictionary<string, int[]>(config.IndicatorPositions);
        foreach (string name in selected)
            cleaned.Remove(name);

        updateConfig(config with { IndicatorPositions = cleaned });
        Logger.Info($"Cleaned {selected.Count} position(s): {string.Join(", ", selected)}");
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
