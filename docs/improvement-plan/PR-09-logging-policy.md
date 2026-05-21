# PR-09: Logging policy + ILogSink + catch 일관화

**Status**: ⏳ pending
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

(empty)
