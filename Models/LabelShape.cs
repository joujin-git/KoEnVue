using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 라벨 인디케이터 외형.
/// config.json의 "label_shape" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabelShape>))]
internal enum LabelShape
{
    /// <summary>둥근 모서리 사각형 (기본값). radius = LabelBorderRadius.</summary>
    [JsonStringEnumMemberName("rounded_rect")]
    RoundedRect,

    /// <summary>원형.</summary>
    [JsonStringEnumMemberName("circle")]
    Circle,

    /// <summary>알약형 (radius = height/2).</summary>
    [JsonStringEnumMemberName("pill")]
    Pill,
}
