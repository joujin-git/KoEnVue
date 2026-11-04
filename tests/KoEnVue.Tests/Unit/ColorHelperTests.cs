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

    // release-review 2026-11-04: 레이어드 DIB 에서 순수 검정(0)은 투명 구멍 — 그리기 색은 거의 검정으로.
    [Theory]
    [InlineData("#000000", 0x00010101u)]
    [InlineData("ZZZZZZ", 0x00010101u)]   // 잘못된 형식 → 검정 → 거의 검정
    [InlineData("#9333EA", 0x00EA3393u)]  // 그 밖의 색은 HexToColorRef 그대로
    [InlineData("#010101", 0x00010101u)]
    public void HexToLayeredColorRef_NeverPureBlack(string hex, uint expected)
    {
        Assert.Equal(expected, ColorHelper.HexToLayeredColorRef(hex));
    }

    // CapsLock 배지 테두리 = CapsLock 색 + 흰색 45% (기본 #9333EA → 사용자가 고른 시안 #C48FF3).
    [Theory]
    [InlineData("#9333EA", 0.45, "#C48FF3")]
    [InlineData("#9333EA", 0.0, "#9333EA")]
    [InlineData("#9333EA", 1.0, "#FFFFFF")]
    [InlineData("#000000", 0.5, "#808080")]
    [InlineData("ZZZZZZ", 0.45, "#737373")] // 잘못된 형식 → 검정 폴백 후 섞기
    public void MixWithWhite_BlendsPerChannel(string hex, double ratio, string expected)
    {
        Assert.Equal(expected, ColorHelper.MixWithWhite(hex, ratio));
    }
}
