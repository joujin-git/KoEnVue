using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 캐럿 위치 추적 방식.
/// config.json의 "caret_method" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaretMethod>))]
internal enum CaretMethod
{
    /// <summary>자동 4-tier fallback (기본값).</summary>
    [JsonStringEnumMemberName("auto")]
    Auto,

    /// <summary>Tier 1: GetGUIThreadInfo 캐럿 좌표.</summary>
    [JsonStringEnumMemberName("gui_thread")]
    GuiThread,

    /// <summary>Tier 2: IUIAutomation TextPattern.</summary>
    [JsonStringEnumMemberName("uia")]
    Uia,

    /// <summary>Tier 4: GetCursorPos 마우스 커서 fallback.</summary>
    [JsonStringEnumMemberName("mouse")]
    Mouse,
}
