using System.Text.Json;
using KoEnVue.App.Config;
using KoEnVue.Core.Http;
using KoEnVue.Core.Logging;

namespace KoEnVue.App.Update;

/// <summary>
/// 백그라운드 스레드에서 GitHub Releases API 를 조회해 새 버전을 감지한다.
/// 호출자(Program.cs)는 콜백 람다 1개만 넘기면 되며, 콜백은 발견 시 1회만 호출.
/// 미발견·네트워크 실패·파싱 실패는 모두 silent log → 사용자에게 노출되지 않는다.
/// <para>
/// 본 클래스는 throw 하지 않는다. 실패는 모두 <see cref="Logger.Debug"/> 로만 기록.
/// </para>
/// </summary>
internal static class UpdateChecker
{
    private const string GitHubApiHost = "api.github.com";
    // GitHub API 는 User-Agent 헤더가 누락되면 403 응답.
    private const string UserAgent = "KoEnVue-UpdateChecker";
    // GitHub API 는 vnd.github+json 미디어 타입 권장.
    private const string AcceptHeader = "Accept: application/vnd.github+json\r\n";

    /// <summary>
    /// 백그라운드 스레드 1개를 띄워 한 번만 GitHub Releases API 를 조회한다.
    /// 새 버전이 있으면 <paramref name="onUpdateFound"/> 호출. 호출 위치는 백그라운드 스레드이므로
    /// 콜백은 메시지 큐에 마샬링하는 등 자체적으로 스레드 안전을 책임진다.
    /// </summary>
    /// <param name="currentVersion">현재 버전 문자열 (예: <c>1.0.0</c>). <see cref="DefaultConfig.AppVersion"/>.</param>
    /// <param name="repoOwner">GitHub 사용자/조직 (예: <c>joujin-git</c>).</param>
    /// <param name="repoName">레포 이름 (예: <c>KoEnVue</c>).</param>
    /// <param name="onUpdateFound">새 버전 발견 시 1회 호출되는 콜백.</param>
    public static void CheckInBackground(string currentVersion, string repoOwner, string repoName, Action<UpdateInfo> onUpdateFound)
    {
        var thread = new Thread(() => RunCheck(currentVersion, repoOwner, repoName, onUpdateFound))
        {
            IsBackground = true,
            Name = "UpdateChecker",
        };
        thread.Start();
    }

    private static void RunCheck(string currentVersion, string repoOwner, string repoName, Action<UpdateInfo> onUpdateFound)
    {
        try
        {
            string path = $"/repos/{repoOwner}/{repoName}/releases/latest";
            Logger.Debug($"UpdateChecker: GET https://{GitHubApiHost}{path}");

            string? body = HttpClientLite.GetString(UserAgent, GitHubApiHost, path, AcceptHeader);
            if (body is null)
            {
                Logger.Debug("UpdateChecker: HTTP fetch failed (null body)");
                return;
            }

            GitHubRelease? release = JsonSerializer.Deserialize(body, GitHubReleaseContext.Default.GitHubRelease);
            if (release is null)
            {
                Logger.Debug("UpdateChecker: deserialize returned null");
                return;
            }

            if (release.Draft || release.Prerelease)
            {
                Logger.Debug($"UpdateChecker: skipping draft={release.Draft} prerelease={release.Prerelease} tag={release.TagName}");
                return;
            }

            if (string.IsNullOrEmpty(release.TagName) || string.IsNullOrEmpty(release.HtmlUrl))
            {
                Logger.Debug("UpdateChecker: tag_name or html_url missing");
                return;
            }

            if (!IsNewer(currentVersion, release.TagName))
            {
                Logger.Debug($"UpdateChecker: current={currentVersion} latest={release.TagName} (no update)");
                return;
            }

            Logger.Info($"UpdateChecker: new version available — current={currentVersion} latest={release.TagName}");
            onUpdateFound(new UpdateInfo
            {
                Version = release.TagName,
                HtmlUrl = release.HtmlUrl,
            });
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            // 정책 항목 1(타입 좁히기): JSON 파싱/소스젠/매핑 오류만 흡수.
            // 로직 버그(NullRef 등)는 propagate 시켜 표면화한다.
            Logger.Debug($"UpdateChecker: parse error — {ex.Message}");
        }
    }

    /// <summary>
    /// 두 버전 문자열을 <see cref="System.Version"/> 으로 파싱해 비교.
    /// 양쪽 모두 앞에 <c>v</c>/<c>V</c> 가 있으면 떼고, semver prerelease 접미사 (<c>-beta.1</c> 등)도 떼고 비교.
    /// 파싱 실패 시 false 반환 (= 업데이트 알림 표시 안 함).
    /// </summary>
    private static bool IsNewer(string current, string latest)
    {
        if (!Version.TryParse(NormalizeVersion(current), out var currentV)) return false;
        if (!Version.TryParse(NormalizeVersion(latest), out var latestV)) return false;
        return latestV > currentV;
    }

    private static string NormalizeVersion(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        ReadOnlySpan<char> span = s.AsSpan();
        if (span[0] == 'v' || span[0] == 'V') span = span[1..];

        int dashIndex = span.IndexOf('-');
        if (dashIndex >= 0) span = span[..dashIndex];

        int plusIndex = span.IndexOf('+');
        if (plusIndex >= 0) span = span[..plusIndex];

        return span.ToString();
    }
}
