# PR-23: UWP(ApplicationFrameWindow) hwndFocus=0 폴백 — 설정 앱 클릭 시 메인 인디 소멸 수정

**상태**: 구현 완료 (2026-06-05)
**유형**: 버그 수정 (사용자 가시)

## 동기

Windows **설정 앱**(SystemSettings, UWP) 의 콘텐츠를 클릭하면 메인 인디케이터가 약 0.2초 뒤
사라지는 결함. 설정 앱은 IME 토글이 멀쩡히 동작하는 일반 창인데도 인디가 없어져 "설정 만지는
동안 한/영 상태를 못 본다" 는 사용성 저하.

**스모킹건 로그**:

```
Filter triggered HIDE: ... hwndFocus=0x0, fgClass=ApplicationFrameWindow, streak=3
```

## 근본 원인

설정 앱의 foreground 창은 `ApplicationFrameHost.exe` 가 소유하는 **`ApplicationFrameWindow`**
클래스지만, 실제 콘텐츠는 **별도 프로세스**(`SystemSettings.exe`) 의 `CoreWindow` 다 (UWP 의
프레임/콘텐츠 분리 아키텍처).

콘텐츠를 클릭하면 ApplicationFrameHost 스레드 기준 [`GetGUIThreadInfo`] 의
`GUITHREADINFO.hwndFocus` 가 `0` 으로 떨어진다 (콘텐츠 포커스가 다른 프로세스라 프레임 스레드는
"포커스 없음" 으로 보고 — **conhost 와 동형**). 그러면:

1. [`SystemFilter.ShouldHide`] **조건 6** (`hwndFocus == 0 && HideWhenNoFocus`) 발동 → filtered
2. [`DetectionState.FilteredStreak`] 가 매 폴링 누적
3. `DefaultConfig.HideHysteresisPolls`(= 3) 도달 시 HIDE 확정 → 메인 인디 소멸

HIDE 디바운스(streak 3) 는 파일 탐색기류의 `hwndFocus` flip-flop 진동을 흡수하려는 장치인데,
설정 앱 콘텐츠 클릭은 진동이 아니라 **연속 `hwndFocus=0`** 이라 디바운스를 통과해 HIDE 가 확정된다.

## 변경

| 파일 | 변경 |
|------|------|
| [Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs) | `Win32Constants.ApplicationFrameWindowClass = "ApplicationFrameWindow"` const 신규 (+ 의도 주석) |
| [Program.cs](../../Program.cs) | `ResolveFocusWindow` 의 `hwndFocus=0` 폴백 조건을 `ConsoleWindowClass` 단독 → `ConsoleWindowClass OR ApplicationFrameWindowClass` 로 확장 + XML doc/주석 갱신 |

[`ResolveFocusWindow`] 의 폴백:

```csharp
if (hwndFocus == IntPtr.Zero)
{
    string fgClass = WindowProcessInfo.GetClassName(hwndForeground);
    if (fgClass.Equals(Win32Constants.ConsoleWindowClass, StringComparison.OrdinalIgnoreCase)
        || fgClass.Equals(Win32Constants.ApplicationFrameWindowClass, StringComparison.OrdinalIgnoreCase))
        hwndFocus = hwndForeground;
}
```

foreground 가 `ApplicationFrameWindow` 이고 `hwndFocus=0` 이면 `hwndForeground` 를 포커스
타깃으로 대체 → 조건 6 의 no-focus HIDE 오탐이 발생하지 않는다.

## 설계 결정: CoreWindow 제외 (폴백 한정)

시작 메뉴 / 검색의 **`Windows.UI.Core.CoreWindow`** 도 동일하게 `hwndFocus=0` 이지만 폴백에서
**의도적으로 제외**한다. 이들은 [`SystemInputProcesses`] 경로로 *정상* 숨김돼야 하는 시스템
오버레이라, 폴백을 적용하면 시작 메뉴를 열었을 때 인디가 사라지지 않는 회귀가 생긴다.

→ 폴백 대상은 **일반 UWP 앱 프레임**인 `ApplicationFrameWindow` 클래스로만 한정. CoreWindow
   는 클래스명 자체가 다르므로 (`Windows.UI.Core.CoreWindow` ≠ `ApplicationFrameWindow`) 조건에
   걸리지 않아 자연 분리된다.

## IME 감지 무영향

[`ImeStatus.Detect(hwndFocus, threadId)`] 의 `threadId` 는 [`DetectionLoop`] 에서
`hwndForeground` 로 **독립 결정**되므로 (Tier 3 `GetKeyboardLayout` 입력), 본 폴백이 바꾸는
`hwndFocus` 는 IME Tier 3 경로를 건드리지 않는다. 폴백은 오직 `SystemFilter.ShouldHide` 의 no-focus
판정에만 영향을 준다.

`position_mode = window` 의 깜박임도 별도 PR 불요 — filtered 구간에서는 HIDE 디바운스 잠정
구간이 `TrackWindowMove` 도달 **전** 에 return 하므로 (상태 미갱신 비대칭), 폴백으로 filtered 자체가
사라지면 깜박임도 자연 해소된다.

## P 규칙

- **P3 ✅** — 클래스명 리터럴 `"ApplicationFrameWindow"` 를 `Win32Constants` const 로 (정의 1곳,
  사용처는 named const 경유). `ConsoleWindowClass` 와 동형 패턴이라 인라인 매직 스트링 0.
- **P6 ✅** — const 의 Core 배치 근거: `ApplicationFrameWindow` 는 Win32 표준 윈도우 클래스명
  (OS 어휘) 이지 KoEnVue 도메인 어휘가 아니므로 `Core/Native/Win32Constants` 가 정위치
  (`ConsoleWindowClass` 와 동일 카테고리). App→Core 단방향 의존 유지.
- P1 / P2 / P4 / P5 무관 (외부 NuGet 0, 사용자 가시 문자열 변화 0, 단일 구현, manifest 불변).

## 검증

`ResolveFocusWindow` / `ShouldHide` 는 라이브 Win32 (`GetGUIThreadInfo`, `GetClassName`,
실제 foreground 상태) 에 의존해 **단위 테스트가 비현실적** — 핸드롤 mock 으로는 실제 UWP
프레임/콘텐츠 분리 시나리오를 재현할 수 없다. **수동 smoke** 로 검증:

1. **수정 대상** — Windows 설정 앱(SystemSettings) 콘텐츠 클릭 시 메인 인디가 **유지**되는지.
2. **회귀가드** — 시작 메뉴를 열고(인디 정상 소멸) ESC 로 닫았을 때 정상 복원되는지. CoreWindow
   제외가 깨지면 시작 메뉴에서 인디가 사라지지 않으므로 본 항목이 가드.

**자동 검증** (2026-06-05): dotnet build 0/0 + AOT publish 4.66 MB (4,883,456 bytes,
SHA256 `B7C3537631DD83632C8CB5A21597D733258800B9B99B6BA734E00CBE413371E0`) AOT 경고 0 + 90/90 PASS.
(폴백/필터는 라이브 Win32 의존이라 단위 테스트가 직접 커버하지 못하므로 빌드·기존 회귀 스위트
통과 + 위 수동 smoke 가 검증 진실원.)

### invariant grep

```bash
git grep -n "ApplicationFrameWindowClass" Core/Native/Win32Types.cs   # 1 (정의)
git grep -n "ApplicationFrameWindowClass" Program.cs                  # 1 (사용, named const 경유)
git grep -n '"ApplicationFrameWindow"' -- '*.cs'                       # 1 (const 정의에만 — 인라인 0)
```

## 참고

- 동형 선례: [implementation-notes.md "Console host + UWP frame fallback"](../implementation-notes.md)
- HIDE 디바운스 메커니즘: [implementation-notes.md "HIDE 디바운스"](../implementation-notes.md) +
  [dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md](../dev-notes/2026-06-02-explorer-hide-flipflop-debounce.md)
