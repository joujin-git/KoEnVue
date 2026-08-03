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
    /// 모달 구간에 소유자와 <b>함께</b> 비활성화할 창을 공급한다. App 이 부팅 시 1회 배선하며,
    /// <see cref="Run"/> 진입 시점에 조회되므로 런타임에 생성·파괴되는 창도 안전하다.
    ///
    /// <para>
    /// 필요한 이유: <c>EnableWindow(owner, false)</c> 는 <b>소유자 하나만</b> 막는데, 소유자를 갖지
    /// 않는 별도 최상위 창은 그 대상이 아니다. KoEnVue 의 한/영 배지가 그런 창이라(WS_EX_TOPMOST,
    /// hWndParent=NULL) 다이얼로그가 떠 있는 동안에도 드래그되고, 드래그 종료가 <c>Settings.Save</c>
    /// 까지 수행해 <b>열려 있는 다이얼로그 뒤에서 설정이 갈아치워진다</b>. 그 뒤 「확인」 을 누르면
    /// 다이얼로그의 커밋 베이스가 방금 저장한 값을 되돌린다 (bug-hunt 2026-08-02 확정 #39).
    /// </para>
    ///
    /// <para>
    /// Core 는 App 의 창을 알 수 없으므로(P6) 공급자로 받는다. 미배선이면 종전 동작 그대로다.
    /// </para>
    /// </summary>
    public static Func<IntPtr[]>? ExtraModalWindows { get; set; }

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
        // 보이지 않는 창도 마찬가지다 — 포커스를 주면 사용자에게는 "아무것도 없는 곳으로 포커스가
        // 사라진" 상태가 되고, KoEnVue 의 경우 그 창이 자기 프로세스라 감지 루프의 self-HWND 가드에
        // 매 틱 걸려 배지가 되살아나지 않는다 (bug-hunt 2026-08-02 확정 #29·#41).
        if (active != ExternalModalSentinel && User32.IsWindowVisible(active))
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

        // 소유자 밖의 최상위 창(한/영 배지 등)도 함께 막는다 — 자세한 이유는
        // <see cref="ExtraModalWindows"/>. 진입 시 한 번 조회해 finally 에서 같은 목록을 되살린다
        // (그 사이 창이 파괴되면 EnableWindow 가 무해하게 실패할 뿐이다).
        IntPtr[] extraDisabled = ExtraModalWindows?.Invoke() ?? [];
        foreach (IntPtr hwndExtra in extraDisabled)
            User32.EnableWindow(hwndExtra, false);

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
            foreach (IntPtr hwndExtra in extraDisabled)
                User32.EnableWindow(hwndExtra, true);
            User32.EnableWindow(hwndOwner, true);

            // **보이지 않는 소유자에게 포커스를 주면 안 된다.** KoEnVue 의 소유자는 0×0 · 스타일 0 의
            // 메시지 전용 창이라(Program.Bootstrap.CreateMainWindow) SetForegroundWindow 가 성공은
            // 하지만, 그 결과 포그라운드가 자기 프로세스의 보이지 않는 창이 된다 — 감지 루프가
            // self-HWND 가드(`hwndForeground == GetHwndMain()`)에 매 틱 걸려 아무것도 post 하지
            // 않고, 모달 게이트가 이미 배지를 숨겨 둔 상태라 사용자가 다른 창을 직접 클릭할 때까지
            // 배지가 돌아오지 않는다 (확정 #29). 가시 소유자면 종전대로 복원한다 — 그쪽이 모달의
            // 정석이고, 이 루프는 Core 의 재사용 컴포넌트라 그 경우도 지켜야 한다.
            if (User32.IsWindowVisible(hwndOwner))
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
