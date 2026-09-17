using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>PR-32 OverlaySuppressProbe — 라이브 WFP 없이 클래스/프로세스 매칭만 박제.</summary>
public class OverlaySuppressProbeTests
{
    private static AppConfig DefaultCfg() => new();

    [Fact]
    public void MatchesSuppressRoot_PopupMenuClass_AlwaysTrue()
    {
        foreach (OverlaySuppressTarget target in Enum.GetValues<OverlaySuppressTarget>())
        {
            Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
                Win32Constants.PopupMenuClass, "notepad", DefaultCfg(), target));
            Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
                "#32768", "", DefaultCfg(), target));
        }
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("shell_traywnd")]
    public void MatchesSuppressRoot_DesktopTaskbar_HidesCursorOnly(string className)
    {
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            className, "explorer", DefaultCfg(), OverlaySuppressTarget.Badge));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            className, "explorer", DefaultCfg(), OverlaySuppressTarget.CursorHalo));
    }

    [Theory]
    [InlineData("XamlExplorerHostIslandWindow_WASDK")]
    [InlineData("TopLevelWindowForOverflowXamlIsland")]
    [InlineData("ControlCenterWindow")]
    public void MatchesSuppressRoot_ShellPopups_HideBoth(string className)
    {
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            className, "explorer", DefaultCfg(), OverlaySuppressTarget.Badge));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            className, "explorer", DefaultCfg(), OverlaySuppressTarget.CursorHalo));
    }

    [Fact]
    public void MatchesSuppressRoot_DesktopTaskbar_BadgeStillHonorsUserProcessRule()
    {
        // 배지 예외가 클래스 매칭만 건너뛰는지(프로세스 규칙 우선순위) 박제용 입력일 뿐이다.
        // 실제로 explorer 를 넣으면 FG 축에서 파일 탐색기 창 전체의 배지도 숨으므로
        // "옛 동작으로 되돌리는 방법"으로 안내하면 안 된다.
        var cfg = new AppConfig() with { SystemHideProcessesUser = ["explorer"] };
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Shell_TrayWnd", "explorer", cfg, OverlaySuppressTarget.Badge));
    }

    [Fact]
    public void MatchesSuppressRoot_DesktopTaskbar_UserClassListCannotOverrideBadgeExemption()
    {
        // 예외는 고정 정책 — system_hide_classes_user 에 넣어도 배지 포인터 축은 숨기지 않는다.
        var cfg = new AppConfig() with { SystemHideClassesUser = ["Shell_TrayWnd"] };
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Shell_TrayWnd", "explorer", cfg, OverlaySuppressTarget.Badge));
    }

    [Fact]
    public void MatchesSuppressRoot_DesktopTaskbar_CursorHaloFollowsConfig()
    {
        // 커서 헤일로는 하드코딩이 아니라 system_hide_classes 를 따른다.
        var cfg = new AppConfig() with { SystemHideClasses = [] };
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Shell_TrayWnd", "explorer", cfg, OverlaySuppressTarget.CursorHalo));
    }

    [Fact]
    public void DefaultSystemHideClasses_UnchangedAndDesktopTaskbarStayInForegroundList()
    {
        // 기본값 내용·순서 불변 (koenvue_config.json 에 기본값이 기록되므로 순서 변경도 diff).
        // 바탕화면·작업 표시줄 4종이 FG 목록에 남아야 클릭해 포커스가 넘어갔을 때 배지가 숨는다.
        Assert.Equal(
            ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
             "XamlExplorerHostIslandWindow_WASDK", "TopLevelWindowForOverflowXamlIsland", "ControlCenterWindow"],
            DefaultConfig.DefaultSystemHideClasses);
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("SHELL_SECONDARYTRAYWND", true)]
    [InlineData("ControlCenterWindow", false)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", false)]
    [InlineData("", false)]
    public void IsDesktopTaskbarClass(string className, bool expected)
    {
        Assert.Equal(expected, DefaultConfig.IsDesktopTaskbarClass(className));
    }

    [Fact]
    public void MatchesSuppressRoot_ShellExperienceHost_True()
    {
        foreach (OverlaySuppressTarget target in Enum.GetValues<OverlaySuppressTarget>())
        {
            Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
                "Windows.UI.Core.CoreWindow", "ShellExperienceHost", DefaultCfg(), target));
        }
    }

    [Fact]
    public void MatchesSuppressRoot_NormalApp_False()
    {
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Notepad", "notepad", DefaultCfg(), OverlaySuppressTarget.Badge));
    }

    [Fact]
    public void MatchesSuppressRoot_SystemInput_OnlyForCursorHalo()
    {
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "SearchHost", DefaultCfg(),
            OverlaySuppressTarget.Badge));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "SearchHost", DefaultCfg(),
            OverlaySuppressTarget.CursorHalo));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", DefaultCfg(),
            OverlaySuppressTarget.CursorHalo));
    }

    [Fact]
    public void MatchesSuppressRoot_UserHideClass()
    {
        var cfg = new AppConfig() with { SystemHideClassesUser = ["MyMenuClass"] };
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "MyMenuClass", "foo", cfg, OverlaySuppressTarget.Badge));
    }
}
