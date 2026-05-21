using System.Text.Json;
using System.Text.Json.Serialization;
using KoEnVue.Core.Logging;

namespace KoEnVue.App.Logging;

/// <summary>
/// <see cref="LogLevel"/> 의 JSON 매핑을 App 레이어에 격리하는 STJ converter.
/// Core enum 은 STJ 의존을 두지 않고 plain enum 으로 유지 (PR-09 E5) — Core lift-out 시
/// System.Text.Json 의존도 함께 떨어지게 한다.
///
/// <para>
/// 표현: <c>"DEBUG"/"INFO"/"WARNING"/"ERROR"</c> (대문자). v0.9.x config.json 호환을 위해 case-insensitive
/// 읽기. 정의 외 값은 <see cref="JsonException"/> — `JsonSettingsManager.Load` 의 catch 가 잡아
/// 전체 defaults 폴백.
/// </para>
/// </summary>
internal sealed class LogLevelJsonConverter : JsonConverter<LogLevel>
{
    public override LogLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        return value?.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => throw new JsonException($"Unknown log_level value: '{value}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, LogLevel value, JsonSerializerOptions options)
    {
        string text = value switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => "INFO",
        };
        writer.WriteStringValue(text);
    }
}
