using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.Detector;

/// <summary>
/// 시스템 필터. 8-조건 단락 평가로 인디케이터 숨김 여부를 판정한다.
/// </summary>
internal static class SystemFilter
{
    private static readonly Guid CLSID_VirtualDesktopManager =
        new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    // IID는 IVirtualDesktopManager의 [Guid] 어트리뷰트와 동일 — 명시적 const 로 추출하여
    // typeof(...).GUID 리플렉션 경로 의존을 제거 (NativeAOT 트림 안전성 + 의도 명시).
    private static readonly Guid IID_IVirtualDesktopManager =
        new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

    private static readonly StrategyBasedComWrappers _comWrappers = new();
    private static readonly IVirtualDesktopManager? _vdm;

    // WS_CAPTION int 캐스트: GetWindowLongW는 int 반환, WS_CAPTION은 uint
    private const int WsCaption = unchecked((int)Win32Constants.WS_CAPTION);

    static SystemFilter()
    {
        try
        {
            Guid clsid = CLSID_VirtualDesktopManager;
            Guid iid = IID_IVirtualDesktopManager;
            int hr = Ole32.CoCreateInstance(ref clsid, IntPtr.Zero,
                Win32Constants.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);

            if (hr == 0 && ppv != IntPtr.Zero)
            {
                _vdm = (IVirtualDesktopManager)_comWrappers.GetOrCreateObjectForComInstance(
                    ppv, CreateObjectFlags.None);
                Marshal.Release(ppv);
                Logger.Info("VirtualDesktopManager COM initialized");
            }
            else
            {
                Logger.Warning($"VirtualDesktopManager CoCreateInstance failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            _vdm = null;
            Logger.Warning($"VirtualDesktopManager init exception: {ex.Message}");
        }
    }

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 8-조건 단락 평가. 하나라도 true면 인디케이터를 숨긴다.
    /// </summary>
    public static bool ShouldHide(IntPtr hwnd, IntPtr hwndFocus, AppConfig config)
    {
        // 1. 보안 데스크톱
        if (hwnd == IntPtr.Zero) return true;

        // 2. 보이지 않거나 최소화됨
        if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;

        // 3. 현재 가상 데스크톱이 아님
        if (!IsOnCurrentVirtualDesktop(hwnd)) return true;

        // 4. 클래스명 블랙리스트 (기본: 바탕화면/작업 표시줄 + Win11 바탕화면 컨텍스트 메뉴)
        string className = WindowProcessInfo.GetClassName(hwnd);
        if (MatchesAny(className, config.SystemHideClasses, config.SystemHideClassesUser))
            return true;

        // GetProcessName(hwnd) 1-tick 메모이제이션 — 감지 80ms 핫패스에서 조건 4-b(owner
        // 루프 최대 5회) / 5 / 8 이 동일 hwnd 의 프로세스명을 각기 재조회하던 중복 제거.
        // P/Invoke 호출자와 ToolHelp32 Snapshot 스캔 비용이 tick 당 1회로 수렴.
        string? hwndProcess = null;
        string ResolveHwndProcess() => hwndProcess ??= WindowProcessInfo.GetProcessName(hwnd);

        // 4-b. 소유자(owner) 창 클래스명 블랙리스트
        //      바탕화면(Progman/WorkerW) 등 숨김 대상 창에서 띄운 대화상자(#32770 등)도
        //      함께 숨긴다. 단, 소유자와 대화상자의 프로세스가 같은 경우만 (시스템 대화상자).
        //      WinUI 앱(메모장 등)의 Common File Dialog는 Progman을 소유자로 사용하지만
        //      프로세스가 다르므로(explorer ≠ Notepad) 제외된다.
        //      무한 루프 방지를 위해 최대 5단계까지만 탐색.
        IntPtr hwndOwner = User32.GetWindow(hwnd, Win32Constants.GW_OWNER);
        for (int depth = 0; hwndOwner != IntPtr.Zero && depth < 5; depth++)
        {
            string ownerClass = WindowProcessInfo.GetClassName(hwndOwner);
            if (MatchesAny(ownerClass, config.SystemHideClasses, config.SystemHideClassesUser))
            {
                // 소유자와 대화상자가 같은 프로세스일 때만 숨김 (시스템 대화상자)
                string ownerProcess = WindowProcessInfo.GetProcessName(hwndOwner);
                if (ownerProcess.Equals(ResolveHwndProcess(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            hwndOwner = User32.GetWindow(hwndOwner, Win32Constants.GW_OWNER);
        }

        // 5. 프로세스명 블랙리스트 (기본: ShellExperienceHost — 작업 표시줄 컨텍스트 메뉴/팝업)
        //    클래스명과 달리 프로세스명 조회(GetProcessName)는 비용이 있으므로
        //    리스트가 비어 있으면 조회 자체를 건너뛴다.
        if (config.SystemHideProcesses.Length > 0 || config.SystemHideProcessesUser.Length > 0)
        {
            if (MatchesAny(ResolveHwndProcess(), config.SystemHideProcesses, config.SystemHideProcessesUser))
                return true;
        }

        // 6. 키보드 포커스 없음
        if (hwndFocus == IntPtr.Zero && config.HideWhenNoFocus) return true;

        // 7. 전체화면 독점
        if (config.HideInFullscreen && IsFullscreenExclusive(hwnd)) return true;

        // 8. 앱 필터 (블랙/화이트리스트) — 동일 tick 의 hwndProcess 캐시 재사용
        if (!PassesAppFilter(hwnd, hwndProcess, config)) return true;

        return false;
    }

    // ================================================================
    // Private 헬퍼
    // ================================================================

    /// <summary>
    /// name 이 두 배열(기본 + 사용자) 중 하나라도 대소문자 무시하여 일치하면 true.
    /// SystemHideClasses/SystemHideProcesses 등 "기본 + 사용자 추가" 2-리스트 조회를 단일화한다.
    /// </summary>
    private static bool MatchesAny(string name, string[] baseList, string[] userList)
    {
        foreach (string s in baseList)
            if (s.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (string s in userList)
            if (s.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 현재 가상 데스크톱에 있는지 확인.
    /// COM 실패 시 true 반환 (안전한 기본값 = 숨기지 않음).
    /// </summary>
    private static bool IsOnCurrentVirtualDesktop(IntPtr hwnd)
    {
        if (_vdm is null) return true;

        try
        {
            int hr = _vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out bool onCurrent);
            return hr == 0 ? onCurrent : true;
        }
        catch (Exception ex)
        {
            // IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop은 [PreserveSig]로 HRESULT를
            // int로 반환하므로 .NET 런타임이 COMException을 던지지 않는다. 실제로 발생할 수 있는
            // 예외는 RCW 상태(InvalidComObjectException), 마샬링, 드물게 NullReferenceException
            // 등으로 일관되게 좁히기 어렵다. 본문이 단일 COM 호출 1줄이라 과대 포획 리스크가 낮으므로
            // wide catch 유지. 기본값은 "숨기지 않음"으로 안전 폴백.
            Logger.Debug($"IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop failed: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 전체화면 독점 판별.
    /// 윈도우가 모니터를 완전히 덮고 + WS_CAPTION이 없으면 전체화면 독점.
    /// 최대화 윈도우(타이틀바 있음)와 구분하기 위해 WS_CAPTION 체크 필수.
    /// </summary>
    private static bool IsFullscreenExclusive(IntPtr hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out RECT rect))
            return false;

        IntPtr hMonitor = User32.MonitorFromPoint(
            new POINT(rect.Left, rect.Top), Win32Constants.MONITOR_DEFAULTTONEAREST);
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        if (!User32.GetMonitorInfoW(hMonitor, ref mi))
            return false;

        // 윈도우가 모니터를 완전히 덮는가?
        bool coversMonitor = rect.Left <= mi.rcMonitor.Left
            && rect.Top <= mi.rcMonitor.Top
            && rect.Right >= mi.rcMonitor.Right
            && rect.Bottom >= mi.rcMonitor.Bottom;

        if (!coversMonitor) return false;

        // WS_CAPTION 체크: 캡션 없으면 전체화면 독점
        int style = User32.GetWindowLongW(hwnd, Win32Constants.GWL_STYLE);
        return (style & WsCaption) != WsCaption;
    }

    /// <summary>
    /// 앱 필터 (블랙/화이트리스트) 판정.
    /// <paramref name="processName"/>이 null 이면(상위 조건 4-b/5 모두 캐시를 요구하지 않은 경우)
    /// 여기서 처음 조회한다 — tick 당 1회 보장.
    /// </summary>
    private static bool PassesAppFilter(IntPtr hwnd, string? processName, AppConfig config)
    {
        if (config.AppFilterList.Length == 0) return true;

        string name = processName ?? WindowProcessInfo.GetProcessName(hwnd);
        bool inList = config.AppFilterList.Contains(name, StringComparer.OrdinalIgnoreCase);

        return config.AppFilterMode switch
        {
            AppFilterMode.Blacklist => !inList,
            AppFilterMode.Whitelist => inList,
            _ => true,
        };
    }

}
