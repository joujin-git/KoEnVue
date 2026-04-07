using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// Fixed 위치 모드 대상 모니터.
/// config.json의 "fixed_position.monitor" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixedMonitor>))]
internal enum FixedMonitor
{
    /// <summary>주 모니터 (기본값).</summary>
    [JsonStringEnumMemberName("primary")]
    Primary,

    /// <summary>마우스 커서가 위치한 모니터.</summary>
    [JsonStringEnumMemberName("mouse")]
    Mouse,

    /// <summary>활성(포그라운드) 윈도우가 위치한 모니터.</summary>
    [JsonStringEnumMemberName("active")]
    Active,
}
