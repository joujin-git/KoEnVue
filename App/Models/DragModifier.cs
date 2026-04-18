using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 인디케이터 드래그 활성 키 — 드래그 개시 게이트.
/// config.json의 "drag_modifier" 키에 대응.
///
/// <para>
/// None = 기본 동작 (모든 좌클릭이 드래그 개시).
/// Ctrl/Alt/CtrlAlt = 해당 키를 정확히 일치하는 조합으로 누른 상태에서만 드래그 개시.
/// 키를 안 누른 상태의 클릭은 오버레이가 소비하며 반응 없음 (아래 창으로 전달되지 않음).
/// </para>
///
/// <para>
/// 크로스 프로세스 클릭 투과는 <c>WS_EX_TRANSPARENT</c> 확장 스타일이 필요하지만,
/// 이를 동적으로 토글하려면 WM_TIMER 폴러 또는 WH_KEYBOARD_LL 훅이 필요해 런타임 비용·
/// NativeAOT 리스크 대비 이득이 작다고 판단, 본 옵션은 "드래그 개시 게이트" 용도로 한정.
/// </para>
///
/// <para>
/// Shift는 드래그 중 축 고정 용도로 이미 사용 중이라 선택지에서 제외
/// (<see cref="KoEnVue.Core.Windowing.LayeredOverlayBase.HandleMoving"/> 참조).
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DragModifier>))]
internal enum DragModifier
{
    /// <summary>없음 — 기존 동작 (기본값).</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Ctrl 누른 채 드래그.</summary>
    [JsonStringEnumMemberName("ctrl")]
    Ctrl,

    /// <summary>Alt 누른 채 드래그.</summary>
    [JsonStringEnumMemberName("alt")]
    Alt,

    /// <summary>Ctrl + Alt 누른 채 드래그.</summary>
    [JsonStringEnumMemberName("ctrl_alt")]
    CtrlAlt,
}
