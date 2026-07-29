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
    // 취소선 개수 — 0 = 없음 / 1 = 단일선 / 2 = 이중선
    // ================================================================

    [Theory]
    [InlineData(true, true, 0)]    // 둘 다 보임
    [InlineData(true, false, 1)]   // 배지만 보임 → 헤일로 숨김
    [InlineData(false, true, 1)]   // 헤일로만 보임 → 배지 숨김
    [InlineData(false, false, 2)]  // 모두 숨김
    public void CountHiddenIndicators_MatchesStage(bool badgeVisible, bool cursorVisible, int expected)
    {
        Assert.Equal(expected, Tray.CountHiddenIndicators(Make(badgeVisible, cursorVisible)));
    }

    /// <summary>순환하는 동안 취소선 개수는 0 → 1 → 1 → 2 순서를 따른다.</summary>
    [Fact]
    public void CountHiddenIndicators_FollowsCycle()
    {
        AppConfig current = Make(badgeVisible: true, cursorVisible: true);
        var counts = new List<int>();

        for (int i = 0; i < 4; i++)
        {
            counts.Add(Tray.CountHiddenIndicators(current));
            current = Tray.ComputeLeftClickCycle(current);
        }

        Assert.Equal([0, 1, 1, 2], counts);
    }
}
