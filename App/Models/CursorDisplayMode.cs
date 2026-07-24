using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 커서 헤일로 표시 방식 (PR-31). config.json <c>cursor_display_mode</c>.
/// AlwaysShow 모드에서만 체감. α/soft 키는 Soft·Motion에서 셰이더에 적용.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CursorDisplayMode>))]
internal enum CursorDisplayMode
{
    /// <summary>항상 흐릿하게 — 이동/정지 동일 안개 (기본).</summary>
    [JsonStringEnumMemberName("soft")]
    Soft,

    /// <summary>항상 선명하게 — 이동/정지 동일 Full.</summary>
    [JsonStringEnumMemberName("sharp")]
    Sharp,

    /// <summary>이동 중 흐릿하게 — PR-29/30 settle 히스테리시스.</summary>
    [JsonStringEnumMemberName("motion")]
    Motion,
}
