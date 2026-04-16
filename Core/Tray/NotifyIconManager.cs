using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Tray;

/// <summary>
/// Shell_NotifyIconW 수명주기 래퍼. NIM_ADD / NIM_MODIFY / NIM_DELETE / NIM_SETVERSION 과
/// NOTIFYICON_VERSION_4 트레이 프로토콜을 단일 관리자 객체로 캡슐화한다.
///
/// 책임 경계:
/// - 본 클래스는 <c>IntPtr hIcon</c> 을 파라미터로 받지만 <b>소유권을 가지지 않는다</b>.
///   호출자(상위 레이어의 <see cref="SafeIconHandle"/>)가 HICON 의 생명주기를 관리하며,
///   본 클래스에서 DestroyIcon 류 정리를 수행하면 이중 해제가 발생하므로 절대 금지한다.
/// - 메뉴 구성 / TrackPopupMenu / 로컬라이제이션 문자열 생성 / 아이콘 렌더링(앱별 상태 의존) 등은
///   호출자 측에 남고, 본 클래스는 순수하게 shell32 프로토콜만 다룬다.
/// - NOTIFYICON_VERSION_4 는 기본적으로 표준 툴팁(szTip) 호버 표시를 억제하므로,
///   NIM_ADD / NIM_MODIFY 양쪽 모두 uFlags 에 NIF_SHOWTIP 를 포함해야 한다 (Win7+).
/// </summary>
internal sealed class NotifyIconManager
{
    /// <summary>szTip fixed-char 버퍼 크기(128)에서 널 종료자 1자를 뺀 복사 상한.</summary>
    private const int TooltipMaxLength = 127;

    private readonly IntPtr _hwndOwner;
    private readonly uint _callbackMessage;
    private readonly Guid _iconGuid;
    private bool _added;

    public NotifyIconManager(IntPtr hwndOwner, uint callbackMessage, Guid iconGuid)
    {
        _hwndOwner = hwndOwner;
        _callbackMessage = callbackMessage;
        _iconGuid = iconGuid;
    }

    /// <summary>
    /// NIM_ADD 로 트레이 아이콘을 등록하고, 곧바로 NIM_SETVERSION 으로
    /// NOTIFYICON_VERSION_4 트레이 프로토콜을 활성화한다.
    /// hIcon 은 호출자 소유이며 본 클래스는 해제하지 않는다.
    /// </summary>
    public unsafe void Add(IntPtr hIcon, string? tip)
    {
        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwndOwner;
        nid.uFlags = Win32Constants.NIF_MESSAGE | Win32Constants.NIF_ICON
                   | Win32Constants.NIF_TIP | Win32Constants.NIF_SHOWTIP | Win32Constants.NIF_GUID;
        nid.uCallbackMessage = _callbackMessage;
        nid.hIcon = hIcon;
        nid.guidItem = _iconGuid;

        CopyTooltip(ref nid, tip);

        if (!Shell32.Shell_NotifyIconW(Win32Constants.NIM_ADD, ref nid))
        {
            Logger.Warning("Shell_NotifyIconW NIM_ADD failed");
            return;
        }

        // NOTIFYICON_VERSION_4 활성화 — WM_CONTEXTMENU 전달 + NIF_SHOWTIP 요구 사항의 전제.
        nid.uVersion = Win32Constants.NOTIFYICON_VERSION_4;
        if (!Shell32.Shell_NotifyIconW(Win32Constants.NIM_SETVERSION, ref nid))
            Logger.Warning("Shell_NotifyIconW NIM_SETVERSION failed");

        _added = true;
    }

    /// <summary>
    /// NIM_MODIFY 로 아이콘만 갱신한다. 툴팁은 유지되지 않고 szTip 은 빈 값으로 설정된다
    /// (NIF_TIP 플래그가 설정되지 않으므로 shell 은 기존 값을 건드리지 않는다).
    /// hIcon 은 호출자 소유이며 본 클래스는 해제하지 않는다.
    /// </summary>
    public unsafe void UpdateIcon(IntPtr hIcon)
    {
        if (!_added) return;

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwndOwner;
        nid.uFlags = Win32Constants.NIF_ICON | Win32Constants.NIF_GUID;
        nid.hIcon = hIcon;
        nid.guidItem = _iconGuid;

        Shell32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// NIM_MODIFY 로 툴팁만 갱신한다. NOTIFYICON_VERSION_4 에서 호버 표시가 보존되도록
    /// NIF_SHOWTIP 플래그를 항상 함께 설정한다.
    /// </summary>
    public unsafe void UpdateTooltip(string? tip)
    {
        if (!_added) return;

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwndOwner;
        nid.uFlags = Win32Constants.NIF_TIP | Win32Constants.NIF_SHOWTIP | Win32Constants.NIF_GUID;
        nid.guidItem = _iconGuid;

        CopyTooltip(ref nid, tip);

        Shell32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// 아이콘 + 툴팁을 단일 NIM_MODIFY 호출로 동시에 갱신한다.
    /// IME 상태 전환 시(Tray.UpdateState)처럼 두 값이 함께 변하는 경로에서 사용.
    /// hIcon 은 호출자 소유이며 본 클래스는 해제하지 않는다.
    /// </summary>
    public unsafe void UpdateIconAndTooltip(IntPtr hIcon, string? tip)
    {
        if (!_added) return;

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.hWnd = _hwndOwner;
        nid.uFlags = Win32Constants.NIF_ICON | Win32Constants.NIF_TIP
                   | Win32Constants.NIF_SHOWTIP | Win32Constants.NIF_GUID;
        nid.hIcon = hIcon;
        nid.guidItem = _iconGuid;

        CopyTooltip(ref nid, tip);

        Shell32.Shell_NotifyIconW(Win32Constants.NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// NIM_DELETE 로 트레이 아이콘을 제거한다. 성공 여부를 bool 로 반환.
    /// 호출자(Tray.cs)는 SafeIconHandle 을 별도로 Dispose 해야 한다.
    /// </summary>
    public unsafe bool Remove()
    {
        if (!_added) return true;

        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.uFlags = Win32Constants.NIF_GUID;
        nid.guidItem = _iconGuid;

        bool removed = Shell32.Shell_NotifyIconW(Win32Constants.NIM_DELETE, ref nid);
        _added = false;
        return removed;
    }

    /// <summary>
    /// <c>NOTIFYICONDATAW.szTip</c> fixed-char 버퍼(길이 128)에 툴팁 텍스트를 복사한다.
    /// null/빈 문자열이면 복사를 생략해 기본적으로 빈 툴팁이 유지된다.
    /// </summary>
    private static unsafe void CopyTooltip(ref NOTIFYICONDATAW nid, string? tip)
    {
        if (string.IsNullOrEmpty(tip)) return;

        ReadOnlySpan<char> src = tip.AsSpan();
        int len = Math.Min(src.Length, TooltipMaxLength);
        fixed (char* pTip = nid.szTip)
        {
            src[..len].CopyTo(new Span<char>(pTip, TooltipMaxLength + 1));
        }
    }
}
