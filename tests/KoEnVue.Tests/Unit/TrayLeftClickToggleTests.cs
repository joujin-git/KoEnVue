using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 트레이 좌클릭 일괄 숨김/복원 — 보이는 것을 함께 숨기고, 숨기기 직전에 보이던 것만 되살린다.
/// </summary>
public class TrayLeftClickToggleTests
{
    /// <param name="badgeVisible">배지가 보이는 중인가 (= !UserHidden).</param>
    /// <param name="cursorVisible">커서 헤일로가 보이는 중인가 (= CursorIndicatorEnabled).</param>
    private static AppConfig Make(bool badgeVisible, bool cursorVisible,
                                  bool restoreBadge = DefaultConfig.TrayHideRestoreBadge,
                                  bool restoreCursor = DefaultConfig.TrayHideRestoreCursor) =>
        new()
        {
            UserHidden = !badgeVisible,
            CursorIndicatorEnabled = cursorVisible,
            TrayHideRestoreBadge = restoreBadge,
            TrayHideRestoreCursor = restoreCursor,
        };

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void HidesEverythingVisible_AndRecordsSnapshot(bool badgeVisible, bool cursorVisible)
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible, cursorVisible));

        Assert.True(hidden.UserHidden);
        Assert.False(hidden.CursorIndicatorEnabled);
        Assert.Equal(badgeVisible, hidden.TrayHideRestoreBadge);
        Assert.Equal(cursorVisible, hidden.TrayHideRestoreCursor);
    }

    /// <summary>핵심 요구: 원래 꺼둔 쪽은 복원 좌클릭에서도 계속 꺼진 채로 남는다.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RoundTrip_RestoresOnlyWhatWasVisible(bool badgeVisible, bool cursorVisible)
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible, cursorVisible));
        AppConfig restored = Tray.ComputeLeftClickToggle(hidden);

        Assert.Equal(badgeVisible, !restored.UserHidden);
        Assert.Equal(cursorVisible, restored.CursorIndicatorEnabled);
    }

    /// <summary>배지만 쓰던 사용자가 좌클릭을 왕복해도 헤일로가 멋대로 켜지지 않는다.</summary>
    [Fact]
    public void CursorStaysOff_WhenItWasOffBeforeHiding()
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible: true, cursorVisible: false));
        AppConfig restored = Tray.ComputeLeftClickToggle(hidden);

        Assert.False(restored.UserHidden);              // 배지는 되살아나고
        Assert.False(restored.CursorIndicatorEnabled);  // 헤일로는 계속 꺼진 채
    }

    /// <summary>메뉴로 배지만 숨겨둔 상태에서 좌클릭하면 헤일로만 숨었다가 헤일로만 돌아온다.</summary>
    [Fact]
    public void BadgeStaysHidden_WhenItWasHiddenBeforeHiding()
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible: false, cursorVisible: true));
        AppConfig restored = Tray.ComputeLeftClickToggle(hidden);

        Assert.True(restored.UserHidden);
        Assert.True(restored.CursorIndicatorEnabled);
    }

    /// <summary>스냅샷이 비어 있으면(직접 편집 등) 좌클릭이 먹통이 되지 않도록 둘 다 되살린다.</summary>
    [Fact]
    public void RestoresBoth_WhenSnapshotIsEmpty()
    {
        AppConfig restored = Tray.ComputeLeftClickToggle(
            Make(badgeVisible: false, cursorVisible: false, restoreBadge: false, restoreCursor: false));

        Assert.False(restored.UserHidden);
        Assert.True(restored.CursorIndicatorEnabled);
    }

    /// <summary>숨김 진입 때만 스냅샷을 갱신한다 — 복원 전이는 기록을 덮어쓰지 않는다.</summary>
    [Fact]
    public void RestoreDoesNotOverwriteSnapshot()
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible: true, cursorVisible: false));
        AppConfig restored = Tray.ComputeLeftClickToggle(hidden);

        Assert.True(restored.TrayHideRestoreBadge);
        Assert.False(restored.TrayHideRestoreCursor);
    }

    [Fact]
    public void DoesNotMutateInputInstance()
    {
        AppConfig original = Make(badgeVisible: true, cursorVisible: true);
        Tray.ComputeLeftClickToggle(original);

        Assert.False(original.UserHidden);
        Assert.True(original.CursorIndicatorEnabled);
    }

    // ================================================================
    // 취소선 개수 — 설정상 비활성은 세지 않는다
    // ================================================================

    [Fact]
    public void HiddenCount_Zero_WhenNothingHidden()
    {
        Assert.Equal(0, Tray.CountHiddenIndicators(Make(badgeVisible: true, cursorVisible: true)));
    }

    [Fact]
    public void HiddenCount_One_WhenOnlyBadgeHidden()
    {
        Assert.Equal(1, Tray.CountHiddenIndicators(Make(badgeVisible: false, cursorVisible: true)));
    }

    /// <summary>헤일로를 평소 꺼두고 쓰는 사용자 — 취소선이 상시 뜨면 안 된다.</summary>
    [Fact]
    public void HiddenCount_Zero_WhenCursorIsDisabledBySetting()
    {
        AppConfig config = Make(badgeVisible: true, cursorVisible: false, restoreCursor: false);

        Assert.Equal(0, Tray.CountHiddenIndicators(config));
    }

    /// <summary>같은 `cursor_indicator_enabled = false` 라도 좌클릭이 숨긴 것이면 센다.</summary>
    [Fact]
    public void HiddenCount_One_WhenCursorHiddenByLeftClick()
    {
        AppConfig config = Make(badgeVisible: true, cursorVisible: false, restoreCursor: true);

        Assert.Equal(1, Tray.CountHiddenIndicators(config));
    }

    [Fact]
    public void HiddenCount_Two_WhenBothHiddenByLeftClick()
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible: true, cursorVisible: true));

        Assert.Equal(2, Tray.CountHiddenIndicators(hidden));
    }

    /// <summary>헤일로를 안 쓰는 사용자의 좌클릭 왕복 — 0 → 1(배지만) → 0.</summary>
    [Fact]
    public void HiddenCount_CursorDisabledUser_RoundTripStaysSingleLine()
    {
        AppConfig start = Make(badgeVisible: true, cursorVisible: false, restoreCursor: false);
        Assert.Equal(0, Tray.CountHiddenIndicators(start));

        AppConfig hidden = Tray.ComputeLeftClickToggle(start);
        Assert.Equal(1, Tray.CountHiddenIndicators(hidden));

        AppConfig restored = Tray.ComputeLeftClickToggle(hidden);
        Assert.Equal(0, Tray.CountHiddenIndicators(restored));
    }

    /// <summary>메뉴로 헤일로를 다시 켜면 배지 숨김만 남아 단일선으로 줄어든다.</summary>
    [Fact]
    public void HiddenCount_DropsToOne_WhenCursorReenabledFromMenu()
    {
        AppConfig hidden = Tray.ComputeLeftClickToggle(Make(badgeVisible: true, cursorVisible: true));
        AppConfig cursorBackOn = hidden with { CursorIndicatorEnabled = true };

        Assert.Equal(1, Tray.CountHiddenIndicators(cursorBackOn));
    }
}
