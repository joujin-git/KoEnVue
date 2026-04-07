using System.Runtime.InteropServices;

namespace KoEnVue.Native;

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
    public string? lpszMenuName;
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

// ================================================================
// 상수
// ================================================================

internal static class Win32Constants
{
    // --- 윈도우 확장 스타일 (dwExStyle) ---
    public const uint WS_EX_LAYERED      = 0x00080000;
    public const uint WS_EX_TRANSPARENT  = 0x00000020;
    public const uint WS_EX_TOPMOST      = 0x00000008;
    public const uint WS_EX_TOOLWINDOW   = 0x00000080;
    public const uint WS_EX_NOACTIVATE   = 0x08000000;

    // --- 윈도우 스타일 ---
    public const uint WS_POPUP           = 0x80000000;
    public const uint WS_CAPTION         = 0x00C00000;  // WS_BORDER | WS_DLGFRAME

    // --- GetWindowLongW 인덱스 ---
    public const int GWL_STYLE           = -16;
    public const int GWL_EXSTYLE         = -20;

    // --- ShowWindow ---
    public const int SW_HIDE             = 0;
    public const int SW_SHOW             = 5;

    // --- System Metrics ---
    public const int SM_CXSMICON         = 49;
    public const int SM_CYSMICON         = 50;

    // --- SendMessageTimeout 플래그 ---
    public const uint SMTO_ABORTIFHUNG   = 0x0002;

    // --- IME 메시지 ---
    public const uint WM_IME_CONTROL     = 0x0283;
    public const uint IMC_GETOPENSTATUS  = 0x0005;
    public const uint IME_CMODE_HANGUL   = 0x01;

    // --- WinEvent 상수 ---
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint EVENT_OBJECT_IME_SHOW   = 0x8027;
    public const uint EVENT_OBJECT_IME_HIDE   = 0x8028;
    public const uint EVENT_OBJECT_IME_CHANGE = 0x8029;  // Windows SDK WinUser.h 정의

    // --- MonitorFromPoint 플래그 ---
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

    // --- DrawTextW 포맷 ---
    public const uint DT_CENTER          = 0x0001;
    public const uint DT_VCENTER         = 0x0004;
    public const uint DT_SINGLELINE      = 0x0020;
    public const uint DT_CALCRECT        = 0x0400;

    // --- Shell_NotifyIconW ---
    public const uint NIM_ADD            = 0x00000000;
    public const uint NIM_MODIFY         = 0x00000001;
    public const uint NIM_DELETE         = 0x00000002;
    public const uint NIM_SETVERSION     = 0x00000004;
    public const uint NIF_MESSAGE        = 0x00000001;
    public const uint NIF_ICON           = 0x00000002;
    public const uint NIF_TIP            = 0x00000004;
    public const uint NIF_GUID           = 0x00000020;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // --- 표준 Win32 메시지 ---
    public const uint WM_NULL            = 0x0000;
    public const uint WM_DESTROY         = 0x0002;
    public const uint WM_TIMER           = 0x0113;
    public const uint WM_COMMAND         = 0x0111;
    public const uint WM_HOTKEY          = 0x0312;
    public const uint WM_POWERBROADCAST  = 0x0218;
    public const uint WM_DISPLAYCHANGE   = 0x007E;
    public const uint WM_SETTINGCHANGE   = 0x001A;
    public const uint WM_DPICHANGED     = 0x02E0;
    public const uint WM_RBUTTONUP       = 0x0205;
    public const uint WM_LBUTTONUP       = 0x0202;
    public const uint WM_APP             = 0x8000;
    public const uint WM_USER            = 0x0400;

    // --- 전원 관리 ---
    public const uint PBT_APMRESUMESUSPEND = 0x0007;

    // --- 메뉴 ---
    public const uint MF_STRING          = 0x0000;
    public const uint MF_SEPARATOR       = 0x0800;
    public const uint MF_POPUP           = 0x0010;
    public const uint MF_CHECKED         = 0x0008;
    public const uint MF_UNCHECKED       = 0x0000;
    public const uint MF_BYCOMMAND       = 0x0000;
    public const uint MF_BYPOSITION      = 0x0400;
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
    public const int VK_LBUTTON          = 0x01;

    // --- 핫키 모디파이어 ---
    public const uint MOD_ALT            = 0x0001;
    public const uint MOD_CONTROL        = 0x0002;
    public const uint MOD_SHIFT          = 0x0004;
    public const uint MOD_WIN            = 0x0008;
    public const uint MOD_NOREPEAT       = 0x4000;

    // --- COM ---
    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint CLSCTX_INPROC_SERVER = 0x1;

    // --- DPI ---
    public const uint MDT_EFFECTIVE_DPI  = 0;

    // --- 고대비 ---
    public const uint SPI_GETHIGHCONTRAST = 0x0042;

    // --- 기타 ---
    public const uint CS_HREDRAW         = 0x0002;
    public const uint CS_VREDRAW         = 0x0001;

    // --- GetKeyboardLayout ---
    // LOWORD(HKL) -> LANGID, 0x0412 = 한국어
    public const ushort LANGID_KOREAN    = 0x0412;
    public const long HKL_LANGID_MASK   = 0xFFFF;  // LOWORD 추출 마스크

    // --- LOWORD/HIWORD 마스크 ---
    public const uint LOWORD_MASK       = 0xFFFF;

    // --- 버퍼 크기 ---
    public const int MAX_CLASS_NAME     = 256;
    public const int MAX_WINDOW_TEXT    = 256;
}
