using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 라벨 인디케이터의 선호 배치 방향.
/// config.json의 "caret_placement" 키에 대응.
/// auto-flip 시 이 방향을 1순위로 시도.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaretPlacement>))]
internal enum CaretPlacement
{
    /// <summary>캐럿 왼쪽 (기본값).</summary>
    [JsonStringEnumMemberName("left")]
    Left,

    /// <summary>캐럿 오른쪽.</summary>
    [JsonStringEnumMemberName("right")]
    Right,

    /// <summary>캐럿 위.</summary>
    [JsonStringEnumMemberName("above")]
    Above,

    /// <summary>캐럿 아래.</summary>
    [JsonStringEnumMemberName("below")]
    Below,
}
