using System.Diagnostics;

namespace KoEnVue.Utils;

/// <summary>
/// 경량 로거. System.Diagnostics.Trace 기반.
/// 외부 로깅 프레임워크 사용 금지(P1).
/// 로그 메시지는 영문(P2).
/// </summary>
internal static class Logger
{
    private static string _logLevel = "WARNING";

    /// <summary>로그 레벨 설정. "DEBUG" | "INFO" | "WARNING" | "ERROR"</summary>
    public static void SetLevel(string level) => _logLevel = level.ToUpperInvariant();

    private static int LevelToInt(string level) => level switch
    {
        "DEBUG" => 0,
        "INFO" => 1,
        "WARNING" => 2,
        "ERROR" => 3,
        _ => 2
    };

    private static bool ShouldLog(string level) => LevelToInt(level) >= LevelToInt(_logLevel);

    public static void Debug(string message)
    {
        if (ShouldLog("DEBUG"))
            Trace.WriteLine($"[DEBUG] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Info(string message)
    {
        if (ShouldLog("INFO"))
            Trace.WriteLine($"[INFO] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Warning(string message)
    {
        if (ShouldLog("WARNING"))
            Trace.WriteLine($"[WARN] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message)
    {
        if (ShouldLog("ERROR"))
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message, Exception ex)
    {
        if (ShouldLog("ERROR"))
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}: {ex.Message}");
    }
}
