using System.Reflection;
using KoEnVue.Core.Logging;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// Logger 재초기화 계약 고정 (AUDIT-2026-07-30 §C).
///
/// <para>
/// 원래 결함은 종료 신호가 <c>volatile bool _shutdownRequested</c> 였다는 것이다. drain 스레드
/// Join 이 타임아웃하면(회전 구간의 File.Move 가 AV 스캔·tail 뷰어에 막히면 3초를 넘긴다) 그 스레드가
/// 살아남는데, 이어지는 <c>Initialize</c> 가 같은 플래그를 <c>false</c> 로 되돌려 <b>좀비의 루프 조건을
/// 다시 참으로 만들었다</b>. 두 번째 스레드까지 더해져 같은 StreamWriter 를 동시에 쓰고, 메인의
/// Dispose 와 겹치면 <c>ObjectDisposedException</c> 이 catch 필터 밖이라 프로세스를 종료했다.
/// </para>
///
/// <para>
/// 수정의 핵심은 <b>단조 증가하는 세대</b>다 — 좀비는 자기 세대가 지나간 것을 알 뿐 되살릴 수단이 없다.
/// Join 타임아웃 자체는 단위 테스트로 만들 수 없으므로(3초 이상 걸리는 파일 잠금이 필요), 여기서는
/// 그 부활을 구조적으로 불가능하게 만든 <b>세대의 단조성</b>과, 재초기화 라운드트립이 실제로
/// 로그를 옳은 파일에 남기는지(= writer/filePath 가 한 쌍으로 갱신되는지)를 고정한다.
/// </para>
///
/// <para>
/// Logger 는 프로세스 전역 정적이라 이 테스트들은 같은 컬렉션으로 묶어 직렬 실행한다.
/// </para>
/// </summary>
[Collection(nameof(LoggerReinitTests))]
[CollectionDefinition(nameof(LoggerReinitTests), DisableParallelization = true)]
public class LoggerReinitTests : IDisposable
{
    private readonly string _dir;

    public LoggerReinitTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "koenvue-logger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Logger.SetLevel(LogLevel.Debug);
    }

    public void Dispose()
    {
        Logger.Shutdown();
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static int Generation() =>
        (int)typeof(Logger)
            .GetField("_generation", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private string PathFor(string name) => Path.Combine(_dir, name);

    /// <summary>drain 스레드가 비동기라 파일 반영을 짧게 기다린다. Shutdown 이 동기 flush 를 보장하지만
    /// 회전/재오픈 경로가 끼면 한 사이클(1s) 늦을 수 있어 여유를 둔다.</summary>
    private static string ReadAllWithRetry(string path)
    {
        for (int i = 0; i < 40 && !File.Exists(path); i++) Thread.Sleep(50);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    // ================================================================
    // 세대 단조성 — 좀비 부활 차단의 근거
    // ================================================================

    [Fact]
    public void 세대는_재초기화마다_증가하고_되돌아가지_않는다()
    {
        int start = Generation();

        Logger.Initialize(true, PathFor("a.log"), 1);
        int afterFirst = Generation();

        Logger.Initialize(true, PathFor("b.log"), 1);
        int afterSecond = Generation();

        Logger.Shutdown();
        int afterShutdown = Generation();

        // 이전 구조에서는 Initialize 가 종료 플래그를 false 로 되돌려 좀비가 부활했다.
        // 세대는 어떤 경로로도 감소하지 않아야 한다.
        Assert.True(afterFirst > start, $"{afterFirst} > {start}");
        Assert.True(afterSecond > afterFirst, $"{afterSecond} > {afterFirst}");
        Assert.True(afterShutdown > afterSecond, $"{afterShutdown} > {afterSecond}");
    }

    [Fact]
    public void 로깅을_끈_재초기화도_세대를_올린다()
    {
        Logger.Initialize(true, PathFor("on.log"), 1);
        int before = Generation();

        // enabled=false 는 조기 반환하지만, 그 전에 기존 스레드를 반드시 무효화해야 한다.
        Logger.Initialize(false, null, 1);

        Assert.True(Generation() > before);
    }

    // ================================================================
    // writer / filePath 쌍 갱신 — torn-pair 회귀 가드
    // ================================================================

    [Fact]
    public void 재초기화_후_로그는_새_파일에만_기록된다()
    {
        string first = PathFor("first.log");
        string second = PathFor("second.log");

        Logger.Initialize(true, first, 1);
        Logger.Error("marker-first");

        Logger.Initialize(true, second, 1);
        Logger.Error("marker-second");
        Logger.Shutdown();

        string firstText = ReadAllWithRetry(first);
        string secondText = ReadAllWithRetry(second);

        Assert.Contains("marker-first", firstText);
        // 옛 writer 가 새 _filePath 와 짝이 어긋난 채 살아 있으면 두 번째 메시지가 첫 파일로 샌다.
        Assert.DoesNotContain("marker-second", firstText);
        Assert.Contains("marker-second", secondText);
        Assert.DoesNotContain("marker-first", secondText);
    }

    [Fact]
    public void Shutdown_이후_재초기화하면_로깅이_되살아난다()
    {
        string path = PathFor("revive.log");

        Logger.Initialize(true, path, 1);
        Logger.Shutdown();

        // Shutdown 이 _drainThread 를 null 로 두므로, 재초기화가 새 스레드를 세우지 못하면
        // 이후 메시지는 pre-init 버퍼로 새어 영구 미기록된다.
        Logger.Initialize(true, path, 1);
        Logger.Error("after-revive");
        Logger.Shutdown();

        Assert.Contains("after-revive", ReadAllWithRetry(path));
    }
}
