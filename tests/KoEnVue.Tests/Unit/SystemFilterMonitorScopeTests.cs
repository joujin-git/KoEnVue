using KoEnVue.App.Detector;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// PR-24: 다른 모니터의 셸 UI 무시 게이트가 쓰는 <see cref="SystemFilter.IsMonitorScopedShell"/> 분류 박제.
/// 작업표시줄(모니터-국한)만 true — 바탕화면/일반앱은 false. 이 분류가 깨지면 cross-monitor
/// 무시가 바탕화면까지 번져 보조 모니터 인디 미숨김 회귀가 생기거나(Progman→true), 작업표시줄을
/// 못 잡아 본 PR 수정이 무효가 된다(Shell_TrayWnd→false). <see cref="SystemFilter.SameMonitor"/> 는
/// 라이브 Win32(MonitorFromWindow) 의존이라 단위테스트 제외 — 수동 smoke 검증.
/// </summary>
public class SystemFilterMonitorScopeTests
{
    [Theory]
    [InlineData("Shell_TrayWnd", true)]            // 주 작업표시줄 — 게이트 대상
    [InlineData("Shell_SecondaryTrayWnd", true)]   // 보조 모니터 작업표시줄
    [InlineData("shell_traywnd", true)]            // 대소문자 무시 (MatchesAny OrdinalIgnoreCase)
    [InlineData("SHELL_SECONDARYTRAYWND", true)]
    [InlineData("Progman", false)]                 // 바탕화면 — 게이트 제외 (전체 데스크톱 덮음, 항상 숨김)
    [InlineData("WorkerW", false)]                 // 바탕화면
    [InlineData("ApplicationFrameWindow", false)]  // 일반 UWP 프레임 (PR-23 대상, 게이트 아님)
    [InlineData("Chrome_WidgetWin_1", false)]      // 일반 앱
    [InlineData("ControlCenterWindow", false)]     // 빠른설정 패널 — 보수적 제외
    [InlineData("", false)]                        // 빈 문자열 (GetClassName 실패)
    public void IsMonitorScopedShell_ClassifiesTaskbarOnly(string className, bool expected)
        => Assert.Equal(expected, SystemFilter.IsMonitorScopedShell(className));
}
