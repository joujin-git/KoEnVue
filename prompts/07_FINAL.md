# Phase 07: 고급 기능 + 빌드 + 테스트

## 목표
UIA 클라이언트, 앱 라이프사이클 고급 기능, 테마, 빌드, 통합 테스���를 완성한다.
이 단계가 완료되면 KoEnVue가 프로덕션 준비 상태가 된다.

## 선행 조건
- Phase 01~06 모두 완료

## 팀 구성
- **이온-감지**: UiaClient.cs + CaretTracker UIA 통합 (병렬 A)
- **이온-시스템**: Startup.cs + 라이프사이클 고급 기능 (병렬 B)
- **이온-렌더**: 테마 프리셋 + ���버그 오버레이 (병렬 C)
- **이온-QA**: 빌드 검증 + 통합 테스트 (순차, 위 3개 완료 후)
- 모두 mode: "plan"

## 병렬 실행 계획
```
병렬 그룹 1 (동시 실행):
  이온-감���: UiaClient.cs
  이온-시스템: Startup.cs + power/monitor/high-contrast
  이온-렌더: 테마 프리셋 + 디버그 오버레이

순차 (그룹 1 완료 후):
  이온-QA: NativeAOT 빌드 + 통합 테스트 + 체크리스트 검증
```

---

## 구현 명세

### Part A: Detector/UiaClient.cs — UI Automation 전용 스레드

#### COM 초기화 + IUIAutomation

```csharp
// [GeneratedComInterface] source generator 사용 (NativeAOT 호환)
[GeneratedComInterface]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
partial interface IUIAutomation
{
    [PreserveSig]
    int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
}

[GeneratedComInterface]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
partial interface IUIAutomationElement
{
    [PreserveSig]
    int GetCurrentPattern(int patternId, out nint patternObject);
    // ... 기타 메서드
}

[GeneratedComInterface]
[Guid("506a921a-fcc9-409f-b23b-37eb74106872")]  // IUIAutomationTextPattern2 (GetCaretRange는 TextPattern2 소속!)
partial interface IUIAutomationTextPattern2
{
    [PreserveSig]
    int GetCaretRange(out bool isActive, out IUIAutomationTextRange range);
}

[GeneratedComInterface]
[Guid("a543cc6a-f4ae-494b-8239-c814481187a8")]  // IUIAutomationTextRange
partial interface IUIAutomationTextRange
{
    [PreserveSig]
    int GetBoundingRectangles(out double[] boundingRects);
}
```

#### UIA 전용 스레드 구현

```csharp
static class UiaClient
{
    private static IUIAutomation? _automation;
    private static readonly ConcurrentQueue<UiaRequest> _requestQueue = new();
    private static readonly ManualResetEventSlim _signal = new();

    public static void Initialize()
    {
        // Phase 03에서 스레드 시작됨
        // 여기서는 COM 객체 초기화
    }

    static void UiaThreadLoop()
    {
        // COM STA 초기화
        Ole32.CoInitializeEx(IntPtr.Zero, Win32Constants.COINIT_APARTMENTTHREADED);

        // IUIAutomation 인스턴스 생성
        Guid clsid = new("ff48dba4-60ef-4201-aa87-54103eef594e"); // CUIAutomation
        Guid iid = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");   // IUIAutomation
        Ole32.CoCreateInstance(ref clsid, IntPtr.Zero, Win32Constants.CLSCTX_INPROC_SERVER, ref iid, out var obj);
        _automation = (IUIAutomation)obj;

        // 요청 대기 루프
        while (!_stopping)
        {
            _signal.Wait(TimeSpan.FromSeconds(1));
            _signal.Reset();

            while (_requestQueue.TryDequeue(out var request))
            {
                var result = GetCaretBoundsInternal(request.HwndFocus);
                request.Completion.TrySetResult(result);
            }
        }

        Ole32.CoUninitialize();
    }

    // 외부 호출 (감지 스레드에서 호출)
    public static (int x, int y, int w, int h)? GetCaretBounds(IntPtr hwndFocus, int timeoutMs)
    {
        var request = new UiaRequest { HwndFocus = hwndFocus };
        _requestQueue.Enqueue(request);
        _signal.Set();

        // 타임아웃 대기 (기본 200ms)
        if (request.Completion.Task.Wait(timeoutMs))
            return request.Completion.Task.Result;

        return null;  // 타임아웃 → null → 다음 fallback
    }

    private static (int, int, int, int)? GetCaretBoundsInternal(IntPtr hwndFocus)
    {
        if (_automation == null) return null;

        IUIAutomationElement? element = null;
        try
        {
            if (_automation.ElementFromHandle(hwndFocus, out element) != 0 || element == null)
                return null;

            // TextPattern2 → GetCaretRange → GetBoundingRectangles
            const int UIA_TextPattern2Id = 10024;  // TextPattern2 (GetCaretRange 포함)
            if (element.GetCurrentPattern(UIA_TextPattern2Id, out nint pattern) != 0)
                return null;

            // [GeneratedComInterface] source generator가 생성한 래퍼를 사용.
            // Marshal.GetObjectForIUnknown은 NativeAOT 비호환 — 사용 금지!
            // StrategyBasedComWrappers는 [GeneratedComInterface]가 자동 생성하는 인프라이므로
            // PRD의 "ComWrappers 수동 래핑 불필요" 원칙에 위배되지 않음.
            // GetCurrentPattern()이 반환하는 raw nint를 타입화된 인터페이스로 변환하는 유일한 방법.
            var textPattern = (IUIAutomationTextPattern2)
                StrategyBasedComWrappers.DefaultMarshallerInstance
                    .GetOrCreateObjectForComInstance(pattern, CreateObjectFlags.None);
            if (textPattern.GetCaretRange(out _, out var range) != 0)
                return null;

            if (range.GetBoundingRectangles(out double[] rects) != 0 || rects.Length < 4)
                return null;

            return ((int)rects[0], (int)rects[1], (int)rects[2], (int)rects[3]);
        }
        catch
        {
            return null;
        }
    }
}
```

핵심 규칙:
- 전용 스레드 1개, `COINIT_APARTMENTTHREADED` (STA)
- 매 호출마다 new Thread 금지
- UIA COM 호출을 감지/메인 스레드에서 직접 실행 금지
- 타임아웃: config.Advanced.UiaTimeoutMs (기본 200ms)
- UIA 결과 캐시: config.Advanced.UiaCacheTtlMs (기본 500ms)
- skip_uia_for_processes: 특정 프로세스에서 UIA 건너���기

#### CaretTracker UIA 통합

```csharp
// CaretTracker.cs의 Tier 2를 실제 구현으로 교체:
// [2순위] UI Automation
if (!config.Advanced.SkipUiaForProcesses.Contains(processName))
{
    var uiaResult = UiaClient.GetCaretBounds(hwndFocus, config.Advanced.UiaTimeoutMs);
    if (uiaResult.HasValue)
    {
        CacheMethod(processName, 2);
        return uiaResult.Value;
    }
}
```

---

### Part B: 라이프사이클 고급 기능

#### Utils/Startup.cs — Task Scheduler 시작 프로그램 등록

```csharp
static class Startup
{
    private const string TaskName = "KoEnVue";

    // 등록
    public static bool Register(string exePath)
    {
        // schtasks /create /tn "KoEnVue" /rl HIGHEST /sc ONLOGON /tr "<exe_path>" /f
        var psi = new ProcessStartInfo("schtasks")
        {
            Arguments = $"/create /tn \"{TaskName}\" /rl HIGHEST /sc ONLOGON " +
                        $"/tr \"\\\"{exePath}\\\"\" /f",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
        return proc?.ExitCode == 0;
        // 최초 등록 시에만 UAC 1회 필요
    }

    // 해제
    public static bool Unregister()
    {
        // schtasks /delete /tn "KoEnVue" /f
        var psi = new ProcessStartInfo("schtasks")
        {
            Arguments = $"/delete /tn \"{TaskName}\" /f",
            UseShellExecute = false, CreateNoWindow = true
        };
        var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
        return proc?.ExitCode == 0;
    }

    // 상태 확인
    public static bool IsRegistered()
    {
        // schtasks /query /tn "KoEnVue"
        var psi = new ProcessStartInfo("schtasks")
        {
            Arguments = $"/query /tn \"{TaskName}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        var proc = Process.Start(psi);
        proc?.WaitForExit(3000);
        return proc?.ExitCode == 0;
    }
}
```

#### 전원 관리 복귀 (NF-23)

```csharp
// Program.cs의 HandlePowerResume() 구현
static void HandlePowerResume()
{
    // WM_POWERBROADCAST + PBT_APMRESUMESUSPEND (0x0007)
    // 1. 폴링 루프 자동 재개 확인 (IsBackground 스레드는 OS가 재개)
    // 2. IME 상태 즉시 재감지 (3-tier 체인)
    var state = ImeStatus.Detect(User32.GetForegroundWindow(), ...);
    HandleImeStateChanged(state);

    // 3. 모니터 DPI/rcWork 재조회 (구성 변경 가능)
    RefreshMonitorInfo();

    // 4. HFONT 재생성 여부 확인 (DPI 변경 시)
    if (DpiChanged()) RecreateFont();
}
```

#### 모니터/작업표시줄 변경 (WM_DISPLAYCHANGE + WM_SETTINGCHANGE)

```csharp
static void HandleDisplayChange()
{
    // WM_DISPLAYCHANGE: 모니터 연결/해제, 해상도 변경
    // 1. 모니터 목록/rcWork 재조회
    RefreshMonitorInfo();
    // 2. 현재 캐럿 위치의 모니터 DPI 재조회
    // 3. DPI 변경 시 HFONT + DIB 재생성
    if (DpiChanged())
    {
        RecreateFont();
        RecreateDIB();
    }
    // 4. 인디케이터 위치 rcWork 클램핑 재계산
}

static void HandleSettingChange()
{
    // WM_SETTINGCHANGE: 작업표시줄 위치/크기 변경
    RefreshMonitorInfo();  // rcWork 재조회

    // 고대비 모드 감지
    // HIGHCONTRAST 구조체 (Win32Types.cs에 정의):
    //   [StructLayout(LayoutKind.Sequential)]
    //   struct HIGHCONTRAST { public uint cbSize; public uint dwFlags; public IntPtr lpszDefaultScheme; }
    //   HCF_HIGHCONTRASTON = 0x00000001
    // SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(HIGHCONTRAST), &hc, 0)
    // hc.dwFlags & HCF_HIGHCONTRASTON != 0 이면 고대비 활성
    // theme: "system" 일 때만 Windows 강조색 연동
    if (_config.Theme == "system")
        RefreshSystemThemeColors();
}
```

#### 고대비 모드 (NF-24)

```
- 사용자 설정 색상을 그대로 사용 (시스템 색상���로 자동 전환하지 않음)
- theme: "system" 선택 시에만 Windows 강조색 연동
- 감지: SystemParametersInfoW(SPI_GETHIGHCONTRAST) 또는 WM_SETTINGCHANGE
```

#### 포터블 모드 자동 감지

```
- exe 디렉토리에 config.json이 존재하고 %APPDATA% 파일이 없을 때 포터블 모���
- 트레이 메뉴에 현재 모드 표시: "[포터블]" 또는 "[설치]"
```

---

### Part C: 테마 + 디버그 오버레이

#### 테마 프리셋 (F-44)

```csharp
static class ThemePresets
{
    // theme이 "custom"이 아니면 색상을 프리셋 값으로 오버라이드
    public static AppConfig Apply(AppConfig config)
    {
        return config.Theme switch
        {
            "custom" => config,  // 사용자 색상 그대로
            "minimal" => config with
            {
                HangulBg = "#1F2937", HangulFg = "#F9FAFB",
                EnglishBg = "#9CA3AF", EnglishFg = "#111827",
                NonKoreanBg = "#D1D5DB", NonKoreanFg = "#374151",
            },
            "vivid" => config with
            {
                HangulBg = "#22C55E", HangulFg = "#FFFFFF",
                EnglishBg = "#EF4444", EnglishFg = "#FFFFFF",
                NonKoreanBg = "#3B82F6", NonKoreanFg = "#FFFFFF",
            },
            "pastel" => config with
            {
                HangulBg = "#86EFAC", HangulFg = "#14532D",
                EnglishBg = "#FDE68A", EnglishFg = "#78350F",
                NonKoreanBg = "#C4B5FD", NonKoreanFg = "#3B0764",
            },
            "dark" => config with
            {
                HangulBg = "#065F46", HangulFg = "#D1FAE5",
                EnglishBg = "#92400E", EnglishFg = "#FEF3C7",
                NonKoreanBg = "#374151", NonKoreanFg = "#F3F4F6",
            },
            "system" => ApplySystemTheme(config),
            _ => config
        };
    }

    static AppConfig ApplySystemTheme(AppConfig config)
    {
        // Windows 강조색 가져오기
        // 우선: DwmGetColorizationColor (dwmapi.dll) — 실제 Windows accent color 반환
        // 대안: GetSysColor(COLOR_HIGHLIGHT) — highlight 색상, accent와 다를 수 있음
        // DwmGetColorizationColor 사용 시 dwmapi.dll P/Invoke 추가 필요
        uint accentColor = User32.GetSysColor(13);  // COLOR_HIGHLIGHT (fallback)
        // COLORREF(0x00BBGGRR) → RGB 분리
        byte r = (byte)(accentColor & 0xFF);
        byte g = (byte)((accentColor >> 8) & 0xFF);
        byte b = (byte)((accentColor >> 16) & 0xFF);
        string hangulBg = $"#{r:X2}{g:X2}{b:X2}";
        // 보색 계산: 각 채널 255 - 원본
        string englishBg = $"#{255 - r:X2}{255 - g:X2}{255 - b:X2}";
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }
}
```

#### 라벨 스타일 옵션 (F-45)

```
label_style 값에 따라 label 인디케이터 내부 표시:
  "text" — 텍스트 표시 (한/En/EN) — 기본값
  "dot"  — 색상 점만 표시 (텍스트 없음)
  "icon" — ㄱ/A 아이콘 스타일
  "flag" — 미지원 (GDI DrawTextW 컬러 이모지 미지원)
```

#### 디버그 오버레이 (F-47)

```csharp
// config.Advanced.DebugOverlay가 true일 때 인디케이터에 추가 정보 표시
if (_config.Advanced.DebugOverlay)
{
    string debugInfo = string.Join("\n",
        $"방식: {currentMethod}순위",      // 감지 방식 (1/2/3순위)
        $"좌표: ({caretX},{caretY})",       // ��럿 좌표
        $"DPI: {dpiX}",                     // 모니터 DPI
        $"폴링: {pollingMs}ms",             // 폴링 소요시간
        $"���래스: {className}");            // hwndFocus 클래스명
    // 인디케이터 옆 또는 아래에 작은 폰트로 표시
}
```

---

### Part D: 빌드 + 테스트

#### NativeAOT 빌드

필수 .csproj 설정 (Phase 01에서 정의):
```xml
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
```

빌드 명령:
```bash
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```

배포물:
```
KoEnVue/
├── KoEnVue.exe      # ~3MB 단일 exe. .NET 런타임 불필요.
└── config.json      # 선택적 (포터블 모���)
```

#### 호환성 매트릭스 테스트 (10종)

| 앱 유형 | 대표 앱 | IME 감지 | 캐럿 추적 | 비고 |
|---------|--------|----------|----------|------|
| Win32 (GDI) | 메모장, mstsc | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 가장 안정적 |
| Win32 (리치에딧) | WordPad | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 정상 |
| WPF | Visual Studio | ImmGetDefaultIMEWnd | UI Automation | rcCaret (0,0,0,0) 가능 |
| WinForms | .NET 앱 | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 정상 |
| Electron | VS Code, Slack | ImmGetDefaultIMEWnd | GTTI or UIA | Chromium IMM32 호환 |
| 브라우저 | Chrome, Edge, Firefox | ImmGetDefaultIMEWnd | UI Automation | 탭 내 캐럿 UIA 필요 |
| UWP | 설정, 계산기 | ImmGetDefaultIMEWnd fallback | UI Automation | TSF 기반 |
| Win Terminal | Terminal | ImmGetDefaultIMEWnd | UI Automation | ConPTY 기반 |
| Java (Swing/FX) | IntelliJ, Eclipse | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 자체 IME 주의 |
| Qt 앱 | Qt Creator | ImmGetDefaultIMEWnd | GetGUIThreadInfo | Qt IME 모듈 경유 |

#### 비기능 요구사항 검증 (NF-01~NF-25)

| ID | 항목 | 목표 | 검증 방법 |
|----|------|------|-----------|
| NF-01 | CPU | idle 0.5% 이하 | 작업 관리자 확인 |
| NF-02 | 메모리 | 15MB 이하 | 작업 관리자 확인 |
| NF-03 | 응답 지연 | 100ms 이내 | 한/영 전환 후 체감 확인 |
| NF-04 | UI 렌더링 | 프레임 드랍 없음 | 페이드 애니메��션 시각 확인 |
| NF-10 | OS | Windows 10 21H2+ / Windows 11 | 대상 OS에��� 실행 |
| NF-11~14 | 앱 호환 | Win32/Electron/UWP/브라우저 | 호환성 매트릭스 |
| NF-15 | 전체화면 | 자동 숨김 | 전체화면 앱에서 확인 |
| NF-16 | 멀티 모니터 | 음수좌표, 혼합 DPI 정상 | 멀티 모니터 환경 테스트 |
| NF-17 | DPI | 100~200% 정확 | 다양한 DPI 설정 테스트 |
| NF-20 | 예외 처리 | 크래시 없이 fallback | API 실패 시나리오 |
| NF-21 | 장시간 실행 | 메모리 누수 없음 | 24시간 실행 후 확인 |
| NF-22 | 프로세스 격리 | 대상 앱 영향 없음 | 읽기 전용 확인 |
| NF-23 | 전원 관리 | 슬립 복귀 재감지 | 슬립→복귀 후 확인 |
| NF-24 | 고대비 | 사용자 색상 유지 | 고대비 모드 전환 |
| NF-25 | 설정 저장 | 실패 시 유지 | 읽기 전용 파일 테스트 |

#### 성공 지표

| 지표 | 목표 |
|------|------|
| 한/영 전환 감지 정확도 | Win32/Electron 앱에서 99% 이상 |
| 캐럿 추적 성공률 | 주요 앱 10종에서 80% 이상 |
| 전환 후 표시 지연 | 100ms 이내 |
| CPU 사용률 | idle 0.5% 이하 |
| 사용자 체감 | 잘못된 모드 타이핑 빈도 50% 이상 감소 |

#### 리스크 확인 (24건)

| 리스크 | 영향 | 확률 | 완화 방안 |
|--------|------|------|-----------|
| 캐럿 위치 미획득 | 중 | 높음 | 4-tier fallback |
| label 전환 시 흔들림 | 중 | 높음 | F-S01~S06 |
| 전체화면 성능 저하 | 중 | 중 | IsFullscreenExclusive |
| 서드파티 IME 비호환 | 중 | 중 | 3-tier + 앱 프로필 |
| 원격 데스크톱 이중 IME | 중 | 중 | mstsc 블랙리스트 |
| 백신 오탐 | 중 | 중 | 상태 조회만, 코드 서명 |
| Win32 오버레이 복잡도 | 중 | 중 | 최소 기능 구현 |
| Shell_NotifyIconW 복잡도 | 중 | 중 | 최소 기능 + workaround |
| UIA 지연 200ms+ | 중 | 중 | 전용 스레드 + 타임아웃 |
| GDI 리소스 누수 | 중 | 중 | SafeHandle + using |
| DPI 혼합 폰트 불일치 | 중 | 중 | HFONT 재생성 |
| Windows API 변경 | 중 | 낮음 | 하위호환 강한 API |
| NativeAOT+COM 제약 | 중 | 낮음 | [GeneratedComInterface] |
| 모니터 핫플러그 | 중 | 낮음 | WM_DISPLAYCHANGE |
| 한/영 연타 누락 | 낮 | 중 | 훅+폴링 하이브리드 |
| TOPMOST 경쟁 | 낮 | 중 | 주기적 재호출 |
| 가상 데스크톱 오표시 | 낮 | 중 | IVirtualDesktopManager |
| 드래그 충돌 | 낮 | 중 | VK_LBUTTON 체크 |
| config 범위 초과 | 낮 | 중 | 검증 클램핑 |
| 설정 레이스 컨디션 | 낮 | 중 | volatile 교체 |
| 트레이 찌꺼기 | 낮 | 중 | 시작 시 NIM_DELETE |
| UAC 프롬프트 | 낮 | 낮 | Task Scheduler 경유 |
| config 인코딩 깨짐 | 낮 | 낮 | UTF-8 BOM 제거 |
| PostMessage 오버플로우 | 낮 | 낮 | 초당 37개, 한계 10K |

---

### 최종 구현 체크리���트

```
[ ] [LibraryImport] source generator ([DllImport] 금지)
[ ] [GeneratedComInterface] source generator (COM)
[ ] [JsonSerializable] source generator (System.Text.Json)
[ ] SendMessageTimeoutW (SendMessage 금지)
[ ] ClientToScreen에 hwndCaret 전달 (hwndFocus 아님)
[ ] DPI에 Math.Round (절삭 금지)
[ ] rcWork 기준 클램핑 (가상 데스크톱 전체 금지)
[ ] SafeHandle GDI 핸들 관리
[ ] volatile ImeState 스레드 동기화
[ ] UIA 전용 STA 스레드 1개 (new Thread 금지)
[ ] 매직 넘버 → const/config (UIA_TextPattern2Id, CLSCTX_INPROC_SERVER 등)
[ ] config.json UTF-8만 (InvariantGlobalization)
[ ] .csproj: AllowUnsafeBlocks, IlcOptimizationPreference, JsonSerializerIsReflectionEnabledByDefault
[ ] DrawTextW → user32.dll, MulDiv → kernel32.dll (gdi32.dll 아님!)
[ ] ICONINFO.fIcon에 [MarshalAs(UnmanagedType.Bool)] 적용
[ ] JsonSourceGenerationOptions(PropertyNamingPolicy = SnakeCaseLower) 설정
[ ] IsBackground = true (메인 종료 시 자동)
[ ] Console.CancelKeyPress 미사용
[ ] 고정 GUID 트레이 찌��기 정리
[ ] Mutex 다중 인스턴스 방지
[ ] UpdateLayeredWindow + 32-bit PARGB DIB
[ ] SetLayeredWindowAttributes 사용 금지
[ ] 페이드는 SourceConstantAlpha만 (픽셀 순회 금지)
[ ] 강조 확대 중심점 기준 (좌상단 금지)
[ ] F-S01~S06 위치 안정성
[ ] 한/영 전환 시 색상만 즉시 변경
[ ] HFONT 1회 생성, DPI 변경 시에만 재생성
[ ] DIB 크기 변경 시에만 재생성
[ ] Shell_NotifyIconW P/Invoke (WinForms 금지)
[ ] SetForegroundWindow + WM_NULL 트레이 메뉴
[ ] 좌표 정수 Math.Round (서브픽셀 금지)
[ ] HFONT -MulDiv(pt, dpiY, 72)
[ ] WM_POWERBROADCAST 처리
[ ] WM_DISPLAYCHANGE + WM_SETTINGCHANGE 처리
[ ] 드래그 감지 VK_LBUTTON
[ ] RegisterHotKey + WM_HOTKEY
[ ] config 저장 실패 시 인메모리 유지 + 재시도
[ ] 앱 프로필 매칭 + 캐시
[ ] %APPDATA% ���선 설정 탐색 순서
```

---

## 검증 기준

- [ ] UiaClient가 전용 STA 스레드에서 COM 호출
- [ ] CaretTracker tier 2가 UiaClient.GetCaretBounds 호출
- [ ] Startup.cs가 schtasks.exe로 등록/해제
- [ ] WM_POWERBROADCAST 복귀 시 IME/DPI 재감지
- [ ] WM_DISPLAYCHANGE 시 모니터/rcWork 재조회
- [ ] 6개 테마 프리셋 적용
- [ ] 디버그 오버레이 정보 표시
- [ ] NativeAOT 빌드 성공 (~3MB exe)
- [ ] 호환성 매트릭스 10종 중 5종 이상 테스트 통과
- [ ] NF-01~NF-04 성능 목표 충족
- [ ] 최종 체크리��트 전체 항목 통과

## 산출물
```
Detector/UiaClient.cs      # UI Automation 전용 스레드
Utils/Startup.cs           # Task Scheduler 등록/해제
KoEnVue.exe                # NativeAOT 단일 exe (~3MB)
```
