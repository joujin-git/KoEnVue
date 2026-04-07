using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// IME 상태 감지 방식.
/// config.json의 "detection_method" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DetectionMethod>))]
internal enum DetectionMethod
{
    /// <summary>자동 3-tier 감지 (기본값).</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>Tier 1: IME 기본 윈도우 SendMessage.</summary>
    [JsonStringEnumMemberName("ime_default")]
    ImeDefault,

    /// <summary>Tier 2: ImmGetContext + ImmGetConversionStatus.</summary>
    [JsonStringEnumMemberName("ime_context")]
    ImeContext,

    /// <summary>Tier 3: GetKeyboardLayout LANGID 비교.</summary>
    [JsonStringEnumMemberName("keyboard_layout")]
    KeyboardLayout,
}
