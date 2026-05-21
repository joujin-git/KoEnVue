using System;
using System.IO;
using KoEnVue.Core.Logging;

namespace KoEnVue.App.Config;

/// <summary>
/// asInvoker 전환 (PR-03) 후 config.json / koenvue.log 경로 결정 + log_file_path sanitize.
/// 기본은 exe 디렉토리 (완전 포터블). exe 위치가 user-non-writable 인 경우 (Program Files 등)
/// %LOCALAPPDATA%\KoEnVue\ 로 자동 fallback.
/// <para>
/// asInvoker 채택 시 Admin 토큰을 전제로 했던 보안 표면 (B1 LogFilePath 임의 write,
/// B2 schtasks symlink, B5 elevated notepad) 이 자연 해소된다. 그러나 사용자가
/// <c>config.json:log_file_path</c> 에 시스템 폴더를 지정하는 등 경계 외 write 시도는
/// 여전히 가능하므로 <see cref="SanitizeLogPath"/> 가 허용 루트 하위인지 검증한다.
/// </para>
/// </summary>
internal static class PortablePath
{
    /// <summary>fallback 루트 — <c>%LOCALAPPDATA%\KoEnVue\</c>. 사용자별 격리, 권한 불요.</summary>
    public static string FallbackRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KoEnVue");

    /// <summary>exe 가 놓인 디렉토리. <see cref="AppContext.BaseDirectory"/> 별칭.</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    // exe 디렉토리 writable 여부 캐시 — write probe (Create + Delete) 비용을 한 번만 지불.
    // 부팅 후 ACL 이 바뀌어도 다음 실행에서 재평가되므로 캐시는 프로세스 수명 한정.
    private static bool? _baseDirectoryWritable;

    /// <summary>
    /// 활성 <c>config.json</c> 경로. 결정 우선순위:
    /// <list type="number">
    /// <item><c>BaseDirectory\config.json</c> 가 이미 있으면 그 경로 (v0.9.x → v0.10.x 마이그레이션).</item>
    /// <item>BaseDirectory 가 writable 이면 <c>BaseDirectory\config.json</c>.</item>
    /// <item>아니면 <c>FallbackRoot\config.json</c> (디렉토리 자동 생성).</item>
    /// </list>
    /// </summary>
    public static string ResolveConfigPath() => ResolveFile(DefaultConfig.ConfigFileName);

    /// <summary>
    /// 기본 <c>koenvue.log</c> 경로. <see cref="ResolveConfigPath"/> 와 동일 결정 로직.
    /// 사용자가 <c>config.json:log_file_path</c> 를 비워두거나 sanitize 에 실패한 경우 폴백.
    /// </summary>
    public static string ResolveLogPath() => ResolveFile("koenvue.log");

    /// <summary>
    /// 사용자가 지정한 <c>log_file_path</c> 를 검증해 효과적 경로를 반환.
    /// 허용된 루트 (<see cref="BaseDirectory"/> / <see cref="FallbackRoot"/>) 하위면 그대로,
    /// 위반이거나 정규화 실패면 <see cref="ResolveLogPath"/> 로 폴백.
    /// <para>
    /// <paramref name="rejectionReason"/> 은 reject 사유를 영문으로 돌려준다 — null 이면 정상.
    /// Logger 가 초기화되기 전에 호출되므로 본 메서드 자체는 로그를 남기지 않고,
    /// 호출자가 <see cref="Logger.Initialize"/> 직후 reason 을 Warning 으로 reissue 해야
    /// 사용자가 koenvue.log 에서 발견할 수 있다 (시나리오 E 가시성).
    /// </para>
    /// </summary>
    public static string SanitizeLogPath(string? requested, out string? rejectionReason)
    {
        rejectionReason = null;
        if (string.IsNullOrWhiteSpace(requested))
            return ResolveLogPath();

        string full;
        try
        {
            full = Path.GetFullPath(requested);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or NotSupportedException or System.Security.SecurityException)
        {
            rejectionReason = $"log_file_path '{requested}' could not be normalized ({ex.GetType().Name})";
            return ResolveLogPath();
        }

        if (IsUnderAllowedRoot(full)) return full;

        rejectionReason =
            $"log_file_path '{requested}' is outside allowed roots (BaseDirectory or %LOCALAPPDATA%\\KoEnVue)";
        return ResolveLogPath();
    }

    /// <summary>
    /// 경로가 BaseDirectory 또는 FallbackRoot 하위인지 검사. 두 루트 모두 user-writable 보장.
    /// 시스템 폴더나 다른 사용자 프로필로의 write 표면을 차단한다.
    /// </summary>
    public static bool IsUnderAllowedRoot(string fullPath)
        => IsUnder(fullPath, BaseDirectory) || IsUnder(fullPath, FallbackRoot);

    private static bool IsUnder(string fullPath, string root)
    {
        try
        {
            string normRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or NotSupportedException or System.Security.SecurityException)
        {
            _ = ex;
            return false;
        }
    }

    private static string ResolveFile(string filename)
    {
        string baseTarget = Path.Combine(BaseDirectory, filename);
        if (File.Exists(baseTarget)) return baseTarget;           // v0.9.x 마이그레이션 우선
        if (IsBaseDirectoryWritable()) return baseTarget;         // 포터블 정상 경로

        // 시스템 위치 fallback — 디렉토리 생성 보장. 실패해도 경로 자체는 반환해
        // 호출자 (Save / Logger.Initialize) 에서 자연스럽게 IO 오류 경로를 타게 한다.
        try
        {
            if (!Directory.Exists(FallbackRoot))
                Directory.CreateDirectory(FallbackRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
        return Path.Combine(FallbackRoot, filename);
    }

    private static bool IsBaseDirectoryWritable()
    {
        if (_baseDirectoryWritable.HasValue) return _baseDirectoryWritable.Value;
        try
        {
            string probe = Path.Combine(BaseDirectory, $".koenvue-writeprobe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            _baseDirectoryWritable = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or DirectoryNotFoundException)
        {
            _baseDirectoryWritable = false;
            _ = ex;
        }
        return _baseDirectoryWritable.Value;
    }
}
