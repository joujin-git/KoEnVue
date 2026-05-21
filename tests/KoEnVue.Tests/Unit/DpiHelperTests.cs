using KoEnVue.Core.Dpi;
using Xunit;

namespace KoEnVue.Tests.Unit;

public class DpiHelperTests
{
    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(100, 1.5, 150)]
    [InlineData(100, 1.25, 125)]
    [InlineData(100, 2.0, 200)]
    [InlineData(0, 1.5, 0)]
    public void Scale_Int_AppliesDpiScale(int baseValue, double scale, int expected)
    {
        Assert.Equal(expected, DpiHelper.Scale(baseValue, scale));
    }

    [Theory]
    // Math.Round 디폴트 = MidpointRounding.ToEven (banker's). F-S05 의 (int) 절삭 회귀 방지를 위해
    // 본 함수는 반드시 Math.Round 를 거쳐야 한다.
    [InlineData(0.5, 1.0, 0)]    // banker's: 0.5 → 0 (짝수)
    [InlineData(1.5, 1.0, 2)]    // banker's: 1.5 → 2 (짝수)
    [InlineData(2.5, 1.0, 2)]    // banker's: 2.5 → 2 (짝수)
    [InlineData(0.6, 1.0, 1)]    // non-midpoint
    [InlineData(10.4, 1.25, 13)] // 13.0 → 13
    public void Scale_Double_UsesMathRound(double baseValue, double scale, int expected)
    {
        Assert.Equal(expected, DpiHelper.Scale(baseValue, scale));
    }

    [Fact]
    public void BASE_DPI_Is96()
    {
        Assert.Equal(96, DpiHelper.BASE_DPI);
    }
}
