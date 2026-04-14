namespace KoEnVue.App.Update;

/// <summary>
/// UpdateChecker 가 발견한 새 버전 정보. Tray 메뉴/브라우저 오픈에서 사용.
/// </summary>
internal sealed record UpdateInfo
{
    /// <summary>표시용 버전 문자열 (예: <c>v1.0.1</c> 또는 <c>1.0.1</c>).</summary>
    public required string Version { get; init; }

    /// <summary>릴리스 페이지 URL — ShellExecuteW 로 기본 브라우저에서 오픈.</summary>
    public required string HtmlUrl { get; init; }
}
