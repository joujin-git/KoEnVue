using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 앱별 프로필 매칭 기준.
/// config.json의 "app_profile_match" 키에 대응.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppProfileMatch>))]
internal enum AppProfileMatch
{
    /// <summary>프로세스 이름으로 매칭 (기본값).</summary>
    [JsonStringEnumMemberName("process")]
    Process,

    /// <summary>윈도우 클래스명으로 매칭.</summary>
    [JsonStringEnumMemberName("class")]
    Class,

    /// <summary>윈도우 타이틀 정규식으로 매칭.</summary>
    [JsonStringEnumMemberName("title")]
    Title,
}
