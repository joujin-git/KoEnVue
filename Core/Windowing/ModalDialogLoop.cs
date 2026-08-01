using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// Win32 무-부모 모달 다이얼로그의 공용 메시지 루프.
/// 프로젝트의 세 다이얼로그(CleanupDialog / ScaleInputDialog / SettingsDialog)는 모두
/// 동일한 패턴을 반복한다:
///   1) 소유자 윈도우 비활성화 (EnableWindow(owner, false))
///   2) 다이얼로그 WndProc 가 종료 플래그를 참으로 세팅할 때까지 중첩 GetMessageW
///      — Tab / Enter / ESC 처리를 위해 IsDialogMessageW 로 전처리
///   3) 소유자 윈도우 재활성화 + 포커스 복원 (EnableWindow(true) + SetForegroundWindow(owner))
///
/// 종료 시그널은 <c>ref bool</c> 플래그를 사용한다. 각 다이얼로그가 이미 static bool
/// 필드를 갖고 있고, PostQuitMessage / WM_QUIT 을 쓰면 메인 메시지 루프 전체가 종료되므로
/// 부적합하다. DestroyWindow 와 자식 컨트롤 정리는 호출자가 직접 수행한다
/// (다이얼로그별 정리 순서가 달라 공통화하지 않는다).
///
/// <para>
/// 재진입 가드: <see cref="IsActive"/> / <see cref="ActiveDialog"/> 를 통해 현재 활성
/// 모달 다이얼로그를 추적한다. 트레이 메뉴(shell32 관리, EnableWindow 무관)나 핫키
/// 경로에서 동일 또는 다른 다이얼로그가 중복 호출되면 호출자는 early-return 하고 기존
/// 창으로 포커스를 복원해야 한다. 루프 진입 시 <see cref="Run"/> 이 자동으로 플래그를
/// 세팅하고, finally 로 해제 보장.
/// </para>
/// </summary>
internal static class ModalDialogLoop
{
    // s_activeDialog 는 UI 스레드에서 쓰이고 감지 스레드(DetectionService.RunLoop)에서도
    // IsActive 를 읽는다. IntPtr 은 volatile 키워드를 받지 않으므로 모든 접근에
    // Volatile.Read/Write 를 명시해 스레드 가시성을 보장한다.
    private static IntPtr s_activeDialog;

    /// <summary>
    /// <see cref="RunExternal"/> 이 실제 HWND 대신 세우는 표식. MessageBoxW 처럼 창 핸들을
    /// 넘겨받지 못하는 외부 모달 구간에서도 <see cref="IsActive"/> 를 참으로 유지하기 위한 값이며,
    /// 진짜 창이 아니므로 <see cref="RejectReentry"/> 의 포커스 복원 대상에서 제외된다 — P3
    /// (매직 리터럴 금지). IntPtr 은 const 를 받지 못해 static readonly 로 둔다.
    /// </summary>
    private static readonly IntPtr ExternalModalSentinel = (IntPtr)(-1);

    /// <summary>
    /// 현재 활성 모달 다이얼로그 존재 여부.
    /// UI 스레드(재진입 가드) + 감지 스레드(DetectionService 게이트) 양쪽에서 읽힌다.
    /// </summary>
    public static bool IsActive => Volatile.Read(ref s_activeDialog) != IntPtr.Zero;

    /// <summary>
    /// 현재 활성 모달 다이얼로그 HWND. 재진입 감지 시 이 창에 포커스를 복원하기 위한
    /// 참조용. <see cref="IsActive"/> 가 false 일 때는 <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static IntPtr ActiveDialog => Volatile.Read(ref s_activeDialog);

    /// <summary>
    /// 재진입 판정 + 기존 모달로의 포커스 복원. 활성 모달이 있으면 그 창을 앞으로 끌어내고
    /// <c>true</c> 를 반환한다 — 호출자는 <b>공유 상태를 건드리기 전에</b> 이 판정을 통과시켜야 한다.
    ///
    /// <para>
    /// 이 헬퍼가 필요한 이유: 각 다이얼로그의 <c>Show()</c> 는 <see cref="DialogShell.Run"/> 을
    /// 부르기 <b>전에</b> 자기 정적 상태(항목 리스트·작업 중 config·컨트롤 HWND)를 리셋하고,
    /// <c>Run</c> 이 재진입으로 <c>false</c> 를 반환해도 에필로그가 그대로 실행돼 그 상태를 파괴한다.
    /// 살아 있던 첫 다이얼로그는 그 뒤 자기 상태를 참조하다 <c>NullReferenceException</c> 을 내고,
    /// WndProc 은 <c>[UnmanagedCallersOnly]</c> 라 관리 예외가 <c>DispatchMessageW</c> 경계를 넘어
    /// NativeAOT 가 프로세스를 종료한다. 따라서 판정은 <c>Show()</c> <b>첫 문장</b>이어야 하며,
    /// 판정 로직이 호출처마다 복제되지 않도록 여기 한 곳에 둔다 — P4 (no duplicate impl).
    /// </para>
    ///
    /// <para>
    /// 모달 중에도 재진입이 성립하는 이유 — <see cref="Run"/> 의 <c>EnableWindow(owner, false)</c> 는
    /// 마우스·키보드 입력만 막고, 중첩 루프의 <c>GetMessageW</c> 는 필터가 없어 explorer 가 소유한
    /// 트레이 아이콘이 post 하는 <c>WM_TRAY_CALLBACK</c> 을 그대로 디스패치한다.
    /// </para>
    /// </summary>
    /// <returns>활성 모달이 있어 호출자가 조기 반환해야 하면 true.</returns>
    public static bool RejectReentry()
    {
        IntPtr active = Volatile.Read(ref s_activeDialog);
        if (active == IntPtr.Zero)
            return false;
        // 센티넬은 실제 창이 아니므로 SetForegroundWindow 대상에서 제외 (외부 모달은 Win32 가 이미 포그라운드).
        if (active != ExternalModalSentinel)
            User32.SetForegroundWindow(active);
        return true;
    }

    /// <summary>
    /// 소유자 비활성화 → 중첩 메시지 루프 → 소유자 재활성화 + 포그라운드 복원.
    /// 진입 시 <see cref="s_activeDialog"/> 에 현재 다이얼로그 HWND 를 기록하고,
    /// try/finally 로 해제를 보장한다 (예외가 전파되어도 가드가 누수되지 않음).
    /// 외부에서 PostQuitMessage 로 WM_QUIT 가 도착하면 루프를 탈출한 뒤 WM_QUIT 를
    /// 재전달하여 메인 메시지 루프도 종료될 수 있도록 한다.
    /// </summary>
    /// <param name="hwndDialog">다이얼로그 윈도우 핸들. IsDialogMessageW 전처리 대상.</param>
    /// <param name="hwndOwner">모달 소유자(= 메인 윈도우) 핸들.</param>
    /// <param name="isClosedFlag">WndProc 가 true 로 전환하면 루프 종료.</param>
    public static void Run(IntPtr hwndDialog, IntPtr hwndOwner, ref bool isClosedFlag)
    {
        Volatile.Write(ref s_activeDialog, hwndDialog);
        User32.EnableWindow(hwndOwner, false);

        bool quitReceived = false;
        int quitCode = 0;

        try
        {
            while (!isClosedFlag)
            {
                int ret = User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
                if (ret <= 0)
                {
                    // WM_QUIT(ret=0): 이 중첩 루프가 소비했으므로 루프 탈출 후 재전달
                    if (ret == 0)
                    {
                        quitReceived = true;
                        quitCode = (int)msg.wParam;
                    }
                    break;
                }
                if (!User32.IsDialogMessageW(hwndDialog, ref msg))
                {
                    User32.TranslateMessage(ref msg);
                    User32.DispatchMessageW(ref msg);
                }
            }
        }
        finally
        {
            User32.EnableWindow(hwndOwner, true);
            User32.SetForegroundWindow(hwndOwner);
            Volatile.Write(ref s_activeDialog, IntPtr.Zero);
        }

        // WM_QUIT 가 이 중첩 루프에서 소비되었으므로 외부 메시지 루프에 재전달
        if (quitReceived)
            User32.PostQuitMessage(quitCode);
    }

    /// <summary>
    /// MessageBoxW 등 Win32 가 자체 메시지 루프를 돌리는 외부 모달 구간에 대해
    /// <see cref="IsActive"/> 가드만 씌운다. <see cref="Run"/> 과 달리 메시지 펌프나
    /// EnableWindow 는 건드리지 않으며, 감지 스레드의 폴링 사이드-이펙트(인디가
    /// 모달 HWND 근처로 튀는 현상)만 억제하는 용도.
    /// 기존에 활성 모달이 있으면 스택처럼 이전 값을 보관 후 finally 에서 복원한다.
    /// </summary>
    public static void RunExternal(IntPtr hwndSentinel, Action action)
    {
        IntPtr prev = Volatile.Read(ref s_activeDialog);
        // IntPtr.Zero 가 넘어와도 IsActive 가 true 로 유지되도록 sentinel 로 대체.
        Volatile.Write(ref s_activeDialog,
            hwndSentinel != IntPtr.Zero ? hwndSentinel : ExternalModalSentinel);
        try { action(); }
        finally { Volatile.Write(ref s_activeDialog, prev); }
    }
}
