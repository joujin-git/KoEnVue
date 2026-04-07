using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 위치 결정 모드.
/// config.json의 "position_mode" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PositionMode>))]
internal enum PositionMode
{
    /// <summary>캐럿 기준 배치 (기본값).</summary>
    [JsonStringEnumMemberName("caret")]
    Caret,

    /// <summary>마우스 커서 기준 배치.</summary>
    [JsonStringEnumMemberName("mouse")]
    Mouse,

    /// <summary>화면 고정 좌표 배치.</summary>
    [JsonStringEnumMemberName("fixed")]
    Fixed,
}
