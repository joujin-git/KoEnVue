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
    private static IntPtr s_activeDialog;

    /// <summary>현재 활성 모달 다이얼로그 존재 여부. UI 스레드에서만 호출.</summary>
    public static bool IsActive => s_activeDialog != IntPtr.Zero;

    /// <summary>
    /// 현재 활성 모달 다이얼로그 HWND. 재진입 감지 시 이 창에 포커스를 복원하기 위한
    /// 참조용. <see cref="IsActive"/> 가 false 일 때는 <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static IntPtr ActiveDialog => s_activeDialog;

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
        s_activeDialog = hwndDialog;
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
            s_activeDialog = IntPtr.Zero;
        }

        // WM_QUIT 가 이 중첩 루프에서 소비되었으므로 외부 메시지 루프에 재전달
        if (quitReceived)
            User32.PostQuitMessage(quitCode);
    }
}
