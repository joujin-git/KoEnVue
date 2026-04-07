using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 로그 출력 레벨.
/// config.json의 "log_level" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
internal enum LogLevel
{
    [JsonStringEnumMemberName("DEBUG")]
    Debug,

    [JsonStringEnumMemberName("INFO")]
    Info,

    [JsonStringEnumMemberName("WARNING")]
    Warning,

    [JsonStringEnumMemberName("ERROR")]
    Error,
}
