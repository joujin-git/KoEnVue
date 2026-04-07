# Phase 05: 시스템 트레이 + 핫키

## 목표

Shell_NotifyIconW P/Invoke 기반 시스템 트레이 아이콘을 구현하고, GDI로 상태별 아이콘을 동적 생성하며, Win32 팝업 메뉴로 간이 설정을 제공하고, RegisterHotKey 기반 글로벌 핫키를 등록한다.

## 선행 조건

- Phase 01 완료: Native/*.cs, Win32Types.cs, SafeGdiHandles.cs, AppMessages.cs, Models/*.cs, ColorHelper.cs, DefaultConfig.cs
- Phase 03 완료: Program.cs (메인 메시지 루프 + WndProc에서 WM_TRAY_CALLBACK, WM_COMMAND, WM_HOTKEY 디스패치)
- Phase 03에서 Mutex 기반 단일 인스턴스 강제가 구현되어 있어야 한다 (Mutex 이름: "KoEnVue_{고정GUID}", GUID는 트레이 아이콘과 동일)
- AppMessages.WM_TRAY_CALLBACK (WM_USER+1)이 Phase 01에서 정의되어 있어야 한다.
- SafeIconHandle이 SafeGdiHandles.cs에 구현되어 있어야 한다.
- ColorHelper.cs의 HEX -> COLORREF 변환이 구현되어 있어야 한다.

## 팀 구성

| 팀원 | 역할 | 담당 파일 |
|------|------|-----------|
| **이온-시스템** | 트레이 + 핫키 | `UI/Tray.cs`, `UI/TrayIcon.cs` |
| **이온-QA** | 체크리스트 검증 | 검증 기준 대조 |

## 병렬 실행 계획

```
Phase 04 (이온-렌더: Overlay.cs + Animation.cs)
  ||  병렬
Phase 05 (이온-시스템: Tray.cs + TrayIcon.cs + 핫키)
```

- Phase 04(렌더링)와 Phase 05(시스템 UI)는 **완전 병렬 실행 가능**.
- Tray.cs 내부에서 TrayIcon.cs의 HICON 생성 메서드를 호출하므로, TrayIcon.cs를 먼저 또는 동시에 구현한다.
- 핫키 등록 로직은 Tray.cs 또는 별도 정적 메서드로 구현 (WM_HOTKEY 처리는 Program.cs WndProc에서 수행).

---

## 구현 명세

### `UI/Tray.cs`

#### 트레이 아이콘 관리 -- Shell_NotifyIconW P/Invoke

- **P/Invoke `Shell_NotifyIconW`로 직접 구현한다. WinForms NotifyIcon 사용 금지.**
- `NOTIFYICONDATAW` 구조체를 `Win32Types.cs`에 정의하여 사용한다.
- 고정 GUID를 사용하여 아이콘 식별 (`NIF_GUID` 플래그).
  - GUID는 앱 전체에서 하나만 사용 (다중 인스턴스 방지 Mutex와 동일 GUID 사용 가능).
  - 이 GUID로 비정상 종료 후 찌꺼기 정리도 수행 (Phase 03 초기화 순서에서 NIM_DELETE 호출).

#### NOTIFYICONDATAW 구조체

```csharp
// unsafe fixed char 사용 — [MarshalAs(ByValTStr)]는 [LibraryImport]/NativeAOT 비호환!
// AllowUnsafeBlocks = true 필수 (.csproj에서 설정)
[StructLayout(LayoutKind.Sequential)]
unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;           // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID
    public uint uCallbackMessage; // AppMessages.WM_TRAY_CALLBACK (WM_USER + 1)
    public IntPtr hIcon;
    public fixed char szTip[128];          // 툴팁 텍스트 (최대 127자)
    public uint dwState;
    public uint dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;         // NOTIFYICON_VERSION_4
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;         // 고정 GUID
}
```

- `cbSize`는 `(uint)sizeof(NOTIFYICONDATAW)`로 설정 (unsafe 컨텍스트).
- `fixed char` 필드에 문자열 복사: `szTip` 등에 `span.CopyTo(new Span<char>(nid.szTip, 128));` 패턴 사용.
- NativeAOT에서 `[StructLayout]` + `[LibraryImport]` + `unsafe fixed` 조합 사용.

#### 트레이 아이콘 작업 (3가지)

**NIM_ADD -- 앱 시작 시:**
```
// tray_enabled == false 이면 트레이 등록 전체 건너뛰기
if (!config.TrayEnabled) return;

uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID
uCallbackMessage = AppMessages.WM_TRAY_CALLBACK (WM_USER + 1)
hIcon = TrayIcon.CreateIcon(currentImeState)
szTip = config.TrayTooltip
    ? I18n.GetTrayTooltip(currentImeState)
    : ""   // tray_tooltip: false → 빈 문자열로 툴팁 숨김
  - 한글: "한글 모드" / "영문 모드" / "영문 모드 (비한국어)"
guidItem = 고정 GUID
Shell_NotifyIconW(NIM_ADD, ref nid)

// NIM_SETVERSION 호출 (NOTIFYICON_VERSION_4 동작 활성화)
nid.uVersion = NOTIFYICON_VERSION_4;
Shell_NotifyIconW(NIM_SETVERSION, ref nid);
```

**NIM_MODIFY -- IME 상태 변경 시:**
```
uFlags = NIF_ICON | NIF_TIP | NIF_GUID
hIcon = TrayIcon.CreateIcon(newImeState)   // 새 아이콘 생성
szTip = I18n.GetTrayTooltip(newImeState)  // 툴팁 갱신
Shell_NotifyIconW(NIM_MODIFY, ref nid)
// 이전 hIcon은 DestroyIcon으로 즉시 해제
```

**NIM_DELETE -- 앱 종료 시:**
```
uFlags = NIF_GUID
guidItem = 고정 GUID
Shell_NotifyIconW(NIM_DELETE, ref nid)
```

#### 콜백 메시지 처리

- 콜백 메시지: `AppMessages.WM_TRAY_CALLBACK` (`WM_USER + 1`)
- Program.cs의 WndProc에서 WM_TRAY_CALLBACK을 수신하고, lParam의 하위 워드로 마우스 이벤트를 분기:
  ```
  WM_RBUTTONUP -> 팝업 메뉴 표시
  WM_LBUTTONUP -> tray_click_action 설정에 따라 동작:
    "toggle"   -> 인디케이터 표시/숨기기 토글
    "settings" -> 설정 파일 열기 (Process.Start)
    "none"     -> 동작 없음
  ```

#### 트레이 툴팁

- `tray_tooltip: true`이면 szTip에 현재 상태 텍스트 설정.
- 한글 표시 (P2):
  - Hangul: `"한글 모드"`
  - English: `"영문 모드"`
  - NonKorean: `"영문 모드 (비한국어)"`
- `language: "en"` 설정 시 영문 텍스트로 전환 (I18n.cs에서 관리).

---

### `UI/TrayIcon.cs`

#### 트레이 아이콘 GDI 생성

- **캐럿+점(caret_dot) 디자인**: 아이콘에 텍스트("한"/"En")를 표시하지 않는다.
- IME 트레이 아이콘과 표현 방식을 차별화하기 위해 도형만 사용.

#### 상태별 배경색

- 한/영 상태를 **배경색으로만** 구분:
  - 한글(Hangul): `config.HangulBg` (기본 `#16A34A` 초록)
  - 영문(English): `config.EnglishBg` (기본 `#D97706` 앰버)
  - 비한국어(NonKorean): `config.NonKoreanBg` (기본 `#6B7280` 회색)
- 색상 변환: `ColorHelper.HexToColorRef(hexString)` 사용 (직접 변환 금지, P4).

#### 아이콘 크기

- `GetSystemMetrics(SM_CXSMICON)` / `GetSystemMetrics(SM_CYSMICON)`으로 시스템이 요구하는 소형 아이콘 크기를 조회.
- 이 크기로 GDI 비트맵을 생성한다 (하드코딩 금지).

#### 아이콘 생성 절차

```
1. GetSystemMetrics(SM_CXSMICON) -> iconW
   GetSystemMetrics(SM_CYSMICON) -> iconH

2. CreateCompatibleDC(NULL) -> memDC

3. BITMAPINFOHEADER (32bpp, BI_RGB) -> CreateDIBSection -> HBITMAP (color bitmap)
   - biWidth = iconW, biHeight = iconH (bottom-up)

4. SelectObject(memDC, hBitmap)

5. 배경색으로 전체 영역 채움:
   - CreateSolidBrush(ColorHelper.HexToColorRef(bgColor))
   - FillRect(memDC, &rect, hBrush)
   - DeleteObject(hBrush)

6. 캐럿+점 디자인 렌더링:
   - 캐럿 모양(세로 바) + 작은 점(dot)을 흰색(#FFFFFF)으로 그림
   - 아이콘 중앙 부근에 배치

7. CreateCompatibleBitmap으로 마스크 비트맵 생성 (monochrome mask)
   - 또는 32bpp ARGB DIB의 alpha 채널을 활용

8. ICONINFO 구조체 구성:
   ICONINFO iconInfo;
   iconInfo.fIcon = true;      // 아이콘 (커서 아님)
   iconInfo.hbmColor = hBitmap; // 색상 비트맵
   iconInfo.hbmMask = hMask;    // 마스크 비트맵

9. CreateIconIndirect(&iconInfo) -> HICON

10. 임시 GDI 리소스 정리:
    - DeleteDC(memDC)
    - DeleteObject(hBitmap)
    - DeleteObject(hMask)
    - SafeIconHandle로 HICON 래핑하여 반환
```

#### HICON 수명 관리

- 상태 변경 시마다 새 HICON 생성.
- 이전 HICON은 `DestroyIcon`으로 즉시 해제.
- `SafeIconHandle` 래퍼 사용 (SafeGdiHandles.cs).
- Shell_NotifyIconW(NIM_MODIFY) 호출 후 이전 아이콘을 안전하게 해제.

---

### 팝업 메뉴

#### 메뉴 생성 방식

- Win32 `CreatePopupMenu` + `AppendMenuW`로 동적 생성한다.
- 서브메뉴(인디케이터 스타일, 표시 모드, 투명도)는 별도 `HMENU`로 생성 후 `MF_POPUP`으로 부모 메뉴에 삽입.
- 라디오 버튼 동작: `CheckMenuRadioItem`으로 현재 선택 항목 표시.
- **모든 메뉴 텍스트는 한글로 표시** (P2). `language: "en"` 설정 시 I18n.cs에서 영문 텍스트 제공.

#### 메뉴 구조 (정확한 텍스트)

```
우클릭
+-- 인디케이터 스타일 >>  (* 점 / 사각 / 밑줄 / 세로바 / 텍스트)
+-- 표시 모드 >>          (* 이벤트 시만 / 항상 표시)
+-- 투명도 >>             (* 진하게 0.95 / 보통 0.85 / 연하게 0.6)
+-- --------
+-- [ ] 시작 프로그램 등록
+-- 설정 파일 열기...
+-- --------
+-- 종료
```

#### 메뉴 항목 ID 정의

AppMessages.cs 또는 Tray.cs 내부 상수로 정의:

```
서브메뉴 "인디케이터 스타일":
  IDM_STYLE_DOT       = 1001   "점"          -> IndicatorStyle.CaretDot
  IDM_STYLE_SQUARE    = 1002   "사각"         -> IndicatorStyle.CaretSquare
  IDM_STYLE_UNDERLINE = 1003   "밑줄"         -> IndicatorStyle.CaretUnderline
  IDM_STYLE_VBAR      = 1004   "세로바"       -> IndicatorStyle.CaretVbar
  IDM_STYLE_LABEL     = 1005   "텍스트"       -> IndicatorStyle.Label

서브메뉴 "표시 모드":
  IDM_DISPLAY_EVENT   = 2001   "이벤트 시만"  -> DisplayMode.OnEvent
  IDM_DISPLAY_ALWAYS  = 2002   "항상 표시"    -> DisplayMode.Always

서브메뉴 "투명도":
  IDM_OPACITY_HIGH    = 3001   "진하게 0.95"  -> opacity = 0.95
  IDM_OPACITY_NORMAL  = 3002   "보통 0.85"    -> opacity = 0.85
  IDM_OPACITY_LOW     = 3003   "연하게 0.6"   -> opacity = 0.6
  (프리셋 값은 config.TrayQuickOpacityPresets = [0.95, 0.85, 0.6] 에서 로드)

메인 메뉴:
  IDM_STARTUP         = 4001   "시작 프로그램 등록"  -> 체크 토글
  IDM_OPEN_SETTINGS   = 4002   "설정 파일 열기..."   -> Process.Start
  IDM_EXIT            = 4003   "종료"                -> PostQuitMessage
```

#### 메뉴 구성 코드 (의사 코드)

```csharp
void ShowTrayMenu(IntPtr hwnd)
{
    // --- 서브메뉴 1: 인디케이터 스타일 ---
    IntPtr hStyleMenu = CreatePopupMenu();
    AppendMenuW(hStyleMenu, MF_STRING, IDM_STYLE_DOT, "점");
    AppendMenuW(hStyleMenu, MF_STRING, IDM_STYLE_SQUARE, "사각");
    AppendMenuW(hStyleMenu, MF_STRING, IDM_STYLE_UNDERLINE, "밑줄");
    AppendMenuW(hStyleMenu, MF_STRING, IDM_STYLE_VBAR, "세로바");
    AppendMenuW(hStyleMenu, MF_STRING, IDM_STYLE_LABEL, "텍스트");
    // 현재 스타일에 라디오 체크
    CheckMenuRadioItem(hStyleMenu, IDM_STYLE_DOT, IDM_STYLE_LABEL,
                       CurrentStyleMenuId(), MF_BYCOMMAND);

    // --- 서브메뉴 2: 표시 모드 ---
    IntPtr hDisplayMenu = CreatePopupMenu();
    AppendMenuW(hDisplayMenu, MF_STRING, IDM_DISPLAY_EVENT, "이벤트 시만");
    AppendMenuW(hDisplayMenu, MF_STRING, IDM_DISPLAY_ALWAYS, "항상 표시");
    CheckMenuRadioItem(hDisplayMenu, IDM_DISPLAY_EVENT, IDM_DISPLAY_ALWAYS,
                       CurrentDisplayMenuId(), MF_BYCOMMAND);

    // --- 서브메뉴 3: 투명도 ---
    IntPtr hOpacityMenu = CreatePopupMenu();
    AppendMenuW(hOpacityMenu, MF_STRING, IDM_OPACITY_HIGH, "진하게 0.95");
    AppendMenuW(hOpacityMenu, MF_STRING, IDM_OPACITY_NORMAL, "보통 0.85");
    AppendMenuW(hOpacityMenu, MF_STRING, IDM_OPACITY_LOW, "연하게 0.6");
    CheckMenuRadioItem(hOpacityMenu, IDM_OPACITY_HIGH, IDM_OPACITY_LOW,
                       CurrentOpacityMenuId(), MF_BYCOMMAND);

    // --- 메인 메뉴 ---
    IntPtr hMenu = CreatePopupMenu();
    AppendMenuW(hMenu, MF_POPUP, hStyleMenu, "인디케이터 스타일");
    AppendMenuW(hMenu, MF_POPUP, hDisplayMenu, "표시 모드");
    AppendMenuW(hMenu, MF_POPUP, hOpacityMenu, "투명도");
    AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
    AppendMenuW(hMenu, isStartupRegistered ? MF_CHECKED : MF_UNCHECKED,
                IDM_STARTUP, "시작 프로그램 등록");
    AppendMenuW(hMenu, MF_STRING, IDM_OPEN_SETTINGS, "설정 파일 열기...");
    AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
    AppendMenuW(hMenu, MF_STRING, IDM_EXIT, "종료");

    // --- 표시 ---
    GetCursorPos(out POINT pt);
    SetForegroundWindow(hwnd);  // 필수 workaround (1)
    TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
    PostMessage(hwnd, WM_NULL, 0, 0);  // 필수 workaround (2)

    // --- 정리 ---
    DestroyMenu(hMenu);  // 서브메뉴도 자동 파괴됨
}
```

#### SetForegroundWindow + WM_NULL 워크어라운드

트레이 아이콘 우클릭(WM_RBUTTONUP) 처리 시 반드시 적용:

```
1. SetForegroundWindow(hwnd) 호출
   -- 이것이 없으면 메뉴가 떴는데 다른 곳을 클릭해도 메뉴가 안 닫히는 Win32 알려진 문제 발생.

2. CreatePopupMenu + AppendMenuW로 메뉴 구성

3. TrackPopupMenu(hMenu, TPM_RIGHTBUTTON, pt.X, pt.Y, 0, hwnd, NULL)

4. PostMessage(hwnd, WM_NULL, 0, 0)
   -- 메뉴 닫힘 관련 Win32 알려진 문제 workaround. Microsoft 공식 권장.
```

#### 메뉴 선택 처리 (WM_COMMAND)

- Program.cs의 WndProc에서 `WM_COMMAND` 메시지를 수신.
- `LOWORD(wParam)` = 메뉴 항목 ID로 분기.
- 처리 순서:
  1. 설정 변경 (volatile AppConfig 교체)
  2. config.json 저장 (Settings.Save)
  3. 인디케이터 즉시 반영 (PostMessage로 WM_CONFIG_CHANGED)

```csharp
void HandleMenuCommand(int menuId)
{
    switch (menuId)
    {
        case IDM_STYLE_DOT:
            UpdateConfig(c => c.IndicatorStyle = IndicatorStyle.CaretDot);
            break;
        case IDM_STYLE_SQUARE:
            UpdateConfig(c => c.IndicatorStyle = IndicatorStyle.CaretSquare);
            break;
        // ... 기타 스타일 ...

        case IDM_DISPLAY_EVENT:
            UpdateConfig(c => c.DisplayMode = DisplayMode.OnEvent);
            break;
        case IDM_DISPLAY_ALWAYS:
            UpdateConfig(c => c.DisplayMode = DisplayMode.Always);
            break;

        case IDM_OPACITY_HIGH:
            UpdateConfig(c => c.Opacity = 0.95);
            break;
        case IDM_OPACITY_NORMAL:
            UpdateConfig(c => c.Opacity = 0.85);
            break;
        case IDM_OPACITY_LOW:
            UpdateConfig(c => c.Opacity = 0.6);
            break;

        case IDM_STARTUP:
            ToggleStartupRegistration();  // Startup.cs 호출
            // Startup.cs는 반드시 Task Scheduler (schtasks.exe CLI) 방식으로 구현.
            // Registry RunKey 방식 사용 금지! (관리자 권한 앱에서 UAC 프롬프트 우회 불가)
            // 구현: Process.Start("schtasks.exe", "/create /tn \"KoEnVue\" /tr ... /sc ONLOGON /rl HIGHEST /f")
            // 삭제: Process.Start("schtasks.exe", "/delete /tn \"KoEnVue\" /f")
            // 상세: PRD §1.5.4 참조
            break;

        case IDM_OPEN_SETTINGS:
            OpenSettingsFile();  // Process.Start로 config.json 열기
            break;

        case IDM_EXIT:
            PostQuitMessage(0);
            break;
    }
}
```

#### 설정 파일 열기 (IDM_OPEN_SETTINGS)

```csharp
void OpenSettingsFile()
{
    string configPath = Settings.GetConfigFilePath();
    // config.json이 없으면 기본 설정으로 생성
    if (!File.Exists(configPath))
        Settings.SaveDefault(configPath);
    // 시스템 기본 편집기로 열기
    Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
}
```

---

### 글로벌 핫키

#### RegisterHotKey P/Invoke

- `User32.RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)` 사용.
- 메인 메시지 루프에서 `WM_HOTKEY` 메시지를 처리.

#### 핫키 5종

| config 키 | 기본값 | 핫키 ID | 동작 |
|-----------|--------|---------|------|
| `hotkey_toggle_visibility` | `Ctrl+Alt+H` | HOTKEY_TOGGLE = 1 | 인디케이터 표시/숨기기 토글 |
| `hotkey_cycle_style` | `Ctrl+Alt+I` | HOTKEY_STYLE = 2 | 스타일 순환: caret_dot -> caret_square -> caret_underline -> caret_vbar -> label -> caret_dot |
| `hotkey_cycle_position` | `Ctrl+Alt+P` | HOTKEY_POSITION = 3 | 위치 모드 순환: caret -> mouse -> fixed -> caret |
| `hotkey_cycle_display` | `Ctrl+Alt+D` | HOTKEY_DISPLAY = 4 | 표시 모드 순환: on_event -> always -> on_event |
| `hotkey_open_settings` | `Ctrl+Alt+S` | HOTKEY_SETTINGS = 5 | 설정 파일 열기 (Process.Start로 config.json) |

#### 핫키 문자열 파싱

config에서 `"Ctrl+Alt+H"` 형태의 문자열을 파싱하여 modifier + vk 조합으로 변환:

```csharp
(uint modifiers, uint vk) ParseHotkey(string hotkeyString)
{
    // "Ctrl+Alt+H" -> parts = ["Ctrl", "Alt", "H"]
    string[] parts = hotkeyString.Split('+');
    uint modifiers = 0;
    uint vk = 0;

    foreach (var part in parts)
    {
        string trimmed = part.Trim();
        switch (trimmed.ToUpperInvariant())
        {
            case "CTRL":  modifiers |= MOD_CONTROL; break;
            case "ALT":   modifiers |= MOD_ALT;     break;
            case "SHIFT": modifiers |= MOD_SHIFT;   break;
            case "WIN":   modifiers |= MOD_WIN;     break;
            default:
                // 단일 키 문자 또는 VK 코드
                if (trimmed.Length == 1)
                    vk = (uint)trimmed.ToUpperInvariant()[0]; // 'A'-'Z', '0'-'9'
                // 특수 키 매핑 (F1-F12, Insert, Delete 등)
                else
                    vk = MapSpecialKey(trimmed);
                break;
        }
    }

    return (modifiers, vk);
}
```

- MOD_CONTROL = 0x0002
- MOD_ALT = 0x0001
- MOD_SHIFT = 0x0004
- MOD_WIN = 0x0008
- 이 상수들은 Win32Types.cs에 정의.

#### 핫키 등록 (앱 시작 시)

```csharp
void RegisterHotkeys(IntPtr hwndMain, AppConfig config)
{
    if (!config.HotkeysEnabled) return;

    TryRegisterHotkey(hwndMain, HOTKEY_TOGGLE, config.HotkeyToggleVisibility);
    TryRegisterHotkey(hwndMain, HOTKEY_STYLE, config.HotkeyCycleStyle);
    TryRegisterHotkey(hwndMain, HOTKEY_POSITION, config.HotkeyCyclePosition);
    TryRegisterHotkey(hwndMain, HOTKEY_DISPLAY, config.HotkeyCycleDisplay);
    TryRegisterHotkey(hwndMain, HOTKEY_SETTINGS, config.HotkeyOpenSettings);
}

void TryRegisterHotkey(IntPtr hwnd, int id, string hotkeyString)
{
    if (string.IsNullOrEmpty(hotkeyString)) return;

    var (modifiers, vk) = ParseHotkey(hotkeyString);
    if (vk == 0) return; // 파싱 실패

    bool success = RegisterHotKey(hwnd, id, modifiers, vk);
    if (!success)
    {
        // 등록 실패 (다른 앱이 같은 핫키 사용 중)
        Logger.Warn($"Failed to register hotkey: {hotkeyString} (id={id})");
        // 해당 핫키만 비활성화, 다른 핫키는 계속 등록 시도
    }
}
```

#### 핫키 해제 (앱 종료 시)

```csharp
void UnregisterHotkeys(IntPtr hwndMain)
{
    UnregisterHotKey(hwndMain, HOTKEY_TOGGLE);
    UnregisterHotKey(hwndMain, HOTKEY_STYLE);
    UnregisterHotKey(hwndMain, HOTKEY_POSITION);
    UnregisterHotKey(hwndMain, HOTKEY_DISPLAY);
    UnregisterHotKey(hwndMain, HOTKEY_SETTINGS);
}
```

- Program.cs 종료 정리에서 호출.

#### WM_HOTKEY 처리 (Program.cs WndProc에서)

```csharp
case WM_HOTKEY:
    int hotkeyId = (int)wParam;
    switch (hotkeyId)
    {
        case HOTKEY_TOGGLE:
            // 인디케이터 표시/숨기기 토글
            _indicatorVisible = !_indicatorVisible;
            if (!_indicatorVisible) HideOverlay();
            break;

        case HOTKEY_STYLE:
            // 스타일 순환: CaretDot -> CaretSquare -> CaretUnderline -> CaretVbar -> Label -> CaretDot
            // IndicatorStyle enum: Label=0, CaretDot=1, CaretSquare=2, CaretUnderline=3, CaretVbar=4
            // 순환 순서: 1→2→3→4→0→1 (CaretDot부터 시작)
            var cur = (int)config.IndicatorStyle;
            var nextStyle = (IndicatorStyle)(cur == 4 ? 0 : cur + 1);  // Vbar(4) 다음은 Label(0)
            UpdateConfig(c => c.IndicatorStyle = nextStyle);
            break;

        case HOTKEY_POSITION:
            // 위치 모드 순환: caret -> mouse -> fixed -> caret
            var nextPos = config.PositionMode switch
            {
                "caret" => "mouse",
                "mouse" => "fixed",
                "fixed" => "caret",
                _ => "caret"
            };
            UpdateConfig(c => c.PositionMode = nextPos);
            break;

        case HOTKEY_DISPLAY:
            // 표시 모드 순환: OnEvent -> Always -> OnEvent
            var nextDisplay = config.DisplayMode == DisplayMode.OnEvent
                ? DisplayMode.Always : DisplayMode.OnEvent;
            UpdateConfig(c => c.DisplayMode = nextDisplay);
            break;

        case HOTKEY_SETTINGS:
            // 설정 파일 열기
            OpenSettingsFile();
            break;
    }
    break;
```

- 핫키로 설정 변경 시에도: 설정 변경 + config.json 저장 + 인디케이터 즉시 반영.

---

## 검증 기준

### 트레이 아이콘 (Tray.cs)
- [ ] Shell_NotifyIconW P/Invoke 직접 구현 (WinForms NotifyIcon 사용 금지)
- [ ] NOTIFYICONDATAW 구조체에 고정 GUID 사용 (NIF_GUID 플래그)
- [ ] tray_enabled == false 시 트레이 등록 건너뛰기
- [ ] NIM_ADD 후 NIM_SETVERSION(NOTIFYICON_VERSION_4) 호출
- [ ] NIM_ADD: 앱 시작 시 아이콘 정상 등록
- [ ] NIM_MODIFY: IME 상태 변경 시 아이콘 + 툴팁 갱신
- [ ] NIM_DELETE: 앱 종료 시 아이콘 제거
- [ ] 콜백 메시지: WM_TRAY_CALLBACK (WM_USER + 1) 정상 수신
- [ ] WM_RBUTTONUP -> 팝업 메뉴 표시
- [ ] WM_LBUTTONUP -> tray_click_action 설정에 따른 동작

### 트레이 아이콘 생성 (TrayIcon.cs)
- [ ] 캐럿+점 디자인 (텍스트 미표시)
- [ ] 한글: #16A34A (초록), 영문: #D97706 (앰버), 비한국어: #6B7280 (회색) 배경색
- [ ] 아이콘 크기: GetSystemMetrics(SM_CXSMICON/SM_CYSMICON) -- 하드코딩 금지
- [ ] GDI HBITMAP -> CreateIconIndirect -> HICON 정상 생성
- [ ] 상태 변경 시 새 HICON 생성 + 이전 HICON DestroyIcon 해제
- [ ] SafeIconHandle 래퍼 사용
- [ ] ColorHelper.HexToColorRef 사용 (직접 색상 변환 금지)

### 팝업 메뉴
- [ ] CreatePopupMenu + AppendMenuW로 동적 생성
- [ ] 서브메뉴: 별도 HMENU + MF_POPUP으로 삽입
- [ ] CheckMenuRadioItem으로 현재 선택 항목 라디오 버튼 표시
- [ ] 메뉴 텍스트 한글 (P2): "인디케이터 스타일", "표시 모드", "투명도", "종료" 등
- [ ] 정확한 메뉴 구조:
  - 인디케이터 스타일 >> (점/사각/밑줄/세로바/텍스트)
  - 표시 모드 >> (이벤트 시만/항상 표시)
  - 투명도 >> (진하게 0.95/보통 0.85/연하게 0.6)
  - --- (구분선)
  - [ ] 시작 프로그램 등록 (체크 토글)
  - 설정 파일 열기...
  - --- (구분선)
  - 종료
- [ ] SetForegroundWindow + WM_NULL workaround 적용
- [ ] WM_COMMAND -> 설정 변경 + config.json 저장 + 인디케이터 즉시 반영

### 글로벌 핫키
- [ ] RegisterHotKey P/Invoke 사용
- [ ] WM_HOTKEY 처리 (메인 메시지 루프)
- [ ] 5종 핫키 등록:
  - Ctrl+Alt+H: 표시/숨기기 토글
  - Ctrl+Alt+I: 스타일 순환 (caret_dot -> square -> underline -> vbar -> label)
  - Ctrl+Alt+P: 위치 모드 순환 (caret -> mouse -> fixed)
  - Ctrl+Alt+D: 표시 모드 순환 (on_event -> always)
  - Ctrl+Alt+S: 설정 파일 열기
- [ ] 핫키 문자열 파싱: "Ctrl+Alt+H" -> MOD_CONTROL | MOD_ALT + VK_H
- [ ] hotkeys_enabled=true일 때만 등록
- [ ] 등록 실패 시: 로그 경고 + 해당 핫키만 비활성화 (다른 핫키 정상 등록)
- [ ] 앱 종료 시 UnregisterHotKey로 해제

### P1-P5 원칙
- [ ] P1: NuGet 외부 패키지 없음
- [ ] P2: 메뉴 텍스트, 툴팁 한글 기본
- [ ] P3: 매직 넘버 없음 (메뉴 ID, 핫키 ID 모두 상수 정의)
- [ ] P4: ColorHelper 사용, SafeIconHandle 사용, P/Invoke는 Native/ 폴더에서만 선언
- [ ] P5: (트레이 자체는 관리자 권한과 무관하나, 시작 프로그램 등록은 Task Scheduler 경유)

## 산출물

| 파일 | 설명 |
|------|------|
| `UI/Tray.cs` | Shell_NotifyIconW 기반 트레이 아이콘 관리 + 팝업 메뉴 + 핫키 등록/해제 |
| `UI/TrayIcon.cs` | GDI 기반 트레이 아이콘 동적 생성 (캐럿+점 디자인, 상태별 배경색) |
