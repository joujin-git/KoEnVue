using System.Collections;
using System.Reflection;
using System.Text.Json;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 앱 프로필 LRU 캐시의 자료구조 불변식 + 무효화 세대 (AUDIT-2026-07-30 §D).
///
/// <para>
/// 두 결함이 한 자리에 있었다. (1) 값 계산(<c>MatchProfile</c>, Regex 포함이라 수 ms)이 <b>락 밖</b>에서
/// 돌아, 그 사이 <c>ClearProfileCache</c> 가 끝나면 <b>옛 global 로 머지된 결과가 무효화 이후에 꽂혀</b>
/// 영구 stale 이 됐다 — 캐시 키가 config 와 무관해 자기치유도 없다. (2) 조회 락과 삽입 락이 분리돼
/// 같은 키가 두 번 삽입될 수 있는데 <c>AddFirst</c> 를 무조건 부르므로 <c>_profileLruOrder</c> 에 중복
/// 항목이 생겨 <c>_profileCache</c> 와 desync 됐다(퇴출이 엉뚱한 키를 지운다).
/// </para>
///
/// <para>
/// (1) 의 진짜 경합은 계산 중 무효화라 결정적 단위 테스트로 만들 수 없다(타이밍 의존 → flaky).
/// 그래서 여기서는 <b>세대가 실제로 증가하는지</b>와, 무효화 뒤 조회가 <b>재계산되는지</b>를 고정한다.
/// (2) 는 자료구조 불변식(<c>_profileCache.Count == _profileLruOrder.Count</c>, 중복 없음)으로 직접 잡힌다.
/// </para>
/// </summary>
[Collection(SettingsDialogStaticsCollection.Name)]
public class ProfileCacheTests : IDisposable
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public ProfileCacheTests() => Settings.ClearProfileCache();
    public void Dispose()
    {
        Settings.ClearProfileCache();
        GC.SuppressFinalize(this);
    }

    private static object Field(string name) =>
        typeof(Settings).GetField(name, PrivateStatic)!.GetValue(null)!;

    private static int Generation() => (int)Field("_profileCacheGeneration");
    private static ICollection Cache() => (ICollection)Field("_profileCache");
    private static ICollection LruOrder() => (ICollection)Field("_profileLruOrder");

    /// <summary>프로필 1개를 가진 global — <c>AppProfiles</c> 가 비면 캐시 경로를 아예 타지 않는다.</summary>
    private static AppConfig GlobalWith(string key, double opacity)
    {
        using var doc = JsonDocument.Parse($$"""{ "opacity": {{opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}} }""");
        return new AppConfig() with
        {
            AppProfiles = new Dictionary<string, JsonElement> { [key] = doc.RootElement.Clone() },
        };
    }

    // ================================================================
    // 세대 — 무효화가 진행 중인 계산을 무력화하는 근거
    // ================================================================

    [Fact]
    public void 무효화는_세대를_올린다()
    {
        int before = Generation();
        Settings.ClearProfileCache();
        Assert.True(Generation() > before, "ClearProfileCache 는 세대를 증가시켜야 한다");
    }

    [Fact]
    public void 무효화_후_조회는_새_global_로_재계산된다()
    {
        const string key = "notepad.exe";

        AppConfig? first = Settings.ResolveForKey(GlobalWith(key, 0.4), key);
        Assert.NotNull(first);
        Assert.Equal(0.4, first!.Opacity, precision: 6);

        // config 를 바꾸고 무효화 — 캐시가 남아 있으면 옛 값이 계속 나온다(영구 stale).
        Settings.ClearProfileCache();

        AppConfig? second = Settings.ResolveForKey(GlobalWith(key, 0.8), key);
        Assert.NotNull(second);
        Assert.Equal(0.8, second!.Opacity, precision: 6);
    }

    // ================================================================
    // LRU 자료구조 불변식
    // ================================================================

    [Fact]
    public void 같은_키를_반복_조회해도_LRU_에_중복이_생기지_않는다()
    {
        const string key = "chrome.exe";
        AppConfig global = GlobalWith(key, 0.5);

        for (int i = 0; i < 5; i++)
            Settings.ResolveForKey(global, key);

        Assert.Single(Cache());
        // 무조건 AddFirst 하던 시절엔 여기가 캐시 수보다 커져 퇴출이 엉뚱한 키를 지웠다.
        Assert.Equal(Cache().Count, LruOrder().Count);
    }

    [Fact]
    public void 여러_키를_조회해도_캐시와_LRU_수가_일치한다()
    {
        string[] keys = ["a.exe", "b.exe", "c.exe"];
        foreach (string k in keys)
        {
            AppConfig global = GlobalWith(k, 0.5);
            Settings.ResolveForKey(global, k);
            Settings.ResolveForKey(global, k); // 두 번째는 캐시 히트 경로
        }

        Assert.Equal(keys.Length, Cache().Count);
        Assert.Equal(Cache().Count, LruOrder().Count);
    }

    [Fact]
    public void 무효화는_캐시와_LRU_를_모두_비운다()
    {
        AppConfig global = GlobalWith("x.exe", 0.5);
        Settings.ResolveForKey(global, "x.exe");
        Assert.NotEmpty(Cache());

        Settings.ClearProfileCache();

        Assert.Empty(Cache());
        Assert.Empty(LruOrder());
    }
}
