using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 비한국어 IME 감지 시 동작.
/// config.json의 "non_korean_ime" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NonKoreanImeMode>))]
internal enum NonKoreanImeMode
{
    /// <summary>인디케이터 숨김 (기본값).</summary>
    [JsonStringEnumMemberName("hide")]
    Hide,

    /// <summary>인디케이터 표시 유지.</summary>
    [JsonStringEnumMemberName("show")]
    Show,

    /// <summary>감소된 투명도로 인디케이터 표시.</summary>
    [JsonStringEnumMemberName("dim")]
    Dim,
}
