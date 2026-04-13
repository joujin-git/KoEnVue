using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// 앱별 필터링 모드.
/// config.json의 "app_filter_mode" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppFilterMode>))]
internal enum AppFilterMode
{
    /// <summary>목록에 있는 앱에서만 숨김 (기본값).</summary>
    [JsonStringEnumMemberName("blacklist")]
    Blacklist,

    /// <summary>목록에 있는 앱에서만 표시.</summary>
    [JsonStringEnumMemberName("whitelist")]
    Whitelist,
}
