using System.Text.Json.Serialization;

namespace KoEnVue.App.Update;

/// <summary>
/// GitHub Releases API <c>/repos/{owner}/{repo}/releases/latest</c> 응답의 부분 매핑.
/// 실제 응답에는 50+ 필드가 있으나 업데이트 알림에는 다음 4개만 필요하므로 partial DTO.
/// 나머지 필드는 STJ 가 조용히 무시.
/// </summary>
internal sealed record GitHubRelease
{
    /// <summary>릴리스 태그 (예: <c>v1.0.1</c>). 버전 비교의 입력값.</summary>
    public string TagName { get; init; } = "";

    /// <summary>릴리스 페이지 HTML URL — 사용자가 메뉴 클릭 시 브라우저로 열림.</summary>
    public string HtmlUrl { get; init; } = "";

    /// <summary>true 이면 알림 대상에서 제외 (안정 릴리스만 통지).</summary>
    public bool Prerelease { get; init; }

    /// <summary>true 이면 알림 대상에서 제외.</summary>
    public bool Draft { get; init; }
}

/// <summary>
/// AppConfigJsonContext 와 분리한 별도 컨텍스트 — GitHub 응답 DTO 와 앱 설정 DTO 의
/// 직렬화 옵션이 다르고(naming policy 는 우연히 같지만 의미 분리), 추후 한쪽 변경 시
/// 다른 쪽에 영향이 가지 않도록 한다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GitHubRelease))]
internal partial class GitHubReleaseContext : JsonSerializerContext { }
