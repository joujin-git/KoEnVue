using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using KoEnVue.Models;

namespace KoEnVue.Utils;

/// <summary>
/// 경량 로거. System.Diagnostics.Trace + 선택적 파일 출력.
/// 외부 로깅 프레임워크 사용 금지(P1).
/// 로그 메시지는 영문(P2).
/// 비동기 큐: ConcurrentQueue + 전용 drain 스레드로 호출 스레드 비차단.
/// </summary>
internal static class Logger
{
    private static LogLevel _logLevel = LogLevel.Warning;

    // 비동기 파일 로깅
    private static readonly ConcurrentQueue<string> _logQueue = new();
    private static readonly ManualResetEventSlim _drainSignal = new(false);
    private static Thread? _drainThread;
    private static volatile bool _shutdownRequested;
    private static StreamWriter? _fileWriter;
    private static string _filePath = "";
    private static long _maxSizeBytes;

    // P3: 매직 넘버 상수화
    private const long BytesPerMb = 1024L * 1024;
    private const int DrainLoopTimeoutMs = 1000;
    private const int ShutdownJoinTimeoutMs = 3000;

    /// <summary>로그 레벨 설정.</summary>
    public static void SetLevel(LogLevel level) => _logLevel = level;

    /// <summary>
    /// 파일 로깅 초기화. 재초기화 안전 (기존 drain 스레드 종료 후 재시작).
    /// config.LogToFile이 true이면 drain 스레드 시작, false이면 종료.
    /// </summary>
    public static void Initialize(AppConfig config)
    {
        // 기존 drain 스레드 종료
        StopDrainThread();

        if (!config.LogToFile) return;

        _filePath = string.IsNullOrEmpty(config.LogFilePath)
            ? Path.Combine(AppContext.BaseDirectory, "koenvue.log")
            : config.LogFilePath;
        _maxSizeBytes = config.LogMaxSizeMb * BytesPerMb;

        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _fileWriter = new StreamWriter(_filePath, append: true, Encoding.UTF8)
                { AutoFlush = true };
        }
        catch
        {
            _fileWriter = null;
            return;
        }

        // drain 스레드 시작
        _shutdownRequested = false;
        _drainSignal.Reset();
        _drainThread = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "LogDrain",
        };
        _drainThread.Start();
    }

    /// <summary>파일 로깅 종료. 잔여 메시지 flush 후 writer dispose.</summary>
    public static void Shutdown()
    {
        StopDrainThread();
    }

    public static void Debug(string message)
    {
        if (LogLevel.Debug >= _logLevel)
        {
            string formatted = $"[DEBUG] {DateTime.UtcNow:HH:mm:ss.fff} {message}";
            Trace.WriteLine(formatted);
            EnqueueToFile(formatted);
        }
    }

    public static void Info(string message)
    {
        if (LogLevel.Info >= _logLevel)
        {
            string formatted = $"[INFO] {DateTime.UtcNow:HH:mm:ss.fff} {message}";
            Trace.WriteLine(formatted);
            EnqueueToFile(formatted);
        }
    }

    public static void Warning(string message)
    {
        if (LogLevel.Warning >= _logLevel)
        {
            string formatted = $"[WARN] {DateTime.UtcNow:HH:mm:ss.fff} {message}";
            Trace.WriteLine(formatted);
            EnqueueToFile(formatted);
        }
    }

    public static void Error(string message)
    {
        if (LogLevel.Error >= _logLevel)
        {
            string formatted = $"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}";
            Trace.WriteLine(formatted);
            EnqueueToFile(formatted);
        }
    }

    public static void Error(string message, Exception ex)
    {
        if (LogLevel.Error >= _logLevel)
        {
            string formatted = $"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}: {ex.Message}";
            Trace.WriteLine(formatted);
            EnqueueToFile(formatted);
        }
    }

    // ================================================================
    // 비동기 큐 내부
    // ================================================================

    /// <summary>비차단 enqueue. drain 스레드가 없으면 무시.</summary>
    private static void EnqueueToFile(string formatted)
    {
        if (_drainThread is null) return;
        _logQueue.Enqueue(formatted);
        _drainSignal.Set();
    }

    /// <summary>
    /// drain 스레드 메인 루프. 큐에서 메시지를 꺼내 파일에 batch 쓰기.
    /// ManualResetEventSlim 대기로 CPU 소모 없음.
    /// </summary>
    private static void DrainLoop()
    {
        while (!_shutdownRequested)
        {
            _drainSignal.Wait(DrainLoopTimeoutMs); // 회전 체크 + shutdown 감지
            _drainSignal.Reset();
            FlushQueue();
        }

        // 종료 전 잔여 메시지 flush
        FlushQueue();
    }

    /// <summary>큐의 모든 메시지를 파일에 쓰고 회전 체크.</summary>
    private static void FlushQueue()
    {
        if (_fileWriter is null) return;

        try
        {
            while (_logQueue.TryDequeue(out string? message))
            {
                _fileWriter.WriteLine(message);
            }

            // 회전 체크
            var fi = new FileInfo(_filePath);
            if (fi.Exists && fi.Length >= _maxSizeBytes)
            {
                _fileWriter.Dispose();
                string oldPath = _filePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(_filePath, oldPath);
                _fileWriter = new StreamWriter(_filePath, append: false, Encoding.UTF8)
                    { AutoFlush = true };
            }
        }
        catch { /* NF-25: file write/rotate failure — silent */ }
    }

    /// <summary>drain 스레드 종료 + 잔여 flush + writer dispose.</summary>
    private static void StopDrainThread()
    {
        if (_drainThread is not null)
        {
            _shutdownRequested = true;
            _drainSignal.Set();
            _drainThread.Join(ShutdownJoinTimeoutMs);
            _drainThread = null;
        }

        // 잔여 메시지 동기 flush
        FlushQueue();

        _fileWriter?.Dispose();
        _fileWriter = null;
    }
}
