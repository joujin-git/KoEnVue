# PR-03: asInvoker migration (BREAKING — v0.9.3.0)

**Status**: ⏳ pending
**Branch**: feat/pr-03-asinvoker
**Base**: main (PR-00/01/02 머지 후 권장)
**Risk**: **High** (가장 큰 정책 변경. CLAUDE.md P5 invariant 갱신)
**Estimated session size**: L (반나절+, 다중 세션 가능)

## 1. 목적 (Why)

본 리뷰의 가장 큰 정책 변경. 앱이 실제로 Admin 권한이 필요 없음:

- 인디케이터/IME 감지/WinEvent 훅: 사용자 권한으로 충분
- WTSRegisterSessionNotification: 자체 세션, 권한 불요
- schtasks `/sc onlogon`: 사용자별 task, AsInvoker 등록 가능 (XML의 `RunLevel`을 `LeastPrivilege`로)
- config.json write: user-writable 위치에 두면 충분

현재 `requireAdministrator`([app.manifest:6](../../app.manifest#L6))는 보안 표면(B1/B2/B3/B5)을 모두 만들고 UX(UAC 매번)을 해친다. **`asInvoker` 채택 시 B-시리즈 보안 위험이 자연 해소**.

연쇄 효과:
- B1 LogFilePath sanitize: Admin 토큰 없으므로 위협 모델 변경. 잔여 sanitize만 적용 (path traversal 방지 + `%LOCALAPPDATA%` 하위 강제 옵션).
- B2 schtasks symlink: Admin 토큰 없으므로 위협 사라짐. 단 CreateNew 플래그는 양식상 추가.
- B5 Admin notepad: 자연 해소.

## 2. 변경 범위 (What)

### 코드
- [ ] [app.manifest:6](../../app.manifest#L6) `requestedExecutionLevel`을 `requireAdministrator` → `asInvoker`로 변경
- [ ] schtasks XML의 `RunLevel`을 `HighestAvailable` → `LeastPrivilege`로 변경. [Tray.cs:898](../../App/UI/Tray.cs#L898) (또는 PR-04 후 StartupTaskManager) 위치.
- [ ] **`%LOCALAPPDATA%` fallback 도입**:
  - [App/Config/DefaultConfig.cs](../../App/Config/DefaultConfig.cs)에 `ConfigSearchPaths()` 메서드 추가: `[BaseDirectory, %LOCALAPPDATA%\KoEnVue]` 순서로 검색. 둘 다 없으면 첫 번째 write-able 위치를 사용.
  - [App/Config/Settings.cs](../../App/Config/Settings.cs)의 config 파일 경로 결정 로직 갱신
  - [Core/Logging/Logger.cs:53-65](../../Core/Logging/Logger.cs#L53) — 기본 로그 경로도 같은 fallback
- [ ] **B1 잔여 LogFilePath sanitize**: [Logger.cs](../../Core/Logging/Logger.cs)의 `Initialize`에서 `Path.GetFullPath` 후 BaseDirectory 또는 `%LOCALAPPDATA%\KoEnVue` 하위인지 검증. 위반 시 default로 fallback + Logger.Warning.
- [ ] **B2 schtasks XML CreateNew**: [Tray.cs:865](../../App/UI/Tray.cs#L865) `File.WriteAllText`를 `new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)` + `StreamWriter`로 교체. 또는 PR-04의 StartupTaskManager에서 처리.
- [ ] [App/UI/Tray.cs:861](../../App/UI/Tray.cs#L861) tempPath 파일명을 `Path.GetRandomFileName()` 또는 GUID 기반으로 unpredictable화.
- [ ] **CLAUDE.md 갱신**: P5 invariant 갱신
  ```
  | **P5** | app.manifest UAC asInvoker. system-위치 설치 시 %LOCALAPPDATA%\KoEnVue 자동 fallback |
  ```
  + verification invariant 추가:
  ```bash
  git grep "requireAdministrator" app.manifest   # 0 매치
  git grep "RunLevel.*HighestAvailable" App/     # 0 매치
  ```

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / **Changed (BREAKING)** + Security에 항목
- [ ] `docs/conventions.md:17` P5 규칙 갱신
- [ ] `docs/KoEnVue_PRD.md:247` 갱신 — "exe 폴더 쓰기 가능" 가정 제거, asInvoker + fallback 명시
- [ ] `README.md`에 "Program Files에 두지 마세요. %USERPROFILE%, USB 등 user-writable 위치 권장" 가이드
- [ ] `docs/User_Guide.md` 갱신 — UAC 프롬프트 사라짐 명시
- [ ] `docs/dev-notes/2026-05-21-asinvoker-migration.md` 신규 — 검증한 시나리오, 깨질 수 있는 워크플로우, fallback 디자인 근거

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드 (CLAUDE.md 신규 항목)
- [ ] `git grep "requireAdministrator" app.manifest` **0 매치**
- [ ] `git grep "RunLevel.*HighestAvailable" App/` **0 매치**
- [ ] `git grep "asInvoker" app.manifest` 1 매치
- [ ] `git grep "LOCALAPPDATA" App/Config/` 1+ 매치

### Tier 3 — 수동 smoke (필수, 다중 시나리오)
- [ ] **시나리오 A**: 정상 사용자 폴더(예: `%USERPROFILE%\Desktop\KoEnVue\`)에서 부팅 — UAC 프롬프트 없음, 트레이 정상, config.json 같은 폴더에 생성
- [ ] **시나리오 B**: `Program Files`(`C:\Program Files\KoEnVue\`)에서 부팅 — UAC 없음, write 실패 후 `%LOCALAPPDATA%\KoEnVue\config.json`에 생성
- [ ] **시나리오 C**: 기존 사용자가 v0.9.x에서 v0.9.3.0 업그레이드 — 기존 `config.json`이 BaseDirectory에 있으면 그대로 사용
- [ ] **시나리오 D**: schtasks 시작 프로그램 등록 — UAC 없이 가능, task가 `LeastPrivilege`로 실행, 로그인 시 자동 시작 확인
- [ ] **시나리오 E**: `config.json`에 `"log_file_path": "C:\\Windows\\evil.log"` 설정 → sanitize 후 default fallback + warning 로그
- [ ] **시나리오 F**: schtasks XML 일시 파일이 `Path.GetRandomFileName()` 사용 확인

## 4. 사이드 이펙트 / 위험

- **위험 1 (큼)**: 기존 사용자가 Program Files 등에 설치한 경우 BaseDirectory write 실패 → fallback. 사용자가 어디에 config가 있는지 헷갈릴 수 있음. README에 명시 + 트레이 메뉴 "설정 파일 열기"가 실제 사용 중 경로를 열도록 보장.
- **위험 2**: schtasks LeastPrivilege로 등록된 task는 기존 HighestAvailable task와 별개. 업그레이드 시 기존 등록을 삭제 후 재등록 필요. PR에서 `SyncStartupPathCore` 로직 확장 — XML의 `RunLevel` 변경 감지 시 재등록.
- **위험 3**: SmartScreen은 잔존 (PR-11에서 SHA256 게시로 완화).
- **위험 4**: 일부 antivirus가 `%LOCALAPPDATA%\KoEnVue\` 쓰기를 차단할 수 있음. 매우 드문 케이스. 사용자 가이드에 예외 등록 안내.

## 5. 롤백 절차

- **부분 revert 가능**: app.manifest만 revert해도 동작. 단 schtasks XML 변경은 별도 revert 필요.
- **데이터 영향**: 기존 사용자의 schtasks task가 RunLevel 변경됨. v0.9.3.0 → v0.9.x 다운그레이드 시 사용자가 직접 schtasks `/delete` + 재등록 필요.
- **CHANGELOG**: BREAKING 명시 필수.

## 6. 세션 진행 로그

| Date | What happened | Next |
|------|---------------|------|
| 2026-05-21 | 코드 + 문서 일괄 구현. app.manifest asInvoker 전환, App/Config/PortablePath.cs 신규 (write-probe 캐시 + BaseDirectory ↔ %LOCALAPPDATA%\KoEnVue fallback + SanitizeLogPath), Settings.Load/Save 가 PortablePath.ResolveConfigPath 사용, Program.cs Logger.Initialize 두 호출처에 SanitizeLogPath + reject-reason reissue, Tray.cs RunLevel LeastPrivilege + tempPath Guid:N + FileMode.CreateNew + FileShare.None + SyncStartupPathCore 의 RunLevel 동기화 (HighestAvailable 잔재 자동 재등록), ShellExecute prefix 검증 주석 정당화 갱신. CLAUDE.md P5 갱신 + invariant 2종 추가. 문서 6건 (CHANGELOG / conventions / PRD §4.3·§5.1·§§6 빌드·§§9 완전삭제·§보안 / README / User_Guide / implementation-notes §config-location·§manifest) + dev-notes 신규. Tier-1 debug build 0 경고 / 0 오류 + AOT publish clean (4.48 MB, +4 KB). Tier-2 grep 가드 4종 통과 (코멘트 단어 우회 정정 후) — `requireAdministrator` app.manifest 0매치, `RunLevel.*HighestAvailable` App/ 0매치, `asInvoker` app.manifest 3매치, `LOCALAPPDATA` App/Config/ 2매치. invariant 4종 0매치. | 사용자 Tier-3 다중 시나리오 (A~F) smoke 검증 |
| 2026-05-21 | Tier-3 시나리오 D (schtasks 등록) 회귀 발견 — 트레이 "시작 프로그램 등록" 클릭 후 메뉴 다시 열어도 체크 표시 없음. `bin/Release/.../publish/KoEnVue.exe` 사용. 진단 위해 두 단계 fix: ①`RunSchtasks` 가 ExitCode/STDOUT/STDERR 로깅 + bool 반환, `ToggleStartupRegistration` 에 post-check 추가 — silent 실패 가시화. ②root cause 확정: schtasks 가 `ExitCode=1, stderr=오류: 액세스가 거부되었습니다.` 반환. 1차 가설(Principal `<UserId>` 가 admin 검증 트리거) 으로 `<UserId>` 제거 → 여전히 거부. 2차 가설(LogonTrigger `<UserId>` 부재 → ANY-user trigger 로 해석 → admin 요구) 확정. `<LogonTrigger>` 안에 `<UserId>{Environment.UserDomainName}\{Environment.UserName}</UserId>` 추가, Principal `<UserId>` 는 그대로 비워둠 (두 필드의 의도 완전히 다름 — trigger 대상 vs 실행 user, 둘 다 채우면 admin 회귀). AOT 재빌드 후 사용자 재시도 → `[INFO] Startup registration created` post-check 통과 확인, 메뉴 체크 표시 정상. v0.9.x admin 토큰이 ANY-user trigger 등록을 통과시켜 줬을 뿐이고 PR-03 회귀의 진짜 root cause 는 LogonTrigger UserId 누락. BuildStartupTaskXml docstring 에 두 `UserId` 의 의도 차이 명시. | dev-notes 사후 절 + CHANGELOG fix 인라인 추가 + Tier-1/2 재검증 |
