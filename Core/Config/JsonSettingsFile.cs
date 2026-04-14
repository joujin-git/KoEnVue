using System.Text;

namespace KoEnVue.Core.Config;

/// <summary>
/// Core-level low-level filesystem helpers for JSON settings files.
/// Split from <see cref="JsonSettingsManager{T}"/> so path I/O primitives
/// (BOM-stripped read, directory-aware write, mtime probe) are in one place
/// and reusable by any T-typed settings manager.
/// </summary>
internal static class JsonSettingsFile
{
    /// <summary>
    /// UTF-8 로 파일 전체를 읽고, BOM 이 붙어 있으면 제거해 반환.
    /// </summary>
    public static string ReadAllTextStripBom(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        if (json.Length > 0 && json[0] == '\uFEFF')
            json = json[1..];
        return json;
    }

    /// <summary>
    /// 상위 디렉터리가 없으면 생성한 뒤 UTF-8 로 전체를 덮어쓴다.
    /// </summary>
    public static void WriteAllText(string path, string json)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// 파일 mtime(UTC). 존재하지 않는 파일은 <see cref="File.GetLastWriteTimeUtc"/> 의
    /// 1601-01-01 센티널을 그대로 반환하므로, 호출자는 반드시 먼저
    /// <see cref="File.Exists"/> 로 가드할 것.
    /// </summary>
    public static DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}
