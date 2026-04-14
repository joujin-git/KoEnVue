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
/// </summary>
internal static class ModalDialogLoop
{
    /// <summary>
    /// 소유자 비활성화 → 중첩 메시지 루프 → 소유자 재활성화 + 포그라운드 복원.
    /// </summary>
    /// <param name="hwndDialog">다이얼로그 윈도우 핸들. IsDialogMessageW 전처리 대상.</param>
    /// <param name="hwndOwner">모달 소유자(= 메인 윈도우) 핸들.</param>
    /// <param name="isClosedFlag">WndProc 가 true 로 전환하면 루프 종료.</param>
    public static void Run(IntPtr hwndDialog, IntPtr hwndOwner, ref bool isClosedFlag)
    {
        User32.EnableWindow(hwndOwner, false);

        while (!isClosedFlag)
        {
            int ret = User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break; // WM_QUIT(0) 또는 오류(-1) — 즉시 탈출
            if (!User32.IsDialogMessageW(hwndDialog, ref msg))
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }

        User32.EnableWindow(hwndOwner, true);
        User32.SetForegroundWindow(hwndOwner);
    }
}
