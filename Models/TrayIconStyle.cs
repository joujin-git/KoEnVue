using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 트레이 아이콘 비주얼 스타일.
/// config.json의 "tray_icon_style" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrayIconStyle>))]
internal enum TrayIconStyle
{
    /// <summary>캐럿+점 아이콘, IME 상태별 배경색 변경 (기본값).</summary>
    [JsonStringEnumMemberName("caret_dot")]
    CaretDot,

    /// <summary>고정 단색 아이콘 (상태 미표시).</summary>
    [JsonStringEnumMemberName("static")]
    Static,
}
