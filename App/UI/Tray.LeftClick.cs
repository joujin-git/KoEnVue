using KoEnVue.App.Models;

namespace KoEnVue.App.UI;

/// <summary>
/// 트레이 아이콘 좌클릭(<see cref="TrayClickAction.Toggle"/>) 의 일괄 숨김/복원 정책.
/// 부수효과 없는 순수 상태 전이만 담당하고, 오버레이 적용·저장·아이콘 갱신은 호출부(Program)가 한다.
/// </summary>
internal static partial class Tray
{
    /// <summary>
    /// 좌클릭 한 번의 상태 전이를 계산한다 — 플로팅 배지와 커서 헤일로를 **함께** 다룬다.
    /// <list type="bullet">
    /// <item>하나라도 보이는 중이면: 지금 보이는 것이 무엇이었는지 스냅샷에 기록하고 둘 다 숨긴다.</item>
    /// <item>둘 다 숨겨진 상태면: 그 스냅샷대로만 되살린다 — 원래 꺼둔 쪽은 계속 꺼둔 채로 둔다.</item>
    /// </list>
    /// 그래서 "배지만 켜고 헤일로는 꺼둔" 사용자가 좌클릭을 두 번 해도 헤일로가 멋대로 켜지지 않는다.
    /// 개별 토글(메뉴 <c>IDM_USER_HIDDEN</c> / <c>IDM_CURSOR_TOGGLE</c>)은 이 스냅샷을 건드리지
    /// 않는다 — 스냅샷은 숨김 진입 시점에만 갱신되므로 다음 좌클릭이 항상 직전 가시 상태를 본다.
    /// </summary>
    /// <param name="config">현재 설정. 이 인스턴스는 수정하지 않는다.</param>
    /// <returns>전이가 반영된 새 설정.</returns>
    internal static AppConfig ComputeLeftClickToggle(AppConfig config)
    {
        bool badgeVisible = !config.UserHidden;
        bool cursorVisible = config.CursorIndicatorEnabled;

        if (badgeVisible || cursorVisible)
        {
            return config with
            {
                TrayHideRestoreBadge = badgeVisible,
                TrayHideRestoreCursor = cursorVisible,
                UserHidden = true,
                CursorIndicatorEnabled = false,
            };
        }

        bool restoreBadge = config.TrayHideRestoreBadge;
        bool restoreCursor = config.TrayHideRestoreCursor;
        // 스냅샷이 비어 있으면(최초 실행 직후 둘 다 꺼둔 상태·사용자가 config 를 직접 편집한 경우)
        // 복원 좌클릭이 아무것도 하지 않아 먹통으로 보인다. 그때는 둘 다 되살린다.
        if (!restoreBadge && !restoreCursor)
        {
            restoreBadge = true;
            restoreCursor = true;
        }

        return config with
        {
            UserHidden = !restoreBadge,
            CursorIndicatorEnabled = restoreCursor,
        };
    }

    /// <summary>
    /// 트레이 아이콘 취소선을 몇 줄 그릴지 — <b>지금 숨겨져 있는</b> 표시 요소의 개수(0/1/2).
    /// <para>
    /// 핵심은 "설정상 비활성" 을 세지 않는 것이다. 커서 헤일로는
    /// <see cref="AppConfig.CursorIndicatorEnabled"/> 하나가 설정 창의 「커서 헤일로 사용」
    /// 체크박스이자 트레이 「커서 헤일로 숨김」 토글이자 좌클릭 대상이라, 값이 <c>false</c> 인
    /// 것만으로는 <b>원래 안 쓰는 기능</b>인지 <b>방금 숨긴 것</b>인지 알 수 없다. 그래서 좌클릭
    /// 숨김 진입 시점에만 갱신되는 <see cref="AppConfig.TrayHideRestoreCursor"/> 기록을 함께 본다
    /// — 그 기록이 있어야만 "숨겨진 것" 으로 센다. 헤일로를 평소 꺼두고 쓰는 사용자에게 취소선이
    /// 상시 표시되던 문제가 이 조건으로 사라진다.
    /// </para>
    /// <para>
    /// 배지는 대칭이 아니다 — 배지에는 "기능 끄기" 설정이 없고
    /// <see cref="AppConfig.UserHidden"/> 이 언제나 명시적 숨김을 뜻하므로 그대로 센다.
    /// </para>
    /// </summary>
    /// <returns>0 = 선 없음, 1 = 단일선, 2 = 이중선.</returns>
    internal static int CountHiddenIndicators(AppConfig config)
    {
        int count = 0;
        if (config.UserHidden) count++;
        if (!config.CursorIndicatorEnabled && config.TrayHideRestoreCursor) count++;
        return count;
    }
}
