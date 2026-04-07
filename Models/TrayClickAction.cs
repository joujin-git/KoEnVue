using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 트레이 아이콘 좌클릭 동작.
/// config.json의 "tray_click_action" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrayClickAction>))]
internal enum TrayClickAction
{
    /// <summary>인디케이터 표시/숨기기 토글 (기본값).</summary>
    [JsonStringEnumMemberName("toggle")]
    Toggle,

    /// <summary>설정 파일 열기.</summary>
    [JsonStringEnumMemberName("settings")]
    Settings,

    /// <summary>동작 없음.</summary>
    [JsonStringEnumMemberName("none")]
    None,
}
