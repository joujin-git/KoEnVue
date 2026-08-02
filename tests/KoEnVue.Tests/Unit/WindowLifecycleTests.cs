using System.Reflection;
using KoEnVue.Core.Native;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 창 lifecycle 계약 고정 (bug-hunt 2026-08-02 G5·G17).
///
/// <para>
/// 두 결함 모두 "정상 동작으로 타는 경로에 가드가 없다" 는 성질이다 — 예외 상황이 아니라
/// 트레이 메뉴 조작이 그대로 유발한다.
/// </para>
///
/// <para>
/// <c>Program</c> 은 프로세스 전역 정적이므로 이 테스트들은 같은 컬렉션으로 묶어 직렬 실행하고,
/// 각 테스트가 만진 필드를 반드시 되돌린다.
/// </para>
/// </summary>
[Collection(nameof(WindowLifecycleTests))]
[CollectionDefinition(nameof(WindowLifecycleTests), DisableParallelization = true)]
public class WindowLifecycleTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly Type ProgramType =
        typeof(KoEnVue.Core.Logging.LogLevel).Assembly.GetType("KoEnVue.Program")
        ?? throw new InvalidOperationException("KoEnVue.Program 을 찾지 못했다 — 타입명이 바뀌었으면 테스트도 갱신할 것.");

    private static FieldInfo Field(string name) =>
        ProgramType.GetField(name, PrivateStatic)
        ?? throw new InvalidOperationException($"Program.{name} not found — 필드명이 바뀌었으면 테스트도 갱신할 것.");

    private static object? Get(string name) => Field(name).GetValue(null);
    private static void Set(string name, object? value) => Field(name).SetValue(null, value);

    private static IntPtr InvokeWndProc(IntPtr hwnd, uint msg)
    {
        MethodInfo m = ProgramType.GetMethod("WndProcCore", PrivateStatic)
            ?? throw new InvalidOperationException("Program.WndProcCore not found.");
        return (IntPtr)m.Invoke(null, [hwnd, msg, IntPtr.Zero, IntPtr.Zero])!;
    }

    // ================================================================
    // G5 — WM_DESTROY 가 핸들 필드를 즉시 비운다 (확정 #10·#40·#43)
    // ================================================================

    /// <summary>
    /// 실제 창 없이 검증한다 — <c>WndProcCore</c> 의 WM_DESTROY 분기는 <c>hwnd</c> 를 세 필드와
    /// 비교만 하므로 더미 핸들로 충분하다. 메인 창 분기가 부르는 <c>PostQuitMessage</c> 는 이
    /// 스레드 큐에 WM_QUIT 를 넣을 뿐이고 테스트는 메시지 루프를 돌리지 않는다.
    /// </summary>
    [Theory]
    [InlineData("_hwndMain")]
    [InlineData("_hwndOverlay")]
    [InlineData("_hwndCursorOverlay")]
    public void 창이_파괴되면_해당_핸들_필드가_즉시_비워진다(string fieldName)
    {
        // 원래 결함: WndProcCore 에 WM_CLOSE case 가 없어 DefWindowProcW 가 DestroyWindow 를
        // 수행하는데, WM_DESTROY 는 PostQuitMessage 만 하고 필드를 비우지 않았다. §N-42 의 리셋은
        // OnProcessExit 한 곳에만 있어, 그 사이 감지 스레드가 `!= IntPtr.Zero` 가드를 통과해 죽은
        // HWND 에 계속 post 했다 — 커널이 그 값을 재발급하면 무관한 창에 WM_APP 이 배달된다.
        // 트레이 「관리자 권한」 토글이 PostMessageW(hwndMain, WM_CLOSE) 로 이 경로를 정상 동작으로 탄다.
        var saved = new Dictionary<string, object?>
        {
            ["_hwndMain"] = Get("_hwndMain"),
            ["_hwndOverlay"] = Get("_hwndOverlay"),
            ["_hwndCursorOverlay"] = Get("_hwndCursorOverlay"),
        };
        try
        {
            // 세 필드를 서로 다른 더미로 채워, 파괴된 창의 필드만 비워지는지 본다.
            Set("_hwndMain", (IntPtr)0x1001);
            Set("_hwndOverlay", (IntPtr)0x1002);
            Set("_hwndCursorOverlay", (IntPtr)0x1003);

            var target = (IntPtr)Get(fieldName)!;
            InvokeWndProc(target, Win32Constants.WM_DESTROY);

            Assert.Equal(IntPtr.Zero, (IntPtr)Get(fieldName)!);

            // 나머지 둘은 그대로여야 한다 — 한 창의 파괴가 다른 창의 핸들을 지우면 안 된다.
            foreach (string other in saved.Keys)
            {
                if (other == fieldName) continue;
                Assert.NotEqual(IntPtr.Zero, (IntPtr)Get(other)!);
            }
        }
        finally
        {
            foreach ((string name, object? value) in saved) Set(name, value);
        }
    }

    // ================================================================
    // G17 — 리로드 재진입은 보류 표시만 남긴다 (확정 #34)
    // ================================================================

    [Fact]
    public void 리로드가_진행_중이면_재진입은_보류_표시만_남긴다()
    {
        // 원래 결함: 리로드 실패 안내(MessageBoxW)는 자체 모달 루프라 _hwndMain 앞으로 post 된
        // WM_CONFIG_CHANGED 를 그대로 디스패치한다. 감지 스레드는 모달과 무관하게 5초 폴링을
        // 계속하므로, 사용자가 박스를 띄워 둔 채 파일을 고쳐 저장하면 안내 박스 **안에서**
        // HandleConfigChanged 가 재진입했다. 재진입한 리로드가 성공하면 _configReloadFailed 래치가
        // 풀려 "연속 실패당 1회" 설계가 깨지고 안내가 무한히 쌓인다.
        object? savedInProgress = Get("_configChangeInProgress");
        object? savedPending = Get("_configChangePending");
        try
        {
            Set("_configChangeInProgress", true);
            Set("_configChangePending", false);

            MethodInfo m = ProgramType.GetMethod("HandleConfigChanged", PrivateStatic)!;
            m.Invoke(null, null);

            // 가드가 없으면 Core 가 그대로 실행돼(파일 I/O·안내 박스) 이 표식이 서지 않는다.
            Assert.True((bool)Get("_configChangePending")!, "재진입은 보류 표시를 남겨야 한다");
        }
        finally
        {
            Set("_configChangeInProgress", savedInProgress);
            Set("_configChangePending", savedPending);
        }
    }

    // 「보류된 변경이 실제로 재처리되는가」는 여기서 고정할 수 없다 — 재처리는 HandleConfigChangedCore
    // 를 타고 Settings.TryLoad(파일 I/O)와 Win32 전이 적용자로 이어진다. do/while 로 pending 을
    // 소비하는 구조라는 사실은 코드와 문서에만 남는다.

    // ================================================================
    // G7·G8 — 셸 호출 중 트레이 갱신 재진입 (확정 #8·#42)
    // ================================================================

    private static FieldInfo TrayField(string name) =>
        typeof(KoEnVue.App.UI.Tray).GetField(name, PrivateStatic)
        ?? throw new InvalidOperationException($"Tray.{name} not found — 필드명이 바뀌었으면 테스트도 갱신할 것.");

    [Fact]
    public void 셸_호출_중_트레이_갱신은_보류_표시만_남긴다()
    {
        // Shell_NotifyIconW 는 explorer 로 가는 **블로킹 크로스프로세스 SendMessage** 라, 그 동안
        // 이 스레드로 들어온 sent 메시지가 계속 디스패치된다. 시스템이 HWND_BROADCAST 로 보내는
        // WM_SETTINGCHANGE / WM_THEMECHANGED 가 Program.HandleSettingChange 를 거쳐 Tray.UpdateState 로
        // 되돌아오면, _currentIcon 을 「셸에 넘기고 → 해제하고 → 재대입」 하는 세 걸음이 뒤엉켜
        // **셸이 지금 그리고 있는 HICON 을 파괴**한다.
        //
        // 가드가 없으면 아래 호출이 곧바로 GDI/셸 경로로 들어간다 — 가드가 있어야만 셸에 닿지 않고
        // 표시만 남기고 돌아온다.
        object? savedInit = TrayField("_initialized").GetValue(null);
        object? savedShell = TrayField("_shellCallInProgress").GetValue(null);
        object? savedPending = TrayField("_updatePending").GetValue(null);
        object? savedPendingCfg = TrayField("_pendingUpdateConfig").GetValue(null);
        try
        {
            TrayField("_initialized").SetValue(null, true);
            TrayField("_shellCallInProgress").SetValue(null, true);
            TrayField("_updatePending").SetValue(null, false);

            var config = new KoEnVue.App.Models.AppConfig();
            KoEnVue.App.UI.Tray.UpdateState(KoEnVue.App.Models.ImeState.English, config);

            Assert.True((bool)TrayField("_updatePending").GetValue(null)!, "재진입은 보류 표시를 남겨야 한다");
            // 더 새로운 상태를 들고 와야 한다 — 버리면 방금 바뀐 테마 색이 반영되지 않는다.
            Assert.Same(config, TrayField("_pendingUpdateConfig").GetValue(null));
        }
        finally
        {
            TrayField("_initialized").SetValue(null, savedInit);
            TrayField("_shellCallInProgress").SetValue(null, savedShell);
            TrayField("_updatePending").SetValue(null, savedPending);
            TrayField("_pendingUpdateConfig").SetValue(null, savedPendingCfg);
        }
    }

    // ================================================================
    // bug-hunt 3차 Q — 세션 위치 캐시는 config 변경을 인지해야 한다
    // ================================================================

    [Fact]
    public void config_에서_사라진_위치_기록은_세션_캐시도_버린다()
    {
        // _hwndPositions 는 드래그로만 채워지고 정리 경로가 죽은 창·HWND 재활용·상한뿐이라
        // config 변경에 대한 무효화가 없었다. GetAppPositionFixed 는 이 캐시를 **1순위**로
        // 조회하므로, 정리 창에서 항목을 지워도 그 창이 살아 있는 동안 옛 좌표가 계속 쓰였다.
        var positions = (Dictionary<IntPtr, (int x, int y, string process)>)Get("_hwndPositions")!;
        var saved = new Dictionary<IntPtr, (int, int, string)>(
            positions.ToDictionary(kv => kv.Key, kv => (kv.Value.x, kv.Value.y, kv.Value.process)));
        try
        {
            positions.Clear();
            positions[(IntPtr)0x2001] = (10, 20, "notepad");   // config 에도 있는 항목
            positions[(IntPtr)0x2002] = (30, 40, "chrome");    // 드래그만 하고 저장 전인 항목

            var prev = new KoEnVue.App.Models.AppConfig
            {
                IndicatorPositions = new() { ["notepad"] = [10, 20] },
            };
            var next = new KoEnVue.App.Models.AppConfig
            {
                IndicatorPositions = new(),   // 사용자가 정리 창에서 notepad 기록을 지웠다
            };

            MethodInfo m = ProgramType.GetMethod("InvalidateSessionPositions", PrivateStatic)!;
            m.Invoke(null, [prev, next]);

            Assert.False(positions.ContainsKey((IntPtr)0x2001), "config 에서 지운 항목은 캐시도 버려야 한다");
            Assert.True(positions.ContainsKey((IntPtr)0x2002),
                "config 에 없던 세션 전용 위치는 무관한 리로드가 지우면 안 된다");
        }
        finally
        {
            positions.Clear();
            foreach ((IntPtr hwnd, var entry) in saved) positions[hwnd] = entry;
        }
    }

    // ================================================================
    // bug-hunt 3차 A·B — 가드와 보류 소비는 한 쌍이다
    // ================================================================

    [Fact]
    public void 중첩된_셸_호출은_바깥_프레임의_가드를_풀지_않는다()
    {
        // 종전에는 가드 대입이 호출 지점마다 흩어져 있어(`_shellCallInProgress = true/false`),
        // 안쪽 프레임의 finally 가 바깥 구간의 가드까지 내려 **그 구간의 재진입이 다시 열렸다**.
        // RunShellCall 은 최외곽 프레임만 가드를 되돌린다.
        MethodInfo run = typeof(KoEnVue.App.UI.Tray).GetMethod("RunShellCall", PrivateStatic)
            ?? throw new InvalidOperationException("Tray.RunShellCall not found — 이름이 바뀌었으면 테스트도 갱신할 것.");

        object? savedShell = TrayField("_shellCallInProgress").GetValue(null);
        try
        {
            TrayField("_shellCallInProgress").SetValue(null, false);

            bool guardAfterInner = false;
            Action outer = () =>
            {
                // 안쪽 프레임 — 아무것도 하지 않고 빠져나온다(셸/GDI 미접촉).
                run.Invoke(null, [(Action)(() => { }), false]);
                guardAfterInner = (bool)TrayField("_shellCallInProgress").GetValue(null)!;
            };

            run.Invoke(null, [outer, false]);

            Assert.True(guardAfterInner, "중첩 프레임이 끝나도 바깥 구간의 가드는 유지돼야 한다");
            Assert.False((bool)TrayField("_shellCallInProgress").GetValue(null)!,
                "최외곽 프레임은 가드를 반드시 되돌려야 한다");
        }
        finally
        {
            TrayField("_shellCallInProgress").SetValue(null, savedShell);
        }
    }

    [Fact]
    public void 아이콘_제거_구간에_들어온_갱신은_버려지지_않는다()
    {
        // Recreate 는 Remove → Initialize 를 잇는다. 제거 구간(NIM_DELETE, 블로킹 IPC)에 들어온
        // 더 새로운 상태를 Remove 의 finally 가 지워 버리면, 뒤따르는 Initialize 의 드레인이
        // 소비할 것이 남지 않아 **그 갱신은 영영 반영되지 않는다**. 제거 중에 재생하지 않되
        // 표식은 남기는 것이 이 설계의 요지다.
        //
        // _notifyIcon = null 이라 `notify?.Remove() ?? true` 로 셸에 닿지 않고,
        // _hwndMain = Zero 라 StopAddRetryTimer 의 KillTimer 도 건너뛴다.
        object? savedInit = TrayField("_initialized").GetValue(null);
        object? savedHwnd = TrayField("_hwndMain").GetValue(null);
        object? savedNotify = TrayField("_notifyIcon").GetValue(null);
        object? savedIcon = TrayField("_currentIcon").GetValue(null);
        object? savedPending = TrayField("_updatePending").GetValue(null);
        object? savedPendingCfg = TrayField("_pendingUpdateConfig").GetValue(null);
        try
        {
            TrayField("_initialized").SetValue(null, true);
            TrayField("_hwndMain").SetValue(null, IntPtr.Zero);
            TrayField("_notifyIcon").SetValue(null, null);
            TrayField("_currentIcon").SetValue(null, null);

            var newer = new KoEnVue.App.Models.AppConfig();
            TrayField("_updatePending").SetValue(null, true);
            TrayField("_pendingUpdateConfig").SetValue(null, newer);

            KoEnVue.App.UI.Tray.Remove();

            Assert.True((bool)TrayField("_updatePending").GetValue(null)!,
                "제거 구간의 보류 표식은 뒤따르는 Initialize 가 소비하도록 남아야 한다");
            Assert.Same(newer, TrayField("_pendingUpdateConfig").GetValue(null));
            Assert.False((bool)TrayField("_initialized").GetValue(null)!);
        }
        finally
        {
            TrayField("_initialized").SetValue(null, savedInit);
            TrayField("_hwndMain").SetValue(null, savedHwnd);
            TrayField("_notifyIcon").SetValue(null, savedNotify);
            TrayField("_currentIcon").SetValue(null, savedIcon);
            TrayField("_updatePending").SetValue(null, savedPending);
            TrayField("_pendingUpdateConfig").SetValue(null, savedPendingCfg);
        }
    }
}
