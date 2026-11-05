using System.Collections;
using System.Reflection;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.Core.Animation;
using KoEnVue.Core.Config;
using KoEnVue.Core.Native;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// bug-hunt 2026-11-05 (8라운드, 확정 115건 → 38묶음) 회귀 고정 — 설정 파일 견고성.
/// 각 테스트는 수정 전 코드에서 실패하는 조건을 만든다.
/// </summary>
public class BugHunt20261105ConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public BugHunt20261105ConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "koenvue-bh1105-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "koenvue_config.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path)) File.SetAttributes(_path, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>앱과 같은 하위 클래스 — 깨진 파일 보관 접미사·null 정리 파이프라인을 포함한다.</summary>
    private AppSettingsManager NewManager() => new(_path, AppConfigJsonContext.Default.AppConfig);

    [Fact]
    public void 문자열_목록과_위치_기록의_null_원소는_로드에서_걸러진다()
    {
        // 수정 전: [null] 이 string[]{null} 로 들어와 감지 스레드의 s.Equals 가 NRE → 프로세스 종료(실측 0xC0000409).
        File.WriteAllText(_path, """
            {
              "system_hide_classes_user": [null, "Foo", "  "],
              "app_filter_list": [null],
              "indicator_positions": { "notepad": null, "calc": [1, 2], "short": [5] },
              "indicator_positions_relative": { "notepad": null }
            }
            """);

        Assert.True(NewManager().TryLoad(out AppConfig cfg));
        Assert.Equal(["Foo"], cfg.SystemHideClassesUser);
        Assert.Empty(cfg.AppFilterList);
        Assert.Equal(["calc"], cfg.IndicatorPositions.Keys);
        Assert.Empty(cfg.IndicatorPositionsRelative);
    }

    [Fact]
    public void 필터_매칭은_null_원소에서_예외를_던지지_않는다()
    {
        Assert.False(SystemFilter.MatchesAny("Notepad", [null!], ["x"]));
        Assert.True(SystemFilter.MatchesAny("Notepad", [null!], ["notepad"]));
    }

    [Theory]
    [InlineData("KoEnVueSettingsDlg")]
    [InlineData("koenvuesettingsviewport")]
    [InlineData("KoEnVueMain")]
    [InlineData("Button")]
    [InlineData("Progman")]
    [InlineData("Shell_TrayWnd")]
    public void 내부_시스템_클래스_이름은_overlay_class_name_으로_쓸_수_없다(string name)
    {
        // 수정 전: 문자 집합만 검사해 그대로 통과 → 다이얼로그가 Program.WndProc 클래스를 재사용해 영구 모달·스택 오버플로.
        AppConfig cfg = Settings.Validate(new AppConfig { Advanced = new AdvancedConfig { OverlayClassName = name } });
        Assert.Equal(DefaultConfig.DefaultOverlayClassName, cfg.Advanced.OverlayClassName);
    }

    [Theory]
    [InlineData("KoEnVueOverlay")]
    [InlineData("MyOverlay_1")]
    public void 겹치지_않는_클래스_이름은_그대로_쓴다(string name)
    {
        AppConfig cfg = Settings.Validate(new AppConfig { Advanced = new AdvancedConfig { OverlayClassName = name } });
        Assert.Equal(name, cfg.Advanced.OverlayClassName);
    }

    [Fact]
    public void 깨진_설정_파일은_덮기_전에_broken_사본으로_보관한다()
    {
        const string Broken = "{ \"font_size\": 20, \"app_profiles\": { \"notepad\": { \"indicator_scale\": 3.0 } }";
        File.WriteAllText(_path, Broken);

        var mgr = NewManager();
        Assert.False(mgr.TryLoad(out AppConfig defaults));
        Assert.Equal(LoadFailure.Content, mgr.LastLoadFailure);

        // 트레이 토글 한 번 — 수정 전에는 사용자 파일이 기본값으로 덮이고 원본은 어디에도 남지 않았다.
        mgr.Save(defaults with { SnapToWindows = !defaults.SnapToWindows });

        Assert.Equal(Broken, File.ReadAllText(_path + DefaultConfig.BrokenConfigBackupSuffix));
        Assert.True(NewManager().TryLoad(out _));
    }

    [Fact]
    public void 기준선_없이_저장해도_디스크의_사용자_값은_살아남는다()
    {
        // 부팅 로드가 일시 잠금으로 실패해 메모리가 기본값인 상황 — 디스크는 정상이다.
        File.WriteAllText(_path, "{ \"font_size\": 20 }");
        var mgr = NewManager();   // 로드 안 함 = 기준선 없음
        var defaults = new AppConfig();

        mgr.Save(defaults with { AnimationEnabled = !defaults.AnimationEnabled });

        Assert.True(NewManager().TryLoad(out AppConfig back));
        Assert.Equal(20, back.FontSize);                                    // 수정 전: 기본값으로 덮임
        Assert.Equal(!defaults.AnimationEnabled, back.AnimationEnabled);    // 방금 바꾼 값은 반영
    }

    [Fact]
    public void 파일이_없으면_실패_이유는_Missing_이다()
    {
        var mgr = NewManager();
        Assert.False(mgr.TryLoad(out _));
        Assert.Equal(LoadFailure.Missing, mgr.LastLoadFailure);
    }

    [Fact]
    public void mtime_이_같아도_길이가_다르면_변경으로_감지한다()
    {
        // FAT32(포터블 USB)는 mtime 해상도가 2초 — 같은 칸에 떨어진 편집을 mtime 만으로는 놓쳤다.
        File.WriteAllText(_path, "{ \"font_size\": 20 }");
        var mgr = NewManager();
        Assert.True(mgr.TryLoad(out _));
        DateTime mtime = File.GetLastWriteTimeUtc(_path);

        File.WriteAllText(_path, "{ \"font_size\": 20, \"snap_to_windows\": false }");
        File.SetLastWriteTimeUtc(_path, mtime);

        Assert.True(mgr.CheckReload());
    }

    [Fact]
    public void 저장_실패는_미저장_표식을_남기고_성공하면_지운다()
    {
        File.WriteAllText(_path, "{ \"font_size\": 20 }");
        var mgr = NewManager();
        Assert.True(mgr.TryLoad(out AppConfig cfg));

        File.SetAttributes(_path, FileAttributes.ReadOnly);
        mgr.Save(cfg with { SnapToWindows = !cfg.SnapToWindows });
        Assert.True(mgr.HasUnsavedChanges);

        File.SetAttributes(_path, FileAttributes.Normal);
        mgr.Save(cfg with { SnapToWindows = !cfg.SnapToWindows });
        Assert.False(mgr.HasUnsavedChanges);
    }

    [Fact]
    public void 디스크를_다시_읽으면_미저장_표식이_풀리고_실패한_저장의_임시_파일은_남지_않는다()
    {
        // 릴리즈 리뷰 2026-11-05: 저장이 계속 실패하면 리로드가 병합 저장만 반복하다 물러나 핫리로드가 세션 내내 죽었다.
        // 이제 리로드가 디스크를 받아들이면 미저장 변경은 포기된다(표식 해제). 실패한 저장은 .tmp 를 남기지 않는다.
        File.WriteAllText(_path, "{ \"font_size\": 20 }");
        var mgr = NewManager();
        Assert.True(mgr.TryLoad(out AppConfig cfg));

        File.SetAttributes(_path, FileAttributes.ReadOnly);
        mgr.Save(cfg with { SnapToWindows = !cfg.SnapToWindows });
        Assert.True(mgr.HasUnsavedChanges);
        Assert.False(File.Exists(_path + ".tmp"));

        Assert.True(mgr.TryLoad(out _));
        Assert.False(mgr.HasUnsavedChanges);
    }

    [Fact]
    public void 시스템_테마는_전경색을_사용자_백업에서_가져온다()
    {
        // 고대비 분기만 전경색을 덮어, 고대비를 껐다 켜면 고대비 글자색이 남았다.
        if (User32.IsHighContrastEnabled()) return;   // 이 머신이 고대비면 다른 분기를 탄다

        var cfg = new AppConfig
        {
            Theme = Theme.System,
            HangulFg = "#000000",
            EnglishFg = "#FFFFFF",
            CustomBackupHangulBg = "#123456",
            CustomBackupHangulFg = "#111111",
            CustomBackupEnglishBg = "#654321",
            CustomBackupEnglishFg = "#222222",
            CustomBackupNonKoreanBg = "#333333",
            CustomBackupNonKoreanFg = "#444444",
        };

        AppConfig applied = ThemePresets.Apply(cfg);
        Assert.Equal("#111111", applied.HangulFg);
        Assert.Equal("#222222", applied.EnglishFg);
    }
}

/// <summary>bug-hunt 2026-11-05 회귀 고정 — 애니메이션 트랙 수명.</summary>
public class BugHunt20261105AnimatorTests
{
    private static readonly nuint FadeId = 1;
    private static readonly nuint HoldId = 2;
    private static readonly nuint HighlightId = 3;
    private static readonly nuint TopmostId = 4;
    private static readonly nuint SlideId = 5;

    [Fact]
    public void 새_슬라이드를_시작하지_않는_표시는_옛_슬라이드를_끊는다()
    {
        var posSpy = new List<(int X, int Y)>();
        var animator = Make(MakeConfig(), onPositionOffset: (x, y) => posSpy.Add((x, y)));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);    // Hidden → Holding
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: false); // 슬라이드 시작
        // 다른 모니터로 간 경우처럼 새 슬라이드가 없는 표시 — 수정 전에는 옛 슬라이드가 계속 배지를 끌고 갔다.
        animator.TriggerShow(150, 150, 150, 150, highlightTrigger: false);

        posSpy.Clear();
        animator.OnWmTimer(SlideId);
        Assert.Empty(posSpy);
    }

    [Fact]
    public void 강조_팝_길이는_시작_때_값으로_고정된다()
    {
        var widths = new List<int>();
        AnimationConfig cfg = MakeConfig();
        var animator = Make(cfg, onScaledSize: (_, _, w, _, _) => widths.Add(w));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: true);   // 300ms 팝 시작
        animator.UpdateConfig(cfg with { HighlightDurationMs = 1 });     // 팝 도중 다른 프로필·리로드
        Thread.Sleep(30);   // 경과 시간이 새 길이(1ms)는 넘고 시작 길이(300ms)에는 한참 못 미치게

        widths.Clear();
        animator.OnWmTimer(HighlightId);
        // 수정 전: 새 길이(1ms)로 ratio=1 이 돼 첫 프레임에 기본 크기(40)로 끝났다. 시작 길이면 ratio≈0.1 이라 아직 확대 중.
        Assert.True(widths[0] > 40, $"first frame width {widths[0]}");
    }

    [Fact]
    public void 제자리_갱신은_흐려진_배지를_밝히지_않는다()
    {
        var alphas = new List<byte>();
        var animator = Make(MakeConfig() with { AlwaysMode = true }, onAlphaChange: alphas.Add);

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);   // Holding (활성 투명도)
        animator.OnWmTimer(HoldId);                                       // Idle (유휴 투명도)
        byte idle = alphas[^1];

        alphas.Clear();
        animator.Refresh(100, 100);
        // 수정 전 경로(TriggerShow 재사용)는 Idle → 활성 투명도로 밝혔다.
        Assert.All(alphas, a => Assert.Equal(idle, a));
        Assert.True(animator.IsVisiblePhase);
    }

    [Fact]
    public void 페이드아웃_중_always_로_바뀌면_유휴_투명도에서_멈춘다()
    {
        // 리뷰 2026-11-05: on_event 배지가 저절로 사라지는 도중 리로드로 always 가 들어오면, 0 을 향하던 페이드가 Idle 로
        // 끝나 투명도 0 인 채 "보이는 단계" 에 갇혔다.
        var alphas = new List<byte>();
        AnimationConfig cfg = MakeConfig() with { AnimationEnabled = true, FadeInMs = 1, FadeOutMs = 1000 };
        var animator = Make(cfg, onAlphaChange: alphas.Add);

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);   // Hidden → FadingIn
        Thread.Sleep(5);
        animator.OnWmTimer(FadeId);                                       // → Holding
        animator.OnWmTimer(HoldId);                                       // on_event: → FadingOut(0, 1000ms)

        AnimationConfig always = cfg with { AlwaysMode = true, FadeOutMs = 1 };
        animator.UpdateConfig(always);
        animator.Refresh(100, 100);
        Thread.Sleep(5);
        animator.OnWmTimer(FadeId);                                       // 페이드 완료 → Idle

        byte idle = (byte)(always.IdleOpacity * byte.MaxValue);
        Assert.Equal(idle, alphas[^1]);
        Assert.True(animator.IsVisiblePhase);
    }

    [Fact]
    public void 강조_팝_도중의_투명도_변경은_팝이_끝날_때_올린다()
    {
        // 릴리즈 리뷰 2회차: 제자리 갱신이 팝 도중 알파 블리트를 기본 크기로 올려 한 프레임 줄었다.
        var alphas = new List<byte>();
        var scaled = new List<byte>();
        AnimationConfig cfg = MakeConfig() with { HighlightDurationMs = 20 };
        var animator = Make(cfg, onAlphaChange: alphas.Add, onScaledSize: (_, _, _, _, a) => scaled.Add(a));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: true);   // Holding + 강조 팝
        alphas.Clear();
        animator.UpdateConfig(cfg with { Opacity = 0.5 });
        animator.Refresh(100, 100);
        Assert.Empty(alphas);                                             // 팝 도중엔 알파 블리트 없음

        Thread.Sleep(30);
        animator.OnWmTimer(HighlightId);                                  // 팝 완료 — 복원 프레임
        byte target = (byte)(0.5 * byte.MaxValue);
        Assert.Equal(target, scaled[^1]);                                 // 새 투명도로 복원
    }

    [Fact]
    public void 드래그_취소는_슬라이드와_강조를_끝낸다()
    {
        var posSpy = new List<(int X, int Y)>();
        var scaled = new List<int>();
        var animator = Make(MakeConfig(),
            onPositionOffset: (x, y) => posSpy.Add((x, y)),
            onScaledSize: (_, _, w, _, _) => scaled.Add(w));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: true);   // 슬라이드 + 강조

        animator.CancelMotion(300, 300);
        posSpy.Clear();
        scaled.Clear();
        animator.OnWmTimer(SlideId);
        animator.OnWmTimer(HighlightId);
        Assert.Empty(posSpy);
        Assert.Empty(scaled);
    }

    private static OverlayAnimator Make(AnimationConfig cfg,
        Action<byte>? onAlphaChange = null,
        Action<int, int>? onPositionOffset = null,
        Action<int, int, int, int, byte>? onScaledSize = null)
    {
        var ids = new AnimationTimerIds(FadeId, HoldId, HighlightId, TopmostId, SlideId);
        return new OverlayAnimator(
            IntPtr.Zero, ids, cfg,
            onAlphaChange: onAlphaChange ?? (_ => { }),
            onPositionOffset: onPositionOffset ?? ((_, _) => { }),
            onTrackPosition: (_, _) => { },
            onScaledSize: onScaledSize ?? ((_, _, _, _, _) => { }),
            onHide: () => { },
            onForceTopmost: () => { },
            getBaseSize: () => (40, 40));
    }

    private static AnimationConfig MakeConfig() => new(
        AnimationEnabled: false,        // 페이드 타이머 없이 동기 전이
        AlwaysMode: false,
        FadeInMs: 150,
        FadeOutMs: 150,
        AlwaysIdleTimeoutMs: 1000,
        EventDisplayDurationMs: 1000,
        Opacity: 0.9,
        IdleOpacity: 0.5,
        ActiveOpacity: 0.9,
        ChangeHighlight: true,
        HighlightScale: 1.3,
        HighlightDurationMs: 300,
        SlideAnimation: true,
        SlideSpeedMs: 100,
        ForceTopmostIntervalMs: 0,
        AnimationFrameMs: 16,
        DimOpacityFactor: 0.5);
}

/// <summary>릴리즈 리뷰 2회차(2026-11-05) — 감지 루프의 숨김 판정은 창마다 기억한다.</summary>
public class BugHunt20261105SuppressionTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void 숨김_판정은_창마다_남고_그_창이_통과해야_풀린다()
    {
        Type t = typeof(DetectionService);
        FieldInfo store = t.GetField("s_suppressedWindows", PrivateStatic)!;
        MethodInfo mark = t.GetMethod("MarkSuppressed", PrivateStatic)!;
        MethodInfo show = t.GetMethod("MarkShowable", PrivateStatic)!;
        object? saved = store.GetValue(null);
        try
        {
            store.SetValue(null, Array.Empty<IntPtr>());
            var fullscreenApp = (IntPtr)0x1111;
            var desktop = (IntPtr)0x2222;

            mark.Invoke(null, [fullscreenApp]);   // F11 전체화면
            mark.Invoke(null, [desktop]);         // 바탕화면을 거쳐
            // 단일 값이던 시절엔 바탕화면 판정이 전체화면 앱의 판정을 덮어, 돌아오자마자 누른 한/영이 배지를 띄웠다.
            Assert.True(DetectionService.IsForegroundSuppressed(fullscreenApp));
            Assert.True(DetectionService.IsForegroundSuppressed(desktop));

            show.Invoke(null, [desktop]);         // 바탕화면 판정만 풀린다
            Assert.False(DetectionService.IsForegroundSuppressed(desktop));
            Assert.True(DetectionService.IsForegroundSuppressed(fullscreenApp));
            Assert.False(DetectionService.IsForegroundSuppressed(IntPtr.Zero));
        }
        finally
        {
            store.SetValue(null, saved);
        }
    }
}

/// <summary>bug-hunt 2026-11-05 회귀 고정 — 트레이 메뉴 토글과 상세 설정 커밋.</summary>
[Collection(SettingsDialogStaticsCollection.Name)]
public class BugHunt20261105CommitTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static int MenuId(string name) =>
        (int)typeof(Tray).GetField(name, PrivateStatic)!.GetRawConstantValue()!;

    [Fact]
    public void 메뉴_토글은_메뉴에_보였던_값의_반대를_목표로_한다()
    {
        FieldInfo snapshotField = typeof(Tray).GetField("_menuSnapshot", PrivateStatic)!;
        // 메뉴에는 「배지 숨김」 이 꺼진 채로 보였다. 메뉴가 열린 사이 리로드로 user_hidden=true 가 반영됐다.
        var shown = new AppConfig { UserHidden = false };
        var current = new AppConfig { UserHidden = true };
        snapshotField.SetValue(null, shown);

        AppConfig? committed = null;
        try
        {
            Tray.HandleMenuCommand(MenuId("IDM_USER_HIDDEN"), current, IntPtr.Zero, IntPtr.Zero,
                currentConfig: () => current, updateConfig: c => committed = c);
        }
        finally
        {
            snapshotField.SetValue(null, null);
        }

        // 사용자는 숨기려고 눌렀다 — 수정 전에는 현재 값(true)을 뒤집어 false(표시)가 됐다.
        Assert.NotNull(committed);
        Assert.True(committed!.UserHidden);
    }

    [Fact]
    public void 상세_설정은_바꾼_칸만_적용_시점의_현재_설정_위에_얹는다()
    {
        // Dbl 팩토리로 투명도 칸 하나를 만든다 — Transfer 는 컨트롤 없이 값만 옮긴다.
        MethodInfo dbl = typeof(SettingsDialog).GetMethod("Dbl", PrivateStatic)!;
        object field = dbl.Invoke(null,
        [
            "투명도", "Opacity", 0.1, 1.0,
            (Func<AppConfig, double>)(c => c.Opacity),
            (Func<AppConfig, double, AppConfig>)((c, v) => c with { Opacity = v }),
        ])!;

        // 사용자가 다이얼로그에서 투명도만 0.4 로 바꿨다(holder). 그 사이 리로드로 다른 필드(스냅)가 외부에서 바뀌었다(current).
        var holder = new AppConfig { Opacity = 0.4 };
        var current = new AppConfig { Opacity = 0.9, SnapToWindows = !new AppConfig().SnapToWindows };

        FieldInfo pending = typeof(SettingsDialog).GetField("_pendingCommit", PrivateStatic)!;
        var list = (IList)pending.GetValue(null)!;
        Type tupleType = pending.FieldType.GetGenericArguments()[0];
        list.Clear();
        list.Add(Activator.CreateInstance(tupleType, field, holder));
        try
        {
            MethodInfo compose = typeof(SettingsDialog).GetMethod("ComposeCommit", PrivateStatic)!;
            var result = (AppConfig)compose.Invoke(null, [current])!;

            Assert.Equal(0.4, result.Opacity, precision: 6);                  // 바꾼 칸
            Assert.Equal(current.SnapToWindows, result.SnapToWindows);         // 외부 편집 보존
        }
        finally
        {
            list.Clear();
        }
    }
}
