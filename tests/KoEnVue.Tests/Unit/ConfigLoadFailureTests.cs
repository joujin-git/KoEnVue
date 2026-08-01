using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Config;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// config 파싱 실패가 <b>호출자에게 전달되는지</b> 고정 (AUDIT-2026-07-30 §G).
///
/// <para>
/// 원래 결함은 "파싱에 실패하면 디폴트를 쓴다"가 아니라 <b>호출자가 그 사실을 알 수 없다</b>는 것이었다.
/// <c>Load()</c> 는 성공이든 실패든 <c>T</c> 하나만 돌려주므로, 핫리로드 경로가 실패분 디폴트를
/// 그대로 <c>_config</c> 에 대입하고 → 이후 아무 저장(트레이 토글·드래그 종료)이나 한 번 일어나면
/// 그 디폴트가 디스크에 확정돼 <b>사용자 설정이 전멸</b>했다. config.json 은 편집 중 한순간만
/// 파싱 불가여도 이 경로를 탄다.
/// </para>
///
/// <para>
/// 따라서 검사 대상은 반환된 값이 아니라 <b>반환된 bool</b> 이다. 값만 보면 "디폴트가 나왔다"까지는
/// 알 수 있어도 그것이 실패분인지 정상 신규 생성인지 구분할 수 없고, 바로 그 구분 불가가 결함이었다.
/// </para>
/// </summary>
public class ConfigLoadFailureTests : IDisposable
{
    private readonly string _dir;

    public ConfigLoadFailureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "koenvue-cfgload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private JsonSettingsManager<AppConfig> ManagerFor(string fileName) =>
        new(Path.Combine(_dir, fileName), AppConfigJsonContext.Default.AppConfig);

    private string Write(string fileName, string content)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ================================================================
    // 실패 경로 — false 를 돌려줘야 한다
    // ================================================================

    [Fact]
    public void TryLoad_깨진_JSON_이면_false()
    {
        // 편집 중 저장으로 흔히 나오는 상태 — 닫히지 않은 객체.
        Write("config.json", """{ "opacity": 0.8 """);

        bool ok = ManagerFor("config.json").TryLoad(out AppConfig config);

        Assert.False(ok);
        Assert.NotNull(config); // 실패해도 non-null 계약 (호출자가 역참조해도 안전)
    }

    [Theory]
    [InlineData("null")]      // 유효 JSON 이지만 설정 객체가 아니다
    [InlineData("[]")]        // 배열
    [InlineData("42")]        // 스칼라
    [InlineData("\"text\"")]  // 문자열
    public void TryLoad_최상위가_객체가_아니면_false(string content)
    {
        // 병합 단계가 최상위를 객체로 가정하고 TryGetProperty 를 부르므로, 이 입력들은
        // JsonElementWrongTypeException(InvalidOperationException)을 냈고 그 타입은 로드 예외 필터
        // **밖**이라 그대로 전파돼 프로세스를 종료시켰다. 손상으로 분류돼야 한다.
        Write("config.json", content);

        Assert.False(ManagerFor("config.json").TryLoad(out AppConfig config));
        Assert.NotNull(config);
    }

    [Fact]
    public void TryLoad_실패시_사용자_파일을_덮어쓰지_않는다()
    {
        const string broken = """{ "opacity": 0.8 """;
        string path = Write("config.json", broken);

        ManagerFor("config.json").TryLoad(out _);

        // 복구 가능성 보존 — 실패분 디폴트가 디스크로 나가면 사용자 편집분이 사라진다.
        Assert.Equal(broken, File.ReadAllText(path));
    }

    // ================================================================
    // 성공 경로 — 실패와 구분되어야 한다
    // ================================================================

    [Fact]
    public void TryLoad_정상_파일이면_true_이고_값이_반영된다()
    {
        Write("config.json", """{ "opacity": 0.42 }""");

        bool ok = ManagerFor("config.json").TryLoad(out AppConfig config);

        Assert.True(ok);
        Assert.Equal(0.42, config.Opacity, precision: 6);
    }

    [Fact]
    public void TryLoad_파일이_없으면_true_이고_디폴트를_생성한다()
    {
        // 신규 설치 경로. 여기서 나오는 디폴트는 정상이므로 실패분 디폴트와 반드시 구분돼야 한다 —
        // 이 구분이 무너지면 호출자가 "디폴트가 나왔으니 실패"로 오판해 첫 실행이 깨진다.
        string path = Path.Combine(_dir, "fresh.json");
        Assert.False(File.Exists(path));

        bool ok = ManagerFor("fresh.json").TryLoad(out AppConfig config);

        Assert.True(ok);
        Assert.NotNull(config);
        Assert.True(File.Exists(path)); // 포터블 UX — 디폴트를 즉시 디스크에 만든다
    }

    [Fact]
    public void Load_는_TryLoad_와_같은_값을_돌려준다()
    {
        // Load 는 TryLoad 위임으로 바뀌었다. 부팅 경로가 계속 이 API 를 쓰므로 동작 동일성을 고정.
        Write("config.json", """{ "opacity": 0.31 }""");

        AppConfig viaLoad = ManagerFor("config.json").Load();
        ManagerFor("config.json").TryLoad(out AppConfig viaTryLoad);

        Assert.Equal(viaTryLoad.Opacity, viaLoad.Opacity, precision: 6);
    }
}
