using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.Detector;

/// <summary>
/// 포인터 suppress 판정을 받는 오버레이. 같은 표면이라도 대상마다 숨김 정책이 달라 이 값으로 가른다.
/// </summary>
internal enum OverlaySuppressTarget
{
    /// <summary>
    /// 한/영 배지. 메뉴(<c>#32768</c>)·셸 팝업·<c>SystemHideProcesses</c> 위에서는 숨기지만,
    /// Start/Search(메인 표시 정책)와 바탕화면·작업 표시줄(<see cref="DefaultConfig.IsDesktopTaskbarClass"/>)
    /// 위에서는 표시 — 포인터가 지나가기만 해서 배지가 사라지지 않게 한다.
    /// </summary>
    Badge,
    /// <summary>커서 헤일로. 모든 suppress 표면 + Start/Search 위에서 숨김.</summary>
    CursorHalo,
}

/// <summary>
/// 포인터 아래 창이 셸·컨텍스트 메뉴 suppress 표면인지 판정 (PR-32).
/// 한/영 배지는 FG <see cref="SystemFilter"/> 와 직교하는 WFP 축으로 쓰고,
/// 커서 헤일로는 기존 셸 UI 숨김을 이 헬퍼로 단일화한다 (P4).
/// 대상별 차이는 <see cref="OverlaySuppressTarget"/> 참조.
/// </summary>
internal static class OverlaySuppressProbe
{
    /// <summary>
    /// 클래스/프로세스 문자열만으로 suppress 여부 판정 (단위테스트·캐시 경로용).
    /// </summary>
    internal static bool MatchesSuppressRoot(
        string className,
        string processName,
        AppConfig config,
        OverlaySuppressTarget target)
    {
        if (MatchesSuppressClass(className, config, target))
            return true;

        if (string.IsNullOrEmpty(processName))
            return false;

        if (target == OverlaySuppressTarget.CursorHalo && DefaultConfig.IsSystemInputProcess(processName))
            return true;

        return SystemFilter.MatchesAny(
            processName, config.SystemHideProcesses, config.SystemHideProcessesUser);
    }

    /// <summary>루트 hwnd 에 대해 클래스·프로세스 조회 후 <see cref="MatchesSuppressRoot"/>.</summary>
    internal static bool IsSuppressRoot(IntPtr root, AppConfig config, OverlaySuppressTarget target)
    {
        if (root == IntPtr.Zero) return false;

        string className = WindowProcessInfo.GetClassName(root);
        // 클래스만으로 단락 가능하면 GetProcessName(OpenProcess) 생략.
        if (MatchesSuppressClass(className, config, target))
            return true;

        string processName = WindowProcessInfo.GetProcessName(root);
        return MatchesSuppressRoot(className, processName, config, target);
    }

    /// <summary>
    /// 커서 좌표 → <see cref="User32.WindowFromPoint"/> → <c>GA_ROOT</c> → suppress 여부.
    /// WS_EX_TRANSPARENT 커서 헤일로는 통과되어 자기 감지 없음.
    /// </summary>
    internal static bool IsOverSuppressSurface(
        POINT cursor, AppConfig config, OverlaySuppressTarget target)
    {
        IntPtr hwnd = User32.WindowFromPoint(cursor);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr root = User32.GetAncestor(hwnd, Win32Constants.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        return IsSuppressRoot(root, config, target);
    }

    /// <summary>현재 커서 위치 기준. 메인 감지 틱·Forced Show 게이트용.</summary>
    internal static bool IsPointerOverSuppressSurface(
        AppConfig config, OverlaySuppressTarget target)
    {
        if (!User32.GetCursorPos(out POINT cursor))
            return false;
        return IsOverSuppressSurface(cursor, config, target);
    }

    /// <summary>클래스명만으로 suppress 인지. <see cref="IsSuppressRoot"/> 의 프로세스 조회 단락에도 쓴다.</summary>
    private static bool MatchesSuppressClass(string className, AppConfig config, OverlaySuppressTarget target)
    {
        if (string.IsNullOrEmpty(className))
            return false;

        if (className.Equals(Win32Constants.PopupMenuClass, StringComparison.OrdinalIgnoreCase))
            return true;

        // 배지는 바탕화면·작업 표시줄 위에서 숨기지 않는다. 클래스 매칭만 건너뛰므로 사용자가
        // system_hide_processes_user 에 넣은 프로세스 규칙은 그대로 적용되고, FG 축(SystemFilter)도
        // 불변이라 그 표면을 클릭해 포커스가 넘어가면 기존대로 숨는다.
        if (target == OverlaySuppressTarget.Badge && DefaultConfig.IsDesktopTaskbarClass(className))
            return false;

        return SystemFilter.MatchesAny(className, config.SystemHideClasses, config.SystemHideClassesUser);
    }
}
