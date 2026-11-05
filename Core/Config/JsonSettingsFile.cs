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
    /// <para>
    /// 원자적 저장: 먼저 <c>path + ".tmp"</c> 에 전체를 기록한 뒤 <see cref="File.Move(string,string,bool)"/>
    /// 로 대상 경로에 덮어쓴다. Windows 동일 볼륨에서 MoveFileExW(MOVEFILE_REPLACE_EXISTING) 는
    /// 원자적 rename 을 보장하므로 쓰기 중 전원 차단/크래시가 발생해도 원본 파일 또는 새 파일 중
    /// 하나는 항상 온전한 상태로 남는다 (truncate 된 반쪽 파일이 생기지 않음).
    /// </para>
    /// </summary>
    public static void WriteAllText(string path, string json)
        => CommitTemp(WriteTemp(path, json), path);

    /// <summary>
    /// <see cref="WriteAllText"/> 의 앞 절반 — <c>path + ".tmp"</c> 에 전체를 쓰고 그 경로를 돌려준다. 대상 파일은
    /// 아직 그대로다. 호출자는 교체(<see cref="CommitTemp"/>) 직전에 디스크를 다시 확인할 수 있다.
    /// </summary>
    public static string WriteTemp(string path, string json)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json, Encoding.UTF8);
        return tmpPath;
    }

    /// <summary><see cref="WriteTemp"/> 가 만든 임시 파일로 대상을 원자적으로 교체한다. rename 은 mtime 을 보존한다.</summary>
    public static void CommitTemp(string tmpPath, string path) => File.Move(tmpPath, path, overwrite: true);

    /// <summary>
    /// 저장 실패 뒤 남은 <c>path + ".tmp"</c> 를 지운다. 지우지 못해도(잠김·권한) 다음 저장이 덮어쓰므로 무시한다.
    /// </summary>
    public static void TryDeleteTemp(string path)
    {
        try
        {
            File.Delete(path + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;   // 다음 WriteTemp 가 덮어쓴다 — 복구할 것이 없다
        }
    }

    /// <summary>
    /// 파일 mtime(UTC). 존재하지 않는 파일은 <see cref="File.GetLastWriteTimeUtc"/> 의
    /// 1601-01-01 센티널을 그대로 반환하므로, 호출자는 반드시 먼저
    /// <see cref="File.Exists"/> 로 가드할 것.
    /// </summary>
    public static DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <summary>
    /// mtime + 길이. 존재하지 않으면 <see cref="FileStamp.None"/>. 길이를 함께 보는 이유는 mtime 해상도다 —
    /// FAT32(포터블 USB) 는 2초 단위라 같은 칸에 떨어진 두 저장의 mtime 이 같아진다(bug-hunt 2026-11-05).
    /// </summary>
    public static FileStamp GetStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : FileStamp.None;
    }
}

/// <summary>
/// 설정 파일의 "버전" 표식 — mtime 과 길이. 둘 중 하나라도 다르면 내용이 바뀐 것으로 본다. mtime 만으로는
/// FAT32 처럼 해상도가 거친 볼륨에서 같은 2초 칸의 외부 편집을 놓쳐, 감지도 3-way 병합도 건너뛰고 앱 저장이
/// 그 편집을 덮었다. 길이까지 같은 편집(같은 칸·같은 길이)은 남는 한계다.
/// </summary>
internal readonly record struct FileStamp(DateTime MtimeUtc, long Length)
{
    /// <summary>"아직 모름/파일 없음" — 어떤 실제 파일 표식과도 같지 않다.</summary>
    public static readonly FileStamp None = new(DateTime.MinValue, -1);
}
