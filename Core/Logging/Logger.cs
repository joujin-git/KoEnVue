using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace KoEnVue.Core.Logging;

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

    // 모든 로그 라인의 타임스탬프 포맷 — 정상 로깅(Write)과 self-catch breadcrumb 가 공유(P3).
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss.fff";

    // 큐 상한 — 회전 실패 등으로 _fileWriter=null 상태가 지속되면 FlushQueue 가 early-return
    // 하여 큐가 무제한 성장한다. 상한 초과 시 최고령 메시지부터 드롭해 최근 로그 우선 보존.
    private const int MaxQueueSize = 10_000;
    private static int _droppedCount;

    // Pre-Initialize 버퍼 — Initialize 호출 전(Settings.Load 단계의 JsonSettingsManager 등)에
    // 발화한 Logger.X 메시지가 Trace 외에는 사라지던 한계 (PR-06 Tier-3 ④ 에서 확인) 해소.
    // Initialize 가 정상 완료되면 본 버퍼의 내용을 _logQueue 로 옮긴 뒤 한 번 drain. 버퍼 상한은
    // 같은 MaxQueueSize — Initialize 전 부트 단계의 로그가 상한을 넘기는 시나리오는 비정상이므로
    // 동일 상한으로 충분.
    private static readonly ConcurrentQueue<string> _preInitBuffer = new();
    private static int _preInitDroppedCount;

    /// <summary>로그 레벨 설정.</summary>
    public static void SetLevel(LogLevel level) => _logLevel = level;

    /// <summary>
    /// 파일 로깅 초기화. 재초기화 안전 (기존 drain 스레드 종료 후 재시작).
    /// <paramref name="enabled"/>이 true이면 drain 스레드 시작, false이면 종료.
    /// <paramref name="logFilePath"/>가 null 또는 빈 문자열이면
    /// <c>AppContext.BaseDirectory\koenvue.log</c>로 폴백.
    /// </summary>
    public static void Initialize(bool enabled, string? logFilePath, int maxSizeMb)
    {
        // 기존 drain 스레드 종료
        StopDrainThread();

        if (!enabled) return;

        _filePath = string.IsNullOrEmpty(logFilePath)
            ? Path.Combine(AppContext.BaseDirectory, "koenvue.log")
            : logFilePath;
        _maxSizeBytes = maxSizeMb * BytesPerMb;

        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _fileWriter = new StreamWriter(_filePath, append: true, Encoding.UTF8)
                { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 파일 로깅 초기화 실패가 앱 부팅을 죽이면 안 되므로 흡수. 다만 Trace.WriteLine으로
            // 한 번은 흔적을 남겨야 디버거에서 "왜 파일 로깅이 안 찍히는가"를 답할 수 있음.
            // 로직 버그(NullRef 등)는 전파해 부팅 초기에 드러냄.
            Trace.WriteLine($"Logger file init failed (falling back to Trace only): {ex.Message}");
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

        // Pre-Initialize 버퍼를 큐로 옮긴 뒤 drain 신호 — 부트 초반의 Logger.X 메시지가
        // koenvue.log 에 정상 기록되도록 한다.
        FlushPreInitBuffer();
    }

    private static void FlushPreInitBuffer()
    {
        int dropped = Interlocked.Exchange(ref _preInitDroppedCount, 0);
        if (dropped > 0)
        {
            _logQueue.Enqueue(
                FormatBreadcrumb($"Logger pre-init buffer dropped {dropped} oldest messages"));
        }

        while (_preInitBuffer.TryDequeue(out string? message))
            _logQueue.Enqueue(message);

        if (!_logQueue.IsEmpty)
            _drainSignal.Set();
    }

    /// <summary>파일 로깅 종료. 잔여 메시지 flush 후 writer dispose.</summary>
    public static void Shutdown()
    {
        StopDrainThread();
    }

    public static void Debug(string message) => Write(LogLevel.Debug, "[DEBUG]", message);
    public static void Info(string message) => Write(LogLevel.Info, "[INFO]", message);
    public static void Warning(string message) => Write(LogLevel.Warning, "[WARN]", message);
    public static void Error(string message) => Write(LogLevel.Error, "[ERROR]", message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, "[ERROR]", $"{message}: {ex.Message}");

    private static void Write(LogLevel level, string prefix, string message)
    {
        if (level < _logLevel) return;
        string formatted = $"{prefix} {DateTime.Now.ToString(TimestampFormat)} {message}";
        Trace.WriteLine(formatted);
        EnqueueToFile(formatted);
    }

    /// <summary>
    /// self-catch breadcrumb 한 줄을 포맷한다. Logger 의 정상 큐 경로를 우회하는 진단 메시지
    /// (pre-init 버퍼 드롭 / 큐 상한 드롭 / drain join 타임아웃)가 공유하는 <c>[WARN] {ts} {msg}</c>
    /// 접두를 단일화. 순수 문자열 빌더 — <see cref="Logger"/> 재호출 없음(드레인 재귀 금지, NF-25).
    /// </summary>
    private static string FormatBreadcrumb(string message)
        => $"[WARN] {DateTime.Now.ToString(TimestampFormat)} {message}";

    // ================================================================
    // 비동기 큐 내부
    // ================================================================

    /// <summary>
    /// 비차단 enqueue. drain 스레드가 없으면 <see cref="_preInitBuffer"/> 로 우회 — Initialize 가
    /// 호출되면 본 버퍼를 큐로 옮겨 한꺼번에 기록한다. <see cref="MaxQueueSize"/> 초과 시 최고령부터
    /// 드롭하여 메모리 무제한 증가를 차단.
    /// </summary>
    private static void EnqueueToFile(string formatted)
    {
        if (_drainThread is null)
        {
            // Pre-Initialize 단계: 버퍼에 누적. Initialize 가 끝나면 FlushPreInitBuffer 가 옮겨 적는다.
            while (_preInitBuffer.Count >= MaxQueueSize && _preInitBuffer.TryDequeue(out _))
                Interlocked.Increment(ref _preInitDroppedCount);
            _preInitBuffer.Enqueue(formatted);
            return;
        }

        // Count 비교는 대략치이지만 상한 근처에서만 오버슈트 가능 — 허용.
        while (_logQueue.Count >= MaxQueueSize && _logQueue.TryDequeue(out _))
            Interlocked.Increment(ref _droppedCount);

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
        // 회전/초기화 실패로 writer 가 없으면 재생성을 1회 시도 — .old 잠금이나 AV 핸들 점유 같은
        // 일시적 실패가 풀리면 로깅이 복구된다(세션 잔여 로그 영구 무음 + 드롭 요약 미기록 방지).
        // 여전히 실패면 이번 사이클은 조용히 포기하고 다음 사이클에 재시도.
        if (_fileWriter is null && !TryReopenWriter()) return;

        try
        {
            // 큐 상한 초과로 드롭이 있었으면 복귀 직후 1회 요약 기록.
            int dropped = Interlocked.Exchange(ref _droppedCount, 0);
            if (dropped > 0)
            {
                _fileWriter.WriteLine(
                    FormatBreadcrumb($"Logger dropped {dropped} oldest messages (queue cap {MaxQueueSize})"));
            }

            while (_logQueue.TryDequeue(out string? message))
            {
                _fileWriter.WriteLine(message);
            }

            // 회전 체크
            var fi = new FileInfo(_filePath);
            if (fi.Exists && fi.Length >= _maxSizeBytes)
            {
                // 회전 실패 시 disposed writer 참조가 남아 다음 WriteLine에서
                // ObjectDisposedException(= 필터 밖)이 터져 드레인 스레드가 죽는 문제 방어.
                // 필드를 먼저 null 로 교체한 뒤 로컬로 Dispose 하면, 이후 File.Move /
                // new StreamWriter 가 IOException/UnauthorizedAccessException 으로 실패해도
                // _fileWriter = null 상태가 유지되어 다음 FlushQueue 진입 시 가드가 안전 처리.
                StreamWriter old = _fileWriter;
                _fileWriter = null;
                old.Dispose();

                string oldPath = _filePath + ".old";
                File.Delete(oldPath);
                File.Move(_filePath, oldPath);
                _fileWriter = new StreamWriter(_filePath, append: false, Encoding.UTF8)
                    { AutoFlush = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // NF-25: file write/rotate failure — Logger 재귀 금지(드레인 자체)라 Trace 로만 흔적 1회.
            // 회전 중이었다면 _fileWriter 는 이미 null(교체)이라 다음 FlushQueue 가 TryReopenWriter 로
            // 복구를 재시도한다. 로직 버그(NullRef 등)는 전파되어 드레인 스레드 크래시로 드러남.
            Trace.WriteLine($"Logger flush/rotate failed (will retry next cycle): {ex.Message}");
        }
    }

    /// <summary>
    /// 회전/초기화 실패로 null 이 된 <see cref="_fileWriter"/> 를 append 모드로 재생성 시도.
    /// 성공 시 true, 실패 시 <see cref="_fileWriter"/> 를 null 로 유지하고 false (다음 사이클 재시도).
    /// <see cref="_filePath"/> 가 비어 있으면(미초기화) 시도하지 않는다.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_fileWriter))]
    private static bool TryReopenWriter()
    {
        if (string.IsNullOrEmpty(_filePath)) return false;
        try
        {
            _fileWriter = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _fileWriter = null;
            return false;
        }
    }

    /// <summary>drain 스레드 종료 + 잔여 flush + writer dispose.</summary>
    private static void StopDrainThread()
    {
        if (_drainThread is not null)
        {
            _shutdownRequested = true;
            _drainSignal.Set();
            bool joined = _drainThread.Join(ShutdownJoinTimeoutMs);
            _drainThread = null;

            // Join 타임아웃 흔적: Logger 큐 경로는 이미 닫혔으므로 _fileWriter에 직접 기록.
            // 정상적으로는 Join이 즉시 복귀하지만, 타임아웃은 프로세스 종료 지연을 유발하므로
            // 흔적이 중요하다. Console.Error 병행 폴백.
            if (!joined)
            {
                try
                {
                    _fileWriter?.WriteLine(
                        FormatBreadcrumb($"Logger drain thread join timed out after {ShutdownJoinTimeoutMs}ms"));
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _ = ex;
                }
                Console.Error.WriteLine(
                    FormatBreadcrumb($"Logger drain thread join timed out after {ShutdownJoinTimeoutMs}ms"));
            }
        }

        // 잔여 메시지 동기 flush
        FlushQueue();

        _fileWriter?.Dispose();
        _fileWriter = null;
    }
}

/// <summary>
/// <see cref="ILogSink"/> 의 기본 구현 — 정적 <see cref="Logger"/> 로 passthrough 한다.
/// App 레이어가 <c>LogProvider.Sink = new LoggerSink();</c> 로 배선하면 Core 코드의 모든
/// <c>LogProvider.Sink?.X(...)</c> 호출이 그대로 Logger 의 큐잉 메커니즘으로 흐른다.
/// </summary>
internal sealed class LoggerSink : ILogSink
{
    public void Debug(string message)   => Logger.Debug(message);
    public void Info(string message)    => Logger.Info(message);
    public void Warning(string message) => Logger.Warning(message);
    public void Error(string message)   => Logger.Error(message);
}
