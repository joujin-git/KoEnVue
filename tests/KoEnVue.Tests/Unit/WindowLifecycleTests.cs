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
}
