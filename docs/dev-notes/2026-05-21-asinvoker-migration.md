# 2026-05-21 — asInvoker 마이그레이션 (PR-03)

본 노트는 PR-03 (v0.9.3.0 BREAKING — `app.manifest requireAdministrator → asInvoker`) 의 의식적 결정과 깨질 수 있는 워크플로우, fallback 디자인 근거를 기록한다. 후속 세션이 같은 트레이드오프를 다시 평가하지 않도록.

## 무엇

| 영역 | v0.9.x | v0.9.3.0 (PR-03) |
|------|--------|-----------------|
| `app.manifest:requestedExecutionLevel` | `requireAdministrator` | `asInvoker` |
| schtasks `<RunLevel>` | `HighestAvailable` | `LeastPrivilege` |
| `config.json` / `koenvue.log` 위치 | `BaseDirectory\` 단일 | `BaseDirectory\` → `%LOCALAPPDATA%\KoEnVue\` fallback |
| `log_file_path` 검증 | 없음 (Admin 권한이 임의 경로 허용) | `PortablePath.SanitizeLogPath` (BaseDirectory / `%LOCALAPPDATA%\KoEnVue` 외 거부) |
| schtasks XML temp path | `koenvue-task-{ProcessId}.xml` + `File.WriteAllText` | `koenvue-task-{Guid:N}.xml` + `FileMode.CreateNew` + `FileShare.None` |
| Tray.cs `OpenUpdatePage` URL prefix 화이트리스트 | 유지 (Admin EoP 방어 표현) | 유지 (사용자 컨텍스트 임의 핸들러 방어로 정당화 갱신) |

새 모듈: [`App/Config/PortablePath.cs`](../../App/Config/PortablePath.cs) — write-probe 캐시 기반 경로 결정 + log path sanitize.

## 왜

### asInvoker 가 정당한 이유

- **인디케이터 / IME 감지 / WinEventHook**: 사용자 권한으로 충분. `ImmGetDefaultIMEWnd` / `SendMessageTimeoutW` / `SetWinEventHook` 모두 same-session same-user 호출이라 elevation 불요.
- **`WTSRegisterSessionNotification`**: 자체 세션에 등록만 — 다른 세션을 보지 않는다.
- **`schtasks /sc onlogon`**: 사용자별 task 라 `LeastPrivilege` 등록 가능. `HighestAvailable` 은 사용자가 Admin 일 때 매 부팅 UAC 트리거하는 게 본질적 단점이었음.
- **`config.json` write**: user-writable 위치에 두면 충분. asInvoker 가 강제되는 시점부터 `Program Files` 같은 위치는 자동 fallback.

### 보안 표면 변화

| 위험 (4-라운드 리뷰) | v0.9.x 상태 | v0.9.3.0 후 |
|---------------------|-------------|------------|
| B1 LogFilePath 임의 write (시스템 폴더 지정 가능) | Admin 토큰으로 임의 경로 write | Admin 토큰 없음 + `SanitizeLogPath` 가 허용 루트 외 거부 |
| B2 schtasks XML symlink TOCTOU (`%TEMP%` 사전 점유) | Admin 토큰으로 임의 위치 write | Admin 토큰 없음 + `Guid:N` 32자 hex + `CreateNew + FileShare.None` |
| B5 Admin notepad (트레이 "설정 파일 열기" 시 elevated 메모장) | High IL 메모장 → 사용자 입력 EoP 잠재 | 메모장이 사용자 IL 로 기동 — 자연 해소 |

### Fallback 디자인 — 왜 `%LOCALAPPDATA%` 인가

- **`AppData\Roaming`**: 도메인 동기화 대상이라 트레이 위치 / 인디 위치 같은 머신-로컬 상태에 부적합. 동기화 충돌도 발생 가능.
- **`AppData\LocalLow`**: low-IL 프로세스용. 본 앱은 medium IL.
- **`%LOCALAPPDATA%` (= `AppData\Local`)**: 사용자별 격리 + 동기화 안 됨 + medium IL writable. 정확히 우리 시나리오.

write-probe 방식 채택 이유: ACL inspection 은 Windows 에서 복잡(SID + DACL + `AuthzAccessCheck`)하고 false-positive 가 잦다. `File.Create` + `Delete` 시도 후 `UnauthorizedAccessException` / `IOException` 잡기가 가장 직접적인 신호. 프로세스 수명 동안 캐시해 매 호출 IO 비용 없앰.

### 마이그레이션 — 왜 BaseDirectory 우선

v0.9.x 사용자는 `BaseDirectory\config.json` 에 자신의 설정을 갖고 있다. `PortablePath.ResolveFile` 의 우선순위 1번이 `File.Exists(baseTarget)` 인 이유 — 업그레이드 후에도 같은 경로를 계속 쓰도록 보장. 시나리오 C 통과 조건.

새 사용자가 `Program Files` 같은 위치에 처음 설치하면 BaseDirectory 에 파일이 없으니 (write-probe 실패 → fallback) `%LOCALAPPDATA%\KoEnVue\config.json` 으로 새로 만들어진다.

## 대안 (채택 안 함)

### A. fallback 없이 그냥 BaseDirectory 만 쓰기
- v0.9.x 호환은 OK 지만, Program Files 설치 시 첫 실행 시 IOException → 침묵 종료. 사용자 경험이 안 좋음.

### B. 항상 `%LOCALAPPDATA%` 만 쓰기
- 포터블 시나리오 (USB 등) 가 깨짐 — 사용자가 USB 를 다른 PC 에 꽂으면 `%LOCALAPPDATA%` 가 비어 있어 매번 설정을 다시 함.

### C. `config.json` 위치를 user 가 환경변수로 명시
- 복잡도 폭증. 트레이 메뉴 "설정 파일 열기" 가 어디를 여는지 헷갈림.

### D. v0.9.3.0 에 `asInvoker` 변경만 하고 fallback 없이 출시
- `Program Files` 사용자가 첫 부팅에 죽음. 회귀 신고 폭증 예상.

### E. `Mutex` / Process 이름으로 elevation 자체 감지 후 분기
- asInvoker 가 항상 사용자 권한이라 분기점 자체가 없어짐 — 의미 0.

## 깨질 수 있는 워크플로우

| 시나리오 | 영향 | 대응 |
|----------|------|------|
| `Program Files\KoEnVue\` 설치 + `config.json` 직접 편집 | 사용자가 BaseDirectory 의 `config.json` 을 편집해도 무시됨 (활성 경로는 `%LOCALAPPDATA%\KoEnVue\config.json`). 트레이 "설정 파일 열기" 가 실제 활성 경로를 여니까 정상 경로로 안내됨. | README + User_Guide 에 명시 |
| v0.9.3.0 → v0.9.x 다운그레이드 | schtasks 항목이 `LeastPrivilege` 상태라 v0.9.x 의 `requireAdministrator` 와 충돌 — 부팅 시 UAC 요구 | 사용자가 트레이에서 시작 등록 토글 (해제 → 재등록) |
| 일부 EDR / antivirus 가 `%LOCALAPPDATA%\KoEnVue\` write 차단 | 드문 케이스. fallback 경로 IO 실패 | User_Guide 에 예외 등록 안내. SmartScreen 은 잔존 (PR-11 SHA256 게시로 별도 완화) |
| symlink 가 `Path.GetTempPath()` 에 미리 박혀 있는 환경 | `CreateNew` 가 IOException → schtasks 등록 실패 + Warning | 외부 침입 신호. 기능보다 양식 차원의 안전 우선 |
| 사용자가 `log_file_path` 에 외부 폴더 지정 | `SanitizeLogPath` 가 거부 + default fallback + `Logger.Warning` reissue (koenvue.log 에 기록) | 정상 동작. config 키 docs 에 허용 범위 명시 |

## 회귀 위험

- **PR-13 (per-app rendering wiring) 과의 상호작용**: PR-13 이 도입한 `ResolveCurrent()` 경로는 `_config` 가 아닌 `resolved` AppConfig 를 쓰지만 `LogFilePath` 같은 시스템 키는 글로벌 `_config` 만 사용 — 충돌 없음.
- **PR-14 (DWM colorization) 과의 상호작용**: 무관.
- **Logger 초기화 순서**: `Settings.Load()` → `Logger.SetLevel` → `PortablePath.SanitizeLogPath` → `Logger.Initialize` → reject reason 이 있으면 `Logger.Warning` reissue. Validate 단계의 Trace-only Warning 과 달리 본 reissue 는 Initialize 후라 파일에 정확히 기록됨. Tier-3 시나리오 E 검증 가능.
- **Hot reload 경로 (`HandleConfigChanged`)**: 같은 sanitize 로직 적용. config 핫 리로드로 `log_file_path` 가 invalid 로 바뀌어도 즉시 거부 + 기존 로그 경로 유지.

## 측정 계획

운영 중 자연 발생 감지:
- `Logger.Warning("log_file_path … rejected")` — 사용자가 외부 경로 지정 빈도
- `Logger.Warning("Startup task out of sync … runlevel='HighestAvailable'")` — v0.9.x → v0.9.3.0 업그레이드 자동 마이그레이션 발생 빈도
- BaseDirectory write-probe 실패 (Program Files 설치 빈도) — 추가 카운터는 두지 않음 (관심도 낮음)

## 검증 (PR-03 §3 Tier-1 / Tier-2 / Tier-3)

Tier-1 / Tier-2 grep 가드는 PR 명세 §3 참고. Tier-3 다중 시나리오 (A~F) 는 사용자 검증 — 본 노트는 결과를 별도로 적지 않고 PR-03 명세 §6 세션 진행 로그에 누적.

## 사후 발견 — schtasks `<LogonTrigger><UserId>` 누락 회귀 (2026-05-21 Tier-3 D)

PR-03 코드 + 문서 + Tier-1/2 통과 후 사용자 Tier-3 시나리오 D (schtasks 시작 프로그램 등록) 에서 회귀가 드러남. 트레이 클릭 후 메뉴 다시 열어도 체크 표시 없음. `RunSchtasks` 가 ExitCode 를 무시하던 (v0.9.x 부터의 silent 표면) 결함이 asInvoker 전환과 결합해 처음으로 사용자에게 노출.

### 진단 흐름

1. `RunSchtasks` 에 ExitCode + STDOUT + STDERR 로깅 + `bool` 반환 추가, `ToggleStartupRegistration` 에 post-check (`IsStartupRegistered()` 재호출) — silent 실패 가시화.
2. 첫 재시도 로그: `[WARN] schtasks /create ... ExitCode=1, stderr=오류: 액세스가 거부되었습니다.`
3. **1차 가설** — Principal `<UserId>{domain}\{user}</UserId>` 가 schtasks 의 SID lookup 검증에서 admin 요구. → Principal `<UserId>` 제거 후 재시도. **결과: 여전히 거부.**
4. **2차 가설** — `<LogonTrigger>` 가 `<UserId>` 없이 비면 schtasks 는 그걸 "**모든** 사용자 로그온 시 발화" 로 해석해 admin 권한 요구. v0.9.x 의 `requireAdministrator` admin 토큰이 ANY-user trigger 등록을 통과시켜 줬을 뿐. → LogonTrigger 안에 `<UserId>{Environment.UserDomainName}\{Environment.UserName}</UserId>` 추가. **결과: `[INFO] Startup registration created`, 메뉴 체크 정상.**

### 두 `<UserId>` 의 의도 차이 — 둘 다 채우면 admin 회귀

| XML 위치 | 의도 | asInvoker 영향 |
|---------|------|---------------|
| `<LogonTrigger><UserId>...</UserId>` | **trigger 대상** user — 본인 logon 만 발화 | **필수**. 비면 ANY-user trigger 로 해석되어 admin 요구 |
| `<Principal><UserId>...</UserId>` | task **실행** user — SID lookup 검증 | **비워둠**. 명시 시 SID lookup 검증에서 admin 요구 |

LogonTrigger 쪽만 채우고 Principal 쪽은 비워두면 schtasks 가 task 생성 시 current user 의 SID 로 Principal 을 자동 채워 권한 검증을 우회하면서, trigger 는 본인 logon 으로 정확히 좁혀짐.

### 회귀 방지 — `RunSchtasks` 가시화는 유지

silent ExitCode 무시는 v0.9.x 부터의 잠재 결함이라 asInvoker 와 무관하게 유지 가치가 있음. ExitCode + STDERR + post-check 패치는 본 PR-03 안에 함께 commit 되어 다음 회귀가 같은 함정에 빠지지 않도록 함.

### 사이드 — SyncStartupPathCore 의 silent skip

`SyncStartupPathCore` 가 `QueryRegisteredTask` 호출 후 ExitCode != 0 면 `(null, null, null)` 반환 → "미등록" 으로 처리해 early-return. user 권한이 admin-잔재 task 를 query 하지 못하는 케이스에서도 silent skip 되어 부팅 시 자동 마이그레이션이 우회 가능. 본 PR 의 회귀 케이스는 admin 잔재가 아니라 *새 등록 자체가 거부* 였으므로 영향 없었지만, 향후 fix 후보로 기록.

## 관련 메모

- [`docs/improvement-plan/PR-03-asinvoker.md`](../improvement-plan/PR-03-asinvoker.md) — PR 명세 본체
- [`docs/improvement-plan/DECISIONS.md`](../improvement-plan/DECISIONS.md) §"PR-03 BREAKING" — 의식적 결정 기록
- [`docs/dev-notes/2026-05-21-mutex-abandoned-handling.md`](2026-05-21-mutex-abandoned-handling.md) — PR-00 (PR-03 전 안전 게이트 첫 단계)
