using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.Detector;

/// <summary>
/// 포인터 아래 창이 셸·컨텍스트 메뉴 suppress 표면인지 판정 (PR-32).
/// 메인 인디는 FG <see cref="SystemFilter"/> 와 직교하는 WFP 축으로 쓰고,
/// 커서 인디는 기존 셸 UI 숨김을 이 헬퍼로 단일화한다 (P4).
/// </summary>
internal static class OverlaySuppressProbe
{
    /// <summary>
    /// 클래스/프로세스 문자열만으로 suppress 여부 판정 (단위테스트·캐시 경로용).
    /// <paramref name="includeSystemInputProcesses"/> 가 true 면 Start/Search 도 숨김(커서).
    /// 메인은 false — Start/Search 위 표시 정책 유지.
    /// </summary>
    internal static bool MatchesSuppressRoot(
        string className,
        string processName,
        AppConfig config,
        bool includeSystemInputProcesses)
    {
        if (!string.IsNullOrEmpty(className))
        {
            if (className.Equals(Win32Constants.PopupMenuClass, StringComparison.OrdinalIgnoreCase))
                return true;
            if (SystemFilter.MatchesAny(className, config.SystemHideClasses, config.SystemHideClassesUser))
                return true;
        }

        if (string.IsNullOrEmpty(processName))
            return false;

        if (includeSystemInputProcesses && DefaultConfig.IsSystemInputProcess(processName))
            return true;

        return SystemFilter.MatchesAny(
            processName, config.SystemHideProcesses, config.SystemHideProcessesUser);
    }

    /// <summary>루트 hwnd 에 대해 클래스·프로세스 조회 후 <see cref="MatchesSuppressRoot"/>.</summary>
    internal static bool IsSuppressRoot(IntPtr root, AppConfig config, bool includeSystemInputProcesses)
    {
        if (root == IntPtr.Zero) return false;

        string className = WindowProcessInfo.GetClassName(root);
        // 클래스만으로 단락 가능하면 GetProcessName(OpenProcess) 생략.
        if (!string.IsNullOrEmpty(className)
            && (className.Equals(Win32Constants.PopupMenuClass, StringComparison.OrdinalIgnoreCase)
                || SystemFilter.MatchesAny(className, config.SystemHideClasses, config.SystemHideClassesUser)))
            return true;

        string processName = WindowProcessInfo.GetProcessName(root);
        return MatchesSuppressRoot(className, processName, config, includeSystemInputProcesses);
    }

    /// <summary>
    /// 커서 좌표 → <see cref="User32.WindowFromPoint"/> → <c>GA_ROOT</c> → suppress 여부.
    /// WS_EX_TRANSPARENT 커서 인디는 통과되어 자기 감지 없음.
    /// </summary>
    internal static bool IsOverSuppressSurface(
        POINT cursor, AppConfig config, bool includeSystemInputProcesses)
    {
        IntPtr hwnd = User32.WindowFromPoint(cursor);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr root = User32.GetAncestor(hwnd, Win32Constants.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        return IsSuppressRoot(root, config, includeSystemInputProcesses);
    }

    /// <summary>현재 커서 위치 기준. 메인 감지 틱·Forced Show 게이트용.</summary>
    internal static bool IsPointerOverSuppressSurface(
        AppConfig config, bool includeSystemInputProcesses)
    {
        if (!User32.GetCursorPos(out POINT cursor))
            return false;
        return IsOverSuppressSurface(cursor, config, includeSystemInputProcesses);
    }
}
