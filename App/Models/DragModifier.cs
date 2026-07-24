using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 플로팅 배지 드래그 활성 키 — 드래그 개시 게이트.
/// config.json의 "drag_modifier" 키에 대응.
///
/// <para>
/// 좌클릭 동작(2026-07): 짧은 좌클릭 = 일시 숨김(포커스·IME 변경 시 재표시).
/// 마우스 이동이 시스템 드래그 임계(<c>SM_CXDRAG</c>/<c>SM_CYDRAG</c>) 이상일 때만
/// 본 게이트를 검사해 네이티브 드래그(<c>HTCAPTION</c>)로 승격한다.
/// </para>
///
/// <para>
/// None = 임계 초과 시 항상 드래그 승격.
/// Ctrl/Alt/CtrlAlt = 해당 키를 정확히 일치하는 조합으로 누른 상태에서만 승격.
/// 키를 안 누른 채 임계만 넘기면 드래그하지 않고, 버튼 업 시 짧은 클릭과 같이 일시 숨김.
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
    /// <summary>없음 — 임계 초과 시 항상 드래그 (기본값).</summary>
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
