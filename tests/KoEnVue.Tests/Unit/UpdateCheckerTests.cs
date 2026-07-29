using KoEnVue.App.Update;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="UpdateChecker.IsNewer"/> / <see cref="UpdateChecker.NormalizeVersion"/> 회귀 가드.
/// 버전 비교 실패 시 업데이트 알림이 조용히 무력화되므로 순수 파싱 경로를 박제한다.
/// </summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V0.9.4.0", "0.9.4.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0+build.42", "1.0.0")]
    [InlineData("v1.2.3-rc.1+meta", "1.2.3")]
    [InlineData("", "")]
    public void NormalizeVersion_StripsPrefixAndSemVerSuffix(string input, string expected)
    {
        Assert.Equal(expected, UpdateChecker.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("0.9.4.0", "v0.9.5.0", true)]
    // 0.9.x → 1.0 정식 승격. major 가 오르며 자릿수가 줄어드는 구간이라 문자열 비교였다면
    // "0.9.9.7" > "1.0.0.0" 으로 뒤집혀 업데이트 알림이 조용히 사라진다 (Version 숫자 비교로 방어).
    [InlineData("0.9.9.7", "v1.0.0.0", true)]
    [InlineData("1.0.0.0", "v0.9.9.7", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)] // prerelease strip → 동일
    [InlineData("1.0.0", "1.0.1-beta", true)]
    [InlineData("not-a-version", "1.0.0", false)]
    [InlineData("1.0.0", "also-bad", false)]
    public void IsNewer_ComparesNormalizedVersions(string current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(current, latest));
    }
}
