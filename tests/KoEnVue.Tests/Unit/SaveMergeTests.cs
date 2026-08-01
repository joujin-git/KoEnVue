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
}
