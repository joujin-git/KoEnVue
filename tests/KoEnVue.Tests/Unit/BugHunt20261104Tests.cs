using System.Reflection;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Config;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// bug-hunt 2026-11-04 확정 결함 중 단위로 재현 가능한 것들. 각 테스트는 수정 전 코드에서 실패하는
/// 조건(음성 대조)을 함께 적는다 — 통과만으로는 가드가 실제로 필요한지 알 수 없기 때문이다.
/// <c>Program</c>·<c>Tray</c> 정적 필드를 만지는 테스트는 <see cref="WindowLifecycleTests"/> 컬렉션에 넣어
/// 직렬 실행한다(같은 프로세스 전역 상태).
/// </summary>
[Collection(nameof(WindowLifecycleTests))]
public class BugHunt20261104Tests : IDisposable
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly Type ProgramType =
        typeof(KoEnVue.Core.Logging.LogLevel).Assembly.GetType("KoEnVue.Program")
        ?? throw new InvalidOperationException("KoEnVue.Program not found.");

    private readonly string _dir;

    public BugHunt20261104Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "koenvue-bh1104-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static FieldInfo ProgramField(string name) =>
        ProgramType.GetField(name, PrivateStatic)
        ?? throw new InvalidOperationException($"Program.{name} not found — 필드명이 바뀌었으면 테스트도 갱신할 것.");

    // ================================================================
    // #10·#37 — 캐시 무효화 뒤 빈 DIB 가 남지 않는다 (배지 엔진)
    // ================================================================

    private static OverlayStyle TestStyle() => new(
        FontFamily: "Segoe UI", FontSizeLogicalPx: 12, IsBold: true,
        LabelWidthLogicalPx: 40, LabelHeightLogicalPx: 24, BorderRadiusLogicalPx: 4,
        BorderWidthLogicalPx: 0, PaddingXLogicalPx: 4,
        BgHex: "#336699", FgHex: "#FFFFFF", BorderHex: "#000000",
        LabelText: "한", CapsLockOn: false, CapsLockHex: "#9333EA", CapsLockBorderWidthLogicalPx: 4,
        MeasureLabels: ["한", "En", "EN"]);

    /// <summary>배경 RoundRect 만 칠하는 렌더 콜백 — 픽셀이 0 이 아니게 되는지만 본다.</summary>
    private static (int w, int h) FillBackground(IntPtr hdc, OverlayStyle style, OverlayMetrics m)
    {
        IntPtr brush = Gdi32.CreateSolidBrush(0x00996633);
        IntPtr old = Gdi32.SelectObject(hdc, brush);
        Gdi32.RoundRect(hdc, 0, 0, m.ScaledWidth, m.ScaledHeight, 0, 0);
        Gdi32.SelectObject(hdc, old);
        Gdi32.DeleteObject(brush);
        return (m.ScaledWidth, m.ScaledHeight);
    }

    private static int CountNonZeroPixels(LayeredOverlayBase engine)
    {
        var bits = (IntPtr)typeof(LayeredOverlayBase).GetField("_ppvBits", PrivateInstance)!.GetValue(engine)!;
        (int w, int h) = engine.GetBaseSize();
        if (bits == IntPtr.Zero || w == 0 || h == 0) return 0;
        var pixels = new int[w * h];
        System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        return pixels.Count(p => p != 0);
    }

    [Fact]
    public void 배지_캐시_무효화_뒤_마지막_모양으로_다시_그린다()
    {
        using var engine = new LayeredOverlayBase(IntPtr.Zero, FillBackground);
        OverlayStyle style = TestStyle();
        engine.Paint(style);
        Assert.True(CountNonZeroPixels(engine) > 0, "Paint 가 DIB 를 그려야 한다");

        // 음성 대조 — 수정 전 파사드의 경로(무효화 + PrepareResources)는 픽셀이 전부 0 인 새 DIB 를 남긴다.
        // 페이드아웃 프레임이 이걸 블리트하면 배지가 뚝 사라졌다(확정 #10).
        engine.HandleDpiChanged();
        engine.PrepareResources(style);
        Assert.Equal(0, CountNonZeroPixels(engine));

        // 수정 — 마지막으로 그린 모양으로 다시 채운다.
        engine.HandleDpiChanged();
        Assert.True(engine.RepaintLastPainted());
        Assert.True(CountNonZeroPixels(engine) > 0, "무효화 뒤에도 마지막 모양이 DIB 에 있어야 한다");
    }

    [Fact]
    public void 그린_적이_없으면_다시_그리기는_false()
    {
        using var engine = new LayeredOverlayBase(IntPtr.Zero, FillBackground);
        Assert.False(engine.RepaintLastPainted());
    }

    [Fact]
    public void 같은_DPI_의_중첩_WM_DPICHANGED_는_할_일이_없다()
    {
        // Show 가 이미 새 모니터 DPI 로 맞춘 뒤 블리트 안에서 도착한 사본(확정 #37) — IsAtDpi 가 true 면 무시.
        using var engine = new LayeredOverlayBase(IntPtr.Zero, FillBackground);
        engine.Paint(TestStyle());
        double scale = (double)typeof(LayeredOverlayBase).GetField("_currentDpiScale", PrivateInstance)!.GetValue(engine)!;
        uint dpi = (uint)Math.Round(scale * 96);
        Assert.True(engine.IsAtDpi(dpi));
        Assert.False(engine.IsAtDpi(dpi + 48));
    }

    // ================================================================
    // #2·#1 — 시스템 브로드캐스트·WM_DPICHANGED 는 해당 창 사본만 처리
    // ================================================================

    /// <summary>
    /// 헤일로 창이 받은 사본이 배지 처리로 가면 <c>Overlay._engine</c>(테스트에선 미초기화)에 닿아 예외가 난다 —
    /// 수정 전 코드는 이 Theory 전부에서 실패한다(음성 대조). 메인 창 사본만 처리하는 것은 위 TaskbarCreated 와 같은 규칙.
    /// </summary>
    [Theory]
    [InlineData(Win32Constants.WM_DISPLAYCHANGE)]
    [InlineData(Win32Constants.WM_SETTINGCHANGE)]
    [InlineData(Win32Constants.WM_THEMECHANGED)]
    [InlineData(Win32Constants.WM_DWMCOLORIZATIONCOLORCHANGED)]
    [InlineData(Win32Constants.WM_DPICHANGED)]
    public void 헤일로_창이_받은_시스템_메시지는_무시한다(uint msg)
    {
        FieldInfo main = ProgramField("_hwndMain");
        FieldInfo overlay = ProgramField("_hwndOverlay");
        FieldInfo cursor = ProgramField("_hwndCursorOverlay");
        object? savedMain = main.GetValue(null), savedOverlay = overlay.GetValue(null), savedCursor = cursor.GetValue(null);
        try
        {
            main.SetValue(null, (IntPtr)0x1111);
            overlay.SetValue(null, (IntPtr)0x2222);
            cursor.SetValue(null, (IntPtr)0x3333);
            MethodInfo wndProc = ProgramType.GetMethod("WndProcCore", PrivateStatic)!;
            var result = (IntPtr)wndProc.Invoke(null, [(IntPtr)0x3333, msg, (IntPtr)144, IntPtr.Zero])!;
            Assert.Equal(IntPtr.Zero, result);
        }
        finally
        {
            main.SetValue(null, savedMain);
            overlay.SetValue(null, savedOverlay);
            cursor.SetValue(null, savedCursor);
        }
    }

    // ================================================================
    // #13 — 세션 잠금 중에는 표시 경로가 배지를 띄우지 않는다
    // ================================================================

    [Fact]
    public void 잠금_중에는_표시_요청을_무시한다()
    {
        FieldInfo locked = ProgramField("_sessionLocked");
        FieldInfo config = ProgramField("_config");
        FieldInfo dismissed = ProgramField("_clickDismissed");
        object? savedLocked = locked.GetValue(null), savedConfig = config.GetValue(null), savedDismissed = dismissed.GetValue(null);
        try
        {
            locked.SetValue(null, true);
            config.SetValue(null, new AppConfig { HideOnLockScreen = true });
            dismissed.SetValue(null, true);
            MethodInfo show = ProgramType.GetMethod("ShowIndicatorAtForeground", PrivateStatic)!;
            // 가드가 없으면 표시 경로로 내려가 _clickDismissed 를 내리고 미초기화 Overlay 에 닿아 예외(음성 대조).
            show.Invoke(null, [ImeState.Hangul, new AppConfig(), false]);
            Assert.True((bool)dismissed.GetValue(null)!, "잠금 중 표시 요청은 첫 줄에서 돌아가야 한다");
        }
        finally
        {
            locked.SetValue(null, savedLocked);
            config.SetValue(null, savedConfig);
            dismissed.SetValue(null, savedDismissed);
        }
    }

    // ================================================================
    // #16 — 셸 호출 중 도착한 트레이 재등록은 보류된다
    // ================================================================

    [Fact]
    public void 셸_호출_중_트레이_재등록은_보류된다()
    {
        Type tray = typeof(KoEnVue.App.UI.Tray);
        FieldInfo F(string n) => tray.GetField(n, PrivateStatic)
            ?? throw new InvalidOperationException($"Tray.{n} not found.");
        string[] names = ["_initialized", "_shellCallInProgress", "_hwndMain", "_recreatePending", "_pendingRecreateConfig"];
        var saved = names.ToDictionary(n => n, n => F(n).GetValue(null));
        try
        {
            F("_initialized").SetValue(null, true);
            F("_shellCallInProgress").SetValue(null, true);
            F("_hwndMain").SetValue(null, (IntPtr)0x1111);
            F("_recreatePending").SetValue(null, false);

            var config = new AppConfig();
            KoEnVue.App.UI.Tray.Recreate(ImeState.English, config);

            // 수정 전에는 곧바로 Remove 로 내려가 _initialized 를 내리고 바깥 프레임의 아이콘을 치웠다(음성 대조).
            Assert.True((bool)F("_recreatePending").GetValue(null)!);
            Assert.True((bool)F("_initialized").GetValue(null)!, "보류 중에는 등록 상태를 건드리지 않는다");
            Assert.Same(config, F("_pendingRecreateConfig").GetValue(null));
        }
        finally
        {
            foreach (var (n, v) in saved) F(n).SetValue(null, v);
        }
    }

    // ================================================================
    // #7·#61 — 일시적 I/O 실패 뒤에는 다음 폴링이 반드시 다시 읽는다
    // ================================================================

    [Fact]
    public void 잠긴_설정_파일_읽기_실패_뒤_폴링이_다시_읽는다()
    {
        string path = Path.Combine(_dir, "koenvue_config.json");
        File.WriteAllText(path, """{ "opacity": 0.8 }""");
        var mgr = new JsonSettingsManager<AppConfig>(path, AppConfigJsonContext.Default.AppConfig);
        Assert.True(mgr.CheckReload()); // 폴링이 변경을 눈치챔 (_lastMtime = 현재 mtime)

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(mgr.TryLoad(out _)); // 편집기가 잡고 있는 순간 — IOException
        }

        // 잠금이 풀린 뒤 mtime 은 그대로다. 수정 전에는 catch 가 그 mtime 을 다시 박아 false — 이 편집은 끝내
        // 반영되지 않았다(음성 대조).
        Assert.True(mgr.CheckReload(), "읽지 못한 내용은 다음 폴링이 다시 읽어야 한다");
    }

    [Fact]
    public void 잠금이_계속되면_재시도는_한도에서_멈춘다()
    {
        // release-review 2026-11-04: 잠금 실패마다 MinValue 로 되돌리면 오래 잡고 있는 프로세스에 5초마다
        // 재시도·Warning 이 끝없이 쌓였다. 한도까지만 재시도하고 그 뒤로는 읽기 시점 mtime 으로 확정한다.
        string path = Path.Combine(_dir, "koenvue_config.json");
        File.WriteAllText(path, """{ "opacity": 0.8 }""");
        var mgr = new JsonSettingsManager<AppConfig>(path, AppConfigJsonContext.Default.AppConfig);
        Assert.True(mgr.CheckReload());

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            int retries = 0;
            for (int i = 0; i < 20; i++)
            {
                Assert.False(mgr.TryLoad(out _));
                if (!mgr.CheckReload()) break;
                retries++;
            }
            Assert.InRange(retries, 1, 10); // 재시도는 하되 끝없이는 아님 (한도 6)
            Assert.False(mgr.CheckReload(), "한도 뒤에는 같은 mtime 으로 다시 발송하지 않는다");
        }
    }

    [Fact]
    public void 손상된_설정_파일은_같은_mtime_으로_재발송하지_않는다()
    {
        string path = Path.Combine(_dir, "koenvue_config.json");
        File.WriteAllText(path, """{ "opacity": 0.8 """);
        var mgr = new JsonSettingsManager<AppConfig>(path, AppConfigJsonContext.Default.AppConfig);
        Assert.True(mgr.CheckReload());
        Assert.False(mgr.TryLoad(out _));
        Assert.False(mgr.CheckReload()); // 스팸 차단 유지

        File.WriteAllText(path, """{ "opacity": 0.7 }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.True(mgr.CheckReload(), "그 뒤의 편집은 mtime 이 달라 다시 읽는다");
    }

    // ================================================================
    // #12 — 마이그레이션은 이번 로드가 읽은 원문으로 판정한다
    // ================================================================

    [Theory]
    [InlineData("""{ "cursor_motion_dim_enabled": false }""", "Sharp")]
    [InlineData("{ \"cursor_motion_dim_enabled\": true, // 주석·트레일링 콤마 허용\n }", "Motion")]
    [InlineData("""{ }""", "Soft")]
    public void 원문_JSON_으로_표시_모드를_마이그한다(string json, string expectedMode)
    {
        Assert.True(CursorDisplayModeMigration.TryResolveFromUserJson(json, out CursorDisplayMode got));
        Assert.Equal(Enum.Parse<CursorDisplayMode>(expectedMode), got);
    }

    [Theory]
    [InlineData("""{ "cursor_display_mode": "sharp" }""")] // 새 키 있음 — 역직렬화 값 유지
    [InlineData("""{ "opacity": """)]                        // 파싱 실패 — 근거 없음
    public void 원문_JSON_판정이_불가하거나_불필요하면_false(string json)
    {
        Assert.False(CursorDisplayModeMigration.TryResolveFromUserJson(json, out _));
    }
}
