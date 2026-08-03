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
/// 그 디폴트가 디스크에 확정돼 <b>사용자 설정이 전멸</b>했다. koenvue_config.json 은 편집 중 한순간만
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
        Write("koenvue_config.json", """{ "opacity": 0.8 """);

        bool ok = ManagerFor("koenvue_config.json").TryLoad(out AppConfig config);

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
        Write("koenvue_config.json", content);

        Assert.False(ManagerFor("koenvue_config.json").TryLoad(out AppConfig config));
        Assert.NotNull(config);
    }

    [Fact]
    public void TryLoad_실패시_사용자_파일을_덮어쓰지_않는다()
    {
        const string broken = """{ "opacity": 0.8 """;
        string path = Write("koenvue_config.json", broken);

        ManagerFor("koenvue_config.json").TryLoad(out _);

        // 복구 가능성 보존 — 실패분 디폴트가 디스크로 나가면 사용자 편집분이 사라진다.
        Assert.Equal(broken, File.ReadAllText(path));
    }

    // ================================================================
    // 성공 경로 — 실패와 구분되어야 한다
    // ================================================================

    [Fact]
    public void TryLoad_정상_파일이면_true_이고_값이_반영된다()
    {
        Write("koenvue_config.json", """{ "opacity": 0.42 }""");

        bool ok = ManagerFor("koenvue_config.json").TryLoad(out AppConfig config);

        Assert.True(ok);
        Assert.Equal(0.42, config.Opacity, precision: 6);
    }

    [Fact]
    public void TryLoad_파일이_없으면_false_이고_아무것도_쓰지_않는다()
    {
        // **계약이 바뀌었다** (bug-hunt 3차 E). 종전에는 여기서 디폴트를 만들어 디스크에 확정하고
        // true 를 돌려줬는데, 그러면 §G 가드("false 면 기존 인스턴스를 두고 물러난다")가 통째로
        // 우회된다 — 런타임 호출자(핫리로드 · Save 의 병합 후 되읽기)가 그 true 를 정상 로드로 받아
        // 전 필드 디폴트를 인메모리와 디스크에 동시에 확정하고, 색·위치·앱 프로필·로그 설정이
        // 한꺼번에 사라진다. 생성은 Load 의 책임으로 옮겼다.
        string path = Path.Combine(_dir, "fresh.json");
        Assert.False(File.Exists(path));

        bool ok = ManagerFor("fresh.json").TryLoad(out AppConfig config);

        Assert.False(ok);
        Assert.NotNull(config);                 // 실패해도 non-null 계약은 유지
        Assert.False(File.Exists(path));        // 디스크를 건드리지 않는다
    }

    // ================================================================
    // bug-hunt 3차 M — 마이그레이션도 정규 로드와 같은 관용도로 읽어야 한다
    // ================================================================

    [Fact]
    public void 주석이_있어도_레거시_커서_설정을_마이그레이션한다()
    {
        // 이 프로젝트는 koenvue_config.json 의 주석과 트레일링 콤마를 정상으로 취급한다(소스젠 컨텍스트와
        // Core 양쪽 모두). 그런데 마이그레이션만 기본 JsonDocumentOptions 로 원본 파일을 다시
        // 파싱해, **주석 한 줄만 있어도** 파싱에 실패했다.
        string path = Write("legacy.json", "{\n  // 사용자 메모\n  \"cursor_motion_dim_enabled\": true,\n}");

        bool migrated = CursorDisplayModeMigration.TryResolveFromUserFile(path, out CursorDisplayMode mode);

        Assert.True(migrated);
        Assert.Equal(CursorDisplayMode.Motion, mode);
    }

    [Fact]
    public void 읽을_수_없는_파일은_커서_표시_모드를_덮어쓰지_않는다()
    {
        // 종전에는 파싱 실패에도 Soft + true 를 돌려줬고, PostDeserializeFixup 이 **매 로드마다**
        // 이것을 불러 사용자의 cursor_display_mode 를 덮어썼다 — 다음 저장이 디스크에도 확정한다.
        // 읽을 수 없는 파일은 판정의 근거가 될 수 없으므로 역직렬화된 값을 그대로 둬야 한다.
        string path = Write("broken-cursor.json", "{ \"cursor_display_mode\": ");

        Assert.False(CursorDisplayModeMigration.TryResolveFromUserFile(path, out _));
    }

    [Fact]
    public void Load_는_파일이_없으면_디폴트를_생성한다()
    {
        // 신규 설치(부팅) 경로 — "비교할 기존 인스턴스가 없는" 호출자만 이쪽을 쓴다.
        // 포터블 UX 상 디폴트를 즉시 디스크에 만드는 동작 자체는 그대로다.
        string path = Path.Combine(_dir, "fresh-load.json");
        Assert.False(File.Exists(path));

        AppConfig config = ManagerFor("fresh-load.json").Load();

        Assert.NotNull(config);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_는_TryLoad_와_같은_값을_돌려준다()
    {
        // Load 는 TryLoad 위임으로 바뀌었다. 부팅 경로가 계속 이 API 를 쓰므로 동작 동일성을 고정.
        Write("koenvue_config.json", """{ "opacity": 0.31 }""");

        AppConfig viaLoad = ManagerFor("koenvue_config.json").Load();
        ManagerFor("koenvue_config.json").TryLoad(out AppConfig viaTryLoad);

        Assert.Equal(viaTryLoad.Opacity, viaLoad.Opacity, precision: 6);
    }
}
