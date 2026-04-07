using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// Fixed 위치 모드 앵커 기준점.
/// config.json의 "fixed_position.anchor" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FixedAnchor>))]
internal enum FixedAnchor
{
    /// <summary>가상 데스크톱 절대 좌표.</summary>
    [JsonStringEnumMemberName("absolute")]
    Absolute,

    /// <summary>모니터 좌상단 기준.</summary>
    [JsonStringEnumMemberName("top_left")]
    TopLeft,

    /// <summary>모니터 우상단 기준 (기본값).</summary>
    [JsonStringEnumMemberName("top_right")]
    TopRight,

    /// <summary>모니터 좌하단 기준.</summary>
    [JsonStringEnumMemberName("bottom_left")]
    BottomLeft,

    /// <summary>모니터 우하단 기준.</summary>
    [JsonStringEnumMemberName("bottom_right")]
    BottomRight,

    /// <summary>모니터 중앙 기준.</summary>
    [JsonStringEnumMemberName("center")]
    Center,
}
