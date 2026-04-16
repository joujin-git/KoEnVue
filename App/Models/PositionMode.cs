using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 인디케이터 위치 모드.
/// config.json의 "position_mode" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PositionMode>))]
internal enum PositionMode
{
    /// <summary>고정 위치 — 화면 절대 좌표에 고정 (기본값).</summary>
    [JsonStringEnumMemberName("fixed")]
    Fixed,

    /// <summary>창 기준 — 포그라운드 창의 모서리 기준 상대 오프셋.</summary>
    [JsonStringEnumMemberName("window")]
    Window,
}
