using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Config;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 저장 시 3-way 병합 (AUDIT-2026-07-30 §N-48).
///
/// <para>
/// 저장은 메모리 인스턴스를 통째로 직렬화해 파일을 덮으므로, 앱이 마지막으로 읽은 뒤 사용자가
/// <c>config.json</c> 에 넣은 편집은 <b>앱이 손대지도 않은 필드까지</b> 사라졌다. 5초 폴링이 그 편집을
/// 읽어가기 전에 트레이 토글 한 번이면 충분하다. §B 는 "창이 열려 있는 동안" 만 닫았고 이건 더 넓다.
/// </para>
///
/// <para>
/// 병합 규칙은 하나다 — <b>앱이 이번에 바꾼 필드는 앱이 이기고, 나머지는 디스크가 이긴다.</b>
/// 기준선은 앱이 마지막으로 디스크와 같다고 아는 상태이므로, 기준선 대비 변경분이 앱의 의도이고
/// 그 외 디스크와의 차이는 사용자 편집이다.
/// </para>
/// </summary>
public class SaveMergeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public SaveMergeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "koenvue-savemerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private JsonSettingsManager<AppConfig> NewManager() =>
        new(_path, AppConfigJsonContext.Default.AppConfig);

    /// <summary>사용자가 편집기로 파일을 고치는 상황. mtime 이 확실히 달라지도록 잠시 벌린다.</summary>
    private void UserEdits(string json)
    {
        Thread.Sleep(20);
        File.WriteAllText(_path, json);
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(1));
    }

    private AppConfig ReadBack()
    {
        NewManager().TryLoad(out AppConfig cfg);
        return cfg;
    }

    // ================================================================
    // 핵심 — 앱이 건드리지 않은 사용자 편집은 살아남는다
    // ================================================================

    [Fact]
    public void 앱이_건드리지_않은_필드는_디스크_편집이_살아남는다()
    {
        var manager = NewManager();
        manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });

        // 사용자가 파일에서 snap_gap_px 를 고쳤다 (앱은 아직 모른다 — 폴링 전).
        UserEdits(File.ReadAllText(_path).Replace("\"snap_gap_px\": 10", "\"snap_gap_px\": 42"));

        // 앱은 opacity 만 바꿔 저장한다.
        manager.Save(new AppConfig() with { Opacity = 0.8, SnapGapPx = 10 });

        AppConfig result = ReadBack();
        Assert.Equal(0.8, result.Opacity, precision: 6);   // 앱이 바꾼 값 반영
        Assert.Equal(42, result.SnapGapPx);                 // 병합 없으면 10 으로 되돌아간다
    }

    [Fact]
    public void 앱이_바꾼_필드는_디스크_편집을_이긴다()
    {
        var manager = NewManager();
        manager.Save(new AppConfig() with { Opacity = 0.5 });

        // 같은 필드를 사용자도 건드렸다 — 이 경우엔 방금 조작한 앱 값이 이겨야 한다.
        UserEdits(File.ReadAllText(_path).Replace("\"opacity\": 0.5", "\"opacity\": 0.11"));

        manager.Save(new AppConfig() with { Opacity = 0.8 });

        Assert.Equal(0.8, ReadBack().Opacity, precision: 6);
    }

    [Fact]
    public void 디스크가_그대로면_병합_없이_저장한다()
    {
        var manager = NewManager();
        manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });
        manager.Save(new AppConfig() with { Opacity = 0.8, SnapGapPx = 10 });

        AppConfig result = ReadBack();
        Assert.Equal(0.8, result.Opacity, precision: 6);
        Assert.Equal(10, result.SnapGapPx);
    }

    // ================================================================
    // 중첩 객체 — 부분 편집이 살아남아야 한다
    // ================================================================

    [Fact]
    public void 중첩_객체의_사용자_편집도_보존된다()
    {
        var manager = NewManager();
        manager.Save(new AppConfig() with
        {
            Opacity = 0.5,
            Advanced = new AdvancedConfig { ForceTopmostIntervalMs = 5000, OverlayClassName = "KoEnVueOverlay" },
        });

        UserEdits(File.ReadAllText(_path)
            .Replace("\"force_topmost_interval_ms\": 5000", "\"force_topmost_interval_ms\": 7000"));

        manager.Save(new AppConfig() with
        {
            Opacity = 0.8,
            Advanced = new AdvancedConfig { ForceTopmostIntervalMs = 5000, OverlayClassName = "KoEnVueOverlay" },
        });

        AppConfig result = ReadBack();
        Assert.Equal(0.8, result.Opacity, precision: 6);
        // 최상위만 병합하면 Advanced 가 통째로 앱 값으로 덮여 7000 이 사라진다.
        Assert.Equal(7000, result.Advanced.ForceTopmostIntervalMs);
    }

    // ================================================================
    // 병합 불가 상황 — 저장을 포기하지는 않는다
    // ================================================================

    [Fact]
    public void 디스크가_깨져_있으면_앱_값을_그대로_쓴다()
    {
        var manager = NewManager();
        manager.Save(new AppConfig() with { Opacity = 0.5 });

        UserEdits("{ this is not json");

        // 병합 대상이 없다고 저장을 포기하면 사용자의 트레이 조작이 조용히 무시된다 — 그쪽이 더 나쁘다.
        manager.Save(new AppConfig() with { Opacity = 0.8 });

        Assert.Equal(0.8, ReadBack().Opacity, precision: 6);
    }

    [Fact]
    public void 첫_저장은_기준선이_없어도_동작한다()
    {
        // Load 도 Save 도 아직 없었던 상태 — 기준선이 null 이라 병합 경로를 타지 않아야 한다.
        NewManager().Save(new AppConfig() with { Opacity = 0.33 });

        Assert.Equal(0.33, ReadBack().Opacity, precision: 6);
    }

    // ================================================================
    // 병합 이후 — 릴리즈 리뷰 2026-08-01 이 드러낸 커버리지 공백
    // ================================================================

    [Fact]
    public void 병합_다음_저장도_사용자_편집을_유지한다()
    {
        // 원래 결함: 병합 결과가 인메모리에 반영되지 않고 mtime 은 self-bump 돼 핫리로드도 안 돌아,
        // **다음 저장이 옛 인메모리 값으로 사용자 편집을 다시 덮었다.** 병합이 손실을 한 번 지연시킬
        // 뿐이었고, 트레이 토글 두 번이면 끝나는 흔한 시퀀스다 (확정 #1·#3·#5).
        var manager = NewManager();
        AppConfig cfg = manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });

        UserEdits(File.ReadAllText(_path).Replace("\"snap_gap_px\": 10", "\"snap_gap_px\": 42"));

        // 1차 저장 — 병합 발동. 반환값을 인메모리로 받아야 한다.
        cfg = manager.Save(cfg with { Opacity = 0.8 });
        Assert.Equal(42, cfg.SnapGapPx);  // 반환값에 병합 결과가 실려야 한다

        // 2차 저장 — 여기서 42 가 10 으로 되돌아가던 것이 원래 결함.
        manager.Save(cfg with { Opacity = 0.9 });

        AppConfig result = ReadBack();
        Assert.Equal(0.9, result.Opacity, precision: 6);
        Assert.Equal(42, result.SnapGapPx);
    }

    [Fact]
    public void 병합_후에도_파일이_들여쓰기를_유지한다()
    {
        // 원래 결함: 병합 출력이 non-indented 라 config.json 이 한 줄로 붕괴했다. 손으로 편집하는
        // 포터블 설정 파일이 사실상 편집 불가가 되고, 그 압축본이 기준선이 되어 다음 비교까지
        // 어긋났다 (확정 #2·#6).
        var manager = NewManager();
        AppConfig cfg = manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });

        int linesBefore = File.ReadAllLines(_path).Length;
        Assert.True(linesBefore > 50, $"기준 파일이 여러 줄이어야 한다 (실제 {linesBefore})");

        UserEdits(File.ReadAllText(_path).Replace("\"snap_gap_px\": 10", "\"snap_gap_px\": 42"));
        manager.Save(cfg with { Opacity = 0.8 });

        int linesAfter = File.ReadAllLines(_path).Length;
        Assert.True(linesAfter > 50, $"병합 후에도 여러 줄이어야 한다 (실제 {linesAfter})");
    }

    [Fact]
    public void 앱이_중첩객체_한_필드를_바꿔도_형제_키의_사용자_편집은_남는다()
    {
        // 원래 결함: 중첩 객체를 통째로 비교해 "앱이 바꿨으면" 서브트리 전체를 앱 값으로 썼다.
        // 상세 설정은 `Advanced with { … }` 로 중첩 객체를 실제로 수정하므로, 사용자가 그 안의
        // 형제 키를 고쳤으면 함께 사라졌다 (확정 #12). 기존 테스트는 양쪽 Advanced 가 동일해
        // 이 경로를 타지 않았다.
        var manager = NewManager();
        AppConfig cfg = manager.Save(new AppConfig() with
        {
            Advanced = new AdvancedConfig { ForceTopmostIntervalMs = 5000, OverlayClassName = "KoEnVueOverlay" },
        });

        // 사용자가 형제 키를 편집.
        UserEdits(File.ReadAllText(_path)
            .Replace("\"overlay_class_name\": \"KoEnVueOverlay\"", "\"overlay_class_name\": \"MyOverlay\""));

        // 앱은 같은 객체 안의 **다른** 필드를 바꾼다.
        manager.Save(cfg with
        {
            Advanced = cfg.Advanced with { ForceTopmostIntervalMs = 9000 },
        });

        AppConfig result = ReadBack();
        Assert.Equal(9000, result.Advanced.ForceTopmostIntervalMs);  // 앱이 바꾼 필드
        Assert.Equal("MyOverlay", result.Advanced.OverlayClassName); // 형제 키의 사용자 편집
    }

    [Fact]
    public void 변경을_눈치챘지만_읽지_못한_경우에도_병합한다()
    {
        // 폴링 기준(_lastMtime)과 동기화 기준(_syncedMtime)을 한 필드로 쓰면, "파일이 바뀐 것은
        // 알았지만 읽지는 못한" 상태에서 병합 가드가 무장 해제된다 — 편집기가 파일을 잠깐 잡고 있어
        // 로드가 실패하는 흔한 경로다 (bug-hunt 2026-08-02 확정 #1·#14).
        var manager = NewManager();
        AppConfig cfg = manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });

        // 사용자가 편집 — 아직 앱은 못 읽었다.
        UserEdits(File.ReadAllText(_path).Replace("\"snap_gap_px\": 10", "\"snap_gap_px\": 42"));

        // 감지 스레드가 변경을 눈치챈다 (CheckReload 가 폴링 기준만 올린다).
        Assert.True(manager.CheckReload());

        // 이 시점에 저장 — 눈치챈 것이 곧 반영은 아니므로 병합이 반드시 일어나야 한다.
        manager.Save(cfg with { Opacity = 0.8 });

        AppConfig result = ReadBack();
        Assert.Equal(0.8, result.Opacity, precision: 6);
        Assert.Equal(42, result.SnapGapPx);
    }

    [Fact]
    public void 표기만_다른_값은_앱이_바꾼_것으로_보지_않는다()
    {
        // 원래 결함: 기준선에 디스크 원문 조각이 섞여 사용자가 쓴 표기(0.80)가 남는데 다음 값은
        // 직렬화기의 정규 표기(0.8)라, 원문 비교가 "앱이 바꿈" 으로 오분류했다 (확정 #15).
        var manager = NewManager();
        AppConfig cfg = manager.Save(new AppConfig() with { Opacity = 0.5, SnapGapPx = 10 });

        // 사용자가 같은 값을 다른 표기로 쓰고, 별개 필드도 고친다.
        UserEdits(File.ReadAllText(_path)
            .Replace("\"opacity\": 0.5", "\"opacity\": 0.50")
            .Replace("\"snap_gap_px\": 10", "\"snap_gap_px\": 42"));

        // 앱은 opacity 를 건드리지 않는다 (기준선과 같은 0.5).
        manager.Save(cfg with { SnapGapPx = 10 });

        Assert.Equal(42, ReadBack().SnapGapPx);
    }
}
