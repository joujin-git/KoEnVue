using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 라벨 텍스트 굵기.
/// config.json의 "font_weight" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FontWeight>))]
internal enum FontWeight
{
    /// <summary>보통 굵기 (FW_NORMAL = 400).</summary>
    [JsonStringEnumMemberName("normal")]
    Normal,

    /// <summary>굵게 (FW_BOLD = 700). 기본값.</summary>
    [JsonStringEnumMemberName("bold")]
    Bold,
}
