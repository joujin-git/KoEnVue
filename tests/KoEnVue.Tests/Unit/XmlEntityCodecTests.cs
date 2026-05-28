using KoEnVue.Core.Xml;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="XmlEntityCodec.Escape"/> / <see cref="XmlEntityCodec.Unescape"/> 의
/// XML 1.0 5 predefined entities 처리 + 순서 invariant 박제. 처리 순서가 회귀하면
/// 중복 인코딩 (Escape) / 잡아채기 (Unescape) 결함이 발생한다.
/// </summary>
public class XmlEntityCodecTests
{
    [Fact]
    public void Escape_All5Entities_Replaced()
    {
        Assert.Equal("&amp;&lt;&gt;&quot;&apos;", XmlEntityCodec.Escape("&<>\"'"));
    }

    [Fact]
    public void Unescape_All5Entities_Restored()
    {
        Assert.Equal("&<>\"'", XmlEntityCodec.Unescape("&amp;&lt;&gt;&quot;&apos;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("path & <tag> \"quoted\" 'single'")]
    [InlineData("한글 & 영어")]
    [InlineData("&&&&&")]
    public void RoundTrip_Preserves(string input)
    {
        Assert.Equal(input, XmlEntityCodec.Unescape(XmlEntityCodec.Escape(input)));
    }

    [Fact]
    public void Escape_AmpersandFirst_NotDoubleEncoded()
    {
        // 처리 순서: & 가 먼저. 만약 < 가 먼저면 "a&b<c" → "a&b&lt;c" → "a&amp;b&amp;lt;c" 로 중복 인코딩.
        // 결과 invariant — 구현이 Replace chain 이든 다른 방식이든 동일한 출력이어야 함.
        Assert.Equal("a&amp;b&lt;c", XmlEntityCodec.Escape("a&b<c"));
    }

    [Fact]
    public void Unescape_AmpAmpLast_NotMangled()
    {
        // 처리 순서: &amp; 가 마지막. 만약 첫 번째면 "&amp;amp;" → "&amp;" → "&" 로 잡아채기.
        Assert.Equal("&amp;", XmlEntityCodec.Unescape("&amp;amp;"));
    }
}
