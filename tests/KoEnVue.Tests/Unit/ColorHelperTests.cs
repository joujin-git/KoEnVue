using KoEnVue.Core.Color;
using Xunit;

namespace KoEnVue.Tests.Unit;

public class ColorHelperTests
{
    [Theory]
    [InlineData("#000000", 0x00000000u)]
    [InlineData("#FFFFFF", 0x00FFFFFFu)]
    [InlineData("#16A34A", 0x004AA316u)]  // COLORREF = 0x00BBGGRR
    [InlineData("D97706", 0x000677D9u)]   // no leading '#'
    [InlineData("invalid", 0u)]            // 잘못된 형식 → 0
    public void HexToColorRef_ReturnsExpectedBgr(string hex, uint expected)
    {
        Assert.Equal(expected, ColorHelper.HexToColorRef(hex));
    }

    [Theory]
    [InlineData("#16A34A", 0x16, 0xA3, 0x4A)]
    [InlineData("FFFFFF", 0xFF, 0xFF, 0xFF)]
    [InlineData("#GGHHII", 0x00, 0x00, 0x00)]  // 비 16진 문자 → (0,0,0)
    public void HexToRgb_ParsesChannels(string hex, byte r, byte g, byte b)
    {
        var actual = ColorHelper.HexToRgb(hex);
        Assert.Equal((r, g, b), actual);
    }

    [Fact]
    public void HexToRgb_RgbToHex_RoundTrips()
    {
        var rgb = ColorHelper.HexToRgb("#16A34A");
        string hex = ColorHelper.RgbToHex(rgb.R, rgb.G, rgb.B);
        Assert.Equal("#16A34A", hex);
    }

    [Theory]
    [InlineData("#16A34A", true, "#16A34A")]
    [InlineData("16a34a", true, "#16A34A")]    // 소문자 + no '#' → 정규화
    [InlineData("  #FFFFFF  ", true, "#FFFFFF")] // 공백 trim
    [InlineData("ZZZZZZ", false, "")]          // 비 16진
    [InlineData("12345", false, "")]           // 길이 불일치
    [InlineData("", false, "")]
    public void TryNormalizeHex_AcceptsAndUppercases(string input, bool ok, string expected)
    {
        bool actualOk = ColorHelper.TryNormalizeHex(input, out string actual);
        Assert.Equal(ok, actualOk);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0x004AA316u, 0x16, 0xA3, 0x4A)]
    [InlineData(0x00000000u, 0x00, 0x00, 0x00)]
    public void ColorRefToRgb_UnpacksBgr(uint colorRef, byte r, byte g, byte b)
    {
        var actual = ColorHelper.ColorRefToRgb(colorRef);
        Assert.Equal((r, g, b), actual);
    }
}
