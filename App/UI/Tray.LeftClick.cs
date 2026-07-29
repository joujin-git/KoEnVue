using KoEnVue.App.Models;

namespace KoEnVue.App.UI;

/// <summary>
/// 트레이 아이콘 좌클릭(<see cref="TrayClickAction.Toggle"/>) 이 도는 표시 상태 4단계.
/// 값은 순환 순서 그대로이며 <see cref="Tray.ComputeLeftClickCycle"/> 이 인덱스 산술에 쓴다.
/// </summary>
internal enum IndicatorVisibility
{
    /// <summary>플로팅 배지 + 커서 헤일로 둘 다 보임.</summary>
    Both = 0,
    /// <summary>배지만 보임 (헤일로 숨김).</summary>
    BadgeOnly = 1,
    /// <summary>헤일로만 보임 (배지 숨김).</summary>
    CursorOnly = 2,
    /// <summary>둘 다 숨김.</summary>
    None = 3,
}

/// <summary>
/// 트레이 좌클릭의 표시 상태 순환 정책. 부수효과 없는 순수 계산만 담당하고,
/// 오버레이 적용·저장·아이콘 갱신은 호출부(Program)가 한다.
/// </summary>
internal static partial class Tray
{
    /// <summary>순환 단계 수 — <see cref="IndicatorVisibility"/> 의 값 개수.</summary>
    private const int VisibilityStageCount = 4;

    /// <summary>현재 config 가 가리키는 표시 단계.</summary>
    internal static IndicatorVisibility GetVisibility(AppConfig config)
    {
        bool badgeVisible = !config.UserHidden;
        bool cursorVisible = config.CursorIndicatorEnabled;

        if (badgeVisible) return cursorVisible ? IndicatorVisibility.Both : IndicatorVisibility.BadgeOnly;
        return cursorVisible ? IndicatorVisibility.CursorOnly : IndicatorVisibility.None;
    }

    /// <summary>
    /// 좌클릭 한 번의 상태 전이 — 현재 단계의 <b>다음</b> 단계로 넘어간다.
    /// <para>
    /// 둘 다 보임 → 배지만 → 헤일로만 → 모두 숨김 → (다시) 둘 다 보임.
    /// </para>
    /// 현재 상태가 어느 단계든(메뉴로 개별 토글해 만든 상태 포함) 그 다음 단계로 이어지므로
    /// 별도의 복원 기록이 필요 없다.
    /// </summary>
    /// <param name="config">현재 설정. 이 인스턴스는 수정하지 않는다.</param>
    /// <returns>다음 단계가 반영된 새 설정.</returns>
    internal static AppConfig ComputeLeftClickCycle(AppConfig config)
    {
        var next = (IndicatorVisibility)(((int)GetVisibility(config) + 1) % VisibilityStageCount);

        return config with
        {
            UserHidden = next is IndicatorVisibility.CursorOnly or IndicatorVisibility.None,
            CursorIndicatorEnabled = next is IndicatorVisibility.Both or IndicatorVisibility.CursorOnly,
        };
    }

    /// <summary>
    /// 트레이 아이콘 취소선을 몇 줄 그릴지 — 지금 숨겨져 있는 표시 요소의 개수(0/1/2).
    /// 0 = 선 없음, 1 = 단일선(하나만 숨김), 2 = 이중선(둘 다 숨김).
    /// </summary>
    internal static int CountHiddenIndicators(AppConfig config)
    {
        int count = 0;
        if (config.UserHidden) count++;
        if (!config.CursorIndicatorEnabled) count++;
        return count;
    }
}
