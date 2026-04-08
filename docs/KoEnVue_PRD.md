# KoEnVue — PRD

## 1. 개요

### 1.1 제품명
**KoEnVue** — Windows 11 IME 한/영 상태 인디케이터

### 1.2 문제 정의
Windows 11에서 텍스트를 입력할 때, 현재 한글/영문 모드를 직관적으로 알기 어렵다. 기존 작업표시줄의 IME 표시기는 화면 우측 하단에 위치하여 타이핑 중 시선 이동이 크고, 특히 대형 모니터나 멀티 모니터 환경에서는 사실상 확인이 불가능하다. 이로 인해 잘못된 입력 모드로 타이핑한 뒤 지우고 다시 입력하는 반복 작업이 빈번하게 발생한다.

### 1.3 솔루션
텍스트 캐럿(입력 커서) 부근에 한/영 상태를 표시하는 경량 오버레이 유틸리티를 개발한다. 상시 표시가 아닌, **포커스 진입 시점**과 **한/영 전환 시점**에만 인디케이터를 1~2초간 표시하여, 시야 방해 없이 입력 모드를 즉시 확인할 수 있게 한다.

### 1.4 대상 사용자
- Windows 11 한국어 사용자
- 개발자, 문서 작업자 등 텍스트 입력이 많은 사용자
- 멀티 모니터/대형 모니터 환경 사용자

### 1.5 핵심 설계 원칙 — 외부 패키지 의존성 제로

> **본 프로젝트는 NuGet 외부 패키지를 일절 사용하지 않는다.**
> .NET 10 기본 라이브러리(System.Text.Json, System.Threading 등)와
> Windows 내장 API(User32, Imm32, Shell32, GDI32, Kernel32, Ole32, UIAutomationCore)만으로 구현한다.
> P/Invoke로 Win32 API를 직접 호출한다.

이 원칙의 이유:
- **배포 단순화**: NativeAOT 단일 exe, .NET 런타임 설치 불필요
- **빌드 경량화**: 단일 exe ~3MB (NativeAOT + 트리밍)
- **장기 유지보수**: 외부 패키지 deprecated/breaking change 리스크 제거
- **보안**: 서드파티 코드 공급망 공격(supply chain attack) 표면 제거
- **시작 속도**: NativeAOT 네이티브 컴파일로 즉시 시작 (JIT 없음)

### 1.5.1 핵심 설계 원칙 — 한글 표시 우선

> **KoEnVue의 모든 UI 텍스트는 한글을 기본 표시 언어로 사용한다.**

KoEnVue는 한국어 IME 사용자를 위한 유틸리티이므로, 앱 내부의 모든 사용자 대면 텍스트를 한글 우선으로 제공한다.

**적용 범위:**

| 영역 | 한글 우선 표시 | 비고 |
|------|---------------|------|
| 인디케이터 라벨 | `한` / `En` (기본값) | `EN`은 비한국어 IME 상태에만 사용 |
| 트레이 메뉴 | `인디케이터 스타일`, `표시 모드`, `투명도` 등 | 모든 메뉴 항목 한글 |
| 트레이 툴팁 | `한글 모드` / `영문 모드` | |
| 설정 GUI (향후) | 모든 라벨·설명·툴팁 한글 | 현재는 트레이 간이 설정으로 대체 |
| 로그 메시지 | 영문 (개발자 대상) | 예외: 로그만 영문 유지 |
| config.json 키 | 영문 (기계 파싱 대상) | JSON 키는 영문이 표준 |

**설정으로 영문 전환 가능:**
`"language": "ko"` (기본값)로 한글 표시, `"language": "en"`으로 영문 전환 지원.
`"language": "auto"`는 Windows 시스템 언어를 따르되, 한국어가 아닌 경우에만 영문으로 표시.

### 1.5.2 핵심 설계 원칙 — 하드코딩 금지

> **모든 매직 넘버, 문자열 리터럴은 상수(const/enum) 또는 설정(config.json)으로 정의한다.**

- **픽셀 오프셋, 타임아웃, 크기** → config.json 설정 키 또는 `DefaultConfig.cs` 상수
- **스타일 이름, IME 상태, 배치 방향** → C# enum (문자열 비교 금지)
- **Win32 상수, 커스텀 메시지 ID** → `Win32Types.cs` 또는 `AppMessages.cs` 상수
- **색상 코드** → config.json에서 로드, `ColorHelper.cs`로 변환
- **DPI 기준값(96)** → `DpiHelper.BASE_DPI` 상수

이 원칙의 이유:
- **오타 방지**: enum은 컴파일 타임에 오류 감지, 문자열은 런타임에만 발견
- **변경 용이**: 값 변경 시 한 곳만 수정
- **가독성**: `config.LabelGap` vs `2` — 의미가 명확

### 1.5.3 핵심 설계 원칙 — 공통모듈 사용 강제

> **동일 로직은 반드시 공통 모듈에 한 번만 구현하고, 모든 호출부에서 공유한다.**

- **DPI 스케일링** → `DpiHelper.cs` 1곳에서만 구현. Overlay, TrayIcon, Animation 모두 DpiHelper 호출
- **색상 변환** → `ColorHelper.cs` 1곳에서만 구현. HEX↔COLORREF↔RGB 양방향 변환을 Overlay, TrayIcon, ThemePresets가 공유
- **GDI 핸들 관리** → `SafeGdiHandles.cs`의 SafeHandle 래퍼를 모든 GDI 코드에서 사용
- **설정 접근** → `AppConfig` record를 volatile 참조로 공유. 각 모듈이 직접 JSON을 읽지 않음
- **P/Invoke 선언** → `Native/` 폴더에 DLL별 1파일로 집중. 다른 모듈에서 P/Invoke 직접 선언 금지
- **Win32 구조체/상수** → `Win32Types.cs`에 한 번 정의. 각 모듈에서 중복 정의 금지

이 원칙의 이유:
- **버그 방지**: 같은 로직을 두 곳에서 구현하면 한 곳만 수정되는 불일치 발생
- **유지보수**: 변경 시 한 곳만 수정
- **코드량 감소**: 바이브 코딩 시 AI가 중복 코드를 생성하는 것을 방지

### 1.5.4 핵심 설계 원칙 — 관리자 권한 실행

> **KoEnVue는 관리자 권한(elevated)으로 실행한다.**

관리자 권한이 필요한 이유:
- **UIPI 우회**: 관리자 권한 앱(작업 관리자, 레지스트리 편집기, 일부 개발 도구)에 `SendMessageTimeout(WM_IME_CONTROL)`를 보내려면 동일 이상의 권한이 필요
- **모든 앱에서 일관된 IME 감지**: 권한 부족으로 특정 앱에서만 감지 실패하는 상황 방지

구현: app.manifest 파일에 UAC `requireAdministrator`를 지정한다. **모든 실행을 Task Scheduler 경유**로 통일하여 UAC 프롬프트 없이 관리자 권한으로 실행한다. Task Scheduler 등록/해제는 `schtasks.exe` CLI를 `Process.Start`로 호출하는 방식을 사용한다 (COM Task Scheduler API 대비 구현 단순, 에러 시 exit code + stderr로 처리).

**실행 방식:**

| 실행 방식 | 구현 | UAC |
|-----------|------|:---:|
| 수동 실행 (바로가기) | `schtasks /run /tn "KoEnVue"` 호출 | 없음 |
| Windows 시작 시 자동 실행 | Task Scheduler "로그온할 때" 트리거 | 없음 |

> **설계 의도**: exe를 직접 더블클릭하면 매니페스트에 의해 UAC 프롬프트가 뜬다.
> 이를 회피하기 위해 바탕화면 바로가기가 exe를 직접 실행하지 않고,
> Task Scheduler에 등록된 작업을 실행(`schtasks /run`)하도록 한다.
> Task Scheduler 작업은 "가장 높은 수준의 권한으로 실행" 옵션이 설정되어 있으므로
> UAC 없이 관리자 권한으로 앱을 시작한다.
> 최초 Task Scheduler 등록 시에만 관리자 권한(UAC)이 1회 필요하다.

### 1.6 기술 스택

| 항목 | 선택 | 이유 |
|------|------|------|
| 언어 | C# 14 / .NET 10 | P/Invoke로 Win32 API 깔끔하게 호출, 강력한 타입 시스템 |
| 컴파일 | NativeAOT (PublishAot) | .NET 런타임 불필요, 단일 네이티브 exe (~3MB), 즉시 시작 |
| 오버레이 윈도우 | P/Invoke → Win32 API (CreateWindowExW, GDI) | WPF/WinForms 없이 직접 구현 → 번들 크기 최소화 |
| 메인 이벤트 루프 | Win32 메시지 루프 (GetMessage/DispatchMessage) | 네이티브 Win32 윈도우 구동 |
| Win32 API 호출 | P/Invoke ([LibraryImport], source generator) | IMM32, User32, Shell32, GDI32, UIAutomation 직접 접근. NativeAOT 완벽 호환 |
| 시스템 트레이 | P/Invoke → Shell_NotifyIconW | Win32 Shell API로 직접 구현 |
| UI Automation | [GeneratedComInterface] source generator (.NET 8+) | NativeAOT 호환 COM Interop 자동 생성. ComWrappers 수동 래핑 불필요 |
| 트레이 아이콘 생성 | P/Invoke → GDI (CreateCompatibleBitmap, CreateIconIndirect) | 외부 이미지 라이브러리 없이 GDI로 동적 생성 |
| 설정 저장 | System.Text.Json + JsonSerializerContext source generator | NuGet 없이 JSON 직렬화. NativeAOT에서 리플렉션 불가하므로 source generator 필수 |
| 로깅 | System.Diagnostics.Trace + 비동기 파일 로깅 (ConcurrentQueue + drain 스레드) | 외부 로깅 프레임워크 불필요 |

> **WPF/WinForms를 사용하지 않는 이유**: KoEnVue의 오버레이는 작은 도형/텍스트 하나를 표시하는 단순한 윈도우다.
> WPF를 사용하면 NativeAOT 호환성 문제가 발생하고, WinForms도 번들 크기가 증가한다.
> Win32 API(CreateWindowExW + WS_EX_LAYERED + GDI)로 직접 구현하면 NativeAOT와 완벽 호환되며 ~3MB 단일 exe를 달성한다.

> **참고**: 최종 사용자 환경에는 .NET 런타임이 필요하지 않다. NativeAOT가 네이티브 코드로 컴파일하여 단일 exe로 배포한다.

---

## 2. 핵심 동작 흐름

```
┌──────────────────────────────────────────────────────────────────┐
│                       메인 폴링 루프 (80ms)                        │
│                                                                  │
│  ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌─────────────┐  │
│  │1.포그라운드│──▶│2.시스템   │──▶│3.IME상태 │──▶│4.이벤트     │  │
│  │  윈도우    │   │  요소필터 │   │  감지     │   │  트리거판정  │  │
│  └──────────┘   └──────────┘   └──────────┘   └──────┬──────┘  │
│                   │ 숨김 대상                          │         │
│                   │ → 즉시 hide                  ┌─────▼─────┐  │
│                   ▼                              │5.캐럿위치  │  │
│                  SKIP                            │  획득      │  │
│                                                  └─────┬─────┘  │
│                                                  ┌─────▼─────┐  │
│                                                  │6.오버레이  │  │
│                                                  │  표시/갱신  │  │
│                                                  └───────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

### 2.1 단계 1 — 포그라운드 윈도우 감지

```
GetForegroundWindow() → HWND
GetWindowThreadProcessId(HWND) → Thread ID
GetGUIThreadInfo(Thread ID) → hwndFocus
```
- 포그라운드 윈도우가 이전 폴링과 동일하면 단계 2(시스템 필터)를 스킵하고 단계 3부터 실행 (동일 윈도우에서도 한/영 전환은 발생하므로 IME 감지는 매번 수행)
- 자기 자신의 윈도우(인디케이터)는 무시

### 2.2 단계 2 — Windows 시스템 요소 필터링

텍스트 입력이 불가능한 Windows 시스템 요소에서는 인디케이터를 표시하지 않는다.
판정 기준은 **포그라운드 윈도우의 클래스명**과 **hwndFocus 존재 여부**이다.

**숨김 대상 (인디케이터 표시 안 함)**

| 대상 | 윈도우 클래스명 | 판정 근거 |
|------|----------------|-----------|
| 바탕화면 | `Progman`, `WorkerW` | 텍스트 입력 컨텍스트 없음 |
| 작업표시줄 | `Shell_TrayWnd`, `Shell_SecondaryTrayWnd` | 텍스트 입력 컨텍스트 없음 |
| 잠금 화면 | — | `hwndFocus == NULL` |
| 보안 데스크톱 (Ctrl+Alt+Del) | — | `GetForegroundWindow() == NULL` |
| 전체화면 게임/영상 | — | 모니터 전체를 덮는 윈도우 + WS_CAPTION 없음 (최대화 윈도우 오판 방지) |
| hwndFocus가 없는 상태 | — | 키보드 포커스를 가진 컨트롤이 없음 = 텍스트 입력 불가 |

**정상 표시 대상 (인디케이터 표시)**

| 대상 | 상세 |
|------|------|
| 설정 앱 (SystemSettings.exe) | 검색창, 텍스트 필드에 포커스 진입 시 표시 |
| 파일 탐색기 | 주소창, 검색창, 파일명 변경 시 표시 |
| 실행 대화상자 (Win+R) | 텍스트 필드 포커스 보유 → 표시 |
| 시작 메뉴 검색 | 검색 입력창이 별도 윈도우로 포커스 → 표시 |

**감지 로직 의사코드**

```csharp
// system_hide_classes는 config.json에서 로드 (하드코딩 금지)
// config.SystemHideClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"]
// + config.SystemHideClassesUser (사용자 추가 목록) 병합

bool ShouldShowIndicator(IntPtr hwnd, IntPtr hwndFocus, RECT monitorRect, AppConfig config)
{
    if (hwnd == IntPtr.Zero) return false;
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
    if (!IsOnCurrentVirtualDesktop(hwnd)) return false;
    
    // 설정에서 로드한 숨김 클래스 목록으로 필터링
    string className = GetWindowClass(hwnd);
    if (config.SystemHideClasses.Contains(className)) return false;
    
    if (hwndFocus == IntPtr.Zero) return false;
    if (IsFullscreenExclusive(hwnd, monitorRect)) return false;
    if (!PassesAppFilter(hwnd, config)) return false;
    
    return true;
}
```

> **설계 의도**: `hwndFocus == NULL` 체크가 핵심이다. 이 한 가지 조건만으로도
> 대부분의 "텍스트 입력이 불가능한 상황"을 자동으로 걸러낸다.
> 클래스명 블랙리스트는 바탕화면/작업표시줄처럼 hwndFocus가 존재하지만
> 실질적으로 텍스트 입력 대상이 아닌 예외 케이스만 처리한다.

**전체화면 독점 감지 (`IsFullscreenExclusive`):**

```csharp
bool IsFullscreenExclusive(IntPtr hwnd, RECT monitorRect)
{
    if (!GetWindowRect(hwnd, out RECT rect)) return false;

    // 모니터 전체를 덮는지 확인
    if (rect.Left > monitorRect.Left || rect.Top > monitorRect.Top ||
        rect.Right < monitorRect.Right || rect.Bottom < monitorRect.Bottom)
        return false;

    // 최대화 윈도우 오판 방지: WS_CAPTION이 있으면 일반 최대화 윈도우
    int style = GetWindowLongW(hwnd, GWL_STYLE);
    return (style & WS_CAPTION) != WS_CAPTION;
}
```

> **주의**: 단순히 "모니터를 덮는가"만 체크하면 최대화 윈도우(타이틀바 있음)가
> 전체화면으로 오판되어 인디케이터가 숨겨진다. `WS_CAPTION` 스타일 체크로 구분한다.
>
> **브라우저 전체화면(F11)**: Chrome/Edge의 F11 전체화면은 `WS_CAPTION`을 제거하므로
> 위 로직에 의해 전체화면 독점으로 판정되어 인디케이터가 숨겨진다.
> 이는 의도된 동작이다 — 브라우저 전체화면은 영상/프레젠테이션 용도가 대부분이며,
> 주소창에 입력할 때는 F11로 전체화면을 해제하는 것이 일반적이다.

### 2.3 단계 3 — IME 한/영 상태 감지

```
Primary:   ImmGetDefaultIMEWnd(HWND) → SendMessageTimeout(WM_IME_CONTROL, SMTO_ABORTIFHUNG, 100ms)
Fallback1: GetGUIThreadInfo() → ImmGetContext(hwndFocus) → ImmGetConversionStatus()
Fallback2: GetKeyboardLayout(ThreadID) → 언어 코드만 확인
```
- Primary 방식을 기본으로 사용 (Win32 + Electron 앱 모두 안정적)
- **SendMessage가 아닌 SendMessageTimeout 사용** — 대상 앱이 응답 없음(hang) 상태일 때 감지 스레드 무한 블로킹 방지. `SMTO_ABORTIFHUNG` 플래그 + 100ms 타임아웃
- 관리자 권한으로 실행하므로 UIPI 제약 없이 모든 앱에 메시지 전송 가능
- 결과를 3가지 상태로 분류:
  - `HANGUL` — 한국어 IME 활성 + 한글 모드
  - `ENGLISH` — 한국어 IME 활성 + 영문 모드
  - `NON_KOREAN` — 한국어 IME 비활성 (영문 키보드 등)

**보조 이벤트 훅 (폴링 보완)**

폴링(80ms) 사이에 한/영 키 빠른 연타로 짝수 번 전환이 발생하면 변화를 놓칠 수 있다. 이를 보완하기 위해 `SetWinEventHook(EVENT_OBJECT_IME_CHANGE)`를 보조 채널로 등록한다.

```
메인 스레드: SetWinEventHook(EVENT_OBJECT_IME_CHANGE, callback)
  └── 콜백 발화 시 → 즉시 IME 상태 재조회 → 변경 감지 시 인디케이터 표시

감지 스레드: 80ms 폴링 (기존 유지)
  └── 훅이 발화하지 않는 앱에 대한 안전망
```

> **설계 의도**: 훅과 폴링의 하이브리드. 훅이 먼저 감지하면 즉시 처리하고,
> 훅이 발화하지 않는 앱에서는 폴링이 백업으로 동작한다.
> 두 채널 모두 동일한 상태 비교 로직을 거치므로 중복 표시는 발생하지 않는다.
>
> **동기화**: 훅 콜백(메인 스레드)과 감지 스레드가 동시에 IME 상태를 조회할 수 있다.
> 상태 비교는 `volatile ImeState _lastState` 필드로 수행한다.
> 훅 콜백은 `PostMessage(WM_IME_STATE_CHANGED)`만 발행하고,
> 실제 인디케이터 업데이트는 메인 스레드의 메시지 핸들러에서 직렬화된다.
> 감지 스레드도 동일하게 `PostMessage`로 위임하므로, 두 채널의 업데이트는
> 메인 스레드의 메시지 큐에서 자연스럽게 직렬화된다.

### 2.4 단계 4 — 이벤트 트리거 판정 (표시 타이밍)

인디케이터는 **상시 표시하지 않는다.** 다음 두 가지 이벤트가 발생한 순간에만 표시하고, 일정 시간 후 자동으로 사라진다.

**트리거 1 — 포커스 변경**
```
조건: 이전 폴링의 hwndFocus ≠ 현재 hwndFocus
의미: 사용자가 다른 입력 필드/앱으로 이동 → "지금 뭐지?" 확인 필요
동작: 인디케이터 표시 (페이드인 → 유지 → 페이드아웃)
```

**트리거 2 — IME 상태 변경**
```
조건: 이전 폴링의 conversion_mode ≠ 현재 conversion_mode
      또는 이전 keyboard_layout ≠ 현재 keyboard_layout
의미: 한/영 키 입력 또는 키보드 레이아웃 변경 → "바뀌었네" 확인 필요
동작: 인디케이터 표시 + 확대 강조 효과 (전환을 더 분명히 인지)
```

**이벤트 없음 — 인디케이터 숨김 유지**
```
타이핑이 진행 중인 동안에는 이미 입력되는 글자를 보면서 한/영 상태를
알 수 있으므로, 인디케이터가 필요 없다.
```

**표시 타이밍 시퀀스**
```
이벤트 발생
  → 페이드인 (150ms)
  → 유지 (1.5초)
  → 페이드아웃 (400ms)
  → 숨김

IME 상태 변경 시 추가:
  → 인디케이터 1.3배 확대 (즉시)
  → 원래 크기로 복귀 (300ms)
  (페이드인과 동시에 진행)

유지 중 새 이벤트 발생 시 (IME 전환/포커스 변경):
  → 유지 타이머 리셋 (처음부터 1.5초 다시 카운트)
  → 이미 표시 중이면 페이드인 생략
```

> **핵심 원칙: 인디케이터는 캐럿을 따라다니지 않는다.**
> 이벤트 발생 시점의 캐럿 위치에 표시되고, 그 위치에 머문다.
> 1.5초 후 자연 소멸하며, 타이핑이 시작되어도 위치가 고정이라 시야를 방해하지 않는다.

### 2.5 단계 5 — 캐럿 위치 획득 및 인디케이터 배치

이벤트 트리거가 발동한 경우에만 캐럿 위치를 획득한다 (불필요한 API 호출 절약).

**캐럿 위치 획득 (우선순위 체인)**
```
1순위: GetGUIThreadInfo() → rcCaret → ClientToScreen(hwndCaret, ...)
       - 유효 조건: rcCaret이 (0,0,0,0)이 아닐 것
       - 주의: ClientToScreen에 hwndFocus가 아닌 hwndCaret을 전달해야 함
         (rcCaret은 hwndCaret의 클라이언트 좌표)

2순위: UI Automation — IUIAutomationTextPattern.GetCaretRange()
       → GetBoundingRectangles()
       - UWP, WinUI, 일부 Electron 앱에서 유효
       - 전용 UIA 스레드에서 실행, 타임아웃 config.Advanced.UiaTimeoutMs (기본 200ms)
       - 매 호출마다 new Thread 생성 금지 → 전용 스레드 1개를 앱 시작 시 생성, 큐 기반 요청

3순위: 포커스 윈도우 영역 기반 fallback
       - GetWindowRect(hwndFocus) → 윈도우 좌측 하단에 배치
       - 캐럿 좌표는 못 얻지만 입력 필드 근처에 표시 가능

4순위 (최종 fallback): 마우스 커서 위치
       - GetCursorPos()
       - 위 방법 모두 실패 시 최종 대안
```
- 사용된 방식을 캐싱하여, 동일 앱에서는 성공한 방식을 우선 시도

**인디케이터 스타일 (5종)**

인디케이터는 **라벨 스타일**(캐럿 옆에 텍스트 표시)과 **캐럿 박스 스타일**(캐럿 위치 자체에 색상 도형 표시)로 나뉜다. 캐럿 박스 스타일은 입력 지점에 직접 표시하므로 시선 이동이 제로이며, 앱 폰트와 무관한 **고정 크기**를 사용한다.

```
┌─────────────────────────────────────────────────────────────┐
│  라벨 스타일 (캐럿 옆)                                        │
│                                                             │
│  label          ┌──┐                                        │
│                 │한│▌동해물과_        캐럿 왼쪽에 텍스트 라벨    │
│                 └──┘                                        │
├─────────────────────────────────────────────────────────────┤
│  캐럿 박스 스타일 (캐럿 위치에 직접 표시, 고정 크기)              │
│                                                             │
│  caret_dot      동해물과●_            소형 원형 (8px)          │
│                                      가장 미니멀, 시야 방해 최소│
│                                                             │
│  caret_square   동해물과■_            소형 사각 (8px)          │
│                                      원형과 유사, 약간 더 선명  │
│                                                             │
│  caret_underline 동해물과▌_           얇은 밑줄 바 (24×3px)    │
│                  ▁▁▁▁                캐럿 바로 아래, 텍스트와   │
│                                      겹치지 않음              │
│                                                             │
│  caret_vbar     동해물과▎_            얇은 세로 바 (3×16px)    │
│                                      캐럿 자체를 색상 바로      │
│                                      대체하는 느낌             │
└─────────────────────────────────────────────────────────────┘
```

| 스타일 | 기본 크기 | 배치 | 특징 |
|--------|----------|------|------|
| `label` | 28×24px | 캐럿 왼쪽 (left→above→below 자동 전환) | "한"/"En" 텍스트로 명확, 접근성 우수 |
| `caret_dot` | 8×8px | 캐럿 오른쪽 상단 | 가장 미니멀, 시선 이동 제로 |
| `caret_square` | 8×8px | 캐럿 오른쪽 상단 | 원형보다 시인성 약간 높음 |
| `caret_underline` | 24×3px | 캐럿 바로 아래 | 텍스트와 겹치지 않으면서 자연스러움 |
| `caret_vbar` | 3×16px | 캐럿 위치에 겹침 | 캐럿과 일체감, 가장 자연스러운 통합 |

- 기본값: `caret_dot` (미니멀하고 어떤 앱에서든 일관적)
- 모든 캐럿 박스 스타일은 **고정 크기**로 앱 폰트에 의존하지 않음
- rcCaret을 못 얻는 앱에서 캐럿 박스 스타일 사용 시 → 마우스 fallback 위치에 표시

**라벨 스타일 배치 위치 (우선순위)**

`label` 스타일은 텍스트를 포함하므로 캐럿과 분리 배치한다:

```
1순위: 캐럿 바로 왼쪽 (캐럿과 수직 정렬)
       ┌──┐
       │한│▌  ← 캐럿
       └──┘
       - IME 조합창/자동완성 드롭다운과 겹치지 않음
       - 유효 조건: 캐럿 왼쪽에 인디케이터 너비만큼 공간이 있을 것

2순위: 캐럿 바로 위 (공간 부족 시)
       ┌──┐
       │한│
       └──┘
         ▌  ← 캐럿

3순위: 캐럿 바로 아래

fallback: 마우스 커서 부근
```

**캐럿 박스 스타일 배치 위치**

캐럿 박스 스타일은 캐럿 위치에 직접 겹치거나 인접 배치한다:

```
caret_dot / caret_square:
  위치: (caret_x + CaretBoxGapX, caret_y - dot_size + CaretBoxGapY)  // 캐럿 오른쪽 상단

caret_underline:
  위치: (caret_x - bar_w/2, caret_y + caret_h + UnderlineGap)  // 캐럿 바로 아래 중앙

caret_vbar:
  위치: (caret_x - VbarOffsetX, caret_y)  // 캐럿 위치에 겹침

// 상수: DefaultConfig.CaretBoxGapX=2, CaretBoxGapY=2, UnderlineGap=1, VbarOffsetX=1
```

### 2.6 단계 5.5 — 멀티 모니터 & 화면 경계 처리

인디케이터 좌표가 결정된 후, **캐럿이 속한 모니터의 작업 영역(rcWork)** 기준으로 경계 보정을 수행한다.

**모니터 특정 로직**

```csharp
// 캐럿 좌표가 속한 모니터를 특정
IntPtr hMonitor = MonitorFromPoint(
    new POINT(caretX, caretY),
    MONITOR_DEFAULTTONEAREST   // 가상 데스크톱 밖이면 가장 가까운 모니터
);

// 해당 모니터의 작업 영역 획득 (작업표시줄 제외)
var monitorInfo = new MONITORINFOEXW();
monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
GetMonitorInfoW(hMonitor, ref monitorInfo);

RECT workArea = monitorInfo.rcWork;   // 실제 사용 가능 영역
RECT fullArea = monitorInfo.rcMonitor; // 모니터 전체 영역
```

> **핵심**: `screen_rect`로 가상 데스크톱 전체를 쓰면 안 된다.
> 반드시 `MonitorFromPoint` → `GetMonitorInfo`의 **rcWork**를 기준으로 한다.
> 이래야 보조 모니터, 비표준 배치(수직 쌓기, 좌측 배치), 음수 좌표를 모두 정확히 처리한다.

**모니터/작업표시줄 변경 감지**

rcWork는 캐시하지 않고 매 폴링마다 `GetMonitorInfo`를 호출한다 (경량 API, 성능 부담 없음). 추가로 다음 시스템 메시지를 메인 스레드에서 처리한다:
- `WM_DISPLAYCHANGE` — 모니터 연결/분리, 해상도 변경 시
- `WM_SETTINGCHANGE` — 작업표시줄 위치/크기 변경 시

**비표준 모니터 배치 대응**

```
예시: 보조 모니터가 주 모니터 왼쪽에 위치한 경우

  보조 모니터              주 모니터
  (-1920,0)─────(0,0)───────(1920,0)
  │             ││              │
  │  rcWork:    ││  rcWork:     │
  │  (-1920,0)  ││  (0,40)      │ ← 작업표시줄 40px
  │  ~(-1,1040) ││  ~(1919,1080)│
  │             ││              │
  (-1920,1080)──(0,1080)──(1920,1080)

캐럿이 (-500, 300)에 있으면:
  → MonitorFromPoint → 보조 모니터
  → rcWork = (-1920, 0, -1, 1040)
  → 이 영역 내에서 경계 보정
```

**Per-Monitor DPI 스케일링**

모니터별 DPI가 다를 수 있으므로, 인디케이터 크기와 오프셋을 해당 모니터의 DPI에 맞춰 스케일링한다.

```csharp
// Per-Monitor DPI v2 (Windows 10 1607+)
Shcore.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
double scale = dpiX / (double)DpiHelper.BASE_DPI;    // BASE_DPI = 96

// 인디케이터 크기/오프셋 스케일링
// F-S05: 정수 반올림 (절삭하면 계통적 과소 스케일링 발생)
int scaledSize = (int)Math.Round(baseSize * scale);
int scaledOffsetX = (int)Math.Round(baseOffsetX * scale);
int scaledOffsetY = (int)Math.Round(baseOffsetY * scale);
int scaledFontSize = (int)Math.Round(baseFontSize * scale);
```

| 모니터 DPI | scale | 8px dot → | 28×24 label → |
|-----------|-------|-----------|---------------|
| 96 (100%) | 1.0 | 8px | 28×24px |
| 120 (125%) | 1.25 | 10px | 35×30px |
| 144 (150%) | 1.5 | 12px | 42×36px |
| 192 (200%) | 2.0 | 16px | 56×48px |

**DPI Awareness 선언**

Per-Monitor DPI Awareness v2를 선언해야 OS가 좌표를 정확하게 전달한다. 선언하지 않으면 OS가 가상화된 좌표를 주므로 보조 모니터에서 위치가 틀어진다.

**app.manifest에 선언** (런타임 API 호출 대신 매니페스트 사용):
- 어떤 윈도우 생성/API 호출보다 먼저 적용됨 → 타이밍 문제 없음
- try/catch 불필요, 런타임 에러 가능성 제거
- app.manifest가 이미 UAC용으로 존재 → 추가 파일 불필요

```xml
<!-- app.manifest에 추가 (trustInfo와 동일 레벨) -->
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  </windowsSettings>
</application>
```

> `<dpiAwareness>PerMonitorV2</dpiAwareness>`는 Windows 10 1703+에서 적용된다.
> `<dpiAware>true/pm</dpiAware>`는 이전 Windows용 fallback이다.
> 런타임에 `SetProcessDpiAwarenessContext()`를 호출할 필요 없음.

**통합 배치 로직 (최종)**

```csharp
(int x, int y, Placement placement, double dpiScale) CalculateIndicatorPosition(
    int caretX, int caretY, int caretH,
    int indW, int indH, IndicatorStyle style, AppConfig config)
{
    // 1. 캐럿이 속한 모니터의 작업 영역 획득
    IntPtr hMonitor = MonitorFromPoint(new POINT(caretX, caretY), MONITOR_DEFAULTTONEAREST);
    RECT workArea = DpiHelper.GetWorkArea(hMonitor);
    
    // 2. 해당 모니터의 DPI 스케일링 적용
    double dpiScale = DpiHelper.GetScale(hMonitor);
    int margin = DpiHelper.Scale(config.ScreenEdgeMargin, dpiScale);
    int sIndW = DpiHelper.Scale(indW, dpiScale);
    int sIndH = DpiHelper.Scale(indH, dpiScale);
    
    int x, y;
    Placement placement;
    
    // 3. 스타일별 기본 위치 계산 (오프셋은 config/상수 참조, 매직 넘버 없음)
    switch (style)
    {
        case IndicatorStyle.Label:
            (x, y, placement) = CalcLabelPosition(caretX, caretY, caretH, sIndW, sIndH, workArea, margin, config);
            break;
        case IndicatorStyle.CaretDot:
        case IndicatorStyle.CaretSquare:
            x = caretX + DpiHelper.Scale(DefaultConfig.CaretBoxGapX, dpiScale);
            y = caretY - sIndH + DpiHelper.Scale(DefaultConfig.CaretBoxGapY, dpiScale);
            placement = Placement.CaretTopRight;
            break;
        case IndicatorStyle.CaretUnderline:
            x = caretX - sIndW / 2;
            y = caretY + caretH + DpiHelper.Scale(DefaultConfig.UnderlineGap, dpiScale);
            placement = Placement.CaretBelow;
            break;
        case IndicatorStyle.CaretVbar:
            x = caretX - DpiHelper.Scale(DefaultConfig.VbarOffsetX, dpiScale);
            y = caretY;
            placement = Placement.CaretOverlap;
            break;
        default:
            (x, y, placement) = (caretX, caretY, Placement.Left);
            break;
    }
    
    // 4. 모니터 작업 영역 내로 클램핑
    x = Math.Max(workArea.Left + margin, Math.Min(x, workArea.Right - sIndW - margin));
    y = Math.Max(workArea.Top + margin, Math.Min(y, workArea.Bottom - sIndH - margin));
    
    return (x, y, placement, dpiScale);
}

(int x, int y, Placement placement) CalcLabelPosition(
    int caretX, int caretY, int caretH,
    int indW, int indH, RECT workArea, int margin, AppConfig config)
{
    int gap = config.LabelGap;  // 캐럿-라벨 간격 (기본값: DefaultConfig.LabelGap = 2)
    
    // 1순위: 캐럿 왼쪽
    int x = caretX - indW - gap;
    int y = caretY + (caretH - indH) / 2;
    if (x >= workArea.Left + margin) return (x, y, Placement.Left);
    
    // 2순위: 캐럿 위
    x = caretX;
    y = caretY - indH - gap;
    if (y >= workArea.Top + margin) return (x, y, Placement.Above);
    
    // 3순위: 캐럿 아래
    x = caretX;
    y = caretY + caretH + gap;
    return (x, y, Placement.Below);
}
```

> **상수 정의 위치** (`DefaultConfig.cs`):
> ```csharp
> static class DefaultConfig
> {
>     public const int LabelGap = 2;        // 캐럿-라벨 간격 (px)
>     public const int CaretBoxGapX = 2;    // 캐럿 박스 X 오프셋 (px)
>     public const int CaretBoxGapY = 2;    // 캐럿 박스 Y 오프셋 (px)
>     public const int UnderlineGap = 1;    // underline 캐럿 아래 간격 (px)
>     public const int VbarOffsetX = 1;     // vbar X 오프셋 (px)
>     public const uint AnimationFrameMs = 16;  // 애니메이션 프레임 간격 (~60fps)
>     public const double DimOpacityFactor = 0.5; // Dim 모드 투명도 감소 계수
>     // ... 기타 기본값
> }
> ```
>
> **DpiHelper.Scale 구현**: `(int)Math.Round(value * scale)` 사용 (F-S05 정수 반올림 원칙).
> `(int)` 절삭은 계통적 과소 스케일링을 유발하므로 금지.

### 2.7 단계 6 — 오버레이 업데이트

- 이벤트 트리거 미발동 시: 아무것도 하지 않음 (인디케이터 숨김 유지)
- 이벤트 트리거 발동 시: **이벤트 발생 시점의 캐럿 위치**에 인디케이터 표시. 이후 캐럿이 이동해도 인디케이터는 **해당 위치에 고정**, 1.5초 후 자연 소멸
- 표시 중 IME 전환/포커스 변경 시: 새 캐럿 위치로 재계산 + 유지 타이머 리셋

### 2.8 앱 라이프사이클

**실행 → 초기화 → 상주 → 종료** 전체 흐름을 정의한다.

```
┌──────────┐   ┌──────────────┐   ┌──────────────┐   ┌────────┐
│  실행     │──▶│  초기화       │──▶│  상주 (메인   │──▶│  종료   │
│          │   │              │   │  폴링 루프)   │   │        │
└──────────┘   └──────────────┘   └──────────────┘   └────────┘
```

**2.8.1 실행**

```
Task Scheduler 경유 실행 (수동: schtasks /run, 자동: 로그온 트리거)
  │
  ├── 관리자 권한으로 실행됨 (UAC 프롬프트 없음)
  │
  ├── 이전 비정상 종료 정리 (고정 GUID로 Shell_NotifyIconW NIM_DELETE 호출)
  │
  ├── 다중 인스턴스 체크 (Mutex: "KoEnVue_{고정GUID}")
  │     └── 이미 실행 중이면 → 기존 인스턴스에 포커스 → 현재 프로세스 종료
  │
  ├── DPI Awareness: app.manifest에서 PerMonitorV2 선언됨 (런타임 API 호출 불필요)
  │
  └── 초기화 단계로 진행
```

**2.8.2 초기화**

```
1. 설정 로드
   ├── %APPDATA%\KoEnVue\config.json 확인
   ├── 없으면 → exe 디렉토리 config.json 확인 (포터블 모드)
   ├── 둘 다 없으면 → 코드 내 DEFAULT_CONFIG 사용 (파일 자동 생성 안 함)
   │                   최초 트레이 메뉴 설정 변경 시에만 config.json 생성
   └── 있으면 → JSON 파싱, config_version 마이그레이션, 알 수 없는 키 무시

2. 한글/영문 UI 텍스트 로드 (I18n.cs, language 설정에 따라)

3. 감지 엔진 초기화
   ├── P/Invoke 함수 선언은 [LibraryImport] source generator가 컴파일 타임에 생성 (DLL 자체는 OS가 런타임에 로드)
   ├── IME 감지 모듈 준비 (ImeStatus.cs)
   └── 캐럿 추적 모듈 준비 (CaretTracker.cs)

4. UI 초기화
   ├── Win32 윈도우 클래스 등록 (RegisterClassExW)
   ├── 오버레이 윈도우 생성 (CreateWindowExW + WS_EX_LAYERED + WS_EX_TRANSPARENT + WS_EX_TOPMOST)
   └── 시스템 트레이 아이콘 등록 (Shell_NotifyIconW)

5. 감지 스레드 시작 (Background Thread)

6. Win32 메인 메시지 루프 진입 (GetMessage/DispatchMessage)
```

**2.8.3 상주 (메인 폴링 루프)**

섹션 2.1~2.7의 폴링 루프가 감지 스레드에서 80ms 간격으로 반복 실행된다.

**설정 변경 반영:**

| 변경 경로 | 반영 방식 |
|-----------|----------|
| 트레이 간이 설정 메뉴 | 즉시 반영 + config.json 자동 저장 |
| config.json 외부 편집 | 파일 변경 감지 (mtime 체크, 5초 간격) → 자동 리로드. 파싱 실패 시 이전 설정 유지 + 로그 경고 |

> **config.json 외부 편집 감지**: 별도 파일 워쳐 스레드를 두지 않고,
> 감지 스레드의 폴링 루프 내에서 5초(약 62폴링)마다 `File.GetLastWriteTimeUtc()`을 체크한다.
> 변경 감지 시 JSON 리로드를 시도하고, 파싱 실패 시 이전 설정을 유지한다.

**2.8.4 종료**

```
트레이 메뉴 "종료" 클릭
  │
  ├── 1. 감지 스레드 정지 플래그 설정 (running = false)
  │     └── Background 스레드이므로 메인 스레드 종료 시 자동 종료
  │
  ├── 2. 오버레이 윈도우 숨김 + 파괴 (DestroyWindow)
  │
  ├── 3. 시스템 트레이 아이콘 제거
  │     └── Shell_NotifyIconW(NIM_DELETE, ...)
  │     └── GDI 리소스 해제 (DestroyIcon, DeleteObject)
  │
  ├── 4. Win32 메시지 루프 종료 (PostQuitMessage)
  │
  └── 5. 프로세스 종료
```

**크래시 복구:**

비정상 종료 시 트레이 아이콘이 찌꺼기로 남을 수 있다. 다음 방어 조치를 적용한다:

- **앱 시작 시 정리**: 고정 GUID로 `Shell_NotifyIconW(NIM_DELETE)` 호출하여 이전 찌꺼기 제거 (없으면 무시)
- **Mutex 이름**: `"KoEnVue_{고정GUID}"` 사용하여 다른 앱과 충돌 방지
- `AppDomain.CurrentDomain.ProcessExit`로 정상 종료 시 정리 함수 등록
- `TerminateProcess`, 전원 차단 시에는 핸들러 미호출 → 다음 시작 시 정리로 대응
- **참고**: `Console.CancelKeyPress`는 `OutputType: WinExe`에서 콘솔이 없으므로 발화하지 않음 → 사용하지 않음

---

## 3. 기능 요구사항

### 3.1 오버레이 표시

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-01 | 인디케이터 스타일 5종 | `label` (텍스트 라벨), `caret_dot` (소형 원형), `caret_square` (소형 사각), `caret_underline` (밑줄 바), `caret_vbar` (세로 바) |
| F-02 | 색상 구분 | 한글: 초록(#16A34A), 영문: 앰버(#D97706), 비한국어 IME: 회색(#6B7280) (사용자 변경 가능). 캐럿 박스 스타일은 색상만으로, 라벨 스타일은 색상+텍스트로 구분 |
| F-03 | 고정 크기 | 캐럿 박스 스타일은 앱 폰트에 의존하지 않는 고정 크기 사용 (DPI 스케일링만 적용) |
| F-04 | 라벨 스타일 자동 배치 | `label` 스타일은 캐럿 왼쪽 → 위 → 아래 순 자동 전환 |
| F-05 | 마우스 fallback | 캐럿 위치 획득 실패 시 마우스 커서 부근에 표시 |
| F-06 | 클릭 투과 | 인디케이터가 마우스 클릭을 가로채지 않음 (WS_EX_TRANSPARENT) |
| F-07 | 항상 위 표시 | 모든 창 위에 표시 (TOPMOST) |
| F-08 | 시스템 요소 자동 숨김 | 바탕화면, 작업표시줄, 잠금화면, 전체화면 게임 등에서 자동 숨김 |
| F-09 | hwndFocus 기반 판정 | 포커스 윈도우가 없으면 텍스트 입력 불가 상태로 판단하여 숨김 |

### 3.1.1 인디케이터 위치 안정성

> **핵심 원칙: 한/영 전환 시 인디케이터가 움직여 보이지 않아야 한다.**
> 색상만 바뀌고 위치·크기는 그대로 유지되어야 전환을 자연스럽게 인지할 수 있다.
> 인디케이터가 전환마다 흔들리거나 점프하면 시각적 노이즈가 되어 오히려 방해가 된다.

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-S01 | label 고정 너비 | `label` 스타일은 텍스트 내용("한"/"En"/"EN")에 관계없이 **고정 너비**를 사용. 텍스트는 라벨 내 중앙 정렬. 글리프 폭 차이로 인한 너비 변동을 원천 차단 |
| F-S02 | 배치 방향 고정 | 한/영 전환 시 배치 방향을 변경하지 않음. 이벤트 발생 시점에 결정된 배치 방향(left/above/below)을 해당 표시 주기 동안 유지. 페이드아웃 후 다음 이벤트에서만 재계산 |
| F-S03 | 캐럿 박스 고정 크기 | 캐럿 박스 스타일(dot/square/underline/vbar)은 한/영 상태와 무관하게 동일한 크기 유지. 색상만 변경 |
| F-S04 | 강조 효과 중심 고정 | 전환 시 확대 강조(1.3배)는 **중심점 기준 확대**로 구현. 좌상단 기준 확대하면 오른쪽+아래로 밀려 보임 |
| F-S05 | 서브픽셀 정렬 방지 | 인디케이터 좌표를 항상 정수(px)로 반올림. 소수점 좌표는 렌더링 시 흔들림(jitter) 유발 |
| F-S06 | 색상 전환 즉시 반영 | 한/영 전환 시 색상 변경은 페이드 없이 즉시 반영 (위치 고정 + 색상 즉시 변경 = "깜빡" 느낌 최소화). 페이드인/아웃은 표시/숨김에만 사용 |

**label 고정 너비 구현:**

```csharp
// 앱 시작 시 라벨 최대 너비를 사전 계산
int CalculateFixedLabelWidth(string fontFamily, int fontSize, bool bold)
{
    string[] candidates = { "한", "En", "EN" };  // 표시 가능한 모든 텍스트
    int maxWidth = 0;
    foreach (var text in candidates)
    {
        int w = MeasureTextWidth(text, fontFamily, fontSize, bold);
        maxWidth = Math.Max(maxWidth, w);
    }
    return maxWidth + LABEL_PADDING_X * 2;
}
// 이후 모든 라벨은 이 고정 너비를 사용
// 텍스트는 고정 너비 라벨 내 중앙 정렬
```

**배치 방향 고정 구현:**

```csharp
class OverlayState
{
    public Placement? CurrentPlacement { get; private set; }
    public bool IsVisible { get; private set; }
    
    public (int x, int y) OnEvent(int caretX, int caretY, int caretH,
                                   int indW, int indH, RECT workArea)
    {
        if (!IsVisible)
        {
            CurrentPlacement = CalculatePlacement(caretX, caretY, caretH, indW, indH, workArea);
            IsVisible = true;
        }
        
        return ApplyPlacement(CurrentPlacement!.Value, caretX, caretY, caretH, indW, indH);
    }
    
    public void OnFadeOutComplete()
    {
        CurrentPlacement = null;
        IsVisible = false;
    }
}
```

**강조 효과 중심 고정 구현:**

```csharp
(int x, int y, int w, int h) ApplyHighlightScale(int x, int y, int w, int h, double scale)
{
    // 중심점 기준 확대 — 인디케이터가 제자리에서 커지고 작아짐 (F-S05 반올림)
    int newW = (int)Math.Round(w * scale);
    int newH = (int)Math.Round(h * scale);
    int newX = x - (newW - w) / 2;   // 중심 X 유지
    int newY = y - (newH - h) / 2;   // 중심 Y 유지
    return (newX, newY, newW, newH);
}
```

### 3.1.2 멀티 모니터 & 화면 경계

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-M01 | 모니터 특정 | `MonitorFromPoint()`로 캐럿이 속한 모니터를 특정, 해당 모니터의 rcWork 기준으로 경계 보정 |
| F-M02 | 작업 영역 기준 | 가상 데스크톱 전체가 아닌 **모니터별 rcWork** (작업표시줄 제외 영역) 내로 인디케이터 클램핑 |
| F-M03 | 비표준 배치 대응 | 모니터가 수직 쌓기, 좌측 배치 등 비표준 배열일 때 음수 좌표 정상 처리 |
| F-M04 | 모니터 간 이동 금지 | 인디케이터가 캐럿이 속한 모니터 밖으로 넘어가지 않도록 클램핑 |
| F-M05 | Per-Monitor DPI | 모니터별 DPI에 맞춰 인디케이터 크기·오프셋·폰트 자동 스케일링 |
| F-M06 | DPI Awareness 선언 | app.manifest에 `PerMonitorV2` 선언 (런타임 API 호출 불필요) |
| F-M07 | 모니터 전환 감지 | 캐럿이 다른 모니터로 이동하면 새 모니터의 DPI/rcWork로 즉시 재계산 |

### 3.2 이벤트 기반 표시

> **핵심 원칙: 인디케이터는 상시 표시하지 않는다.**
> 사용자가 한/영 상태를 확인해야 하는 두 가지 순간에만 짧게 표시한다.

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-10 | 포커스 변경 시 표시 | hwndFocus가 변경되면 인디케이터 표시 (앱 전환, 탭 전환, 텍스트 필드 클릭 등) |
| F-11 | IME 전환 시 표시 | 한/영 전환 또는 키보드 레이아웃 변경 시 인디케이터 표시 |
| F-12 | 전환 시 강조 효과 | IME 상태 변경 시 인디케이터 확대(1.3배) → 원래 크기 복귀(300ms)로 전환을 강조 |
| F-13 | 자동 페이드아웃 | 표시 후 1.5초 유지 → 페이드아웃(400ms) → 숨김. 위치 고정이라 타이핑 중에도 시야 방해 없음 |
| F-14 | 페이드인 애니메이션 | 표시 시 부드러운 페이드인 (150ms) |
| F-15 | 타이머 리셋 | 유지 중 새 이벤트(IME 전환/포커스 변경) 발생 시 유지 타이머를 처음부터 다시 카운트 |
| F-16 | always 모드 (선택) | 설정으로 상시 표시 모드도 지원. **유휴→활성 전환 트리거**: 포커스 변경 또는 IME 상태 변경 시 활성 투명도 적용, 이벤트 없이 3초 경과 시 유휴 투명도로 복귀. 유휴 시 `idle_opacity`, 활성 시 `active_opacity` 사용 |
| F-17 | 위치 고정 | 인디케이터는 이벤트 발생 시점의 캐럿 위치에 고정. 캐럿 이동을 따라가지 않음. 새 이벤트 시에만 위치 재계산 |

### 3.3 시스템 트레이

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-20 | 트레이 아이콘 | P/Invoke Shell_NotifyIconW + GDI로 직접 구현. **캐럿+점(A안)** 디자인, 한/영 상태를 **배경색으로만** 표시. 배경색은 설정의 `hangul_bg` / `english_bg` / `non_korean_bg` 값을 인디케이터와 공유. 텍스트("한"/"En") 표시 안 함 — IME 트레이 아이콘과 표현 방식 차별화 |
| F-21 | 트레이 메뉴 | 우클릭 시 컨텍스트 메뉴: 간이 설정 서브메뉴 + 설정 파일 열기 + 시작 프로그램 등록 + 종료 |
| F-22 | 시작 프로그램 등록 | 트레이 메뉴에서 토글. Task Scheduler 작업 등록: "가장 높은 수준의 권한으로 실행" + "로그온할 때 실행" 트리거. 수동 실행용 바로가기도 `schtasks /run /tn "KoEnVue"` 경유. 최초 등록 시에만 UAC 1회 필요 |
| F-23 | 트레이 좌클릭 | 인디케이터 표시/숨기기 토글 또는 설정 창 열기 (설정으로 선택) |
| F-24 | 트레이 툴팁 | 마우스 오버 시 "한글 모드" / "영문 모드" 등 현재 상태 텍스트 표시 (한글) |
| F-25 | 간이 설정 메뉴 | 트레이 메뉴 내 서브메뉴로 핵심 3가지 설정을 GUI 없이 즉시 변경 가능 (아래 구조 참조) |

**트레이 메뉴 구조 (한글 표시)**

```
트레이 아이콘 우클릭
│
├── 인디케이터 스타일 ▶  ● 점 (caret_dot)         ← 기본값
│                        ○ 사각 (caret_square)
│                        ○ 밑줄 (caret_underline)
│                        ○ 세로바 (caret_vbar)
│                        ○ 텍스트 (label)
│
├── 표시 모드 ▶          ● 이벤트 시만 (on_event)  ← 기본값
│                        ○ 항상 표시 (always)
│
├── 투명도 ▶             ○ 진하게 (0.95)
│                        ● 보통 (0.85)             ← 기본값
│                        ○ 연하게 (0.6)
│
├── ─────────────────
├── ☐ 시작 프로그램 등록
├── 설정 파일 열기...
├── ─────────────────
└── 종료
```

> **설계 의도**: JSON 편집 없이도 가장 자주 변경하는 3가지(스타일, 표시 모드, 투명도)를
> 트레이 메뉴에서 바로 조절할 수 있다. 선택 변경 시 config.json에 자동 저장되고,
> 인디케이터에 즉시 반영된다. 세부 설정이 필요한 사용자는 "설정 파일 열기"로 JSON을 편집한다.

### 3.4 설정

> **설정은 2단계로 제공한다:**
> - **간이 설정**: 트레이 메뉴 서브메뉴에서 핵심 3가지(스타일, 표시 모드, 투명도)를 즉시 변경
> - **상세 설정**: config.json 파일을 텍스트 에디터로 직접 편집 (트레이 메뉴 "설정 파일 열기"로 접근)
>
> 별도 설정 GUI는 향후 WinUI 3 또는 Win32 다이얼로그로 검토 가능하나, 현재 범위에서는 트레이 간이 설정 + config.json 편집으로 충분하다.

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-30 | 설정 파일 | `config.json`으로 저장/로드, 없으면 기본값 사용. `%APPDATA%\KoEnVue\` 또는 exe 디렉토리(포터블 모드) |
| F-31 | 표시 모드 | `on_event` (이벤트 시만 표시, **기본값**) / `always` (항상 표시, 유휴 시 반투명) |
| F-32 | 위치 모드 | `caret` (캐럿 추적) / `mouse` (마우스 추적) / `fixed` (화면 고정, 앵커 지원) |
| F-33 | 색상 커스터마이징 | 한글/영문/비한국어 각각의 배경색·글자색 |
| F-34 | 투명도 (스타일별) | label 스타일: 기본/유휴/활성 3단계. 캐럿 박스 스타일: 별도 기본/유휴/활성 + 최소 하한 |
| F-35 | 오프셋 | 캐럿/마우스 대비 인디케이터 표시 오프셋 (x, y), 배치 방향(상/하/좌/우) |
| F-36 | 폴링 간격 | IME 상태 확인 주기(poll_interval_ms), 캐럿 위치 획득 실패 시 재시도 간격(caret_poll_interval_ms) 별도 조절 가능 |
| F-37 | NON_KOREAN 처리 | 비한국어 IME: 숨기기 / 텍스트 표시 / 반투명 표시 |
| F-38 | 앱 필터 | 블랙리스트/화이트리스트로 특정 앱에서만 활성/비활성 |
| F-39 | 설정 마이그레이션 | config_version 기반 자동 마이그레이션, 알 수 없는 키 무시, 누락 키 기본값 채움 |
| F-3A | 트레이 간이 설정 연동 | 트레이 메뉴에서 변경한 설정이 config.json에 자동 저장되고 즉시 반영 |
| F-3B | 한글 UI | 트레이 메뉴, 툴팁, 설정 GUI의 모든 텍스트를 한글로 표시 (language 설정으로 영문 전환 가능) |

### 3.5 추가 기능

| ID | 요구사항 | 상세 |
|----|----------|------|
| F-40 | 설정 GUI | Win32 다이얼로그 또는 WinUI 3 기반 설정 창 (향후 검토). 현재는 트레이 간이 설정 + config.json 편집으로 대체 |
| F-41 | 앱별 프로필 | 프로세스명/창 제목/윈도우 클래스별 설정 오버라이드 (인디케이터 스타일 포함) |
| F-42 | 글로벌 핫키 | 표시 토글, 스타일 순환, 위치 모드 순환, 표시 모드 순환, 설정 창 열기 |
| ~~F-43~~ | ~~효과음~~ | **미지원**. 현재 범위에서는 시각 피드백(색상·애니메이션)에 집중. 효과음은 향후 검토 가능 |
| F-44 | 테마 프리셋 | minimal / vivid / pastel / dark / system(Windows 강조색 연동) |
| F-45 | 라벨 스타일 텍스트 | `label` 스타일에서 텍스트(한/영) / 색상 점 / ㄱ/A 아이콘 중 선택 (국기 이모지는 GDI 제약으로 미지원) |
| F-46 | 포터블 모드 | exe 디렉토리에 config.json 존재 시 AppData 대신 해당 파일 사용 |
| F-47 | 디버그 오버레이 | 감지 방식, 캐럿 좌표, 모니터 DPI, 폴링 소요시간 등 표시 (개발용) |

---

## 4. 비기능 요구사항

### 4.1 성능

| ID | 요구사항 | 목표값 |
|----|----------|--------|
| NF-01 | CPU 사용률 | 평균 0.5% 이하 (idle 상태 기준) |
| NF-02 | 메모리 사용량 | 15MB 이하 (실행 시 RSS 기준, NativeAOT 경량) |
| NF-03 | 응답 지연 | 한/영 전환 후 100ms 이내에 인디케이터 업데이트 |
| NF-04 | UI 렌더링 | 인디케이터 위치 이동 시 끊김 없을 것 (프레임 드랍 없음) |

### 4.2 호환성

| ID | 요구사항 | 상세 |
|----|----------|------|
| NF-10 | OS | Windows 10 21H2 이상, Windows 11 전 버전 |
| NF-11 | Win32 앱 | 메모장, Office, Visual Studio 등 — ImmGetDefaultIMEWnd 방식으로 지원 |
| NF-12 | Electron 앱 | VS Code, Slack, Discord 등 — ImmGetDefaultIMEWnd 방식으로 지원 |
| NF-13 | UWP/WinUI 앱 | 설정, Windows Terminal 등 — UI Automation fallback으로 지원 |
| NF-14 | 브라우저 | Chrome, Edge, Firefox — Electron과 동일 방식 |
| NF-15 | 전체화면 앱 | 전체화면 게임/영상에서는 인디케이터 자동 숨김 |
| NF-16 | 멀티 모니터 | 비표준 배치(수직, 좌측, 음수좌표), Per-Monitor DPI 혼합 환경 정상 동작 |
| NF-17 | DPI 스케일링 | 100%~200% DPI 범위에서 인디케이터 크기/위치 정확 |

### 4.3 안정성

| ID | 요구사항 | 상세 |
|----|----------|------|
| NF-20 | 예외 처리 | Win32 API 호출 실패 시 크래시 없이 fallback 동작 |
| NF-21 | 장시간 실행 | 24시간 이상 연속 실행 시 메모리 누수 없음 |
| NF-22 | 프로세스 격리 | 대상 앱의 동작에 영향을 주지 않음 (읽기 전용 모니터링) |
| NF-23 | 전원 관리 복귀 | 슬립/하이버네이트 복귀 시 폴링 루프가 자동 재개되며 IME 상태를 즉시 재감지. 모니터 DPI/rcWork도 재조회 (WM_POWERBROADCAST + PBT_APMRESUMESUSPEND 처리) |
| NF-24 | 고대비 모드 | Windows 고대비 모드 활성화 시 사용자 설정 색상을 그대로 사용 (시스템 색상으로 자동 전환하지 않음). `theme: "system"` 선택 시에만 Windows 강조색 연동 |
| NF-25 | 설정 저장 실패 | config.json 저장 실패 시 (읽기 전용, 디스크 풀, 권한 부족) 로그 경고 출력 + 인메모리 설정은 유지하여 앱 동작 지속. 다음 저장 시점에 재시도 |

---

## 5. 아키텍처

### 5.1 모듈 구조

```
KoEnVue/
├── KoEnVue.csproj               # .NET 10 프로젝트 파일 (NativeAOT 설정 포함)
├── app.manifest                 # UAC 관리자 권한 매니페스트 + PerMonitorV2 DPI
├── Program.cs                   # 엔트리포인트, Win32 메시지 루프, 3-스레드 관리, 이벤트 파이프라인
├── Models/
│   ├── AppConfig.cs             # sealed record AppConfig — 불변 설정 객체 (100+ 속성, volatile 참조 교체용)
│   ├── DebugInfo.cs             # record DebugInfo — 디버그 오버레이 표시 데이터
│   ├── ImeState.cs              # enum ImeState { Hangul, English, NonKorean }
│   ├── IndicatorStyle.cs        # enum IndicatorStyle { Label, CaretDot, CaretSquare, CaretUnderline, CaretVbar }
│   ├── Placement.cs             # enum Placement { Left, Above, Below, CaretTopRight, CaretBelow, CaretOverlap }
│   ├── CaretPlacement.cs        # enum CaretPlacement { Left, Right, Above, Below }
│   ├── DisplayMode.cs           # enum DisplayMode { OnEvent, Always }
│   ├── AppFilterMode.cs         # enum AppFilterMode { Blacklist, Whitelist }
│   ├── CaretMethod.cs           # enum CaretMethod { Auto, GuiThread, Uia, Mouse }
│   ├── DetectionMethod.cs       # enum DetectionMethod { Auto, ImeDefault, ImeContext, KeyboardLayout }
│   ├── FontWeight.cs            # enum FontWeight { Normal, Bold }
│   ├── LabelShape.cs            # enum LabelShape { RoundedRect, Circle, Pill }
│   ├── LabelStyle.cs            # enum LabelStyle { Text, Dot, Icon }
│   ├── LogLevel.cs              # enum LogLevel { Debug, Info, Warning, Error }
│   ├── NonKoreanImeMode.cs      # enum NonKoreanImeMode { Hide, Show, Dim }
│   ├── PositionMode.cs          # enum PositionMode { Caret, Mouse, Fixed }
│   ├── Theme.cs                 # enum Theme { Custom, Minimal, Vivid, Pastel, Dark, System }
│   ├── TrayClickAction.cs       # enum TrayClickAction { Toggle, Settings, None }
│   ├── AppProfileMatch.cs       # enum AppProfileMatch { Process, Class, Title }
│   ├── MultiMonitorMode.cs      # enum MultiMonitorMode { FollowCaret, FollowMouse, PrimaryOnly }
│   ├── TrayIconStyle.cs         # enum TrayIconStyle { CaretDot, Static }
│   ├── FixedAnchor.cs           # enum FixedAnchor { Absolute, TopLeft, TopRight, BottomLeft, BottomRight, Center }
│   └── FixedMonitor.cs          # enum FixedMonitor { Primary, Mouse, Active }
├── Detector/
│   ├── ImeStatus.cs             # IME 한/영 상태 감지 (3-tier fallback + SetWinEventHook 하이브리드)
│   ├── CaretTracker.cs          # 캐럿 위치 추적 (4-tier fallback + tier-1 재시도 + LRU 캐싱)
│   ├── SystemFilter.cs          # Windows 시스템 요소 필터링 (8-조건 단락 평가 + IVirtualDesktopManager COM)
│   └── UiaClient.cs             # UI Automation COM 직접 접근 (STA 스레드 전용)
├── UI/
│   ├── Overlay.cs               # 플로팅 오버레이 윈도우 (GDI 렌더링 + LabelStyle + 테두리 + Fixed앵커 + DebugOverlay)
│   ├── Animation.cs             # 페이드인/아웃/슬라이드 애니메이션 (WM_TIMER 상태 머신 + Dim 모드)
│   ├── Tray.cs                  # 시스템 트레이 (Shell_NotifyIconW + 팝업 메뉴 + schtasks 자동시작)
│   └── TrayIcon.cs              # 트레이 아이콘 GDI 생성 (캐럿+점 디자인, 상태별 배경색 변경)
├── Config/
│   ├── DefaultConfig.cs         # 기본 설정 상수 + 픽셀 오프셋 + 타이밍 상수
│   ├── Settings.cs              # 설정 로드/저장/검증/마이그레이션/핫리로드/앱프로필
│   └── ThemePresets.cs          # 6개 테마 프리셋 (custom, minimal, vivid, pastel, dark, system)
├── Utils/
│   ├── DpiHelper.cs             # DPI 조회/스케일 계산 공통 유틸 (MonitorFromPoint + GetDpiForMonitor)
│   ├── ColorHelper.cs           # 색상 변환 공통 유틸 (HEX↔COLORREF↔RGB 양방향)
│   ├── Logger.cs                # 로깅 (Trace + 비동기 파일 로깅: ConcurrentQueue + drain 스레드, 4-레벨)
│   └── I18n.cs                  # 한글/영문 UI 텍스트 관리 (한글 표시 우선, P2)
├── Native/
│   ├── User32.cs                # User32.dll [LibraryImport] 선언 (40+)
│   ├── Imm32.cs                 # Imm32.dll [LibraryImport] 선언
│   ├── Shell32.cs               # Shell32.dll [LibraryImport] 선언
│   ├── Gdi32.cs                 # Gdi32.dll [LibraryImport] 선언
│   ├── Kernel32.cs              # Kernel32.dll [LibraryImport] 선언
│   ├── Shcore.cs                # Shcore.dll [LibraryImport] 선언 (DPI)
│   ├── Ole32.cs                 # Ole32.dll [LibraryImport] 선언 (COM)
│   ├── OleAut32.cs              # OleAut32.dll [LibraryImport] 선언 (UIA)
│   ├── Win32Types.cs            # Win32 구조체/상수/열거형 정의
│   ├── AppMessages.cs           # 커스텀 윈도우 메시지 상수 (WM_APP+1~5, WM_USER+1, TIMER_ID 1~5)
│   ├── SafeGdiHandles.cs        # GDI SafeHandle 래퍼 (SafeFontHandle, SafeBitmapHandle, SafeIconHandle)
│   ├── VirtualDesktop.cs        # IVirtualDesktopManager COM 인터페이스 ([GeneratedComInterface])
│   └── UiaInterfaces.cs         # IUIAutomation COM 인터페이스 정의
```

### 5.2 스레드 모델

```
┌─────────────────────────────┐
│      메인 스레드 (UI)           │
│  - Win32 메시지 루프           │
│    (GetMessage/DispatchMessage) │
│  - 오버레이 렌더링              │
│    (UpdateLayeredWindow)        │
│  - 애니메이션 (WM_TIMER)       │
│  - 트레이 메뉴 처리            │
│  - SetWinEventHook 콜백 수신   │
├─────────────────────────────┤
│      감지 스레드 (Background)  │
│  - 80ms 폴링 루프            │
│  - IME 상태 감지             │
│  - 캐럿 위치 추적             │
│  - 상태 변경 시 메인 스레드에   │
│    PostMessage()로 커스텀     │
│    메시지 전송                │
├─────────────────────────────┤
│      UIA 스레드 (Background)   │
│  - 앱 시작 시 1개 생성         │
│  - UI Automation COM 호출 전용 │
│  - 큐 기반 요청/응답           │
│  - 타임아웃: uia_timeout_ms    │
└─────────────────────────────┘
```

- 감지 스레드에서 직접 윈도우를 조작하지 않음
- `PostMessage(WM_APP + N, ...)` 커스텀 메시지로 메인 스레드에 업데이트 위임
- 감지 스레드는 `IsBackground = true`로 설정하여 메인 스레드 종료 시 자동 종료

**커스텀 메시지 정의** (`Native/AppMessages.cs`):

| 상수명 | 값 | 용도 | wParam | lParam |
|--------|-----|------|--------|--------|
| `AppMessages.WM_IME_STATE_CHANGED` | WM_APP + 1 | IME 상태 변경 | ImeState enum 값 | 0 |
| `AppMessages.WM_FOCUS_CHANGED` | WM_APP + 2 | 포커스 변경 | 새 hwndFocus | 0 |
| `AppMessages.WM_CARET_UPDATED` | WM_APP + 3 | 캐럿 위치 갱신 | x 좌표 (IntPtr, 64-bit) | y 좌표 (IntPtr, 64-bit) |
| `AppMessages.WM_HIDE_INDICATOR` | WM_APP + 4 | 인디케이터 숨기기 | 0 | 0 |
| `AppMessages.WM_CONFIG_CHANGED` | WM_APP + 5 | 설정 변경 감지 | 0 | 0 |
| `AppMessages.WM_TRAY_CALLBACK` | WM_USER + 1 | 트레이 아이콘 콜백 | — | — |

### 5.3 캐럿 위치 추적 상세 로직

```csharp
class CaretTracker
{
    public (int x, int y, string method)? GetCaretPosition(IntPtr hwndCaret, IntPtr hwndFocus, uint threadId)
    {
        // 1순위: GetGUIThreadInfo — hwndCaret의 클라이언트 좌표 → 스크린 좌표
        var pos = TryGuiThreadInfo(threadId);  // ClientToScreen(hwndCaret, ...) 사용
        if (pos != null) return (pos.Value.x, pos.Value.y, "gui_thread_info");

        // 2순위: UI Automation — 전용 UIA 스레드에서 실행
        pos = _uiaThread.RequestWithTimeout(hwndFocus, _config.Advanced.UiaTimeoutMs);
        if (pos != null) return (pos.Value.x, pos.Value.y, "ui_automation");

        // 3순위: 포커스 윈도우 영역 기반 fallback
        pos = TryFocusWindowRect(hwndFocus);
        if (pos != null) return (pos.Value.x, pos.Value.y, "focus_window_rect");

        // 4순위 (최종 fallback): 마우스 커서
        var cursor = GetCursorPosition();
        return (cursor.x, cursor.y, "mouse_fallback");
    }
    
    (int x, int y)? TryFocusWindowRect(IntPtr hwndFocus)
    {
        // 포커스 윈도우의 좌측 하단에 인디케이터 배치 (캐럿 못 얻을 때)
        if (GetWindowRect(hwndFocus, out RECT rect))
            return (rect.Left, rect.Bottom + DefaultConfig.FocusWindowGap);
        return null;
    }
}
```

**앱별 방식 캐싱**: 특정 프로세스에서 성공한 방식을 기억하여 다음 폴링 시 해당 방식을 먼저 시도. Dictionary + LRU 로직으로 관리하며 최대 50개 프로세스까지 저장.

**캐럿 위치 고정**: 인디케이터는 이벤트 발생 시점의 캐럿 위치에 1회 배치되고, 이후 캐럿을 따라가지 않는다. 캐럿 빠른 이동에 의한 인디케이터 떨림 문제가 원천적으로 제거된다.

**드래그 감지**: 마우스 왼쪽 버튼이 눌려 있으면(`GetAsyncKeyState(VK_LBUTTON)`) 인디케이터를 즉시 숨겨 드래그 앤 드롭과의 충돌을 방지한다.

### 5.4 오버레이 위치 결정

인디케이터 배치는 섹션 2.5~2.6에 정의된 통합 로직을 따른다:

```
1. MonitorFromPoint(caret_xy) → 캐럿이 속한 모니터 특정
2. GetMonitorInfoW() → rcWork (작업표시줄 제외 작업 영역)
3. GetDpiForMonitor() → scale factor 계산
4. 인디케이터 크기/오프셋에 scale 적용
5. indicator_style에 따라 기본 좌표 계산:
   - label: left → above → below 자동 전환
   - caret_dot/square: 캐럿 오른쪽 상단
   - caret_underline: 캐럿 바로 아래 중앙
   - caret_vbar: 캐럿 위치에 겹침
6. rcWork 내로 클램핑 (모니터 경계 넘어가지 않도록)
```

### 5.5 트레이 메뉴 구현

Win32 `CreatePopupMenu` + `AppendMenuW`로 메뉴를 동적 생성하고, `TrackPopupMenu`로 표시한다. 서브메뉴(인디케이터 스타일, 표시 모드, 투명도)는 별도 `HMENU`로 생성 후 `MF_POPUP`으로 부모 메뉴에 삽입한다. 라디오 버튼 동작은 `CheckMenuRadioItem`으로 구현한다.

```
트레이 아이콘 콜백 (AppMessages.WM_TRAY_CALLBACK)
  ├── WM_RBUTTONUP → SetForegroundWindow(hwnd) → CreatePopupMenu → AppendMenuW (서브메뉴 포함) → TrackPopupMenu → PostMessage(WM_NULL)
  └── WM_LBUTTONUP → tray_click_action 설정에 따라 동작
       └── WM_COMMAND → 메뉴 항목 ID로 분기 → 설정 변경 + config.json 저장 + 인디케이터 즉시 반영
```

> **SetForegroundWindow + PostMessage(WM_NULL)**: 이 조합이 없으면 메뉴가 떴는데
> 다른 곳을 클릭해도 메뉴가 안 닫히는 Win32 알려진 문제가 발생한다. Microsoft 공식 workaround.

### 5.6 오버레이 렌더링 파이프라인

> **핵심: `UpdateLayeredWindow` + 32-bit PARGB DIB 방식을 사용한다.**
> `SetLayeredWindowAttributes`는 윈도우 전체 알파만 지원하여 둥근 모서리의 per-pixel 투명도 처리가 불가하다.
> `UpdateLayeredWindow`는 비트맵의 알파 채널을 직접 제어하므로, `label_shape`(rounded_rect/circle/pill)의
> 둥근 모서리와 페이드 애니메이션을 모두 단일 파이프라인으로 처리한다.

**렌더링 절차:**

```
1. CreateCompatibleDC(NULL) → 메모리 DC 생성
2. BITMAPINFOHEADER (32bpp, BI_RGB) → CreateDIBSection → HBITMAP + 픽셀 버퍼 포인터
3. SelectObject(memDC, hBitmap)
4. 픽셀 버퍼에 도형 렌더링 (배경색 + 알파):
   - label: RoundRect/Ellipse + DrawTextW (premultiplied alpha 적용)
   - caret_dot: 원형 FillEllipse
   - caret_square: 사각형 FillRect
   - caret_underline: 얇은 사각형
   - caret_vbar: 세로 사각형
5. UpdateLayeredWindow(hwnd, NULL, &pos, &size, memDC, &srcPoint, 0, &blendFunc, ULW_ALPHA)
   - BLENDFUNCTION: AlphaFormat = AC_SRC_ALPHA (per-pixel alpha)
6. 페이드 애니메이션: blendFunc.SourceConstantAlpha 값을 0~255로 조절 후 UpdateLayeredWindow 재호출
7. 강조 효과: size를 확대/축소하여 UpdateLayeredWindow 재호출 (중심점 기준)
```

> **Premultiplied Alpha**: UpdateLayeredWindow는 premultiplied alpha를 요구한다.
> 각 채널 값 = 원본 값 × (alpha / 255). GDI DrawTextW/FillRect 후
> DIB 픽셀 버퍼를 직접 순회하며 premultiply 처리한다.
>
> **페이드 최적화**: 비트맵은 premultiplied 상태로 유지하고 재생성하지 않는다.
> 페이드 애니메이션은 `BLENDFUNCTION.SourceConstantAlpha`(0~255)만 조절하여
> `UpdateLayeredWindow`를 재호출한다. 이 방식은 매 프레임 픽셀 순회 없이
> 윈도우 전체 투명도만 변경하므로 CPU 부담이 최소화된다.
> 색상 변경(한/영 전환) 시에만 비트맵 픽셀을 갱신하고 premultiply를 재적용한다.

**GDI 텍스트 렌더링 (label 스타일):**

```
1. CreateFontW(fontSize, fontFamily, fontWeight) → HFONT 생성
2. SelectObject(memDC, hFont) → 메모리 DC에 폰트 적용
3. SetBkMode(memDC, TRANSPARENT) → 배경 투명
4. SetTextColor(memDC, fgColor)
5. DrawTextW(memDC, text, rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE)
6. DIB 픽셀 버퍼에서 텍스트 영역 premultiplied alpha 보정
7. 사용 후 SelectObject로 이전 폰트 복원
```

- HFONT는 앱 시작 시 한 번 생성하고 재사용 (폴링마다 생성/삭제 금지). `SafeHandle` 또는 `IDisposable` 패턴으로 GDI 핸들 수명 관리, GC가 누수 최종 방어
- 폰트 설정 변경 시에만 HFONT 재생성
- **모니터 전환 시 DPI가 달라지면 HFONT 재생성** (현재 모니터 DPI를 캐시하고, 변경 감지 시 DeleteObject → CreateFontW)
- 트레이 아이콘 생성 시 `GetSystemMetrics(SM_CXSMICON/SM_CYSMICON)`으로 DPI에 맞는 아이콘 크기 조회
- **렌더링 버퍼 재사용**: 인디케이터 크기가 변하지 않으면 DIB 재생성 없이 픽셀만 갱신. DPI 변경/스타일 변경 시에만 DIB 재생성

---

## 6. 설정 스키마

### 6.1 전체 설정 구조

```jsonc
{
  "config_version": 1,                // 설정 스키마 버전. 향후 스키마 변경 시 자동 마이그레이션에 사용

  // ─────────────────────────────────────────────
  // [표시 모드]
  // ─────────────────────────────────────────────
  "display_mode": "on_event",         // "on_event"   — 이벤트 시에만 표시 (기본값, 권장)
                                      //                포커스 변경 + IME 상태 변경 시 표시 후 자동 숨김
                                      // "always"     — 항상 표시 (유휴 시 idle_opacity 적용)
  "event_display_duration_ms": 1500,  // on_event 모드: 이벤트 발생 후 표시 유지 시간 (ms)
  "always_idle_timeout_ms": 3000,    // always 모드: 이벤트 없이 이 시간 경과 시 유휴 투명도로 복귀 (ms)
  "event_triggers": {                 // on_event 모드: 표시 트리거 개별 on/off
    "on_focus_change": true,          //   포커스 변경 시 표시 (앱 전환, 텍스트 필드 클릭 등)
    "on_ime_change": true             //   한/영 전환 시 표시
  },

  // ─────────────────────────────────────────────
  // [위치]
  // ─────────────────────────────────────────────
  "position_mode": "caret",           // "caret"  — 텍스트 캐럿 추적 (권장)
                                      // "mouse"  — 마우스 커서 추적
                                      // "fixed"  — 화면 고정 위치
  "fixed_position": {                 // fixed 모드 전용
    "x": 100,
    "y": 100,
    "anchor": "top_right",            // "top_left" | "top_right" | "bottom_left"
                                      // | "bottom_right" | "center" | "absolute"
                                      // absolute 외에는 x,y가 해당 모서리로부터의 오프셋
    "monitor": "primary"              // "primary"  — 주 모니터 기준 (기본값)
                                      // "mouse"    — 마우스가 있는 모니터 기준
                                      // "active"   — 포그라운드 윈도우가 있는 모니터 기준
                                      // anchor="absolute"일 때는 가상 데스크톱 전체 좌표 사용 (모니터 무관)
  },
  "caret_offset": { "x": -2, "y": 0 },   // label 스타일 전용. 캐럿 대비 오프셋 (px). 캐럿 박스 스타일은 고정 오프셋 사용 (섹션 2.5)
  "mouse_offset": { "x": 20, "y": 25 },  // 마우스 대비 오프셋 (px), 모든 스타일 공통
  "caret_placement": "left",          // label 스타일 전용. 캐럿 박스 스타일은 스타일별 고정 위치 사용 (섹션 2.5)
                                      // "left"  — 캐럿 왼쪽 (기본값, 권장, IME 조합창과 충돌 방지)
                                      // "above" — 캐럿 위
                                      // "below" — 캐럿 아래
                                      // "right" — 캐럿 오른쪽
  "caret_placement_auto_flip": true,  // true면 공간 부족 시 자동 전환:
                                      //   "left"  → above → below
                                      //   "right" → above → below
                                      //   "above" → below → left
                                      //   "below" → above → left
  "screen_edge_margin": 8,           // 화면 가장자리 여백 (px), 인디케이터가 화면 밖으로 나가지 않도록

  // ─────────────────────────────────────────────
  // [외관 — 인디케이터 스타일]
  // ─────────────────────────────────────────────
  "indicator_style": "caret_dot",    // "label"           — 캐럿 옆 텍스트 라벨 ("한"/"En")
                                     // "caret_dot"       — 캐럿 위치 소형 원형 (기본값)
                                     // "caret_square"    — 캐럿 위치 소형 사각
                                     // "caret_underline" — 캐럿 아래 얇은 밑줄 바
                                     // "caret_vbar"      — 캐럿 위치 얇은 세로 바

  // [외관 — 캐럿 박스 스타일 공통 (caret_dot / caret_square / caret_underline / caret_vbar)]
  "caret_dot_size": 8,              // caret_dot 지름 (px, DPI 스케일링 전 기본값)
  "caret_square_size": 8,           // caret_square 한 변 크기 (px)
  "caret_underline_width": 24,      // caret_underline 너비 (px)
  "caret_underline_height": 3,      // caret_underline 두께 (px)
  "caret_vbar_width": 3,            // caret_vbar 너비 (px)
  "caret_vbar_height": 16,          // caret_vbar 높이 (px)

  // [외관 — 라벨 스타일 전용 (label)]
  "label_shape": "rounded_rect",    // "rounded_rect" — 둥근 사각형
                                    // "circle"       — 원형
                                    // "pill"         — 알약형 (좌우 완전 라운드)
  "label_width": 28,                // 라벨 최소 너비 (px). F-S01 자동 계산 결과가 이 값보다 크면 자동 계산값 사용
  "label_height": 24,               // 라벨 높이 (px)
  "label_border_radius": 6,         // rounded_rect 모서리 반경 (px)
  "border_width": 0,                // 테두리 두께 (0이면 테두리 없음)
  "border_color": "#000000",         // 테두리 색상 (RGB hex)
  "shadow_enabled": false,           // 그림자 효과 (UpdateLayeredWindow + PARGB DIB로 구현 가능하나, 렌더링 복잡도 대비 효용이 낮아 현재 미구현)

  // ─────────────────────────────────────────────
  // [외관 — 텍스트]
  // ─────────────────────────────────────────────
  "font_family": "맑은 고딕",         // 폰트 이름
  "font_size": 12,                   // 폰트 크기 (pt). GDI CreateFontW 전달 시 -MulDiv(pt, dpiY, 72)로 px 변환.
                                     // DPI 스케일링은 DpiHelper가 모니터별 dpiY를 조회하여 처리
  "font_weight": "bold",             // "normal" | "bold"
  "hangul_label": "한",              // 한글 모드 표시 텍스트
  "english_label": "En",             // 영문 모드(한국어 IME) 표시 텍스트
  "non_korean_label": "EN",          // 비한국어 IME 표시 텍스트
  "label_style": "text",             // "text"  — 텍스트 표시 (한/En/EN)
                                     // "dot"   — 색상 점만 표시 (텍스트 없음)
                                     // "icon"  — ㄱ/A 아이콘 스타일
                                     // 참고: "flag" (이모지) 미지원 — GDI DrawTextW는 컬러 이모지 렌더링 불가

  // ─────────────────────────────────────────────
  // [외관 — 색상]
  // ─────────────────────────────────────────────
  "hangul_bg": "#16A34A",            // 한글 모드 배경색 (Green 600)
  "hangul_fg": "#FFFFFF",            // 한글 모드 글자색
  "english_bg": "#D97706",           // 영문 모드(한국어 IME) 배경색 (Amber 600)
  "english_fg": "#FFFFFF",           // 영문 모드(한국어 IME) 글자색
  "non_korean_bg": "#6B7280",        // 비한국어 IME 배경색 (Gray 500)
  "non_korean_fg": "#FFFFFF",        // 비한국어 IME 글자색
  "opacity": 0.85,                   // 기본 투명도 (0.0 ~ 1.0) — label 스타일 기준
  "idle_opacity": 0.4,               // 유휴 상태 투명도 (always 모드에서 타이핑 안 할 때)
  "active_opacity": 0.95,            // 활성 상태 투명도 (전환 직후 또는 타이핑 중)

  // [외관 — 캐럿 박스 스타일 투명도]
  // 캐럿 박스는 크기가 작으므로 label보다 높은 투명도가 필요하다.
  // 아래 값이 설정되면 캐럿 박스 스타일에만 적용되고, label 스타일은 위 값을 사용한다.
  // 미설정(null)이면 위 기본값을 공유한다.
  "caret_box_opacity": 0.95,         // 캐럿 박스 기본 투명도 (label의 0.85보다 높음)
  "caret_box_idle_opacity": 0.65,    // 캐럿 박스 유휴 투명도 (label의 0.4이면 거의 안 보임)
  "caret_box_active_opacity": 1.0,   // 캐럿 박스 활성 투명도 (전환 직후 완전 불투명)
  "caret_box_min_opacity": 0.5,      // 캐럿 박스 최소 투명도 하한 (이 아래로 내려가지 않음)

  // [외관 — 투명도 적용 우선순위]
  // 스타일별 opacity 결정 로직:
  //   1. 캐럿 박스 스타일(caret_dot/square/underline/vbar) → caret_box_* 값 사용
  //      └── caret_box_*가 null이면 → 기본 opacity/idle_opacity/active_opacity 사용
  //      └── 최종값이 caret_box_min_opacity 미만이면 → caret_box_min_opacity로 클램핑
  //   2. label 스타일 → opacity/idle_opacity/active_opacity 값 직접 사용
  // 상태별:
  //   - on_event 모드: opacity (표시 중) → fade_out → 0 (숨김)
  //   - always 모드:   active_opacity (이벤트 직후) → idle_opacity (유휴, always_idle_timeout_ms 경과 후)

  // ─────────────────────────────────────────────
  // [외관 — 테마 프리셋]
  // ─────────────────────────────────────────────
  "theme": "custom",                 // "custom"     — 위 색상 설정 사용
                                     // "minimal"    — 흑백 미니멀
                                     // "vivid"      — 원색 강조
                                     // "pastel"     — 파스텔 톤
                                     // "dark"       — 어두운 배경
                                     // "system"     — Windows 강조 색상 따라감
  // theme이 "custom"이 아니면, 위 색상 설정은 테마 프리셋 값으로 오버라이드됨

  // ─────────────────────────────────────────────
  // [애니메이션]
  // ─────────────────────────────────────────────
  "animation_enabled": true,         // 애니메이션 전체 on/off
  "fade_in_ms": 150,                 // 페이드인 시간 (ms)
  "fade_out_ms": 400,                // 페이드아웃 시간 (ms)
  "change_highlight": true,          // 전환 시 강조 효과
  "highlight_scale": 1.3,            // 전환 시 확대 배율
  "highlight_duration_ms": 300,      // 확대 후 원래 크기 복귀까지 시간 (ms)
  "slide_animation": false,          // 위치 이동 시 슬라이드 효과 (true면 부드럽게 이동)
                                     // 후순위: 인디케이터가 캐럿을 따라가지 않으므로(F-17)
                                     // 슬라이드 발생 시나리오가 제한적 (표시 중 새 이벤트로 위치 변경 시에만)
  "slide_speed_ms": 100,             // 슬라이드 애니메이션 소요 시간 (ms)

  // ─────────────────────────────────────────────
  // [동작 — 폴링 및 감지]
  // ─────────────────────────────────────────────
  "poll_interval_ms": 80,            // IME 상태 확인 주기 (ms), 50~500 범위 권장. 항상 실행
  "caret_poll_interval_ms": 50,      // 캐럿 위치 획득 재시도 간격 (ms). 이벤트 발생 시점에 1순위(GetGUIThreadInfo) 실패 시
                                     // 이 간격으로 최대 3회 재시도한 뒤, 다음 fallback 단계로 진행.
                                     // 최종 fallback(마우스 커서)까지 실패하는 경우는 없음 (GetCursorPos는 항상 성공).
                                     // 인디케이터 표시 중 캐럿을 따라가지 않음
  "detection_method": "auto",        // "auto"        — 자동 선택 (권장)
                                     // "ime_default"  — ImmGetDefaultIMEWnd 고정
                                     // "ime_context"  — ImmGetContext 고정
                                     // "keyboard_layout" — GetKeyboardLayout만 사용
  "caret_method": "auto",            // "auto"        — 다단계 fallback (권장)
                                     // "gui_thread"   — GetGUIThreadInfo 고정
                                     // "uia"          — UI Automation 고정
                                     // "mouse"        — 마우스 위치만 사용
  "app_method_cache_size": 50,       // 앱별 감지 방식 캐시 크기 (LRU)

  // ─────────────────────────────────────────────
  // [동작 — 한/영 외 상태 처리]
  // ─────────────────────────────────────────────
  "non_korean_ime": "hide",          // "hide"     — 비한국어 IME일 때 인디케이터 숨김
                                     // "show"     — EN 등으로 표시
                                     // "dim"      — 반투명하게 표시
  "hide_in_fullscreen": true,        // 전체화면 앱에서 자동 숨김 (IsFullscreenExclusive 판정: 모니터 전체 + WS_CAPTION 없음)
                                     // false로 설정하면 브라우저 F11 전체화면 등에서도 인디케이터 표시
                                     // 참고: hide_in_game은 제거됨 — 전체화면 게임은 이 조건으로 이미 감지됨
  "hide_when_no_focus": true,        // hwndFocus가 NULL일 때 숨김 (핵심 필터링 조건)
  "hide_on_lock_screen": true,       // 잠금 화면에서 숨김

  // ─────────────────────────────────────────────
  // [동작 — Windows 시스템 요소 필터링]
  // ─────────────────────────────────────────────
  "system_hide_classes": [           // 인디케이터를 숨길 윈도우 클래스명 목록
    "Progman",                       //   바탕화면
    "WorkerW",                       //   바탕화면 (워커)
    "Shell_TrayWnd",                 //   작업표시줄
    "Shell_SecondaryTrayWnd"         //   보조 모니터 작업표시줄
  ],
  "system_hide_classes_user": [      // 사용자가 추가하는 숨김 클래스명 (위 목록에 병합)
    // "MyCustomClass"
  ],

  // ─────────────────────────────────────────────
  // [동작 — 앱별 프로필]
  // ─────────────────────────────────────────────
  "app_profiles": {
    // 프로세스명(소문자)별 설정 오버라이드
    // 여기 지정된 키만 오버라이드, 나머지는 글로벌 설정 상속
    "code.exe": {                    // VS Code
      "caret_method": "gui_thread",
      "caret_offset": { "x": 0, "y": 2 }
    },
    "excel.exe": {                   // Excel — 셀 편집 시 캐럿 위치 부정확
      "position_mode": "mouse"
    },
    "mstsc.exe": {                   // 원격 데스크톱 — 원격 IME와 충돌 방지
      "enabled": false               // 앱 프로필 전용 키: false면 해당 앱에서 인디케이터 비활성화
    }                                 // (글로벌 설정에는 없음 — 앱 프로필에서만 사용)
    // "notepad.exe": { ... }
    // "chrome.exe": { ... }
  },
  "app_profile_match": "process",    // "process" — 프로세스명으로 매칭
                                     // "title"   — 창 제목 패턴으로 매칭 (정규식)
                                     // "class"   — 윈도우 클래스명으로 매칭

  // ─────────────────────────────────────────────
  // [동작 — 블랙리스트 / 화이트리스트]
  // ─────────────────────────────────────────────
  "app_filter_mode": "blacklist",    // "blacklist" — 지정 앱에서만 비활성
                                     // "whitelist" — 지정 앱에서만 활성
  "app_filter_list": [               // 필터 대상 프로세스명 목록
    // "GameApp.exe",
    // "vlc.exe"
  ],

  // ─────────────────────────────────────────────
  // [핫키]
  // ─────────────────────────────────────────────
  "hotkeys_enabled": true,
  "hotkey_toggle_visibility": "Ctrl+Alt+H",   // 인디케이터 표시/숨기기 토글
  "hotkey_cycle_style": "Ctrl+Alt+I",         // 인디케이터 스타일 순환 (caret_dot→caret_square→caret_underline→caret_vbar→label)
  "hotkey_cycle_position": "Ctrl+Alt+P",       // 위치 모드 순환 (caret→mouse→fixed)
  "hotkey_cycle_display": "Ctrl+Alt+D",        // 표시 모드 순환 (on_event→always)
  "hotkey_open_settings": "Ctrl+Alt+S",        // 설정 창 열기

  // ─────────────────────────────────────────────
  // [시스템 트레이]
  // ─────────────────────────────────────────────
  "tray_enabled": true,              // 시스템 트레이 아이콘 표시
  "tray_icon_style": "caret_dot",     // "caret_dot"    — 캐럿+점 아이콘, 한/영 상태별 배경색 변경 (기본값)
                                     //                    hangul_bg / english_bg / non_korean_bg 설정 색상 사용
                                     //                    (인디케이터와 동일 색상 공유)
                                     //                    텍스트 미표시 — IME 트레이 아이콘과 표현 방식 차별화
                                     // "static"       — 고정 단색 아이콘 (상태 미표시)
  "tray_tooltip": true,              // 트레이 아이콘 툴팁 표시 ("한글 모드" / "영문 모드")
  "tray_click_action": "toggle",     // 좌클릭 동작: "toggle" — 표시/숨기기
                                     //              "settings" — 설정 창 열기
                                     //              "none" — 동작 없음
  "tray_show_notification": false,   // 한/영 전환 시 Windows 토스트 알림 — 현재 미지원 (false 고정).
                                     // COM 기반 IToastNotificationManagerStatics 필요하여 NativeAOT 구현 복잡도 높음.
                                     // 향후 검토 가능하나, 오버레이 인디케이터가 주 피드백이므로 우선순위 낮음
  "tray_quick_opacity_presets": [0.95, 0.85, 0.6],  // 트레이 간이 설정 투명도 프리셋 목록 [진하게, 보통, 연하게]
  // 트레이 메뉴 언어는 "language" 설정을 따름 (별도 오버라이드 키 없음)

  // ─────────────────────────────────────────────
  // [시스템]
  // ─────────────────────────────────────────────
  "startup_with_windows": false,     // Windows 시작 시 자동 실행
  "startup_minimized": true,         // 시작 시 최소화 (트레이만 표시)
  "single_instance": true,           // 다중 인스턴스 방지
  "log_level": "WARNING",           // "DEBUG" | "INFO" | "WARNING" | "ERROR"
  "log_to_file": false,             // 파일 로깅 활성화
  "log_file_path": "",              // 로그 파일 경로 (비어있으면 앱 디렉토리/koenvue.log)
  "log_max_size_mb": 10,            // 로그 파일 최대 크기 (MB)
  "language": "ko",                   // UI 언어: "ko" (한글, 기본값) | "en" (영문) | "auto" (시스템 언어 따름)
                                     // 한글 표시 우선 원칙에 따라 기본값은 "ko"
                                     // "auto"는 Windows 시스템 언어가 한국어가 아닐 때만 영문 전환

  // ─────────────────────────────────────────────
  // [다중 모니터 & 화면 경계]
  // ─────────────────────────────────────────────
  "multi_monitor": "follow_caret",   // "follow_caret"  — 캐럿이 있는 모니터에 표시 (권장)
                                     // "follow_mouse"  — 마우스가 있는 모니터에 표시
                                     // "primary_only"  — 주 모니터에만 표시
  // 참고: DPI Awareness는 app.manifest에서 PerMonitorV2로 선언되며 런타임에 변경 불가.
  // 아래 per_monitor_scale이 false면 모든 모니터에 주 모니터 DPI를 적용 (보조 모니터에서 크기 부정확할 수 있음).
  "per_monitor_scale": true,         // 모니터별 DPI에 따라 인디케이터 크기/오프셋/폰트 자동 스케일링
  "clamp_to_work_area": true,        // true: rcWork 기준 클램핑 (작업표시줄 제외 영역)
                                     // false: rcMonitor 기준 (전체 모니터 영역)
  "prevent_cross_monitor": true,     // 인디케이터가 캐럿이 속한 모니터 밖으로 나가지 않도록 방지

  // ─────────────────────────────────────────────
  // [고급 — 일반 사용자는 변경 불필요]
  // ─────────────────────────────────────────────
  "advanced": {
    "force_topmost_interval_ms": 5000,  // TOPMOST 재적용 주기 (다른 TOPMOST 앱과 충돌 시)
    "uia_timeout_ms": 200,              // UI Automation 호출 타임아웃
    "uia_cache_ttl_ms": 500,            // UI Automation 결과 캐시 유효 시간
    "skip_uia_for_processes": [         // UI Automation을 건너뛸 프로세스 (성능 문제 시)
      // "devenv.exe"
    ],
    "ime_fallback_chain": [             // IME 감지 순서 커스터마이즈
      "ime_default_wnd",
      "ime_context",
      "keyboard_layout"
    ],
    "caret_fallback_chain": [           // 캐럿 추적 순서 커스터마이즈
      "gui_thread_info",
      "uia_text_pattern",
      "focus_window_rect",
      "mouse_cursor"
    ],
    "overlay_class_name": "KoEnVueOverlay",  // 오버레이 윈도우 클래스명
    "prevent_sleep": false,                  // 실행 중 절전 모드 방지 (기본 비활성)
    "debug_overlay": false                   // true면 감지 방식, 캐럿 좌표 등 디버그 정보 표시
  }
}
```

### 6.2 설정 우선순위

```
1. 앱별 프로필 (app_profiles)    ← 최우선
2. 사용자 config.json
3. 코드 내 DEFAULT_CONFIG        ← 최하위
```

앱별 프로필에 정의된 키만 오버라이드되고, 나머지는 글로벌 설정을 상속한다.

### 6.3 설정 파일 위치

```
<exe 실행 경로>\config.json       ← 포터블 모드 (우선, F-46)
%APPDATA%\KoEnVue\config.json     ← 사용자 설정 (포터블 config 없을 때)
```

포터블 모드: exe와 같은 디렉토리에 config.json이 있으면 %APPDATA% 대신 해당 파일 사용. USB 등 이동식 저장소에서 설정 유지 가능.

### 6.4 설정 마이그레이션

설정 파일에 `"config_version": 1` 필드를 포함한다. 향후 설정 스키마가 변경되면 버전 번호를 증가시키고, 앱 시작 시 자동으로 마이그레이션을 수행한다. 알 수 없는 키는 무시하고, 누락된 키는 기본값으로 채운다.

**설정 로드 파이프라인 (Settings.LoadFromFile)**:
```
MergeWithDefaults → Deserialize → EnsureSubObjects → Migrate(config_version 체인) → Validate(범위 클램핑) → ThemePresets.Apply(테마 색상 적용)
```
> **MergeWithDefaults**: .NET 10 STJ 소스 생성기가 record의 init 기본값을 보존하지 않는 문제의 우회책.
> 기본 AppConfig를 JSON으로 직렬화한 뒤, 사용자 JSON 키를 위에 덮어씌워 병합한다.
> 이로써 사용자가 생략한 키에 코드 내 init 기본값이 적용된다.

### 6.5 기본 설정 예제

전체 설정 키는 약 80개이지만, 대부분 기본값으로 충분하다. 사용자가 config.json을 처음 편집할 때 참고할 수 있는 **필수 설정만 포함한 간소화 예제**:

```jsonc
{
  "config_version": 1,
  "indicator_style": "caret_dot",
  "display_mode": "on_event",
  "hangul_bg": "#16A34A",
  "english_bg": "#D97706",
  "hangul_label": "한",
  "english_label": "En",
  "opacity": 0.85,
  "startup_with_windows": false
}
```

> 위 9개 키만으로 핵심 동작을 커스터마이즈할 수 있다.
> 나머지 키는 생략하면 코드 내 DEFAULT_CONFIG 기본값이 적용된다.
> 전체 설정 키 목록은 섹션 6.1 참조.

**구현 우선순위별 설정 키 분류:**

| 우선순위 | 대상 키 | 구현 시점 |
|----------|---------|-----------|
| **MVP (Week 1~2)** | `config_version`, `display_mode`, `indicator_style`, `opacity`, `hangul_bg/fg`, `english_bg/fg`, `non_korean_bg/fg`, `hangul_label`, `english_label`, `non_korean_label`, `font_family`, `font_size`, `font_weight`, `poll_interval_ms`, `caret_poll_interval_ms`, `animation_enabled`, `fade_in_ms`, `fade_out_ms`, `change_highlight`, `highlight_scale`, `highlight_duration_ms`, `system_hide_classes`, `hide_in_fullscreen`, `hide_when_no_focus`, `tray_enabled`, `startup_with_windows`, `language`, `log_level` | Week 1~2 |
| **Standard (Week 3)** | `event_display_duration_ms`, `always_idle_timeout_ms`, `event_triggers`, `position_mode`, `caret_offset`, `mouse_offset`, `caret_placement`, `caret_placement_auto_flip`, `screen_edge_margin`, `caret_box_*` (opacity 4종), `non_korean_ime`, `app_profiles`, `app_filter_mode/list`, `hotkeys_enabled`, `hotkey_*` | Week 3 |
| **Advanced (Week 4+)** | `fixed_position`, `label_shape`, `label_border_radius`, `border_width/color`, `theme`, `slide_animation`, `tray_icon_style`, `tray_click_action`, `multi_monitor`, `per_monitor_scale`, `advanced.*` | Week 4+ |

### 6.6 설정 값 검증

설정 로드 후 범위 검증을 수행한다. 범위를 벗어나면 가장 가까운 유효값으로 클램핑한다 (에러 아닌 조용한 보정).

```csharp
static readonly Dictionary<string, (double min, double max)> Validation = new()
{
    ["poll_interval_ms"]          = (50, 500),
    ["caret_poll_interval_ms"]    = (30, 500),
    ["event_display_duration_ms"] = (500, 10000),
    ["always_idle_timeout_ms"]    = (1000, 30000),
    ["opacity"]                   = (0.1, 1.0),
    ["idle_opacity"]              = (0.1, 1.0),
    ["active_opacity"]            = (0.1, 1.0),
    ["caret_box_opacity"]         = (0.1, 1.0),
    ["caret_box_idle_opacity"]    = (0.1, 1.0),
    ["caret_box_active_opacity"]  = (0.1, 1.0),
    ["caret_box_min_opacity"]     = (0.1, 1.0),
    ["highlight_scale"]           = (1.0, 2.0),
    ["screen_edge_margin"]        = (0, 50),
    ["fade_in_ms"]                = (0, 2000),
    ["fade_out_ms"]               = (0, 2000),
    ["highlight_duration_ms"]     = (0, 2000),
    ["caret_dot_size"]            = (4, 32),
    ["caret_square_size"]         = (4, 32),
    ["caret_underline_width"]     = (8, 64),
    ["caret_underline_height"]    = (1, 16),
    ["caret_vbar_width"]          = (1, 16),
    ["caret_vbar_height"]         = (4, 64),
    ["font_size"]                 = (8, 36),
    ["label_width"]               = (16, 128),
    ["label_height"]              = (12, 96),
    ["label_border_radius"]       = (0, 48),
    ["border_width"]              = (0, 8),
    ["slide_speed_ms"]            = (0, 2000),
    ["log_max_size_mb"]           = (1, 100),
};
```

### 6.7 config.json 인코딩 처리

> **주의**: .csproj에 `InvariantGlobalization: true`가 설정되어 있으므로
> CP949/EUC-KR 등 문화권별 인코딩은 사용할 수 없다 (NuGet 패키지 없이는 불가).
> Windows 10 이후 메모장도 UTF-8이 기본 저장 형식이므로 UTF-8만 지원한다.

```
1. UTF-8 BOM 감지 → 제거
2. MergeWithDefaults (기본 AppConfig JSON과 사용자 JSON 병합 — STJ 소스 생성기 init 기본값 우회책)
3. System.Text.Json 역직렬화 (ReadCommentHandling=Skip, AllowTrailingCommas=true)
4. EnsureSubObjects (null 참조 타입 보정) → Migrate(config_version 체인) → Validate(범위 클램핑) → ThemePresets.Apply
5. 파싱 실패 시 → DEFAULT_CONFIG 사용 + 로그 경고
```

> **JSON 주석/후행 쉼표 지원**: 사용자가 config.json을 직접 편집할 때
> `// 주석`과 후행 쉼표(`[1, 2,]`)를 허용한다. `JsonSourceGenerationOptions`에
> `ReadCommentHandling = Skip`, `AllowTrailingCommas = true`를 설정하여 처리.

### 6.8 설정 동시 접근 안전성

감지 스레드(읽기)와 메인 스레드(쓰기)가 설정 객체를 공유한다. **Immutable 객체 교체 방식**으로 레이스 컨디션을 방지한다:

```csharp
// C# record로 불변 설정 객체 정의 — 21개 enum 타입 사용 (P3 원칙: 문자열 비교 금지, 전수 전환 완료)
// record with 표현식으로 갱신 (AppConfigBuilder 불필요)
record AppConfig(IndicatorStyle IndicatorStyle, DisplayMode DisplayMode, Theme Theme, LabelStyle LabelStyle, ...);

// volatile 참조 교체 — 읽기 측 락 불필요
volatile AppConfig _config = new AppConfig();

// record with 표현식으로 변경 (AppConfigBuilder 불필요)
_config = _config with { IndicatorStyle = newStyle };  // 원자적 참조 교체
```

---

## 7. 앱 유형별 호환성 매트릭스

| 앱 유형 | 대표 앱 | IME 상태 감지 | 캐럿 위치 획득 | 비고 |
|---------|--------|:---:|:---:|------|
| Win32 (GDI) | 메모장, mstsc | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 가장 안정적 |
| Win32 (리치에딧) | WordPad | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 정상 |
| WPF | Visual Studio | ImmGetDefaultIMEWnd | UI Automation | rcCaret이 (0,0,0,0)일 수 있음 |
| WinForms | .NET 앱 | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 정상 |
| Electron | VS Code, Slack | ImmGetDefaultIMEWnd | GetGUIThreadInfo 또는 UI Automation | Chromium IMM32 호환 |
| 브라우저 | Chrome, Edge | ImmGetDefaultIMEWnd | UI Automation | 탭 내 캐럿은 UI Automation 필요 |
| UWP | 설정, 계산기 | ImmGetDefaultIMEWnd fallback | UI Automation | TSF 기반, IMM32 제한적 |
| Windows Terminal | Terminal | ImmGetDefaultIMEWnd | UI Automation | ConPTY 기반 |
| Java (Swing/FX) | IntelliJ, Eclipse | ImmGetDefaultIMEWnd | GetGUIThreadInfo | 자체 IME 핸들링 주의 |
| Qt 앱 | Qt Creator | ImmGetDefaultIMEWnd | GetGUIThreadInfo | Qt IME 모듈 경유 |

---

## 8. 구현 순서

### Week 1: 코어 엔진
- [x] Win32 P/Invoke 선언 (Native/*.cs — User32, Imm32, Shell32, GDI32, Kernel32, Shcore, Ole32, OleAut32, Win32Types, SafeGdiHandles, AppMessages) — **Phase 01 완료**
- [x] IME 상태 감지 모듈 (ImeStatus.cs) — 3-tier fallback (ImmGetDefaultIMEWnd → ImmGetConversionStatus → GetKeyboardLayout) + SetWinEventHook 하이브리드 — **Phase 02 완료**
- [x] 캐럿 위치 추적 모듈 (CaretTracker.cs) — 4-tier fallback (GetGUIThreadInfo → UIA placeholder → GetWindowRect → GetCursorPos) + 앱별 LRU 캐싱(50) + tier-1 50ms×3 재시도 — **Phase 02+03 완료**
- [x] 시스템 요소 필터링 (SystemFilter.cs) — 8-조건 단락 평가 (보안데스크톱 + 숨김/최소화 + 가상데스크톱 + 클래스명 + 포커스 + 전체화면 + 드래그 + 앱필터) + IVirtualDesktopManager COM — **Phase 02 완료**
- [x] 이벤트 트리거 판정 로직 — 포커스 변경 + IME 상태 변경 감지 + EventTriggers 가드 + 드래그 중 숨김 — **Phase 03 완료**
- [x] 위치 안정성 로직 — label 고정 너비, 배치 방향 고정, 중심점 기준 확대, 서브픽셀 정렬 방지, 이벤트 시점 위치 고정(캐럿 미추적, 1.5초 자연 소멸) — **Phase 04 완료**
- [x] 앱 라이프사이클 기반 — 관리자 권한(app.manifest), 고정 GUID(DefaultConfig.AppGuid), Mutex, 크래시 복구(NIM_DELETE), ProcessExit 정리 — **Phase 01+03 완료**
- [x] 설정 로드/저장 (Settings.cs) — System.Text.Json (Source Generator), UTF-8 파싱, 값 범위 검증, volatile 참조 교체, mtime 5초 체크, 핫리로드, 앱프로필 — **Phase 06 완료**
- [ ] 수동 검증: 메모장, VS Code, Chrome에서 상태 감지 확인 (NativeAOT 단일 exe 특성상 별도 테스트 프로젝트 없이 디버그 빌드 + 디버그 오버레이로 검증. 핵심 유틸 로직은 #if DEBUG 조건부 자체 검증 assert 포함)

### Week 2: UI + 트레이
- [x] 플로팅 오버레이 (Overlay.cs) — Win32 CreateWindowExW + WS_EX_LAYERED + GDI, 5종 인디케이터 스타일, 클릭 투과 — **Phase 04 완료**
- [x] 멀티 모니터 + DPI (DpiHelper.cs) — MonitorFromPoint, GetMonitorInfo(rcWork), Per-Monitor DPI, 공통 스케일링 — **Phase 01 완료**
- [x] DPI Awareness 선언 — app.manifest에 PerMonitorV2 + dpiAware fallback 추가 — **Phase 01 완료**
- [x] 페이드 애니메이션 (Animation.cs) — UpdateLayeredWindow (BLENDFUNCTION.SourceConstantAlpha) + SetTimer/WM_TIMER 상태 머신 — **Phase 04 완료**
- [x] 시스템 트레이 (Tray.cs) — P/Invoke Shell_NotifyIconW 직접 구현, 팝업 메뉴 + 간이 설정 서브메뉴 (한글 표시) — **Phase 05 완료**
- [x] 트레이 아이콘 (TrayIcon.cs) — GDI로 캐럿+점 아이콘 생성, 한/영 상태별 배경색 변경 — **Phase 05 완료**
- [x] 시작 프로그램 등록 — schtasks 기반 Task Scheduler 등록/해제 (Tray.cs 통합) — **Phase 05 완료**

### Week 3: 고급 감지 + 앱별 대응
- [x] UI Automation COM 접근 캐럿 추적 (UiaClient.cs) — STA 스레드 전용, [GeneratedComInterface] — **Phase 07 완료**
- [x] 앱별 방식 캐싱 로직 (Dictionary + LRU, 최대 50개) — CaretTracker.cs에 구현 — **Phase 02 완료**
- [x] 앱별 프로필 (프로세스명/창 제목/윈도우 클래스 매칭) — Settings.cs 앱프로필 — **Phase 06 완료**
- [x] 블랙리스트/화이트리스트 앱 필터 — SystemFilter.PassesAppFilter에 구현 — **Phase 02 완료**
- [x] 글로벌 핫키 (P/Invoke → RegisterHotKey) — 5종 핫키 등록/해제/파싱 + F1-F12 키 지원 — **Phase 03+05 완료**
- [x] 포터블 모드 자동 감지 — exe 디렉토리에 config.json 존재 여부로 포터블/설치 모드 자동 판별 — **Phase 06 완료**

### Week 4: 마감 + 배포
- [x] 테마 프리셋 (ThemePresets.cs — 6개 테마) — **Phase 07 완료**
- [x] 라벨 스타일 표시 텍스트 옵션 (텍스트/점/ㄱA 아이콘) — 국기 이모지는 GDI 제약으로 미지원 — **Phase 04 완료**
- [x] 디버그 오버레이 모드 (DebugOverlay) — **Phase 07 완료**
- [x] NativeAOT 빌드 (dotnet publish -c Release -r win-x64) — **Phase 07 완료**
- [x] NonKoreanImeMode "dim" 구현 (Animation.cs GetTargetAlpha × 0.5) — **Phase 08 완료**
- [x] 슬라이드 애니메이션 (ease-out cubic 보간, TIMER_ID_SLIDE) — **Phase 08 완료**
- [x] 라벨 테두리 렌더링 (GDI CreatePen + NULL_BRUSH, 펜폭 인셋) — **Phase 08 완료**
- [x] Fixed 위치 앵커/모니터 해석 (MonitorFromWindow, 6종 앵커 + 3종 모니터) — **Phase 08 완료**
- [x] 파일 로깅 (비동기 ConcurrentQueue + drain 스레드, 단일 회전) — **Phase 08 완료**
- [x] string→enum P3 전환 (8개 config 필드: LabelStyle, Theme, TrayClickAction, AppProfileMatch, MultiMonitorMode, TrayIconStyle, FixedAnchor, FixedMonitor) — **Phase 08 완료**
- [ ] 통합 테스트: 5개 이상 앱 유형 × 멀티 모니터 환경에서 정상 동작 확인

---

## 9. 의존성

### 9.1 런타임 의존성 — 없음 (Zero Dependencies)

> **NuGet 외부 패키지: 0개**
> **.NET 런타임 불필요** — NativeAOT 네이티브 컴파일

| 모듈 | 분류 | 용도 | 비고 |
|------|------|------|------|
| `System.Text.Json` | .NET 기본 라이브러리 | 설정 파일 JSON 직렬화/역직렬화 | NuGet 불필요 |
| `System.Threading` | .NET 기본 라이브러리 | 감지 스레드 (Background Thread) | NuGet 불필요 |
| `System.Runtime.InteropServices` | .NET 기본 라이브러리 | P/Invoke, COM Interop | NuGet 불필요 |
| `System.Diagnostics` | .NET 기본 라이브러리 | 로깅 (Trace/Debug), Stopwatch 타이밍 | NuGet 불필요 |
| `System.Collections.Concurrent` | .NET 기본 라이브러리 | ConcurrentQueue (비동기 로깅 큐) | NuGet 불필요 |
| `System.IO` | .NET 기본 라이브러리 | 파일/경로 처리, StreamWriter 파일 로깅 | NuGet 불필요 |

### 9.2 Win32 P/Invoke 대상 DLL

| DLL | 용도 |
|-----|------|
| `user32.dll` | 윈도우 관리, 메시지 루프, 캐럿/포커스 조회, SetWinEventHook |
| `imm32.dll` | IME 상태 감지 (ImmGetDefaultIMEWnd, ImmGetConversionStatus) |
| `shell32.dll` | 시스템 트레이 (Shell_NotifyIconW) |
| `gdi32.dll` | 오버레이/아이콘 렌더링 (CreateFont, CreateCompatibleBitmap, DrawText) |
| `kernel32.dll` | Mutex, 프로세스 정보 |
| `shcore.dll` | Per-Monitor DPI (GetDpiForMonitor) |
| `ole32.dll` | COM 초기화 (CoInitializeEx, CoCreateInstance) |
| `oleaut32.dll` | UI Automation COM 인터페이스 |

### 9.3 빌드 (개발 환경에서만 필요)

| 도구 | 용도 | 비고 |
|------|------|------|
| .NET 10 SDK | 컴파일 + NativeAOT 빌드 | 무료 (https://dotnet.microsoft.com) |
| Visual Studio 2022 Community (선택) | IDE + 디버거 | 무료, 또는 VS Code + C# 확장 |

**빌드 명령:**
```bash
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```

**프로젝트 설정 (.csproj):**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
    <IlcOptimizationPreference>Size</IlcOptimizationPreference>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
</Project>
```

> **NativeAOT 필수 설정**:
> - `JsonSerializerIsReflectionEnabledByDefault: false` — 리플렉션 fallback 차단. `[JsonSerializable(typeof(AppConfig))]`가 적용된 `JsonSerializerContext` 사용 필수
> - `InvariantGlobalization: true` — 문화권별 데이터 제거로 바이너리 크기 감소. CP949/EUC-KR 인코딩 미지원

**관리자 권한 매니페스트 (app.manifest):**
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

**예상 번들 크기:**

| 구성 | 크기 |
|------|------|
| NativeAOT 컴파일 결과 | ~3MB |
| ILC 트리밍 (미사용 코드 제거) 포함 | 위 수치에 반영 |
| **합계 (단일 exe)** | **~3MB** |

> **최종 사용자 환경에는 .NET 런타임이 필요하지 않다.**
> NativeAOT가 네이티브 코드로 컴파일하여 단일 exe로 배포한다. 시작 지연 없음.

### 9.4 배포 구성

```
KoEnVue/
├── KoEnVue.exe              # 메인 프로그램 (~3MB, .NET 런타임 불필요, 즉시 시작)
└── config.json              # 설정 파일 (포터블 모드, 선택적)
```

| 파일 | 대상 | 런타임 필요 | 용도 |
|------|------|:-----------:|------|
| `KoEnVue.exe` | 모든 사용자 | 불필요 | 메인 프로그램 (인디케이터 + 트레이) |
| `config.json` | 모든 사용자 | 불필요 | 설정 파일 |

> `KoEnVue.exe` 단일 파일로 배포. .NET 런타임, Python 등 어떤 런타임도 불필요.
> 일반 사용자는 트레이 간이 설정 + config.json 편집으로 모든 설정이 가능하다.

---

## 10. 리스크 및 완화 방안

| 리스크 | 영향 | 확률 | 완화 방안 |
|--------|------|------|-----------|
| 특정 앱에서 캐럿 위치를 전혀 못 얻음 | 중 | 높음 | 포커스 윈도우 영역 fallback → 마우스 fallback 순으로 보장, 앱별 예외 목록 관리 |
| 전체화면 게임에서 오버레이가 성능 저하 유발 | 중 | 중 | 전체화면 감지 후 자동 비활성화 |
| Windows 보안 업데이트로 API 동작 변경 | 중 | 낮음 | IMM32는 하위호환이 강함, UI Automation도 안정적 API |
| 백신 소프트웨어가 관리자 권한 앱으로 오탐 | 중 | 중 | 키 입력 후킹 없이 상태 조회만 수행. 코드 서명 적용 시 해소 |
| UAC 프롬프트로 사용자 경험 저하 | 낮 | 낮음 | 모든 실행을 Task Scheduler 경유로 통일 → UAC 미표시. 최초 Task 등록 시에만 UAC 1회 필요 |
| 한/영 키 빠른 연타로 상태 누락 | 낮 | 중 | SetWinEventHook(EVENT_OBJECT_IME_CHANGE) 보조 훅 + 폴링 하이브리드 |
| 서드파티 IME (날개셋 등) 비호환 | 중 | 중 | 3단계 fallback 체인 + 앱별 프로필 detection_method 오버라이드 |
| 원격 데스크톱 이중 IME | 중 | 중 | mstsc.exe 기본 블랙리스트. 원격 세션 내 사용 시 원격 PC에 별도 설치 |
| Win32 오버레이 직접 구현 복잡도 | 중 | 중 | 기능이 단순(도형 1개 + 색상 + 투명도)하므로 관리 가능. Win32 메시지 루프 + GDI 렌더링 패턴은 잘 문서화되어 있음 |
| Shell_NotifyIconW 직접 구현 복잡도 | 중 | 중 | Win32 Shell API 문서/예제 참조하여 최소 기능만 구현. SetForegroundWindow + WM_NULL 메뉴 닫힘 workaround 적용 |
| UI Automation 호출 지연 (200ms+) | 중 | 중 | 전용 UIA 스레드에서 타임아웃 실행, 초과 시 null 반환 → 다음 fallback 진행. 매 호출마다 new Thread 금지 |
| NativeAOT + COM Interop 제약 | 중 | 낮음 | .NET 8+의 [GeneratedComInterface] source generator 사용으로 NativeAOT 호환 COM 코드 자동 생성. ComWrappers 수동 래핑 불필요 |
| GDI 리소스 누수 (HFONT, HICON, HBITMAP) | 중 | 중 | SafeHandle/IDisposable + using 패턴으로 GDI 핸들 수명 관리. HFONT는 앱 수명 동안 재사용, 상태 변경 시에만 아이콘 재생성. GC가 누수 최종 방어 |
| 모니터 DPI 혼합 시 폰트 크기 불일치 | 중 | 중 | 모니터 전환 감지 시 HFONT를 새 DPI에 맞춰 재생성. rcWork는 매 폴링마다 GetMonitorInfo 호출 (캐시 안 함) |
| 모니터 핫플러그/작업표시줄 이동 | 중 | 낮음 | WM_DISPLAYCHANGE + WM_SETTINGCHANGE 처리. MonitorFromPoint에 MONITOR_DEFAULTTONEAREST 사용 |
| TOPMOST 경쟁 (다른 TOPMOST 앱) | 낮 | 중 | 인디케이터 표시 중 config.Advanced.ForceTopmostIntervalMs(기본 5초)마다 SetWindowPos(HWND_TOPMOST) 재호출 |
| 가상 데스크톱에서 보이지 않는 앱에 인디케이터 표시 | 낮 | 중 | IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop COM 호출로 현재 데스크톱 확인 |
| 드래그 앤 드롭 시 WS_EX_TRANSPARENT 충돌 | 낮 | 중 | GetAsyncKeyState(VK_LBUTTON) 체크, 드래그 중 인디케이터 즉시 숨김 |
| config.json 인코딩 깨짐 (메모장 편집) | 낮 | 낮음 | UTF-8 BOM 제거 후 UTF-8 파싱. 실패 시 DEFAULT_CONFIG 유지. InvariantGlobalization으로 CP949/EUC-KR 미지원 |
| config.json 설정 값 범위 초과 | 낮 | 중 | 로드 후 범위 검증, 유효값으로 클램핑 (섹션 6.6) |
| 감지 스레드/메인 스레드 설정 객체 레이스 컨디션 | 낮 | 중 | Immutable record + volatile 참조 교체로 원자적 갱신 (섹션 6.8) |
| label 스타일 한/영 전환 시 인디케이터 흔들림 | 중 | 높음 | 고정 너비 라벨 + 배치 방향 고정 + 중심점 기준 확대로 해결. F-S01~S06 참조 |
| 비정상 종료 시 트레이 아이콘 찌꺼기 | 낮 | 중 | AppDomain.ProcessExit 핸들러 + 앱 시작 시 고정 GUID로 이전 아이콘 NIM_DELETE |
| PostMessage 큐 오버플로우 | 낮 | 낮 | 80ms × 2~3메시지 = 초당 37개, 큐 한계(10,000) 도달 불가. 실패 시 로그 경고 + 다음 폴링 재시도 |

---

## 11. 성공 지표

| 지표 | 목표 |
|------|------|
| 한/영 전환 감지 정확도 | Win32/Electron 앱에서 99% 이상 |
| 캐럿 추적 성공률 | 주요 앱 10종에서 80% 이상 (나머지는 마우스 fallback) |
| 전환 후 표시 지연 | 100ms 이내 |
| CPU 사용률 | idle 0.5% 이하 |
| 사용자 체감 | "잘못된 모드로 타이핑" 빈도 체감 50% 이상 감소 |
