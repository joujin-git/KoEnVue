# PR-32: 컨텍스트 메뉴 위 플로팅 배지·커서 헤일로 일관 숨김 (WFP 이중축)

> 상태: **✅ implemented** — 2026-07-24, main 직접. Tier-1 build/test/publish + reviewer ✅. 수동 smoke는 사용자 확인.
>
> 선행: PR-26 (강제 Show + 레벨 HIDE) · 세션 2026-07-24 (우클릭 dismiss 보류, `#32768` 축 권고)

## 동기

클래식 컨텍스트 메뉴(`#32768`)는 **포그라운드를 뺏지 않는** 경우가 많다. 메인은 FG+`SystemFilter`, 커서는 `WindowFromPoint`+같은 hide 목록이라, 목록에만 `#32768`을 넣으면 **커서만 숨고 메인은 앱 위에 남는다**.

목표: **식별 가능한 컨텍스트 메뉴·기존 셸 hide 표면** 위에서 메인·커서가 **최대한 동일하게** 숨김. 기존 Show/HIDE 레이스(히스테리시스·Forced Show·clickDismiss)를 깨지 않음.

## 범위

| Tier | 대상 | v1 |
|------|------|----|
| A | 클래식 팝업 `#32768` (탐색기·앱 `TrackPopupMenu`) | **필수** |
| B | 기존 `SystemHideClasses` / `ShellExperienceHost` 등 | **유지 + WFP 보강** |
| C | 앱 자체 메뉴 (`#32768`) | A에 포함 |
| D | WinUI/`Popup`/브라우저 커스텀 | **비보장** |

**비포함**: 우클릭 dismiss 확장 · WinEvent `MENUPOPUPSTART/END` · Start/Search 메인 숨김 통일 · `WS_EX_TOOLWINDOW` 단독.

## 설계 (A안)

```text
hideMain = FgFiltered(히스테리시스 유지)  OR  PointerSuppress(즉시)
showMain = !FgFiltered AND !PointerSuppress  (+ UserHidden / clickDismiss)
```

| 축 | 판정 | 타이밍 |
|----|------|--------|
| FG | `SystemFilter.ShouldHide` (기존 8조건) | `HideHysteresisPolls` |
| Pointer | `WindowFromPoint` → `GA_ROOT` → `#32768` ∪ SystemHide* | **즉시** |

- **단일 진실원**: `App/Detector/OverlaySuppressProbe` — 커서 `IsOverShellUi` 치환.
- **Start/Search**: 커서만 `IsSystemInputProcess` (메인 표시 정책 불변).
- **`#32768`**: `Win32Constants.PopupMenuClass` const. FG `DefaultSystemHideClasses`에는 **넣지 않음**(프로브 전용 — 목록 오염 방지). FG가 드물게 `#32768`이어도 Pointer 축이 잡음.

### 우클릭 dismiss 보류

메뉴 생존을 모르고, Shift+F10/앱키/터치를 못 잡으며, 메뉴 생성 전 dismiss → 이후 Show 레이스만 키움. **창 판정(WFP)** 이 정답.

## 구현 체크리스트

1. [x] `Core/Native/Win32Types.cs` — `PopupMenuClass = "#32768"`
2. [x] `App/Detector/OverlaySuppressProbe.cs` — `MatchesSuppressRoot` / `IsSuppressRoot` / `IsPointerOverSuppressSurface`
3. [x] `Program.TryHandleFilter` — Pointer 즉시 HIDE (히스테리시스 우회)
4. [x] `Program.TryShowIndicatorIfForegroundAllowed` — Pointer면 Show 스킵
5. [x] `CursorOverlay` — 동일 프로브 (`includeSystemInput: true`)
6. [x] 단위테스트 — `MatchesSuppressRoot` (라이브 WFP 비의존)
7. [x] docs: INDEX / CHANGELOG / architecture / implementation-notes / config-reference

## 검증

**Tier-1** (2026-07-24): `dotnet build` 0/0 · `publish` 5,094,400 B · `dotnet test` **117/117 PASS** · DllImport(cs) 0 · reviewer ✅.

**수동 smoke** (사용자)1. 탐색기 빈곳 우클릭 → 메인·커서 둘 다 숨김
2. 메모장 등 앱 우클릭 메뉴 위: 동일
3. 작업표시줄/바탕 우클릭 (Win11): 둘 다 숨김 (회귀)
4. 메뉴 닫은 직후: 메인 복귀·깜박임 없음
5. 메뉴 연 채 한/영 토글: 메뉴 위면 계속 숨김
6. 트레이 KoEnVue 메뉴: 회귀 없음
7. Start/Search: 메인 표시 · 커서 숨김
8. 탐색기 클릭 직후: FG 히스테리시스 깜박임 없음
9. clickDismiss 후 복귀·직후 메뉴: 정상
10. (비보장) Chrome 페이지 메뉴

## 대안 (기각·보류)

| 안 | 판정 |
|----|------|
| 목록에 `#32768`만 추가 | **기각** — 메인 잔류 |
| 우클릭 dismiss | **보류** |
| WinEvent 메뉴 훅 | **보류** (smoke 실패 시) |
| Core 풀 프로브 | **기각** (P6 — const만 Core) |
