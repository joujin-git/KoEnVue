using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue;

/// <summary>
/// 플로팅 배지 좌클릭 일시 숨김 · 드래그 승격 · 위치 저장.
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 오버레이 좌버튼 다운 — 캡처 + 원점 기록. 이후 MOVE 에서 드래그 승격, UP 에서 클릭 숨김.
    /// </summary>
    private static void BeginOverlayPointerTrack()
    {
        User32.SetCapture(_hwndOverlay);
        User32.GetCursorPos(out POINT pt);
        _overlayDragOriginX = pt.X;
        _overlayDragOriginY = pt.Y;
        _overlayDragPending = true;
        _overlayDragPromoted = false;
    }

    /// <summary>
    /// 캡처 중 마우스 이동이 시스템 드래그 임계를 넘고 drag_modifier 가 맞으면
    /// 네이티브 HTCAPTION 드래그로 승격한다.
    /// </summary>
    private static void TryPromoteOverlayDrag(IntPtr hwnd)
    {
        if (!User32.GetCursorPos(out POINT pt)) return;

        int cxDrag = User32.GetSystemMetrics(Win32Constants.SM_CXDRAG);
        int cyDrag = User32.GetSystemMetrics(Win32Constants.SM_CYDRAG);
        int dx = Math.Abs(pt.X - _overlayDragOriginX);
        int dy = Math.Abs(pt.Y - _overlayDragOriginY);
        if (dx < cxDrag && dy < cyDrag) return;
        if (!IsDragModifierPressed(_config.DragModifier)) return;

        _overlayDragPromoted = true;
        _overlayDragPending = false; // 네이티브 sizemove 로 이관 — LBUTTONUP 재진입 방지
        User32.ReleaseCapture();
        // 화면 좌표를 lParam 에 담아 기존 ENTERSIZEMOVE / MOVING / EXITSIZEMOVE 경로 재사용.
        // 멀티모니터 음수 좌표도 LOWORD/HIWORD 로 올바르게 패킹 (부호 확장 방지).
        int packed = (pt.X & (int)Win32Constants.LOWORD_MASK)
            | ((pt.Y & (int)Win32Constants.LOWORD_MASK) << 16);
        User32.SendMessageW(hwnd, Win32Constants.WM_NCLBUTTONDOWN,
            (IntPtr)Win32Constants.HTCAPTION, (IntPtr)packed);
    }

    /// <summary>
    /// 좌버튼 업 — 승격되지 않은 짧은 클릭이면 일시 숨김(클릭 자리 마우스 통과).
    /// </summary>
    private static void EndOverlayPointerTrack(bool dismissIfClick)
    {
        _overlayDragPending = false;
        if (User32.GetCapture() == _hwndOverlay)
            User32.ReleaseCapture();

        if (!dismissIfClick || _overlayDragPromoted) return;
        if (_config.UserHidden || !_indicatorVisible) return;

        _clickDismissed = true;
        HideOverlay("click dismiss");
    }

    /// <summary>
    /// 현재 드래그 활성 키 설정에 해당하는 모디파이어가 눌려 있는지 확인.
    /// None 모드는 항상 true — 임계 초과 시 드래그 승격 가능.
    /// WM_MOUSEMOVE 메인 스레드에서 호출 — GetAsyncKeyState 스레드 제약 없음.
    /// </summary>
    private static bool IsDragModifierPressed(DragModifier mode)
    {
        if (mode == DragModifier.None) return true;

        bool ctrl = (User32.GetAsyncKeyState(Win32Constants.VK_CONTROL) & Win32Constants.KEY_PRESSED) != 0;
        bool alt  = (User32.GetAsyncKeyState(Win32Constants.VK_MENU) & Win32Constants.KEY_PRESSED) != 0;
        return mode switch
        {
            DragModifier.Ctrl    => ctrl && !alt,
            DragModifier.Alt     => alt && !ctrl,
            DragModifier.CtrlAlt => ctrl && alt,
            _                    => true,
        };
    }

    /// <summary>
    /// 오버레이 드래그 종료 → 새 위치를 현재 앱에 저장.
    /// 시스템 입력 프로세스(시작 메뉴, 검색 창)는 z-band 한계로 창 위에 띄울 수 없어
    /// 사용자가 드래그해 옮긴 위치가 가려지면 다시 잡을 수 없게 된다.
    /// 저장하지 않고 항상 기본 위치를 사용한다.
    /// </summary>
    private static void HandleOverlayDragEnd()
    {
        var (x, y) = Overlay.EndDrag();

        if (DefaultConfig.IsSystemInputProcess(_currentProcessName))
        {
            Logger.Debug($"Skip saving indicator position for system input process: {_currentProcessName}");
            Overlay.Show(x, y, _lastImeState, ResolveCurrent());
            return;
        }

        if (_config.PositionMode == PositionMode.Window)
        {
            // 창 기준 모드: 절대좌표 → 창 기준 상대 오프셋으로 변환 후 저장.
            // 상대값은 창 프레임 기준이므로 저장 전 work-area 클램프를 하지 않는다
            // (클램프하면 오프셋이 오염됨). 표시용 절대좌표만 Show 직전에 클램프.
            if (_currentProcessName.Length > 0)
            {
                RelativePositionConfig? rel =
                    Overlay.ComputeRelativeFromCurrentPosition(_lastForegroundHwnd);
                if (rel is not null)
                {
                    var positions = new Dictionary<string, int[]>(
                        _config.IndicatorPositionsRelative)
                    {
                        [_currentProcessName] = [(int)rel.Corner, rel.DeltaX, rel.DeltaY]
                    };
                    _config = _config with { IndicatorPositionsRelative = positions };
                    Settings.Save(_config);
                    Logger.Debug($"Saved relative position for {_currentProcessName}: "
                        + $"corner={rel.Corner}, delta=({rel.DeltaX}, {rel.DeltaY}) logical px");
                }
            }
        }
        else
        {
            // 고정 모드: 저장 전 work area 로 클램프 — config.json 에 off-screen 좌표가
            // 영구 기록되지 않도록 방어. 읽기 경로(GetAppPositionFixed)도 클램프하지만
            // 저장 시점에 정제해 두면 설정 파일 값 품질이 보장된다.
            (x, y) = ClampToVisibleArea(x, y);

            if (_lastForegroundHwnd != IntPtr.Zero)
                _hwndPositions[_lastForegroundHwnd] = (x, y);
            if (_currentProcessName.Length > 0)
            {
                var positions = new Dictionary<string, int[]>(_config.IndicatorPositions)
                {
                    [_currentProcessName] = [x, y]
                };
                _config = _config with { IndicatorPositions = positions };
                Settings.Save(_config);
                Logger.Debug($"Saved indicator position for {_currentProcessName}: ({x}, {y})");
            }
        }
        // Window 모드: 상대 저장은 드래그 원좌표 기준 유지, Show 만 화면 안으로.
        // Fixed 모드는 위에서 이미 클램프됨(idempotent).
        (x, y) = ClampToVisibleArea(x, y);
        // 새 위치의 모니터 DPI로 리소스 재생성
        Overlay.Show(x, y, _lastImeState, ResolveCurrent());
    }
}
