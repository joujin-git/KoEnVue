using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 작업 영역의 모서리. default_indicator_position의 anchor로 사용.
/// config.json의 "corner" 키에 snake_case로 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Corner>))]
internal enum Corner
{
    [JsonStringEnumMemberName("top_left")]
    TopLeft,

    [JsonStringEnumMemberName("top_right")]
    TopRight,

    [JsonStringEnumMemberName("bottom_left")]
    BottomLeft,

    [JsonStringEnumMemberName("bottom_right")]
    BottomRight,
}
