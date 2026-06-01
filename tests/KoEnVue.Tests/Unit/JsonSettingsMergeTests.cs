using System.Text.Json;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Config;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="JsonSettingsManager{T}.MergeWithDefaults"/> 의 병합 회귀 가드.
/// config.json 은 user-writable 이고 NativeAOT STJ 소스 생성기는 reflection off 상태에서
/// JSON 에 없는 <c>init</c> 기본값을 드롭한다. 따라서 병합 단계가 "사용자가 명시하지 않은
/// 필드는 기본값 유지" 의 단일 진실원 — 본 테스트가 두 회귀를 박제한다:
/// <list type="bullet">
///   <item>(A) 주석/트레일링 콤마가 든 정상 config 가 "손상"으로 오판되던 문제</item>
///   <item>(B) 중첩 객체를 부분만 지정하면 형제 필드가 <c>default(T)</c> 로 리셋되던 문제</item>
/// </list>
/// </summary>
public class JsonSettingsMergeTests
{
    private static AppConfig Merge(string userJson)
    {
        string merged = JsonSettingsManager<AppConfig>.MergeWithDefaults(
            userJson, AppConfigJsonContext.Default.AppConfig);
        return JsonSerializer.Deserialize(merged, AppConfigJsonContext.Default.AppConfig)!;
    }

    // ---- (B) 중첩 객체 부분 지정 시 형제 기본값 보존 ----

    [Fact]
    public void PartialNested_EventTriggers_PreservesSiblingDefault()
    {
        // on_ime_change 만 지정 — on_focus_change 는 기본값(true) 유지되어야 한다.
        var cfg = Merge("""{ "event_triggers": { "on_ime_change": false } }""");
        Assert.False(cfg.EventTriggers.OnImeChange);  // 사용자 지정
        Assert.True(cfg.EventTriggers.OnFocusChange); // 기본값 보존 (회귀 시 false)
    }

    [Fact]
    public void PartialNested_Advanced_PreservesSiblingDefault()
    {
        // overlay_class_name 만 지정 — force_topmost_interval_ms 는 기본값 유지되어야 한다.
        var cfg = Merge("""{ "advanced": { "overlay_class_name": "MyOverlay" } }""");
        Assert.Equal("MyOverlay", cfg.Advanced.OverlayClassName);
        Assert.Equal(DefaultConfig.ForceTopmostIntervalMs, cfg.Advanced.ForceTopmostIntervalMs); // 회귀 시 0
    }

    [Fact]
    public void PartialNested_RelativePosition_PreservesSiblingDefaults()
    {
        // delta_x 만 지정 — corner / delta_y 는 기본값 유지되어야 한다.
        var cfg = Merge("""{ "default_indicator_position_relative": { "delta_x": 99 } }""");
        Assert.NotNull(cfg.DefaultIndicatorPositionRelative);
        Assert.Equal(99, cfg.DefaultIndicatorPositionRelative!.DeltaX);
        Assert.Equal(DefaultConfig.DefaultRelativeCorner, cfg.DefaultIndicatorPositionRelative.Corner);
        Assert.Equal(DefaultConfig.DefaultRelativeOffsetY, cfg.DefaultIndicatorPositionRelative.DeltaY);
    }

    [Fact]
    public void FullNested_FullyOverridden()
    {
        var cfg = Merge("""{ "event_triggers": { "on_focus_change": false, "on_ime_change": false } }""");
        Assert.False(cfg.EventTriggers.OnFocusChange);
        Assert.False(cfg.EventTriggers.OnImeChange);
    }

    // ---- (A) 주석 / 트레일링 콤마 허용 ----

    [Fact]
    public void Comments_DoNotCorruptConfig()
    {
        var cfg = Merge("""
        {
            // 사용자가 손으로 단 주석
            "poll_interval_ms": 50
        }
        """);
        Assert.Equal(50, cfg.PollIntervalMs);
    }

    [Fact]
    public void TrailingCommas_DoNotCorruptConfig()
    {
        var cfg = Merge("""{ "poll_interval_ms": 50, "label_width": 80, }""");
        Assert.Equal(50, cfg.PollIntervalMs);
        Assert.Equal(80, cfg.LabelWidth);
    }

    // ---- 기존 동작 보존 (회귀 방지) ----

    [Fact]
    public void TopLevelScalar_OverriddenByUser()
    {
        var cfg = Merge("""{ "label_width": 123 }""");
        Assert.Equal(123, cfg.LabelWidth);
        Assert.Equal(DefaultConfig.LabelHeight, cfg.LabelHeight); // 미지정 필드는 기본값
    }

    [Fact]
    public void Array_ReplacedWholesale_AllowsShrinkToEmpty()
    {
        // 기본 system_hide_processes = ["ShellExperienceHost"]. 빈 배열로 축소 가능해야 한다
        // (인덱스 단위 병합이면 항목 제거가 불가능해진다).
        var cfg = Merge("""{ "system_hide_processes": [] }""");
        Assert.Empty(cfg.SystemHideProcesses);
    }

    [Fact]
    public void EmptyObject_YieldsAllDefaults()
    {
        var cfg = Merge("{}");
        Assert.Equal(DefaultConfig.PollIntervalMs, cfg.PollIntervalMs);
        Assert.True(cfg.EventTriggers.OnFocusChange);
        Assert.True(cfg.EventTriggers.OnImeChange);
    }

    [Fact]
    public void UnknownUserKey_DoesNotThrow_KnownKeysApplied()
    {
        // 미래 호환/오타 키가 있어도 병합은 통과하고 알려진 키는 정상 적용된다.
        var cfg = Merge("""{ "future_unknown_key": 42, "label_width": 77 }""");
        Assert.Equal(77, cfg.LabelWidth);
    }
}
