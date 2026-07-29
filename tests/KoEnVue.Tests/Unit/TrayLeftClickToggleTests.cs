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
                                  bool restoreBadge = true, bool restoreCursor = true) =>
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
}
