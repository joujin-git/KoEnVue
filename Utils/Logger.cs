using System.Diagnostics;
using KoEnVue.Models;

namespace KoEnVue.Utils;

/// <summary>
/// 경량 로거. System.Diagnostics.Trace 기반.
/// 외부 로깅 프레임워크 사용 금지(P1).
/// 로그 메시지는 영문(P2).
/// </summary>
internal static class Logger
{
    private static LogLevel _logLevel = LogLevel.Warning;

    /// <summary>로그 레벨 설정.</summary>
    public static void SetLevel(LogLevel level) => _logLevel = level;

    public static void Debug(string message)
    {
        if (LogLevel.Debug >= _logLevel)
            Trace.WriteLine($"[DEBUG] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Info(string message)
    {
        if (LogLevel.Info >= _logLevel)
            Trace.WriteLine($"[INFO] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Warning(string message)
    {
        if (LogLevel.Warning >= _logLevel)
            Trace.WriteLine($"[WARN] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message)
    {
        if (LogLevel.Error >= _logLevel)
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message, Exception ex)
    {
        if (LogLevel.Error >= _logLevel)
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}: {ex.Message}");
    }
}
