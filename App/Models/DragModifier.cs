using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 인디케이터 드래그 활성 키.
/// config.json의 "drag_modifier" 키에 대응.
///
/// <para>
/// None = 기존 동작 (항상 드래그 가능, 모든 마우스 이벤트를 오버레이가 소비).
/// Ctrl/Alt/CtrlAlt = 해당 키 누른 상태에서만 드래그 가능. 키를 놓으면 클릭/휠이 아래 창으로 투과
/// (WM_NCHITTEST → HTTRANSPARENT).
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
