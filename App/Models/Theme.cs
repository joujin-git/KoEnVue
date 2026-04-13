using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 테마 프리셋.
/// config.json의 "theme" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Theme>))]
internal enum Theme
{
    /// <summary>사용자 지정 색상 (기본값).</summary>
    [JsonStringEnumMemberName("custom")]
    Custom,

    /// <summary>미니멀 테마.</summary>
    [JsonStringEnumMemberName("minimal")]
    Minimal,

    /// <summary>비비드 테마.</summary>
    [JsonStringEnumMemberName("vivid")]
    Vivid,

    /// <summary>파스텔 테마.</summary>
    [JsonStringEnumMemberName("pastel")]
    Pastel,

    /// <summary>다크 테마.</summary>
    [JsonStringEnumMemberName("dark")]
    Dark,

    /// <summary>시스템 강조색 기반.</summary>
    [JsonStringEnumMemberName("system")]
    System,
}
