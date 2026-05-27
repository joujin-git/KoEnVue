# 2026-05-27 — admin_elevation: UIPI 우회 통합 설계

**관련**: [improvement-plan/PR-15-admin-elevation.md](../improvement-plan/PR-15-admin-elevation.md) · [2026-05-27-cursor-indicator.md "확정 결론"](2026-05-27-cursor-indicator.md) · [2026-05-21-asinvoker-migration.md](2026-05-21-asinvoker-migration.md)

**Branch**: `fix/admin-elevation` (HEAD=95c1d36, 6 commits)
**Status**: 코드 완료, Tier-3 6-시나리오 사용자 검증 대기

---

## 1. 문제 — UIPI 메커니즘 확정

v0.9.3.0 [PR-03](../improvement-plan/PR-03-asinvoker.md) 의 `app.manifest` `requireAdministrator` → `asInvoker` BREAKING 전환의 부작용: **관리자 권한 콘솔 (관리자 cmd, 관리자 Windows Terminal) 의 한/영 IME 상태 미감지**.

메커니즘은 **UIPI (User Interface Privilege Isolation)** — Medium IL 프로세스 (asInvoker 로 부팅된 KoEnVue) 가 High IL 프로세스 (관리자 콘솔) 의 IME 입력 윈도우에 `WM_IME_CONTROL` 메시지를 보내면 OS 가 차단해 `SendMessageTimeoutW(SMTO_ABORTIFHUNG)` 가 즉시 ABORT 반환. ImeStatus 알고리즘 자체는 v0.9.2.8 과 동일 (`Win32Constants` → `ImeConstants` 단순 리네임만, `git diff v0.9.2.8..HEAD -- App/Detector/ImeStatus.cs` 확인) — 회귀의 진짜 원인은 매니페스트의 IL 변화.

[2026-05-27-cursor-indicator.md "확정 결론" L338-L378](2026-05-27-cursor-indicator.md) 의 진단 결과:

| KoEnVue IL | 대상 콘솔 IL | 결과 |
|-----------|-------------|------|
| Medium (asInvoker) | High (admin cmd) | **차단** (현재 회귀) |
| High (requireAdministrator, v0.9.2.8) | High (admin cmd) | OK |
| High | Medium (일반 cmd) | OK (High → Medium 자유) |
| Medium | Medium (일반 cmd / WT / 메모장) | OK |

PR-03 의 매 부팅 UAC 프롬프트 제거 정책은 유지하되, admin 콘솔을 자주 쓰는 사용자에게 선택지를 제공할 필요.

---

## 2. 분담 설계 — 단일 옵션 + 두 메커니즘

### 단일 config 키

`admin_elevation: bool` (default `false`) — top-level (Advanced 중첩이 아님). UI 노출 빈도 (트레이 + Settings 양쪽) + 사용자 인지도 (security 관련 옵션) 가 디폴트 끔 + 잘 안 만지는 Advanced 의 패턴과 다름.

매니페스트는 `asInvoker` 그대로 유지 — **P5 invariant 보존**. PR-03 의 "default UAC 0" 정책도 보존 (옵션 비활성이 default).

### 두 메커니즘

| 메커니즘 | 커버 경로 | UAC 빈도 | 책임 모듈 |
|---------|---------|---------|----------|
| 자체 elevation (self-relaunch) | 단일 실행 / 직접 실행 / 임시 실행 | UAC 1회 (사용자 클릭 시점) | [App/Bootstrap/AdminElevation](../../App/Bootstrap/AdminElevation.cs) |
| schtasks `<RunLevel>HighestAvailable</RunLevel>` | 부팅 자동 시작 | 등록 시 UAC 1회 + 부팅마다 0 | [App/Startup/StartupTaskManager](../../App/Startup/StartupTaskManager.cs) |

### UAC 빈도 매트릭스

| 옵션 | 부팅 자동 시작 | 단일 실행 (직접 클릭) |
|------|--------------|---------------------|
| `admin_elevation: false` (default) | UAC 0 | UAC 0 |
| `admin_elevation: true` | UAC 0 (schtasks 가 admin 토큰 자동 부여) | UAC 1회 (self-relaunch) |

자체 elevation 의 키는 `Advapi32.GetCurrentProcessIntegrityLevelRid()` — 이미 High IL (schtasks 경로 부팅) 이면 self-check skip → mutex 정상 획득. 두 메커니즘이 한 부팅에서 중복 발화하지 않음.

### Portable 영향

- **자체 elevation 만 사용** (단일 실행 on-demand 패턴): config 만 동행 → portable 100%. schtasks 불필요.
- **schtasks 추가** (부팅 자동 시작 패턴): schtasks 작업은 시스템 영역 — 새 PC 에서 재등록 1회 UAC.

---

## 3. 결정 5종 (사용자 결정 결과)

planner 산출 spec §9 의 미해결 질문 5종 — 모두 **권장안 채택**.

### 3.1 config 키 위치 — top-level (vs Advanced 중첩)

채택 이유: UI 노출 빈도 (트레이 + Settings 양쪽) + 사용자 인지도 (security 관련 옵션). Advanced 는 거의 안 만지는 옵션 (`force_topmost_interval_ms` 등) 그룹.

### 3.2 self-elevation 시점 — Mutex 획득 전 (vs 후)

채택 이유: 원본 인스턴스가 mutex 안 잡았으므로 race 0 + Dispose 보일러플레이트 0. 자식 (High IL) 이 깨끗하게 새로 `createdNew=true` 획득.

구현: `Program.MainImpl` 의 step 순서 재구성 — 기존 `0 → 0a → 1 (mutex) → ...` 에 `0b Settings.Load` + `0c AdminElevation.TryRelaunchAsAdmin` 삽입.

### 3.3 UAC 거부 fallback — (c) MessageBox 안내 후 일반 권한 계속

- (a) silent — 사용자가 "왜 admin 안 됐지?" 디버깅 불가, KoEnVue silent 정책 위반.
- (b) 종료 — admin 콘솔만 안 잡혀도 일반 콘솔/메모장은 정상. 전체 봉쇄는 과도.
- (c) 채택 — `MessageBoxW` 1회 안내 + 일반 권한 계속. modal 차단 ~2초, UAC 거부는 명시적 사용자 행위 (UAC 다이얼로그의 "아니요") 라 1회 알림 정당.

메시지 워딩: "관리자 권한 부여가 취소됐습니다. 일반 권한으로 계속 실행되며, 관리자 권한 콘솔 (관리자 cmd 등) 의 한/영 상태는 표시되지 않습니다. 다음에 적용하려면 트레이 메뉴에서 '관리자 권한으로 실행' 을 다시 켜고 재시작하세요."

### 3.4 재시작 UX — Tray YesNo MessageBox / Settings 사일런트

- **Tray 토글** — 즉시성 → `MessageBoxW(MB_YESNO)` "다음 실행부터 적용됩니다. 지금 재시작하시겠습니까?" YES 시 `AdminElevation.ClearReentryGuard()` + 자기 재실행.
- **Settings OK** — 다른 옵션과 함께 batch 저장하는 종료적 행위 → 별도 알림 없이 다음 부팅 시 자동 적용.

### 3.5 pre-Init 로그 — crash.txt 재사용 + 태그 분리

`AdminElevation.TryRelaunchAsAdmin` 은 `Logger.Initialize` 이전에 호출되고, ExitForChild 흐름에서는 Logger.Initialize 가 아예 안 됨 → pre-Init 버퍼 flush 불가 → 로그 손실.

채택: **별도 elevation.txt 만들지 않고 koenvue_crash.txt 재사용** + 태그 분리 (`ELEVATION` = INFO, `ELEVATION-ERR` = ERROR). 사용자 디스크에 파일이 하나 더 생기는 비용 회피, 사용자가 진단 자료 모을 때 한 파일만 보면 됨. `Program.AppendCrashFile` 가 PR-10 (G5) 부터 존재 — `internal` 로 노출해 AdminElevation 이 직접 호출.

---

## 4. 회귀 위험 (planner spec §9 리스크 5종)

### 4.1 UIPI 의 비대칭성 — High → Medium 자유

admin_elevation=true 인 KoEnVue (High IL) 가 일반 메모장 (Medium) 의 IME 잡는 것은 OK. 검증 매트릭스 §1 의 4 케이스 보존 — 비대칭성 자체가 UIPI 의 설계 사항이라 의도 부합.

### 4.2 ShellExecuteW("runas") 의 비동기성

Windows 가 UAC 다이얼로그 표시는 비동기, ShellExecuteW 반환은 다이얼로그 결과 받은 후 동기. 그러나 일부 환경 (UAC 비활성 시) 즉시 spawn. 원본의 `return Exit` 가 자식 부팅 완료 전이라 race 발생 가능 — 자식이 mutex 잡으려 할 때 원본이 아직 안 죽음.

**완화**: 원본 인스턴스가 mutex 잡은 적 없음 (step 0c 가 mutex 전) → race 영역 0. 자식이 즉시 `createdNew=true` 획득.

### 4.3 environment 변수 상속 실패

`ShellExecuteW` 의 standard `CreateProcess` 경로는 환경 변수 자동 상속이지만, 일부 shell extension 이 fresh environment block 으로 변형 가능 → `KOENVUE_ELEVATED` 가 자식에 전달 안 되면 UAC 거부 후 무한 루프 위험.

**완화**: 본 PR 에서는 환경 변수만 사용 — 회귀 발견 시 argv 플래그 (`--elevated`) 추가는 별도 PR. Belt-and-suspenders 는 적용 부담 + 의도 모호 (어느 채널이 정본인지) 이라 일단 환경 변수만.

### 4.4 schtasks `<RunLevel>` 변경이 즉시 반영 안 되는 경우

Task Scheduler 가 task 캐시. **완화**: `SyncStartupPathAsync` 의 기존 강제 재등록 패턴 그대로 사용 — delete + create 시퀀스 보장. `expectedRunLevel` 비교 후 mismatch 시 즉시 재등록.

### 4.5 사용자 혼란 — "관리자 권한으로 실행" 의미 모호

매 부팅 UAC 가 뜬다고 오해할 수 있음. **완화**: `I18n.MenuAdminElevationTooltip` 에 정확히 "자동 시작과 단일 실행 모두 적용. 단일 실행 시 UAC 1회, 자동 시작 시 UAC 없음." 명시.

---

## 5. Tier-3 검증 매트릭스 (6 시나리오)

planner spec §3.10 의 표 그대로.

| # | 시나리오 | 기대 |
|---|---------|-----|
| A | `admin_elevation: false` (default) + 정상 부팅 | UAC 0, 일반 권한 동작, 기존 PR-03 행동 그대로 |
| B | `admin_elevation: true` + 단일 실행 (직접 클릭) | UAC 1회 → 승인 → 인디 정상 + 관리자 cmd 한/영 표시 정상 |
| C | `admin_elevation: true` + 단일 실행 + UAC 거부 | `MessageBoxW` 표시 → OK → 일반 권한 계속 + 관리자 cmd 한/영 미표시 (예상 동작) |
| D | `admin_elevation: true` + 시작 프로그램 등록 후 재부팅 | UAC 0 (schtasks `/RL HIGHEST` + 자체 elevation 스킵), 관리자 cmd 한/영 정상 |
| E | `admin_elevation: false → true` 토글 + 시작 프로그램 등록 상태 | schtasks 자동 재등록 (RunLevel `HighestAvailable` 로 갱신), 재부팅 후 UAC 0 + admin 동작 |
| F | `admin_elevation: true → false` 토글 + 시작 프로그램 등록 상태 | schtasks 자동 재등록 (`LeastPrivilege` 복귀), 재부팅 후 UAC 0 + 일반 동작 (PR-03 디폴트) |

Tier-1 (build) + Tier-2 (invariant grep) 는 모든 PR 공통 — [docs/conventions.md §P6 verification](../conventions.md#p6-verification-invariants) 참조 + PR-15 신규 4종 (`ShellExecuteW.*runas` App/Bootstrap/ = 1, `GetTokenInformation` Core/Native/ = 1, `RunLevelHighestAvailable` App/Startup/ = 1, `requireAdministrator` app.manifest = 0 유지).

---

## 6. 관련 코드 + commit hash

| Commit | 파일 | 변경 요약 |
|--------|------|----------|
| [e0a9674](https://github.com/joujin-git/KoEnVue/commit/e0a9674) | `Core/Native/Advapi32.cs` (신규 95 LOC) + `Core/Native/Win32Types.cs` | P/Invoke 4 + `GetCurrentProcessIntegrityLevelRid()` + IL const 6 |
| [44b4d93](https://github.com/joujin-git/KoEnVue/commit/44b4d93) | `App/Models/AppConfig.cs` + `App/Config/DefaultConfig.cs` | `AdminElevation : bool` 키 top-level + 디폴트 `false` |
| [f6a51ef](https://github.com/joujin-git/KoEnVue/commit/f6a51ef) | `App/Bootstrap/AdminElevation.cs` (신규 180 LOC) + `App/Localization/I18n.cs` (5 키) + `Program.cs` (`AppendCrashFile` internal) | self-elevation 모듈 + ko/en 메시지 + crash.txt 채널 |
| [b60f556](https://github.com/joujin-git/KoEnVue/commit/b60f556) | `Program.cs` (`MainImpl` 재구성) | step 0b `Settings.Load` + step 0c `TryRelaunchAsAdmin` 호출 (mutex 전) |
| [e014772](https://github.com/joujin-git/KoEnVue/commit/e014772) | `App/Startup/StartupTaskManager.cs` + `Program.cs` + `App/UI/Tray.cs` | `RunLevel` 분기 + `ReregisterIfAdminChanged` 신규 + caller 변경 |
| [95c1d36](https://github.com/joujin-git/KoEnVue/commit/95c1d36) | `Core/Native/Win32Types.cs` (MB_YESNO/IDYES/IDNO) + `App/Bootstrap/AdminElevation.cs` (`ClearReentryGuard`) + `App/UI/Tray.cs` (IDM + case) + `App/UI/Tray.Menu.cs` + `App/UI/Dialogs/SettingsDialog.Fields.cs` | UI 배선 — 트레이 즉시 토글 + 재시작 안내 + Settings Bool |

**publish 크기**: 4,861,440 B = 4.64 MB (이전 baseline 4.62 MB + ~30 KB). PR-08 의 ~+100 KB / stage 가이드 안.

**branch**: `fix/admin-elevation` HEAD=95c1d36 → Commit 7 (본 dev-note + 문서 동기화 + CHANGELOG + 버전 bump 0.9.3.0 → 0.9.4.0) 가 마지막 commit. Tier-3 사용자 smoke 통과 후 main 머지 + tag v0.9.4.0 + GitHub Release.
