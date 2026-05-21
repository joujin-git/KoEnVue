# PR-09: Logging policy + ILogSink + catch 일관화

**Status**: ✅ Tier-1+2+3 통과 (FF 머지 대기)
**Branch**: feat/pr-09-logging-policy
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

6개 발견 묶음:

1. **E4**: Core 12개 파일이 `using KoEnVue.Core.Logging`에 강결합. Core 분리 시 drain thread/파일 로테이션 전체 끌고 와야 함. → 정적 `LogProvider.Sink?.X(...)`로 추상화.
2. **E5**: [LogLevel.cs](../../Core/Logging/LogLevel.cs)에 `[JsonStringEnumMemberName]` 의존. Core enum이 STJ에 종속. → JSON converter를 App으로 이동.
3. **F1**: Debug 레벨에서 "failed" 단어 사용 5곳 — [Win32DialogHelper.cs:164](../../Core/Windowing/Win32DialogHelper.cs#L164) 정책 코멘트와 모순.
4. **F3**: [Tray.cs:1100](../../App/UI/Tray.cs#L1100) bare `catch(Exception)` — OOM/ThreadAbort 흡수. (PR-04에서 PositionCleanupService로 이동 후라면 거기서)
5. **F4**: [HttpClientLite.cs:116](../../Core/Http/HttpClientLite.cs#L116) bare `catch(Exception)` — 좁히기.
6. **F5**: `Marshal.GetLastPInvokeError()` 호출 누락 — 부트 경로의 단발성 실패에 추가.

### N1 부트 순서 해결책 (E4 도입 시)
[Settings.Load](../../App/Config/Settings.cs)가 `JsonSettingsManager.Load`를 호출하고 그 안에서 `Logger.Info/Warning` 호출. **`Logger.Initialize`는 그 다음**.

→ `LogProvider.Sink`를 **bootstrap 첫 라인**에서 설정 강제. Sink가 `LoggerSink` wrapper면 내부적으로 Logger의 큐잉 메커니즘 활용.

## 2. 변경 범위 (What)

### 코드 — E4 ILogSink
- [ ] [Core/Logging/ILogSink.cs](../../Core/Logging/) 신규 — `interface ILogSink { void Debug(...); void Info(...); void Warning(...); void Error(...); }`
- [ ] [Core/Logging/LogProvider.cs](../../Core/Logging/) 신규 — `static class LogProvider { public static ILogSink? Sink { get; set; } }`
- [ ] Core 12 파일의 `using KoEnVue.Core.Logging; Logger.X(...)` → `LogProvider.Sink?.X(...)`로 일괄 치환:
  - LayeredOverlayBase, WindowProcessInfo, Win32DialogHelper, JsonSettingsManager, NotifyIconManager, HttpClientLite, 등
- [ ] [Core/Logging/Logger.cs](../../Core/Logging/Logger.cs)에 `ILogSink` 구현 추가:
  ```csharp
  public sealed class LoggerSink : ILogSink {
      public void Debug(string msg) => Logger.Debug(msg);
      // ...
  }
  ```
- [ ] [Program.cs:MainImpl](../../Program.cs)의 **첫 라인**에 `LogProvider.Sink = new LoggerSink();` 추가 (Mutex 획득 전)

### 코드 — E5 LogLevel STJ 분리
- [ ] [Core/Logging/LogLevel.cs](../../Core/Logging/LogLevel.cs)의 `[JsonStringEnumMemberName]` 제거 — plain enum
- [ ] [App/Models/AppConfig.cs](../../App/Models/AppConfig.cs) 또는 별도 `LogLevelJsonConverter` — `[JsonSerializable(typeof(LogLevel))]` 또는 converter 등록

### 코드 — F1 "failed" 워딩
- [ ] 다음 5+곳의 Debug 로그에서 "failed" 단어 제거 (다른 표현으로):
  - [NotifyIconManager.cs:88, 108, 131](../../Core/Tray/NotifyIconManager.cs#L88)
  - [WindowProcessInfo.cs:75](../../Core/Windowing/WindowProcessInfo.cs#L75)
  - [JsonSettingsManager.cs:157](../../Core/Config/JsonSettingsManager.cs#L157)

### 코드 — F3/F4 catch 좁히기
- [ ] [App/UI/Tray.cs:1100](../../App/UI/Tray.cs#L1100) (또는 PR-04 후 PositionCleanupService) — `catch (Win32Exception or InvalidOperationException)`
- [ ] [Core/Http/HttpClientLite.cs:116](../../Core/Http/HttpClientLite.cs#L116) — `catch (Win32Exception or IOException or ArgumentException or ObjectDisposedException or InvalidOperationException)` (보수적 enumerate)

### 코드 — F5 last-error 추가
- [ ] [Program.Bootstrap.cs:100-111](../../Program.Bootstrap.cs#L100) `CreateWindowExW` 실패 시 `Marshal.GetLastPInvokeError()` 포함
- [ ] [App/UI/Tray.cs:644, 667, 704](../../App/UI/Tray.cs#L644) (또는 PR-04 후 UriLauncher) — `ShellExecuteW` 실패 시 last-error 포함
- [ ] [Core/Tray/NotifyIconManager.cs:58](../../Core/Tray/NotifyIconManager.cs#L58) NIM_ADD 실패 시 last-error 포함

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed
- [ ] `docs/conventions.md` 정책 명시:
  - "Core는 Logger 직접 호출 금지 — `LogProvider.Sink?.X` 사용"
  - "Debug 로그는 'failed' 회피"
  - "Warning 이상은 last-error 포함 가능"
  - "catch는 타입 좁히기, bare Exception 금지 (justified case 외)"
- [ ] (선택) `docs/dev-notes/2026-05-21-log-policy-formalization.md`

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "Logger\." Core/` 0 매치 (Logger 클래스 자체 제외)
- [ ] `git grep "LogProvider\." Core/` 1+ 매치
- [ ] `git grep "ILogSink" Core/Logging/ILogSink.cs` 1 매치
- [ ] `git grep "Debug.*failed" Core/` 0 매치
- [ ] `git grep "catch (Exception" App/ Core/` — Mutex bare catch(이미 의도된) 외 0 매치
- [ ] `git grep "LogProvider.Sink = " Program.cs` 1 매치

### Tier 3 — 수동 smoke
- [ ] 정상 부팅 → koenvue.log에 정상 로그 기록
- [ ] LogLevel="DEBUG"로 설정 후 부팅 → Debug 라인이 "failed" 워딩 없이 기록
- [ ] (선택) 의도적 NIM_ADD 실패 유도(이미 등록된 GUID로) → Warning 로그에 last-error 코드 포함

## 4. 사이드 이펙트 / 위험

- **위험 1 (큼)**: 부트 순서. `LogProvider.Sink`가 set되기 전에 호출되면 silent loss. **검증**: Program.cs:MainImpl 첫 라인에 sink 설정 + `dotnet build` 후 grep으로 확인.
- **위험 2**: ILogSink가 null인 동안 호출 → null-conditional `?.X(...)`로 no-op. Logger.Shutdown 호출 후에도 sink가 살아있으면 늦은 로그가 큐에 쌓일 수 있음. → Shutdown에서 sink를 null로.
- **위험 3**: F4 좁히기 후 새 예외 유형 발생 시 unhandled. **대안**: justified bare catch + 명시적 코멘트 "// 정책 예외: WinHttp 영역의 광범위 흡수 의도".
- **위험 4**: F5의 last-error 추가는 호출 직후에만 의미. 다른 P/Invoke가 끼면 last-error 덮어쓰기. → `Marshal.GetLastPInvokeError()`를 실패 분기 즉시 첫 라인에서 캡처.

## 5. 롤백 절차

- 단순 revert 가능 (Y) — 단 변경 다중 파일. squash 권장.
- 데이터 영향 없음 (N) — log 포맷 변경뿐.

## 6. 세션 진행 로그

| Date | Session | What | Next |
|---|---|---|---|
| 2026-05-21 | 1 | E4+E5+F1+F3+F5 구현 + F4 명세-convention 충돌 해소. **E4**: 신규 `Core/Logging/ILogSink.cs` 인터페이스 (Debug/Info/Warning/Error 4 메서드) + `Core/Logging/LogProvider.cs` 정적 `Sink?` 단일 점 + `Logger.cs` 끝에 `LoggerSink : ILogSink` passthrough 구현. Core 6 파일(JsonSettingsManager / NotifyIconManager / UriLauncher / LayeredOverlayBase / Win32DialogHelper / WindowProcessInfo)의 18 호출 `Logger.X(...)` → `LogProvider.Sink?.X(...)` 일괄 치환. `Program.MainImpl:106` 첫 라인(Mutex 획득 전)에 `LogProvider.Sink = new LoggerSink();` 배선. **E4+ (스펙 보강)**: PR-06 Tier-3 ④ 의 "Settings.Load 가 발화하는 Warning 이 Logger.Initialize 이전이라 Trace-only" 한계를 같이 해소 — Logger.cs 에 `_preInitBuffer` (ConcurrentQueue<string>) + `_preInitDroppedCount` 추가, `EnqueueToFile` 가 `_drainThread is null` 일 때 본 버퍼로 우회, `Initialize` 가 drain 스레드 시작 직후 `FlushPreInitBuffer()` 호출해 옮겨 적기. 본 보강은 명세 N1 의 의도("Logger 의 큐잉 메커니즘 활용") 를 실제로 동작하게 만드는 보완. **E5**: `Core/Logging/LogLevel.cs` 에서 `[JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]` + 4개 `[JsonStringEnumMemberName]` 제거 — Core enum 이 STJ 의존 0. 신규 `App/Logging/LogLevelJsonConverter.cs` `JsonConverter<LogLevel>` 직접 구현 (Read case-insensitive, Write 대문자, 정의 외 JsonException throw). AppConfig.LogLevel 속성에 `[JsonConverter(typeof(LogLevelJsonConverter))]` 부착. **F1**: Core Debug 5 라인의 "failed" 워딩 회피 — NotifyIconManager 3 (NIM_MODIFY icon/tooltip/icon+tooltip) "skipped — shell rejected update", WindowProcessInfo.GetProcessName "skipped (access denied or terminated)", JsonSettingsManager.CheckReload "mtime probe skipped (file locked or restricted)". **F3**: PositionCleanupService.CollectRunningProcessNames 바깥 catch 를 `when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)` 로 narrow + 주석. **F4**: HttpClientLite.cs:116 의 `catch (Exception)` 는 conventions.md §2 가 justified-bare-catch 예시로 명시 — narrow 대신 현 catch + 주석 유지하기로 결정 (명세 < convention 우선). **F5**: 3 군 last-error 추가 — Program.Bootstrap.CreateMainWindow (CreateWindowExW 실패 시 `error=` 필드), UriLauncher.Open (ShellExecuteW rc<=32 시 `error=` 추가), NotifyIconManager NIM_ADD/NIM_SETVERSION (실패 시 `error=`). Tier-1 debug + AOT publish clean (0 경고, 4.80 MB — vs 4.82 baseline -16 KB, JsonStringEnumConverter<LogLevel> 메타가 user converter 로 교체). Tier-2 grep 가드 6종 통과: `Logger.X` in Core = 4 매치(LoggerSink passthrough 만), `LogProvider.` in Core 17, `interface ILogSink` 1, `Sink?.Debug.*failed` 0, `catch (Exception` justified-3 (SystemFilter:58/165, HttpClientLite:116, UpdateChecker:109) 외 모두 narrowed, `LogProvider.Sink = ` 1. invariant 4종 + P5 2종 0매치. 문서 4건 갱신 (CHANGELOG / conventions §8+§9 신규 + Detection loop catch 가 §10 으로 번호 이동 / PR-09 §1+§6 / 본 §6). | Tier-3 사용자 smoke (정상 부팅 / "language":"fr" 편집 후 koenvue.log Warning 가시화 / Debug 레벨 부팅) 검증 후 머지 |
| 2026-05-21 | 2 | Tier-3 사용자 가시 smoke 3종 통과 (로그 직접 확인). ① 정상 부팅 (16:58 / 17:01 등 다중 부팅) INFO 라인 정상. ② Debug 레벨 (16:59:51) — `Window class registered`/`PositionUpdated`/`IME state`/`UpdateChecker: GET` 등 풀 DEBUG 라인 가시 + "failed" 단어 부재 확인 (F1 통과). ③ `"language": "fr"` (17:01:45.891) — `[WARN] Failed to load config from ...: DeserializeUnableToConvertValue, AppConfig Path: $.language ... Using defaults without overwriting.` 라인이 koenvue.log 에 정상 기록 + 그 다음 `KoEnVue starting` 으로 디폴트 폴백 진입. PR-06 시점엔 Trace-only 라 absent 였던 evidence 가 pre-Init 버퍼로 가시화. FF merge to main 진행. | 브랜치 삭제 후 다음 PR (07/08/10/11 자유 선택) |
