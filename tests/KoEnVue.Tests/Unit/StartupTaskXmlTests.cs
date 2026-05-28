using KoEnVue.App.Startup;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="StartupTaskManager.BuildStartupTaskXml"/> 가 emit 하는 schtasks 2.0 XML 의 invariant 박제.
/// PR-03 D 회귀 (LogonTrigger.UserId 누락 → ANY-user trigger) + PR-15 RunLevel 분기 + asInvoker 호환성.
/// </summary>
public class StartupTaskXmlTests
{
    [Theory]
    [InlineData(true,  "<RunLevel>HighestAvailable</RunLevel>")]
    [InlineData(false, "<RunLevel>LeastPrivilege</RunLevel>")]
    public void RunLevel_DerivedFromAdminElevation(bool admin, string expected)
    {
        string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\KoEnVue.exe", admin);
        Assert.Contains(expected, xml);
    }

    [Fact]
    public void Delay_AlwaysEmitsPT15S()
    {
        string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\KoEnVue.exe", false);
        Assert.Contains("<Delay>PT15S</Delay>", xml);
    }

    [Fact]
    public void LogonTrigger_UserId_NotEmpty_PR03_D_RegressionGuard()
    {
        // PR-03 D 회귀: <UserId> 가 비면 ANY-user trigger 로 해석 → admin 요구 → ExitCode=1.
        // CI portable: "비어있지 않음" + "\\" 포함만 박제 (호스트명 ASCII 의존 회피).
        string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\KoEnVue.exe", false);
        const string openTag = "<UserId>";
        const string closeTag = "</UserId>";
        int start = xml.IndexOf(openTag, System.StringComparison.Ordinal);
        int end = xml.IndexOf(closeTag, System.StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "UserId 태그 쌍이 존재해야 함");
        string userId = xml.Substring(start + openTag.Length, end - start - openTag.Length);
        Assert.NotEmpty(userId);
        Assert.Contains("\\", userId);
    }

    [Fact]
    public void Command_WrapsPathInQuotes_ForPathsEqualMigrationCompat()
    {
        // 양끝 큰따옴표는 schtasks /tr 방식과의 호환 — QueryRegisteredTask 의 Trim('"') 가 두 방식 모두 처리.
        // XmlEntityCodec.Escape 가 " 를 &quot; 로 변환하므로 XML 안에서는 &quot; 형태로 박제.
        string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\Program Files\KoEnVue.exe", false);
        Assert.Contains("<Command>&quot;C:\\Program Files\\KoEnVue.exe&quot;</Command>", xml);
    }

    [Fact]
    public void Command_AmpersandInPath_EscapedViaXmlEntityCodec()
    {
        // 메타문자 & 가 &amp; 로 변환되는지 — XmlEntityCodec.Escape 호출 박제.
        string xml = StartupTaskManager.BuildStartupTaskXml(@"C:\foo&bar\KoEnVue.exe", false);
        Assert.Contains("<Command>&quot;C:\\foo&amp;bar\\KoEnVue.exe&quot;</Command>", xml);
    }
}
