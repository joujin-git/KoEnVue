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
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            Win32Constants.PopupMenuClass, "notepad", DefaultCfg(), includeSystemInputProcesses: false));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "#32768", "", DefaultCfg(), includeSystemInputProcesses: false));
    }

    [Fact]
    public void MatchesSuppressRoot_ShellTray_True()
    {
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Shell_TrayWnd", "explorer", DefaultCfg(), includeSystemInputProcesses: false));
    }

    [Fact]
    public void MatchesSuppressRoot_ShellExperienceHost_True()
    {
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "ShellExperienceHost", DefaultCfg(),
            includeSystemInputProcesses: false));
    }

    [Fact]
    public void MatchesSuppressRoot_NormalApp_False()
    {
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Notepad", "notepad", DefaultCfg(), includeSystemInputProcesses: false));
    }

    [Fact]
    public void MatchesSuppressRoot_SystemInput_OnlyWhenRequested()
    {
        Assert.False(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "SearchHost", DefaultCfg(),
            includeSystemInputProcesses: false));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "SearchHost", DefaultCfg(),
            includeSystemInputProcesses: true));
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", DefaultCfg(),
            includeSystemInputProcesses: true));
    }

    [Fact]
    public void MatchesSuppressRoot_UserHideClass()
    {
        var cfg = new AppConfig() with { SystemHideClassesUser = ["MyMenuClass"] };
        Assert.True(OverlaySuppressProbe.MatchesSuppressRoot(
            "MyMenuClass", "foo", cfg, includeSystemInputProcesses: false));
    }
}
