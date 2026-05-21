using KoEnVue.App.Config;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="Settings.Validate"/> 의 clamp + enum-fallback 회귀 가드.
/// config.json 은 user-writable 이고 STJ 소스 생성기는 integer enum 을 범위 체크 없이 캐스트하므로
/// Validate 가 silent 보정의 단일 진실원 — 본 테스트가 그 보정 동작을 박제한다.
/// </summary>
public class SettingsValidateTests
{
    [Fact]
    public void PollIntervalMs_BelowMin_ClampedToMin()
    {
        var cfg = new AppConfig() with { PollIntervalMs = -100 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DefaultConfig.MinPollMs, result.PollIntervalMs);
    }

    [Fact]
    public void PollIntervalMs_AboveMax_ClampedToMax()
    {
        var cfg = new AppConfig() with { PollIntervalMs = 999_999 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DefaultConfig.MaxPollMs, result.PollIntervalMs);
    }

    [Fact]
    public void Opacity_BelowMin_ClampedToMin()
    {
        var cfg = new AppConfig() with { Opacity = -0.5 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DefaultConfig.MinOpacity, result.Opacity);
    }

    [Fact]
    public void Opacity_AboveMax_ClampedToMax()
    {
        var cfg = new AppConfig() with { Opacity = 2.0 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DefaultConfig.MaxOpacity, result.Opacity);
    }

    [Fact]
    public void FontSize_BelowMin_ClampedToMin()
    {
        var cfg = new AppConfig() with { FontSize = 0 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DefaultConfig.MinFontSize, result.FontSize);
    }

    [Fact]
    public void IndicatorScale_RoundedToOneDecimal()
    {
        var cfg = new AppConfig() with { IndicatorScale = 2.345 };
        var result = Settings.Validate(cfg);
        Assert.Equal(2.3, result.IndicatorScale);
    }

    [Fact]
    public void DisplayMode_InvalidCast_FallsBackToAlways()
    {
        // STJ 소스 생성기 회귀 — 정의되지 않은 정수가 enum 으로 들어오는 케이스 재현.
        var cfg = new AppConfig() with { DisplayMode = (DisplayMode)99 };
        var result = Settings.Validate(cfg);
        Assert.Equal(DisplayMode.Always, result.DisplayMode);
    }

    [Fact]
    public void Language_InvalidCast_FallsBackToAuto()
    {
        var cfg = new AppConfig() with { Language = (AppLanguage)99 };
        var result = Settings.Validate(cfg);
        Assert.Equal(AppLanguage.Auto, result.Language);
    }

    [Fact]
    public void Theme_InvalidCast_FallsBackToCustom()
    {
        var cfg = new AppConfig() with { Theme = (Theme)99 };
        var result = Settings.Validate(cfg);
        Assert.Equal(Theme.Custom, result.Theme);
    }

    [Fact]
    public void NonKoreanIme_ValidValue_Preserved()
    {
        var cfg = new AppConfig() with { NonKoreanIme = NonKoreanImeMode.Dim };
        var result = Settings.Validate(cfg);
        Assert.Equal(NonKoreanImeMode.Dim, result.NonKoreanIme);
    }

    [Fact]
    public void Advanced_InvalidOverlayClassName_FallsBackToDefault()
    {
        // 한국어 문자, 빈 문자열, 256자 초과 등은 RegisterClassExW 가 받지 않으므로
        // 디폴트 "KoEnVueOverlay" 로 폴백. AdvancedConfig 의 init 디폴트와 일치.
        var cfg = new AppConfig() with
        {
            Advanced = new AdvancedConfig { OverlayClassName = "한글클래스" },
        };
        var result = Settings.Validate(cfg);
        Assert.Equal("KoEnVueOverlay", result.Advanced.OverlayClassName);
    }

    [Fact]
    public void Advanced_ValidOverlayClassName_Preserved()
    {
        var cfg = new AppConfig() with
        {
            Advanced = new AdvancedConfig { OverlayClassName = "My_Custom_Class_1" },
        };
        var result = Settings.Validate(cfg);
        Assert.Equal("My_Custom_Class_1", result.Advanced.OverlayClassName);
    }
}
