using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 라벨 내부 콘텐츠 스타일.
/// config.json의 "label_style" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabelStyle>))]
internal enum LabelStyle
{
    /// <summary>텍스트 표시 (한/En/EN). 기본값.</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>색상 점 표시.</summary>
    [JsonStringEnumMemberName("dot")]
    Dot,

    /// <summary>아이콘 표시 (ㄱ/A).</summary>
    [JsonStringEnumMemberName("icon")]
    Icon,
}
