using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.Detector;

// ================================================================
// IVirtualDesktopManager COM 인터페이스
// ================================================================

[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
internal partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow,
        [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
}

// ================================================================
// SystemFilter
// ================================================================

/// <summary>
/// 시스템 필터. 8-조건 단락 평가로 인디케이터 숨김 여부를 판정한다.
/// </summary>
internal static class SystemFilter
{
    private static readonly Guid CLSID_VirtualDesktopManager =
        new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    private static readonly StrategyBasedComWrappers _comWrappers = new();
    private static readonly IVirtualDesktopManager? _vdm;

    // WS_CAPTION int 캐스트: GetWindowLongW는 int 반환, WS_CAPTION은 uint
    private const int WsCaption = unchecked((int)Win32Constants.WS_CAPTION);

    static SystemFilter()
    {
        try
        {
            Guid clsid = CLSID_VirtualDesktopManager;
            Guid iid = typeof(IVirtualDesktopManager).GUID;
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

        // 4. 클래스명 블랙리스트
        string className = GetClassName(hwnd);
        foreach (string c in config.SystemHideClasses)
        {
            if (c.Equals(className, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (string c in config.SystemHideClassesUser)
        {
            if (c.Equals(className, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 5. 키보드 포커스 없음
        if (hwndFocus == IntPtr.Zero && config.HideWhenNoFocus) return true;

        // 6. 전체화면 독점
        if (config.HideInFullscreen && IsFullscreenExclusive(hwnd)) return true;

        // 7. 드래그 중 (마우스 좌클릭 누름)
        if (User32.GetAsyncKeyState(Win32Constants.VK_LBUTTON) < 0) return true;

        // 8. 앱 필터 (블랙/화이트리스트)
        if (!PassesAppFilter(hwnd, config)) return true;

        return false;
    }

    // ================================================================
    // Private 헬퍼
    // ================================================================

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
        catch
        {
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
    /// </summary>
    private static bool PassesAppFilter(IntPtr hwnd, AppConfig config)
    {
        if (config.AppFilterList.Length == 0) return true;

        string processName = GetProcessName(hwnd);
        bool inList = config.AppFilterList.Contains(processName, StringComparer.OrdinalIgnoreCase);

        return config.AppFilterMode switch
        {
            "blacklist" => !inList,
            "whitelist" => inList,
            _ => true,
        };
    }

    /// <summary>
    /// 윈도우 클래스명 조회.
    /// </summary>
    private static string GetClassName(IntPtr hwnd)
    {
        char[] buffer = new char[Win32Constants.MAX_CLASS_NAME];
        int len = User32.GetClassNameW(hwnd, buffer, Win32Constants.MAX_CLASS_NAME);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    /// <summary>
    /// HWND로부터 프로세스 이름 조회.
    /// </summary>
    private static string GetProcessName(IntPtr hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0) return string.Empty;

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)processId);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
