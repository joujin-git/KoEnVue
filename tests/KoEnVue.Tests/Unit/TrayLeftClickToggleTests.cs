using KoEnVue.App.Models;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 트레이 좌클릭 표시 상태 순환 — 둘 다 보임 → 배지만 → 헤일로만 → 모두 숨김 → (다시) 둘 다.
/// </summary>
public class TrayLeftClickToggleTests
{
    /// <param name="badgeVisible">배지가 보이는 중인가 (= !UserHidden).</param>
    /// <param name="cursorVisible">커서 헤일로가 보이는 중인가 (= CursorIndicatorEnabled).</param>
    private static AppConfig Make(bool badgeVisible, bool cursorVisible) =>
        new() { UserHidden = !badgeVisible, CursorIndicatorEnabled = cursorVisible };

    // ================================================================
    // 현재 단계 판독
    // ================================================================

    // xUnit 테스트 시그니처는 public 이어야 하므로 internal enum 을 직접 못 받는다 — 단계는
    // 순서값(int)으로 넘기고 본문에서 캐스팅한다. 0=Both / 1=BadgeOnly / 2=CursorOnly / 3=None.
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 3)]
    public void GetVisibility_MapsConfigToStage(bool badgeVisible, bool cursorVisible, int expectedStage)
    {
        Assert.Equal((IndicatorVisibility)expectedStage, Tray.GetVisibility(Make(badgeVisible, cursorVisible)));
    }

    // ================================================================
    // 순환
    // ================================================================

    [Theory]
    [InlineData(0, 1)]  // Both      → BadgeOnly
    [InlineData(1, 2)]  // BadgeOnly → CursorOnly
    [InlineData(2, 3)]  // CursorOnly→ None
    [InlineData(3, 0)]  // None      → Both
    public void Cycle_AdvancesToNextStage(int fromStage, int expectedStage)
    {
        var from = (IndicatorVisibility)fromStage;
        AppConfig start = Make(
            badgeVisible: from is IndicatorVisibility.Both or IndicatorVisibility.BadgeOnly,
            cursorVisible: from is IndicatorVisibility.Both or IndicatorVisibility.CursorOnly);

        Assert.Equal((IndicatorVisibility)expectedStage, Tray.GetVisibility(Tray.ComputeLeftClickCycle(start)));
    }

    /// <summary>네 번 누르면 제자리 — 어느 단계에서 시작해도 마찬가지.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Cycle_FourClicksReturnToStart(bool badgeVisible, bool cursorVisible)
    {
        AppConfig start = Make(badgeVisible, cursorVisible);
        AppConfig current = start;

        for (int i = 0; i < 4; i++)
            current = Tray.ComputeLeftClickCycle(current);

        Assert.Equal(Tray.GetVisibility(start), Tray.GetVisibility(current));
        Assert.Equal(start.UserHidden, current.UserHidden);
        Assert.Equal(start.CursorIndicatorEnabled, current.CursorIndicatorEnabled);
    }

    /// <summary>한 바퀴 도는 동안 네 단계를 모두 정확히 한 번씩 거친다.</summary>
    [Fact]
    public void Cycle_VisitsEveryStageOnce()
    {
        AppConfig current = Make(badgeVisible: true, cursorVisible: true);
        var seen = new List<IndicatorVisibility>();

        for (int i = 0; i < 4; i++)
        {
            seen.Add(Tray.GetVisibility(current));
            current = Tray.ComputeLeftClickCycle(current);
        }

        Assert.Equal(
        [
            IndicatorVisibility.Both,
            IndicatorVisibility.BadgeOnly,
            IndicatorVisibility.CursorOnly,
            IndicatorVisibility.None,
        ], seen);
    }

    [Fact]
    public void Cycle_DoesNotMutateInputInstance()
    {
        AppConfig original = Make(badgeVisible: true, cursorVisible: true);
        Tray.ComputeLeftClickCycle(original);

        Assert.False(original.UserHidden);
        Assert.True(original.CursorIndicatorEnabled);
    }

    // ================================================================
    // 아이콘 도형 — 배지/헤일로 중 무엇을 그리는가 (네 단계가 모두 달라야 한다)
    // ================================================================

    [Theory]
    [InlineData(0, true, true)]     // Both       → 링 + 배지
    [InlineData(1, true, false)]    // BadgeOnly  → 배지만
    [InlineData(2, false, true)]    // CursorOnly → 링만
    [InlineData(3, false, false)]   // None       → 배경색만
    public void GetShapes_MatchesStage(int stage, bool expectedBadge, bool expectedHalo)
    {
        (bool badge, bool halo) = Tray.GetShapes((IndicatorVisibility)stage);

        Assert.Equal(expectedBadge, badge);
        Assert.Equal(expectedHalo, halo);
    }

    /// <summary>그리는 도형이 곧 지금 보이는 요소다 — 아이콘과 실제 표시가 어긋나지 않는다.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void GetShapes_MirrorsActualVisibility(bool badgeVisible, bool cursorVisible)
    {
        (bool badge, bool halo) = Tray.GetShapes(Tray.GetVisibility(Make(badgeVisible, cursorVisible)));

        Assert.Equal(badgeVisible, badge);
        Assert.Equal(cursorVisible, halo);
    }

    /// <summary>순환하는 동안 아이콘 모양이 네 단계 모두 서로 다르다.</summary>
    [Fact]
    public void GetShapes_EveryStageLooksDifferent()
    {
        AppConfig current = Make(badgeVisible: true, cursorVisible: true);
        var shapes = new List<(bool Badge, bool Halo)>();

        for (int i = 0; i < 4; i++)
        {
            shapes.Add(Tray.GetShapes(Tray.GetVisibility(current)));
            current = Tray.ComputeLeftClickCycle(current);
        }

        Assert.Equal([(true, true), (true, false), (false, true), (false, false)], shapes);
        Assert.Equal(4, shapes.Distinct().Count());
    }
}
