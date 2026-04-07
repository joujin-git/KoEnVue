# Phase 01: 프로젝트 셋업 + 공통 기반

## 목표

KoEnVue 프로젝트의 **모든 기반 인프라**를 구축한다.
이 단계가 완료되면 Phase 02 이후의 모든 기능 코드가 컴파일되는 데 필요한 P/Invoke 선언, 모델, 유틸리티, 프로젝트 설정이 갖춰진다.
어떤 기능 코드도 이 단계에서 작성하지 않는다 -- 선언과 정의만.

## 선행 조건

- `00_TEAM_ION.md`를 읽고 팀 구성 완료
- 하드 제약 조건(P1-P5) 숙지

## 팀 구성

| 팀원 | 모드 | 담당 파일 |
|------|------|-----------|
| **이온-파운데이션** | plan | `Native/User32.cs`, `Native/Imm32.cs`, `Native/Shell32.cs`, `Native/Gdi32.cs`, `Native/Kernel32.cs`, `Native/Shcore.cs`, `Native/Ole32.cs`, `Native/OleAut32.cs`, `Native/Win32Types.cs`, `Native/SafeGdiHandles.cs` |
| **이온-감지** | plan | `Models/ImeState.cs`, `Models/IndicatorStyle.cs`, `Models/Placement.cs`, `Models/DisplayMode.cs`, `Models/AppConfig.cs`, `Native/AppMessages.cs` |
| **이온-렌더** | plan | `Utils/DpiHelper.cs`, `Utils/ColorHelper.cs`, `Utils/Logger.cs` |
| **이온-시스템** | plan | `Config/DefaultConfig.cs`, `KoEnVue.csproj`, `app.manifest` |

## 병렬 실행 계획

**4명 모두 완전 병렬** -- 서로 다른 파일을 생성하며 의존 관계 없음.

```
이온-파운데이션 ─── Native/*.cs + Win32Types.cs + SafeGdiHandles.cs ──┐
이온-감지       ─── Models/*.cs + AppMessages.cs                     ──┤── 전부 병렬
이온-렌더       ─── Utils/*.cs                                       ──┤
이온-시스템     ─── DefaultConfig.cs + .csproj + app.manifest        ──┘
```

완료 후 이온-QA가 검증.

---

## 구현 명세

### 0. 프로젝트 폴더 구조 (전원 확인)

```
KoEnVue/
├── KoEnVue.csproj
├── app.manifest
├── Program.cs                    (Phase 03에서 구현)
├── Models/
│   ├── ImeState.cs
│   ├── IndicatorStyle.cs
│   ├── Placement.cs
│   ├── DisplayMode.cs
│   └── AppConfig.cs
├── Detector/                     (Phase 02에서 구현)
│   ├── ImeStatus.cs
│   ├── CaretTracker.cs
│   ├── SystemFilter.cs
│   └── UiaClient.cs
├── UI/                           (Phase 04-05에서 구현)
│   ├── Overlay.cs
│   ├── Tray.cs
│   ├── TrayIcon.cs
│   └── Animation.cs
├── Config/
│   ├── Settings.cs               (Phase 06에서 구현)
│   └── DefaultConfig.cs
├── Utils/
│   ├── Startup.cs                (Phase 07에서 구현)
│   ├── I18n.cs                   (Phase 06에서 구현)
│   ├── DpiHelper.cs
│   ├── ColorHelper.cs
│   └── Logger.cs
├── Native/
│   ├── User32.cs
│   ├── Imm32.cs
│   ├── Shell32.cs
│   ├── Gdi32.cs
│   ├── Kernel32.cs
│   ├── Shcore.cs
│   ├── Ole32.cs
│   ├── OleAut32.cs
│   ├── Win32Types.cs
│   ├── AppMessages.cs
│   └── SafeGdiHandles.cs
```

---

### 1. KoEnVue.csproj (이온-시스템)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
    <IlcOptimizationPreference>Size</IlcOptimizationPreference>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
</Project>
```

| 항목 | 이유 |
|------|------|
| `OutputType: WinExe` | 콘솔 창 없는 GUI 앱. `Console.CancelKeyPress`는 발화하지 않음 |
| `PublishAot: true` | NativeAOT 단일 exe ~3MB |
| `InvariantGlobalization: true` | 바이너리 크기 감소. CP949/EUC-KR 미지원. config.json은 UTF-8만 |
| `JsonSerializerIsReflectionEnabledByDefault: false` | `[JsonSerializable(typeof(AppConfig))]` source generator 필수 |
| `IlcOptimizationPreference: Size` | 바이너리 크기 최적화 |

---

### 2. app.manifest (이온-시스템)

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
    </windowsSettings>
  </application>
</assembly>
```

- **UAC**: `requireAdministrator` -- UIPI 우회하여 모든 앱에 SendMessageTimeout 전송 가능
- **DPI**: `PerMonitorV2` (Windows 10 1703+) + `true/pm` fallback (이전 Windows)
- 런타임 `SetProcessDpiAwarenessContext()` 호출 불필요 -- 매니페스트가 모든 윈도우 생성보다 먼저 적용됨

---

### 3. Native/User32.cs (이온-파운데이션)

모든 P/Invoke는 `[LibraryImport]` source generator 사용. `[DllImport]` 절대 금지 (NativeAOT).

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class User32
{
    // === 포그라운드/포커스 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);
    // char[] 배열로 선언하여 unsafe 의존 제거. 호출 시: var buf = new char[256]; GetClassNameW(hwnd, buf, 256);

    // === 윈도우 상태 조회 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    // === 좌표 변환 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    // === 모니터 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    // === 마우스 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    // === 윈도우 클래스/생성/파괴 ===

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight,
        [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
        ref BLENDFUNCTION pblend, uint dwFlags);

    // === 메시지 루프 ===

    [LibraryImport("user32.dll")]
    internal static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    // 반환: >0 메시지 있음, 0 WM_QUIT, -1 에러. bool이 아닌 int로 선언해야 함.

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DispatchMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int nExitCode);

    // === 타이머 ===

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    // === 이벤트 훅 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    // === 메시지 전송 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // === 포커스/메뉴 ===

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(
        IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CheckMenuRadioItem(
        IntPtr hMenu, uint first, uint last, uint check, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr hMenu);

    // === 입력 상태 ===

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    // === 키보드 레이아웃 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetKeyboardLayout(uint idThread);

    // === 핫키 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    // === 윈도우 배치/시스템 ===

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(
        uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int nIndex);

    // === 콜백 대리자 ===

    internal delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // WndProc 대리자 (NativeAOT에서는 [UnmanagedCallersOnly] + 함수 포인터 방식 권장)
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // === 아이콘 ===

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    // DrawTextW는 user32.dll 소속 (gdi32.dll 아님!)
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);
}
```

---

### 4. Native/Imm32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Imm32
{
    [LibraryImport("imm32.dll")]
    internal static partial IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    internal static partial IntPtr ImmGetContext(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);
}
```

---

### 5. Native/Shell32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Shell32
{
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
```

---

### 6. Native/Gdi32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Gdi32
{
    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut,
        uint iCharSet, uint iOutPrecision, uint iClipPrecision,
        uint iQuality, uint iPitchAndFamily, string pszFaceName);

    // 참고: CreateIconIndirect, FillRect는 user32.dll 소속 → User32.cs에 선언됨

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom,
        int width, int height);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(IntPtr hdc, uint color);

    // 참고: DrawTextW는 user32.dll 소속 → User32.cs에 선언됨
    // 참고: MulDiv는 kernel32.dll 소속 → Kernel32.cs에 선언됨
}
```

참고: `CreateIconIndirect`와 `FillRect`는 User32.dll 소속이므로 User32.cs에 선언되어 있다. `ICONINFO` 구조체는 Win32Types.cs에 정의되어 있다.

---

### 7. Native/Kernel32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetLastError();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateMutexW(IntPtr lpMutexAttributes, 
        [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseMutex(IntPtr hMutex);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    // MulDiv는 kernel32.dll 소속 (gdi32.dll 아님!)
    [LibraryImport("kernel32.dll")]
    internal static partial int MulDiv(int nNumber, int nNumerator, int nDenominator);
}
```

---

### 8. Native/Shcore.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Shcore
{
    /// <summary>
    /// Per-Monitor DPI 조회. MDT_EFFECTIVE_DPI = 0.
    /// </summary>
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hmonitor, uint dpiType,
        out uint dpiX, out uint dpiY);
}
```

---

### 9. Native/Ole32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Ole32
{
    /// <summary>
    /// COM 초기화. COINIT_APARTMENTTHREADED = 0x2 (STA, UIA 필수).
    /// </summary>
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
}
```

---

### 10. Native/OleAut32.cs (이온-파운데이션)

```csharp
using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class OleAut32
{
    [LibraryImport("oleaut32.dll")]
    internal static partial void SysFreeString(IntPtr bstrString);
}
```

---

### 11. Native/Win32Types.cs (이온-파운데이션)

모든 Win32 구조체와 상수를 이 파일에 **1회만** 정의한다. 다른 파일에서 중복 정의 금지(P4).

```csharp
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

    // --- DrawTextW 포맷 ---
    public const uint DT_CENTER          = 0x0001;
    public const uint DT_VCENTER         = 0x0004;
    public const uint DT_SINGLELINE      = 0x0020;

    // --- Shell_NotifyIconW ---
    public const uint NIM_ADD            = 0x00000000;
    public const uint NIM_MODIFY         = 0x00000001;
    public const uint NIM_DELETE         = 0x00000002;
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
}
```

---

### 12. Native/AppMessages.cs (이온-감지)

커스텀 윈도우 메시지 상수. 감지 스레드 -> 메인 스레드 통신에 사용.

```csharp
namespace KoEnVue.Native;

/// <summary>
/// 커스텀 윈도우 메시지 정의.
/// 감지 스레드와 훅 콜백이 PostMessage로 메인 스레드에 이벤트를 전달할 때 사용.
/// </summary>
internal static class AppMessages
{
    // --- WM_APP 기반 커스텀 메시지 ---

    /// <summary>
    /// IME 상태 변경.
    /// wParam: (IntPtr)(int)ImeState enum 값
    /// lParam: 0
    /// </summary>
    public const uint WM_IME_STATE_CHANGED = Win32Constants.WM_APP + 1;

    /// <summary>
    /// 포커스 윈도우 변경.
    /// wParam: 새 hwndFocus (IntPtr)
    /// lParam: 0
    /// </summary>
    public const uint WM_FOCUS_CHANGED = Win32Constants.WM_APP + 2;

    /// <summary>
    /// 캐럿 위치 갱신.
    /// wParam: x 좌표 (signed int -> IntPtr). 스크린 좌표.
    /// lParam: y 좌표 (signed int -> IntPtr). 스크린 좌표.
    /// </summary>
    public const uint WM_CARET_UPDATED = Win32Constants.WM_APP + 3;

    /// <summary>
    /// 인디케이터 즉시 숨기기.
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_HIDE_INDICATOR = Win32Constants.WM_APP + 4;

    /// <summary>
    /// 설정 변경 감지 (config.json 리로드 또는 트레이 메뉴 변경).
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_CONFIG_CHANGED = Win32Constants.WM_APP + 5;

    // --- WM_USER 기반 ---

    /// <summary>
    /// 트레이 아이콘 콜백.
    /// Shell_NotifyIconW의 uCallbackMessage에 설정.
    /// </summary>
    public const uint WM_TRAY_CALLBACK = Win32Constants.WM_USER + 1;
}
```

---

### 13. Native/SafeGdiHandles.cs (이온-파운데이션)

GDI 핸들을 SafeHandle로 래핑하여 누수 방지. 모든 GDI 코드(Overlay.cs, TrayIcon.cs, Animation.cs)에서 이 래퍼 사용 필수(P4).

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KoEnVue.Native;

/// <summary>
/// HFONT 래퍼. ReleaseHandle에서 DeleteObject 호출.
/// HFONT는 앱 시작 시 1회 생성, DPI 변경 시에만 재생성.
/// </summary>
internal sealed class SafeFontHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFontHandle() : base(ownsHandle: true) { }
    public SafeFontHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return Gdi32.DeleteObject(handle);
    }
}

/// <summary>
/// HBITMAP 래퍼. ReleaseHandle에서 DeleteObject 호출.
/// DIB 재생성 조건: DPI 변경, 스타일 변경, 크기 변경.
/// </summary>
internal sealed class SafeBitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeBitmapHandle() : base(ownsHandle: true) { }
    public SafeBitmapHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return Gdi32.DeleteObject(handle);
    }
}

/// <summary>
/// HICON 래퍼. ReleaseHandle에서 DestroyIcon 호출.
/// 트레이 아이콘 상태 변경 시 새 HICON 생성 후 이전 핸들 해제.
/// </summary>
internal sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeIconHandle() : base(ownsHandle: true) { }
    public SafeIconHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        // DestroyIcon은 User32.cs에 선언됨
        return User32.DestroyIcon(handle);
    }
}
```

참고: `DestroyIcon`은 User32.cs에 이미 선언되어 있다.

---

### 14. Models/ImeState.cs (이온-감지)

```csharp
namespace KoEnVue.Models;

/// <summary>
/// IME 감지 결과 3가지 상태.
/// 문자열 비교 금지 -- 반드시 enum으로 비교(P3).
/// </summary>
internal enum ImeState
{
    /// <summary>한국어 IME 활성 + 한글 모드</summary>
    Hangul,

    /// <summary>한국어 IME 활성 + 영문 모드</summary>
    English,

    /// <summary>한국어 IME 비활성 (영문 키보드 등)</summary>
    NonKorean
}
```

---

### 15. Models/IndicatorStyle.cs (이온-감지)

```csharp
namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 스타일 5종.
/// config.json의 "indicator_style" 키에 대응.
/// </summary>
internal enum IndicatorStyle
{
    /// <summary>캐럿 옆 텍스트 라벨 ("한"/"En"). 기본 28x24px.</summary>
    Label,

    /// <summary>소형 원형. 기본 8x8px. 기본 스타일.</summary>
    CaretDot,

    /// <summary>소형 사각. 기본 8x8px.</summary>
    CaretSquare,

    /// <summary>얇은 밑줄 바. 기본 24x3px.</summary>
    CaretUnderline,

    /// <summary>얇은 세로 바. 기본 3x16px.</summary>
    CaretVbar
}
```

---

### 16. Models/Placement.cs (이온-감지)

```csharp
namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 배치 방향.
/// Label 스타일: Left -> Above -> Below 자동 전환.
/// 캐럿 박스 스타일: CaretTopRight / CaretBelow / CaretOverlap 고정.
/// </summary>
internal enum Placement
{
    /// <summary>캐럿 왼쪽 (label 기본)</summary>
    Left,

    /// <summary>캐럿 오른쪽 (label 설정 가능)</summary>
    Right,

    /// <summary>캐럿 위 (label fallback)</summary>
    Above,

    /// <summary>캐럿 아래 (label fallback)</summary>
    Below,

    /// <summary>캐럿 오른쪽 상단 (caret_dot, caret_square)</summary>
    CaretTopRight,

    /// <summary>캐럿 바로 아래 (caret_underline)</summary>
    CaretBelow,

    /// <summary>캐럿 위치에 겹침 (caret_vbar)</summary>
    CaretOverlap
}
```

---

### 17. Models/DisplayMode.cs (이온-감지)

```csharp
namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 표시 모드.
/// config.json의 "display_mode" 키에 대응.
/// </summary>
internal enum DisplayMode
{
    /// <summary>이벤트 시에만 표시 (기본값). 페이드인 -> 유지 -> 페이드아웃 -> 숨김.</summary>
    OnEvent,

    /// <summary>항상 표시. 유휴 시 idle_opacity, 활성 시 active_opacity.</summary>
    Always
}
```

---

### 18. Models/AppConfig.cs (이온-감지)

Immutable record + `[JsonSerializable]` source generator. volatile 참조 교체로 스레드 안전성 확보.

```csharp
using System.Text.Json.Serialization;
using KoEnVue.Models;

namespace KoEnVue.Models;

/// <summary>
/// 불변 설정 객체. volatile 참조 교체로 감지 스레드(읽기)와 메인 스레드(쓰기) 간 안전 공유.
/// 락 불필요 -- 원자적 참조 교체로 충분.
/// </summary>
internal sealed record AppConfig
{
    // [표시 모드]
    public DisplayMode DisplayMode { get; init; } = DisplayMode.OnEvent;
    public int EventDisplayDurationMs { get; init; } = 1500;
    public int AlwaysIdleTimeoutMs { get; init; } = 3000;
    public EventTriggersConfig EventTriggers { get; init; } = new();

    // [위치]
    public string PositionMode { get; init; } = "caret";   // "caret" | "mouse" | "fixed"
    public FixedPositionConfig FixedPosition { get; init; } = new();
    public OffsetConfig CaretOffset { get; init; } = new(-2, 0);
    public OffsetConfig MouseOffset { get; init; } = new(20, 25);
    public string CaretPlacement { get; init; } = "left";   // "left" | "above" | "below" | "right"
    public bool CaretPlacementAutoFlip { get; init; } = true;
    public int ScreenEdgeMargin { get; init; } = 8;

    // [외관 -- 스타일]
    public IndicatorStyle IndicatorStyle { get; init; } = IndicatorStyle.CaretDot;
    public int CaretDotSize { get; init; } = 8;
    public int CaretSquareSize { get; init; } = 8;
    public int CaretUnderlineWidth { get; init; } = 24;
    public int CaretUnderlineHeight { get; init; } = 3;
    public int CaretVbarWidth { get; init; } = 3;
    public int CaretVbarHeight { get; init; } = 16;
    public string LabelShape { get; init; } = "rounded_rect";
    public int LabelWidth { get; init; } = 28;
    public int LabelHeight { get; init; } = 24;
    public int LabelBorderRadius { get; init; } = 6;
    public int BorderWidth { get; init; } = 0;
    public string BorderColor { get; init; } = "#000000";
    public bool ShadowEnabled { get; init; } = false;

    // [외관 -- 색상]
    public string HangulBg { get; init; } = "#16A34A";
    public string HangulFg { get; init; } = "#FFFFFF";
    public string EnglishBg { get; init; } = "#D97706";
    public string EnglishFg { get; init; } = "#FFFFFF";
    public string NonKoreanBg { get; init; } = "#6B7280";
    public string NonKoreanFg { get; init; } = "#FFFFFF";
    public double Opacity { get; init; } = 0.85;
    public double IdleOpacity { get; init; } = 0.4;
    public double ActiveOpacity { get; init; } = 0.95;
    public double CaretBoxOpacity { get; init; } = 0.95;
    public double CaretBoxIdleOpacity { get; init; } = 0.65;
    public double CaretBoxActiveOpacity { get; init; } = 1.0;
    public double CaretBoxMinOpacity { get; init; } = 0.5;

    // [외관 -- 텍스트]
    public string FontFamily { get; init; } = "맑은 고딕";
    public int FontSize { get; init; } = 12;
    public string FontWeight { get; init; } = "bold";
    public string HangulLabel { get; init; } = "한";
    public string EnglishLabel { get; init; } = "En";
    public string NonKoreanLabel { get; init; } = "EN";
    public string LabelStyle { get; init; } = "text";  // "text" | "dot" | "icon"

    // [외관 -- 테마]
    public string Theme { get; init; } = "custom";

    // [애니메이션]
    public bool AnimationEnabled { get; init; } = true;
    public int FadeInMs { get; init; } = 150;
    public int FadeOutMs { get; init; } = 400;
    public bool ChangeHighlight { get; init; } = true;
    public double HighlightScale { get; init; } = 1.3;
    public int HighlightDurationMs { get; init; } = 300;
    public bool SlideAnimation { get; init; } = false;
    public int SlideSpeedMs { get; init; } = 100;

    // [동작 -- 감지]
    public int PollIntervalMs { get; init; } = 80;
    public int CaretPollIntervalMs { get; init; } = 50;
    public string DetectionMethod { get; init; } = "auto";
    public string CaretMethod { get; init; } = "auto";
    public string NonKoreanIme { get; init; } = "hide";
    public bool HideInFullscreen { get; init; } = true;
    public bool HideWhenNoFocus { get; init; } = true;
    public bool HideOnLockScreen { get; init; } = true;
    public string[] SystemHideClasses { get; init; } = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    public string[] SystemHideClassesUser { get; init; } = [];
    public int AppMethodCacheSize { get; init; } = 50;

    // [앱별 프로필] -- Phase 06에서 구현
    public Dictionary<string, JsonElement> AppProfiles { get; init; } = new();
    public string AppProfileMatch { get; init; } = "process";
    public string AppFilterMode { get; init; } = "blacklist";
    public string[] AppFilterList { get; init; } = [];

    // [핫키]
    public bool HotkeysEnabled { get; init; } = true;
    public string HotkeyToggleVisibility { get; init; } = "Ctrl+Alt+H";
    public string HotkeyCycleStyle { get; init; } = "Ctrl+Alt+I";
    public string HotkeyCyclePosition { get; init; } = "Ctrl+Alt+P";
    public string HotkeyCycleDisplay { get; init; } = "Ctrl+Alt+D";
    public string HotkeyOpenSettings { get; init; } = "Ctrl+Alt+S";

    // [시스템 트레이]
    public bool TrayEnabled { get; init; } = true;
    public string TrayIconStyle { get; init; } = "caret_dot";
    public bool TrayTooltip { get; init; } = true;
    public string TrayClickAction { get; init; } = "toggle";
    public bool TrayShowNotification { get; init; } = false;
    public double[] TrayQuickOpacityPresets { get; init; } = [0.95, 0.85, 0.6];

    // [시스템]
    public bool StartupWithWindows { get; init; } = false;
    public bool StartupMinimized { get; init; } = true;
    public bool SingleInstance { get; init; } = true;
    public string LogLevel { get; init; } = "WARNING";
    public string Language { get; init; } = "ko";
    public bool LogToFile { get; init; } = false;
    public string LogFilePath { get; init; } = "";
    public int LogMaxSizeMb { get; init; } = 10;

    // [다중 모니터]
    public string MultiMonitor { get; init; } = "follow_caret";
    public bool PerMonitorScale { get; init; } = true;
    public bool ClampToWorkArea { get; init; } = true;
    public bool PreventCrossMonitor { get; init; } = true;

    // [고급]
    public AdvancedConfig Advanced { get; init; } = new();

    // [버전]
    public int ConfigVersion { get; init; } = 1;
}

// === 중첩 설정 레코드 ===

internal sealed record EventTriggersConfig
{
    public bool OnFocusChange { get; init; } = true;
    public bool OnImeChange { get; init; } = true;
}

internal sealed record FixedPositionConfig
{
    public int X { get; init; } = 100;
    public int Y { get; init; } = 100;
    public string Anchor { get; init; } = "top_right";  // top_left|top_right|bottom_left|bottom_right|center|absolute
    public string Monitor { get; init; } = "primary";   // primary|mouse|active
}

internal sealed record OffsetConfig(int X, int Y)
{
    public int X { get; init; } = X;
    public int Y { get; init; } = Y;
    public OffsetConfig() : this(0, 0) { }
}

internal sealed record AdvancedConfig
{
    public int ForceTopmostIntervalMs { get; init; } = 5000;
    public int UiaTimeoutMs { get; init; } = 200;
    public int UiaCacheTtlMs { get; init; } = 500;
    public string[] SkipUiaForProcesses { get; init; } = [];
    public string[] ImeFallbackChain { get; init; } = ["ime_default_wnd", "ime_context", "keyboard_layout"];
    public string[] CaretFallbackChain { get; init; } = ["gui_thread_info", "uia_text_pattern", "focus_window_rect", "mouse_cursor"];
    public string OverlayClassName { get; init; } = "KoEnVueOverlay";
    public bool PreventSleep { get; init; } = false;
    public bool DebugOverlay { get; init; } = false;
}

/// <summary>
/// NativeAOT 필수: JsonSerializerContext source generator.
/// 리플렉션 비활성화 상태에서 직렬화/역직렬화를 수행.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(EventTriggersConfig))]
[JsonSerializable(typeof(FixedPositionConfig))]
[JsonSerializable(typeof(OffsetConfig))]
[JsonSerializable(typeof(AdvancedConfig))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class AppConfigJsonContext : JsonSerializerContext { }
// SnakeCaseLower: C# PascalCase 프로퍼티 ↔ JSON snake_case 키 자동 매핑
// 예: FadeInMs ↔ "fade_in_ms", HangulBg ↔ "hangul_bg"
```

---

### 19. Config/DefaultConfig.cs (이온-시스템)

모든 상수를 이 파일에 집중. 매직 넘버 직접 사용 금지(P3).

```csharp
namespace KoEnVue.Config;

/// <summary>
/// 기본 상수값. 코드 전체에서 매직 넘버 대신 이 상수를 참조한다.
/// config.json에서 오버라이드 가능한 값은 AppConfig 기본값에 정의하고,
/// 여기에는 코드 레벨 픽셀 오프셋/간격/타이밍 상수만 정의한다.
/// </summary>
internal static class DefaultConfig
{
    // === 배치 간격 (px, DPI 스케일링 전 기본값) ===

    /// <summary>캐럿-라벨 간격 (label 스타일)</summary>
    public const int LabelGap = 2;

    /// <summary>포커스 윈도우 fallback 시 윈도우 하단 간격</summary>
    public const int FocusWindowGap = 4;

    /// <summary>caret_dot/square X 오프셋 (캐럿 오른쪽 상단)</summary>
    public const int CaretBoxGapX = 2;

    /// <summary>caret_dot/square Y 오프셋 (캐럿 오른쪽 상단)</summary>
    public const int CaretBoxGapY = 2;

    /// <summary>caret_underline 캐럿 아래 간격</summary>
    public const int UnderlineGap = 1;

    /// <summary>caret_vbar X 오프셋 (캐럿 위치에 겹침)</summary>
    public const int VbarOffsetX = 1;

    /// <summary>label 텍스트 좌우 패딩 (F-S01 고정 너비 계산용)</summary>
    public const int LABEL_PADDING_X = 4;

    // === 애니메이션 타이밍 (ms) ===

    /// <summary>페이드인 지속 시간</summary>
    public const int FadeInDurationMs = 150;

    /// <summary>유지 시간</summary>
    public const int HoldDurationMs = 1500;

    /// <summary>페이드아웃 지속 시간</summary>
    public const int FadeOutDurationMs = 400;

    /// <summary>IME 전환 시 확대 배율</summary>
    public const double ScaleFactor = 1.3;

    /// <summary>확대 -> 원래 크기 복귀 시간</summary>
    public const int ScaleReturnMs = 300;

    // === 감지 ===

    /// <summary>감지 폴링 간격</summary>
    public const int PollingIntervalMs = 80;

    // === DPI ===

    /// <summary>DPI 기준값. DpiHelper.Scale에서 사용.</summary>
    public const int BASE_DPI = 96;

    // === 앱 식별 ===

    /// <summary>
    /// 고정 GUID. 트레이 아이콘 식별 + Mutex 이름에 사용.
    /// 크래시 복구(NIM_DELETE)에서 이전 찌꺼기를 정리하는 데 필수.
    /// </summary>
    public static readonly Guid AppGuid = new("INSERT-A-FIXED-GUID-HERE");
    // 실제 구현 시 고유 GUID로 교체할 것.

    /// <summary>Mutex 이름: "KoEnVue_{GUID}"</summary>
    public static readonly string MutexName = $"KoEnVue_{AppGuid}";

    // === 오버레이 ===

    /// <summary>오버레이 윈도우 클래스명</summary>
    public const string OverlayClassName = "KoEnVueOverlay";

    // === 캐시 ===

    /// <summary>앱별 캐럿 감지 방식 캐시 최대 크기 (LRU)</summary>
    public const int AppMethodCacheMaxSize = 50;

    // === always 모드 ===

    /// <summary>always 모드 유휴 전환 타임아웃</summary>
    public const int AlwaysIdleTimeoutMs = 3000;

    // === 설정 파일 ===

    /// <summary>설정 파일 변경 감지 간격 (약 5초 = 62폴링 x 80ms)</summary>
    public const int ConfigCheckIntervalPolls = 62;

    // === IME 감지 ===

    /// <summary>SendMessageTimeout 타임아웃 (ms)</summary>
    public const uint ImeMessageTimeoutMs = 100;

    // === TOPMOST 재적용 ===

    /// <summary>TOPMOST 재적용 간격 (다른 TOPMOST 앱과 충돌 시)</summary>
    public const int ForceTopmostIntervalMs = 5000;

    // === UIA ===

    /// <summary>UI Automation 호출 타임아웃</summary>
    public const int UiaTimeoutMs = 200;
}
```

---

### 20. Utils/DpiHelper.cs (이온-렌더)

DPI 스케일링을 이 파일 1곳에서만 구현(P4).

```csharp
using KoEnVue.Native;
using KoEnVue.Config;

namespace KoEnVue.Utils;

/// <summary>
/// DPI 조회 및 스케일링 유틸리티.
/// P4 원칙: 모든 DPI 관련 로직은 이 1곳에서만 구현.
/// </summary>
internal static class DpiHelper
{
    /// <summary>DPI 기준값. 100% 스케일링 = 96 DPI.</summary>
    public const int BASE_DPI = DefaultConfig.BASE_DPI;  // = 96

    /// <summary>
    /// 기본값에 DPI 스케일을 적용하여 정수 픽셀로 변환.
    /// 반드시 Math.Round 사용. (int) 절삭은 계통적 과소 스케일링 유발(F-S05).
    /// </summary>
    public static int Scale(int baseValue, double dpiScale)
    {
        return (int)Math.Round(baseValue * dpiScale);
    }

    /// <summary>
    /// double 오프셋에 DPI 스케일 적용.
    /// </summary>
    public static int Scale(double baseValue, double dpiScale)
    {
        return (int)Math.Round(baseValue * dpiScale);
    }

    /// <summary>
    /// 특정 모니터의 DPI 스케일 배율을 조회한다.
    /// MonitorFromPoint -> GetDpiForMonitor -> dpiX / BASE_DPI.
    /// </summary>
    public static double GetScale(IntPtr hMonitor)
    {
        int hr = Shcore.GetDpiForMonitor(hMonitor, Win32Constants.MDT_EFFECTIVE_DPI,
            out uint dpiX, out uint _);
        if (hr != 0 || dpiX == 0) return 1.0;  // 실패 시 100% 기본값
        return dpiX / (double)BASE_DPI;
    }

    /// <summary>
    /// 스크린 좌표에서 해당 모니터의 작업 영역(rcWork)을 조회한다.
    /// MonitorFromPoint -> GetMonitorInfoW -> rcWork.
    /// 작업표시줄 제외된 실제 사용 가능 영역.
    /// </summary>
    public static RECT GetWorkArea(IntPtr hMonitor)
    {
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMonitor, ref monitorInfo);
        return monitorInfo.rcWork;
    }

    /// <summary>
    /// 좌표가 속한 모니터 핸들을 반환한다.
    /// MONITOR_DEFAULTTONEAREST: 가상 데스크톱 밖이면 가장 가까운 모니터.
    /// </summary>
    public static IntPtr GetMonitorFromPoint(int x, int y)
    {
        return User32.MonitorFromPoint(new POINT(x, y), Win32Constants.MONITOR_DEFAULTTONEAREST);
    }
}
```

---

### 21. Utils/ColorHelper.cs (이온-렌더)

색상 변환을 이 파일 1곳에서만 구현(P4).

```csharp
namespace KoEnVue.Utils;

/// <summary>
/// 색상 변환 유틸리티.
/// P4 원칙: HEX -> COLORREF 변환은 이 1곳에서만 구현.
/// config.json 색상 문자열을 Win32 GDI가 요구하는 COLORREF 형식으로 변환한다.
/// </summary>
internal static class ColorHelper
{
    /// <summary>
    /// HEX 문자열 (#RRGGBB 또는 RRGGBB)을 Win32 COLORREF (0x00BBGGRR)로 변환한다.
    /// COLORREF는 BGR 순서임에 주의.
    /// </summary>
    /// <param name="hex">색상 문자열. 예: "#16A34A", "D97706"</param>
    /// <returns>COLORREF 값 (0x00BBGGRR)</returns>
    public static uint HexToColorRef(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6) return 0; // 잘못된 형식 -> 검정

        byte r = byte.Parse(span[0..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(span[4..6], System.Globalization.NumberStyles.HexNumber);

        // COLORREF = 0x00BBGGRR
        return (uint)((b << 16) | (g << 8) | r);
    }

    /// <summary>
    /// HEX 문자열을 (R, G, B) 튜플로 파싱.
    /// premultiplied alpha 처리 등에서 개별 채널이 필요할 때 사용.
    /// </summary>
    public static (byte R, byte G, byte B) HexToRgb(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6) return (0, 0, 0);

        byte r = byte.Parse(span[0..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(span[4..6], System.Globalization.NumberStyles.HexNumber);

        return (r, g, b);
    }
}
```

---

### 22. Utils/Logger.cs (이온-렌더)

```csharp
using System.Diagnostics;

namespace KoEnVue.Utils;

/// <summary>
/// 경량 로거. System.Diagnostics.Trace 기반.
/// 외부 로깅 프레임워크 사용 금지(P1).
/// 로그 메시지는 영문(P2).
/// </summary>
internal static class Logger
{
    private static string _logLevel = "WARNING";

    /// <summary>로그 레벨 설정. "DEBUG" | "INFO" | "WARNING" | "ERROR"</summary>
    public static void SetLevel(string level) => _logLevel = level.ToUpperInvariant();

    private static int LevelToInt(string level) => level switch
    {
        "DEBUG" => 0,
        "INFO" => 1,
        "WARNING" => 2,
        "ERROR" => 3,
        _ => 2
    };

    private static bool ShouldLog(string level) => LevelToInt(level) >= LevelToInt(_logLevel);

    public static void Debug(string message)
    {
        if (ShouldLog("DEBUG"))
            Trace.WriteLine($"[DEBUG] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Info(string message)
    {
        if (ShouldLog("INFO"))
            Trace.WriteLine($"[INFO] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Warning(string message)
    {
        if (ShouldLog("WARNING"))
            Trace.WriteLine($"[WARN] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message)
    {
        if (ShouldLog("ERROR"))
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}");
    }

    public static void Error(string message, Exception ex)
    {
        if (ShouldLog("ERROR"))
            Trace.WriteLine($"[ERROR] {DateTime.UtcNow:HH:mm:ss.fff} {message}: {ex.Message}");
    }
}
```

---

## 검증 기준

이온-QA가 아래 체크리스트를 전수 확인한다.

### 프로젝트 구조
- [ ] `KoEnVue.csproj` 존재, 8개 PropertyGroup 항목 모두 포함 (AllowUnsafeBlocks 포함)
- [ ] `app.manifest` 존재, UAC `requireAdministrator` + DPI `PerMonitorV2` + `dpiAware` fallback
- [ ] 폴더 구조: Models/, Detector/, UI/, Config/, Utils/, Native/ 모두 존재

### P/Invoke (Native/)
- [ ] 모든 P/Invoke에 `[LibraryImport]` 사용, `[DllImport]` 없음
- [ ] User32.cs: 42개 이상 함수 선언 (GetForegroundWindow ~ DestroyIcon, GetKeyboardLayout, CreateIconIndirect, FillRect 포함)
- [ ] Imm32.cs: 4개 함수 (ImmGetDefaultIMEWnd, ImmGetContext, ImmReleaseContext, ImmGetConversionStatus)
- [ ] Shell32.cs: Shell_NotifyIconW
- [ ] Gdi32.cs: 12개 이상 함수 (CreateCompatibleDC ~ RoundRect. DrawTextW/MulDiv/CreateIconIndirect/FillRect는 각각 올바른 DLL 소속 파일에 위치)
- [ ] Kernel32.cs: 5개 함수 (GetLastError, CreateMutexW, ReleaseMutex, CloseHandle, MulDiv)
- [ ] Shcore.cs: GetDpiForMonitor
- [ ] Ole32.cs: CoInitializeEx, CoCreateInstance, CoUninitialize
- [ ] OleAut32.cs: SysFreeString
- [ ] WndProc, WinEventProc 대리자 정의 존재

### Win32Types.cs
- [ ] 구조체 10종 이상: GUITHREADINFO, RECT, POINT, SIZE, MONITORINFOEXW, NOTIFYICONDATAW, BITMAPINFOHEADER, BLENDFUNCTION, WNDCLASSEXW, MSG, ICONINFO
- [ ] 상수: WS_EX_LAYERED, WS_EX_TRANSPARENT, WS_EX_TOPMOST, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE
- [ ] 상수: WS_POPUP, WS_CAPTION, GWL_STYLE
- [ ] 상수: SM_CXSMICON, SMTO_ABORTIFHUNG, WINEVENT_OUTOFCONTEXT
- [ ] 상수: WM_IME_CONTROL(0x0283), IMC_GETOPENSTATUS(0x0005)
- [ ] 상수: ULW_ALPHA, AC_SRC_OVER, AC_SRC_ALPHA
- [ ] 상수: NIM_ADD, NIM_MODIFY, NIM_DELETE, NIF_MESSAGE, NIF_ICON, NIF_TIP, NIF_GUID
- [ ] 상수: WM_TIMER, WM_COMMAND, WM_HOTKEY, WM_POWERBROADCAST, WM_DISPLAYCHANGE, WM_SETTINGCHANGE
- [ ] 상수: PBT_APMRESUMESUSPEND, VK_LBUTTON, LANGID_KOREAN(0x0412)
- [ ] 핫키 모디파이어: MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN, MOD_NOREPEAT

### AppMessages.cs
- [ ] WM_IME_STATE_CHANGED = WM_APP + 1
- [ ] WM_FOCUS_CHANGED = WM_APP + 2
- [ ] WM_CARET_UPDATED = WM_APP + 3 (wParam=x, lParam=y 문서화)
- [ ] WM_HIDE_INDICATOR = WM_APP + 4
- [ ] WM_CONFIG_CHANGED = WM_APP + 5
- [ ] WM_TRAY_CALLBACK = WM_USER + 1

### SafeGdiHandles.cs
- [ ] SafeFontHandle: ReleaseHandle -> DeleteObject
- [ ] SafeBitmapHandle: ReleaseHandle -> DeleteObject
- [ ] SafeIconHandle: ReleaseHandle -> DestroyIcon

### Models
- [ ] ImeState enum: Hangul, English, NonKorean (3값)
- [ ] IndicatorStyle enum: Label, CaretDot, CaretSquare, CaretUnderline, CaretVbar (5값)
- [ ] Placement enum: Left, Right, Above, Below, CaretTopRight, CaretBelow, CaretOverlap (7값)
- [ ] DisplayMode enum: OnEvent, Always (2값)
- [ ] AppConfig: immutable record, 80+ 프로퍼티, `[JsonSerializable]` context 존재

### DefaultConfig.cs
- [ ] LabelGap = 2
- [ ] FocusWindowGap = 4
- [ ] CaretBoxGapX = 2, CaretBoxGapY = 2
- [ ] UnderlineGap = 1
- [ ] VbarOffsetX = 1
- [ ] LABEL_PADDING_X = 4
- [ ] FadeInDurationMs = 150
- [ ] HoldDurationMs = 1500
- [ ] FadeOutDurationMs = 400
- [ ] ScaleFactor = 1.3
- [ ] ScaleReturnMs = 300
- [ ] PollingIntervalMs = 80
- [ ] BASE_DPI = 96
- [ ] AppGuid (고정 GUID)
- [ ] MutexName = "KoEnVue_{GUID}"

### Utils
- [ ] DpiHelper.Scale: `(int)Math.Round(value * scale)` -- 절삭 아님
- [ ] DpiHelper.GetScale: Shcore.GetDpiForMonitor + MDT_EFFECTIVE_DPI
- [ ] DpiHelper.GetWorkArea: MonitorFromPoint + GetMonitorInfoW -> rcWork
- [ ] ColorHelper.HexToColorRef: "#RRGGBB" -> 0x00BBGGRR (BGR 순서)
- [ ] Logger: Trace 기반, DEBUG/INFO/WARNING/ERROR 4레벨

### 하드 제약 준수
- [ ] P1: NuGet 외부 패키지 없음
- [ ] P2: 로그 메시지 영문, UI 텍스트 한글 기본값
- [ ] P3: 매직 넘버 없음 (모든 값이 상수/config 참조)
- [ ] P4: P/Invoke는 Native/ 폴더에만, Win32 상수는 Win32Types.cs에만
- [ ] P5: app.manifest에 requireAdministrator

---

## 산출물

| 파일 | 담당 |
|------|------|
| `KoEnVue.csproj` | 이온-시스템 |
| `app.manifest` | 이온-시스템 |
| `Native/User32.cs` | 이온-파운데이션 |
| `Native/Imm32.cs` | 이온-파운데이션 |
| `Native/Shell32.cs` | 이온-파운데이션 |
| `Native/Gdi32.cs` | 이온-파운데이션 |
| `Native/Kernel32.cs` | 이온-파운데이션 |
| `Native/Shcore.cs` | 이온-파운데이션 |
| `Native/Ole32.cs` | 이온-파운데이션 |
| `Native/OleAut32.cs` | 이온-파운데이션 |
| `Native/Win32Types.cs` | 이온-파운데이션 |
| `Native/SafeGdiHandles.cs` | 이온-파운데이션 |
| `Native/AppMessages.cs` | 이온-감지 |
| `Models/ImeState.cs` | 이온-감지 |
| `Models/IndicatorStyle.cs` | 이온-감지 |
| `Models/Placement.cs` | 이온-감지 |
| `Models/DisplayMode.cs` | 이온-감지 |
| `Models/AppConfig.cs` | 이온-감지 |
| `Config/DefaultConfig.cs` | 이온-시스템 |
| `Utils/DpiHelper.cs` | 이온-렌더 |
| `Utils/ColorHelper.cs` | 이온-렌더 |
| `Utils/Logger.cs` | 이온-렌더 |
