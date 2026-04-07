using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 다중 모니터 인디케이터 추적 모드.
/// config.json의 "multi_monitor" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MultiMonitorMode>))]
internal enum MultiMonitorMode
{
    /// <summary>캐럿이 있는 모니터에 표시 (기본값).</summary>
    [JsonStringEnumMemberName("follow_caret")]
    FollowCaret,

    /// <summary>마우스가 있는 모니터에 표시.</summary>
    [JsonStringEnumMemberName("follow_mouse")]
    FollowMouse,

    /// <summary>주 모니터에만 표시.</summary>
    [JsonStringEnumMemberName("primary_only")]
    PrimaryOnly,
}
