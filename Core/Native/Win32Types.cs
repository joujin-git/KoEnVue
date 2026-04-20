using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

// ================================================================
// 구조체
// ================================================================

[StructLayout(LayoutKind.Sequential)]
internal struct GUITHREADINFO
{
    public uint cbSize;
    public uint flags;
    public IntPtr hwndActive;
    public IntPtr hwndFocus;
    public IntPtr hwndCapture;
    public IntPtr hwndMenuOwner;
    public IntPtr hwndMoveSize;
    public IntPtr hwndCaret;
    public RECT rcCaret;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;

    public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
}

/// <summary>
/// GetTextMetricsW 출력. 폰트 셀 메트릭으로, DT_VCENTER가 셀 중앙(tmAscent+tmDescent의 중점)을
/// 기준으로 정렬하기 때문에 발생하는 시각적 하향 치우침을 보정하기 위해 사용한다.
/// tmInternalLeading은 라틴 액센트용 상단 reserved 영역으로 한글/대문자 영문에는 비어 있어,
/// tmInternalLeading &gt; tmDescent인 폰트는 글리프가 cell 중앙보다 아래로 치우친다.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct TEXTMETRICW
{
    public int tmHeight;
    public int tmAscent;
    public int tmDescent;
    public int tmInternalLeading;
    public int tmExternalLeading;
    public int tmAveCharWidth;
    public int tmMaxCharWidth;
    public int tmWeight;
    public int tmOverhang;
    public int tmDigitizedAspectX;
    public int tmDigitizedAspectY;
    public char tmFirstChar;
    public char tmLastChar;
    public char tmDefaultChar;
    public char tmBreakChar;
    public byte tmItalic;
    public byte tmUnderlined;
    public byte tmStruckOut;
    public byte tmPitchAndFamily;
    public byte tmCharSet;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    // CCHDEVICENAME = 32
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;

    // szTip: 128 chars
    public fixed char szTip[128];

    public uint dwState;
    public uint dwStateMask;

    // szInfo: 256 chars
    public fixed char szInfo[256];

    public uint uVersion;  // union with uTimeout

    // szInfoTitle: 64 chars
    public fixed char szInfoTitle[64];

    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;  // 실제로는 함수 포인터, Marshal.GetFunctionPointerForDelegate로 변환
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public IntPtr lpszMenuName;   // 항상 IntPtr.Zero (null 문자열 마샬링 NRE 방지)
    public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO
{
    [MarshalAs(UnmanagedType.Bool)]
    public bool fIcon;       // Win32 BOOL은 4바이트 — MarshalAs 필수
    public int xHotspot;
    public int yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct HIGHCONTRAST
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr lpszDefaultScheme;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SCROLLINFO
{
    public uint cbSize;
    public uint fMask;
    public int nMin;
    public int nMax;
    public uint nPage;
    public int nPos;
    public int nTrackPos;
}

// ================================================================
// 상수
// ================================================================

internal static class Win32Constants
{
    // --- 윈도우 확장 스타일 (dwExStyle) ---
    public const uint WS_EX_LAYERED      = 0x00080000;
    public const uint WS_EX_TOPMOST      = 0x00000008;
    public const uint WS_EX_TOOLWINDOW   = 0x00000080;
    public const uint WS_EX_NOACTIVATE   = 0x08000000;
    public const uint WS_EX_COMPOSITED   = 0x02000000;

    // --- 윈도우 스타일 ---
    public const uint WS_POPUP           = 0x80000000;
    public const uint WS_CAPTION         = 0x00C00000;  // WS_BORDER | WS_DLGFRAME
    public const uint WS_SYSMENU        = 0x00080000;
    public const uint WS_CHILD          = 0x40000000;
    public const uint WS_VISIBLE        = 0x10000000;
    public const uint WS_TABSTOP        = 0x00010000;
    public const uint WS_CLIPCHILDREN   = 0x02000000;

    // --- 버튼/체크박스 스타일 ---
    public const uint BS_AUTOCHECKBOX   = 0x00000003;
    public const uint BS_DEFPUSHBUTTON  = 0x00000001;
    public const uint BM_GETCHECK       = 0x00F0;
    public const uint BM_SETCHECK       = 0x00F1;
    public const nint BST_CHECKED       = 1;
    public const nint BST_UNCHECKED     = 0;

    // --- Edit 컨트롤 ---
    public const uint WS_BORDER         = 0x00800000;
    public const uint ES_LEFT           = 0x0000;
    public const uint ES_AUTOHSCROLL    = 0x0080;
    public const uint EM_SETSEL         = 0x00B1;

    // --- 다이얼로그 표준 ID ---
    public const int IDOK               = 1;
    public const int IDCANCEL           = 2;

    // --- GetWindowLongW 인덱스 ---
    public const int GWL_STYLE           = -16;

    // --- GetWindow 관계 ---
    public const uint GW_OWNER           = 4;

    // --- ShowWindow ---
    public const int SW_HIDE             = 0;
    public const int SW_SHOWNORMAL       = 1;
    public const int SW_SHOW             = 5;

    // --- System Metrics ---
    public const int SM_CYCAPTION        = 4;
    public const int SM_CXFIXEDFRAME     = 7;
    public const int SM_CYFIXEDFRAME     = 8;
    public const int SM_CXVSCROLL        = 2;
    public const int SM_CXSMICON         = 49;
    public const int SM_CYSMICON         = 50;
    public const int SM_CXPADDEDBORDER   = 92;

    // --- Hit Test ---
    public const uint WM_NCHITTEST       = 0x0084;
    public const nint HTCLIENT           = 1;
    public const nint HTCAPTION          = 2;

    // --- 윈도우 이동/리사이즈 ---
    public const uint WM_MOVING          = 0x0216;
    public const uint WM_ENTERSIZEMOVE   = 0x0231;
    public const uint WM_EXITSIZEMOVE    = 0x0232;

    // --- SendMessageTimeout 플래그 ---
    public const uint SMTO_ABORTIFHUNG   = 0x0002;

    // --- IME 메시지 ---
    public const uint WM_IME_CONTROL     = 0x0283;
    public const uint IMC_GETOPENSTATUS      = 0x0005;
    public const uint IMC_GETCONVERSIONMODE  = 0x0001;
    public const uint IME_CMODE_HANGUL       = 0x01;

    // --- WinEvent 상수 ---
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint EVENT_OBJECT_IME_CHANGE = 0x8029;  // Windows SDK WinUser.h 정의

    // --- MonitorFromPoint / MonitorFromWindow 플래그 ---
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // --- UpdateLayeredWindow ---
    public const uint ULW_ALPHA          = 0x00000002;
    public const byte AC_SRC_OVER        = 0x00;
    public const byte AC_SRC_ALPHA       = 0x01;

    // --- GDI ---
    public const int TRANSPARENT         = 1;
    public const uint BI_RGB             = 0;
    public const uint DIB_RGB_COLORS     = 0;

    // --- CreateFontW 파라미터 ---
    public const int FW_NORMAL           = 400;
    public const int FW_BOLD             = 700;
    public const byte DEFAULT_CHARSET    = 1;
    public const byte OUT_TT_PRECIS      = 4;
    public const byte CLIP_DEFAULT_PRECIS = 0;
    public const byte CLEARTYPE_QUALITY  = 5;
    public const byte DEFAULT_PITCH      = 0;

    // --- GDI stock objects ---
    public const int NULL_PEN            = 8;
    public const int NULL_BRUSH          = 5;

    // --- GDI pen styles ---
    public const int PS_SOLID            = 0;

    // --- DrawTextW 포맷 ---
    public const uint DT_CENTER          = 0x0001;
    public const uint DT_VCENTER         = 0x0004;
    public const uint DT_SINGLELINE      = 0x0020;

    // --- ChangeWindowMessageFilterEx 액션 (UIPI 필터) ---
    public const uint MSGFLT_ALLOW       = 1;

    // --- Shell_NotifyIconW ---
    public const uint NIM_ADD            = 0x00000000;
    public const uint NIM_MODIFY         = 0x00000001;
    public const uint NIM_DELETE         = 0x00000002;
    public const uint NIM_SETVERSION     = 0x00000004;
    public const uint NIF_MESSAGE        = 0x00000001;
    public const uint NIF_ICON           = 0x00000002;
    public const uint NIF_TIP            = 0x00000004;
    public const uint NIF_GUID           = 0x00000020;
    // NOTIFYICON_VERSION_4는 기본적으로 표준 툴팁을 억제한다. szTip을 계속 보이려면 NIF_SHOWTIP 필요.
    public const uint NIF_SHOWTIP        = 0x00000080;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // --- 표준 Win32 메시지 ---
    public const uint WM_NULL            = 0x0000;
    public const uint WM_CLOSE           = 0x0010;
    public const uint WM_DESTROY         = 0x0002;
    public const uint WM_TIMER           = 0x0113;
    public const uint WM_COMMAND         = 0x0111;
    public const uint WM_CONTEXTMENU     = 0x007B;
    public const uint WM_POWERBROADCAST  = 0x0218;
    public const uint WM_DISPLAYCHANGE   = 0x007E;
    public const uint WM_SETTINGCHANGE   = 0x001A;
    public const uint WM_SETFONT         = 0x0030;
    public const uint WM_DPICHANGED     = 0x02E0;
    public const uint WM_LBUTTONUP       = 0x0202;
    public const uint WM_APP             = 0x8000;
    public const uint WM_USER            = 0x0400;

    // --- 전원 관리 ---
    public const uint PBT_APMRESUMESUSPEND = 0x0007;

    // --- 세션 알림 (Wtsapi32) ---
    // WTSRegisterSessionNotification dwFlags: 현재 세션만 수신.
    public const uint NOTIFY_FOR_THIS_SESSION = 0;
    // 세션 상태 변경 메시지 — wParam 이 WTS_SESSION_* 이벤트 ID.
    public const uint WM_WTSSESSION_CHANGE   = 0x02B1;
    public const uint WTS_SESSION_LOCK       = 0x7;
    public const uint WTS_SESSION_UNLOCK     = 0x8;

    // --- 메뉴 ---
    public const uint MF_STRING          = 0x0000;
    public const uint MF_SEPARATOR       = 0x0800;
    public const uint MF_POPUP           = 0x0010;
    public const uint MF_CHECKED         = 0x0008;
    public const uint MF_UNCHECKED       = 0x0000;
    public const uint MF_GRAYED          = 0x0001;
    public const uint MF_BYCOMMAND       = 0x0000;
    public const uint TPM_BOTTOMALIGN    = 0x0020;
    public const uint TPM_LEFTALIGN      = 0x0000;
    public const uint TPM_RIGHTBUTTON    = 0x0002;
    public const uint TPM_RETURNCMD      = 0x0100;

    // --- SetWindowPos ---
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOMOVE         = 0x0002;
    public const uint SWP_NOSIZE         = 0x0001;
    public const uint SWP_NOACTIVATE     = 0x0010;

    // --- 입력 ---
    public const int VK_SHIFT            = 0x10;
    public const int VK_CONTROL          = 0x11;
    public const int VK_MENU             = 0x12;  // Alt 키
    public const int VK_CAPITAL          = 0x14;

    // --- COM ---
    // Apartment 모드 초기화는 Main 의 [STAThread] 로 CLR 이 수행 — COINIT_APARTMENTTHREADED 상수 불필요.
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    // --- HRESULT ---
    /// <summary>HRESULT 성공값. winerror.h S_OK = 0x00000000.</summary>
    public const int S_OK = 0;

    // --- DPI ---
    public const uint MDT_EFFECTIVE_DPI  = 0;

    // --- DWM Window Attributes ---
    /// <summary>DWM이 실제로 합성하는 "보이는" 프레임 경계. GetWindowRect는 invisible resize border를 포함하므로 시각적 정렬에는 이 값을 써야 한다.</summary>
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    /// <summary>창의 cloaked 상태(가상 데스크톱 숨김, UWP suspend 등). 0이 아니면 화면에 표시되지 않음.</summary>
    public const uint DWMWA_CLOAKED      = 14;

    // --- 시스템 색상 ---
    public const int COLOR_HIGHLIGHT      = 13;
    public const int COLOR_BTNFACE        = 15;

    // --- 정적 컨트롤 스타일 ---
    public const uint SS_ETCHEDHORZ       = 0x0010;

    // --- 스크롤바 ---
    public const uint WS_VSCROLL          = 0x00200000;
    public const int  SB_VERT             = 1;
    public const uint SIF_RANGE           = 0x0001;
    public const uint SIF_PAGE            = 0x0002;
    public const uint SIF_POS             = 0x0004;
    public const uint SIF_TRACKPOS        = 0x0010;
    public const uint SIF_ALL             = 0x0017;
    public const uint WM_VSCROLL          = 0x0115;
    public const uint WM_MOUSEWHEEL       = 0x020A;
    public const int  SB_LINEUP           = 0;
    public const int  SB_LINEDOWN         = 1;
    public const int  SB_PAGEUP           = 2;
    public const int  SB_PAGEDOWN         = 3;
    public const int  SB_THUMBPOSITION    = 4;
    public const int  SB_THUMBTRACK       = 5;
    public const int  SB_TOP              = 6;
    public const int  SB_BOTTOM           = 7;
    public const int  WHEEL_DELTA         = 120;

    // ScrollWindowEx flags
    public const uint SW_SCROLLCHILDREN   = 0x0001;
    public const uint SW_INVALIDATE       = 0x0002;
    public const uint SW_ERASE            = 0x0004;

    // --- 콤보박스 ---
    public const uint CBS_DROPDOWNLIST    = 0x0003;
    public const uint CBS_HASSTRINGS      = 0x0200;
    public const uint CB_ADDSTRING        = 0x0143;
    public const uint CB_GETCURSEL        = 0x0147;
    public const uint CB_SETCURSEL        = 0x014E;

    // --- GetKeyboardLayout ---
    // LOWORD(HKL) -> LANGID, 0x0412 = 한국어
    public const ushort LANGID_KOREAN    = 0x0412;
    public const long HKL_LANGID_MASK   = 0xFFFF;  // LOWORD 추출 마스크

    // HIWORD(HKL) 최상위 니블이 0xE = IME 디바이스. 0x0409_0409 같은 단순
    // 키보드 레이아웃과 0xE001_0412(한글), 0xE001_0411(일본어 IME) 등을 구분.
    public const uint HKL_IME_DEVICE_MASK = 0xF0000000;
    public const uint HKL_IME_DEVICE_SIG  = 0xE0000000;

    // --- LOWORD/HIWORD 마스크 ---
    public const uint LOWORD_MASK       = 0xFFFF;

    // --- 버퍼 크기 ---
    public const int MAX_CLASS_NAME     = 256;
    public const int MAX_WINDOW_TEXT    = 256;

    // --- 윈도우 클래스명 ---
    public const string ConsoleWindowClass = "ConsoleWindowClass";
}
