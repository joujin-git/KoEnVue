# AbandonedMutexException 안전망 — 도입 결정 (2026-05-21)

> **결과**: `Program.Bootstrap.TryAcquireMutex` 에 `catch (AbandonedMutexException)` 1건 추가. PR-00 ([docs/improvement-plan/PR-00-mutex-abandoned.md](../improvement-plan/PR-00-mutex-abandoned.md)).

## 무엇 (What)

기존 `TryAcquireMutex` 는 `new Mutex(true, DefaultConfig.MutexName, out createdNew)` 한 줄로 명명된 mutex 를 만들고 `createdNew` 분기만 본다. 이전 인스턴스가 비정상 종료해 OS 가 `WAIT_ABANDONED` 를 통지하는 케이스는 catch 가 없어 예외가 `Program.Main` 의 마지막 try 까지 buble-up → `koenvue_crash.txt` 로만 기록되고 프로세스는 즉시 종료. 사용자 관점에서 "한 번 비정상 종료된 뒤 KoEnVue 재실행이 안 됨" 의 형태.

수정: `try { … } catch (AbandonedMutexException ex)` 만 추가. catch 본체는 (1) `Logger.Warning` 1줄, (2) `_mutex ??= new Mutex(true, name, out _)` 안전망, (3) `return true` 로 정상 부팅 경로 진입. `_mutex.ReleaseMutex` 호출은 추가하지 않는다 — 어차피 ProcessExit 까지 보유.

## 왜 (Why)

### 잠재 deadlock 의 실측 가능성

명명된 Win32 mutex 의 `WAIT_ABANDONED` 는 [MSDN WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject) 가 명시한 표준 동작 — 소유 스레드가 `ReleaseMutex` 없이 사라지면 OS 가 mutex 상태를 inconsistent 로 마킹, 다음 waiter 에게 `WAIT_ABANDONED_0` 반환. .NET 의 `Mutex(initiallyOwned: true, name, out _)` 생성자는 내부적으로 `OpenExistingMutex` + `WaitOne` 시퀀스에 해당하므로 같은 경로 노출.

실측 trigger: 사용자가 `taskkill /f /pid <pid>` 또는 작업 관리자 "작업 끝내기" 로 KoEnVue 를 강제 종료 → 즉시 재실행. 100% 재현은 아니지만 (OS 가 GC 시점에 mutex slot 을 청소하기도 함) 잠재 결함으로 처리.

### 왜 PR-00 인가 — 우선순위

본 PR 은 외부 가시 동작 0건, 코드 변경 5줄, 빌드 영향 0건. improvement-plan 의 13-PR 시퀀스에서 가장 안전 & 가장 작은 unit 으로 워밍업 + Tier-1 ~ Tier-3 검증 절차의 동작 확인을 겸한다. PR-03 (BREAKING `requireAdministrator → asInvoker`) 전 안전 게이트 (PR-00 ~ 02) 의 첫 단계.

### `[STAThread]` 와의 무관

`Mutex` 생성자는 STA/MTA 무관 — Win32 mutex 는 thread affinity 만 가지고 apartment 와는 별개. 본 변경이 `[STAThread]` 메인 스레드 진입 직후라 thread affinity 일관성 유지.

## 대안 (Alternatives considered)

### A. `WaitOne(0)` 명시 호출로 owning 의도 외부화

```csharp
_mutex = new Mutex(false, DefaultConfig.MutexName, out bool createdNew);
try { _mutex.WaitOne(0); } catch (AbandonedMutexException) { … }
```

**기각**: 생성자에 `initiallyOwned: true` 의도가 사라지면서 두 줄 분리. createdNew 의미가 흐려지고 race window (생성 ↔ WaitOne 사이) 도 미세하게 늘어남. catch 위치만 옮길 뿐 본질은 동일.

### B. `Mutex.OpenExisting` + `WaitOne` 우선 시도

기존 mutex 있으면 take, 없으면 create — 2단계 분기. NotifyExistingInstance 분기를 위한 createdNew 정보는 똑같이 필요하니 단순화 효과 없음. AbandonedMutexException 처리는 여전히 필요.

### C. mutex 사용 자체 폐기 → Named pipe / 윈도우 클래스 atomic 등록

`User32.RegisterClassExW` + 단일 BOOL 반환을 instance gate 로 쓰는 패턴은 .NET / NativeAOT 에서 가능하지만 (a) 기존 NotifyExistingInstance 가 `FindWindowW(MainClassName, null)` 로 클래스명에 의존하는데 그 자체로 instance gate 가 되면 race window 가 더 미묘함, (b) 기존 mutex 기반 로직이 정상 동작하는 한 변경 비용 > 효익. 별도 PR 로 검토 대상조차 안 됨.

### D. catch 없이 두 번째 인스턴스 자동 종료 유지

현재 동작 (예외 → crash 로그 → 종료) 도 의미상 "두 번째 인스턴스는 시작 안 함" 이라 deadlock 은 아님. 다만 사용자 체감은 "재실행 실패" 라 동일한 부정 경험. 로그가 `Logger.Error($"Fatal: ...")` 로 남으므로 운영 가시성은 있지만 의도적 동작이 아닌 사고. catch 1건이 더 정확한 의도 표현.

## 회귀 위험

### 위험 R1 — catch 본체 내 재예외

`_mutex ??= new Mutex(true, MutexName, out _)` 가 다시 AbandonedMutexException 을 throw 할 수 있다. catch 안에서 throw 되면 외부 `Main` 의 catch 가 흡수해 기존 동작 (crash 로그 후 종료) 과 동일. 즉 **악화 없음**. 정상 케이스 (.NET 7+ 에서 첫 throw 후 OS 가 mutex 상태를 reset 함) 는 두 번째 `new Mutex` 가 createdNew=false 로 정상 반환 → `??=` 가 작동하지 않음 (이미 set) 또는 작동하더라도 valid wrapper.

### 위험 R2 — `_mutex` 가 두 mutex 객체 leak

생성자 도중 throw 라 `_mutex = new Mutex(...)` 의 LHS 할당이 일어났다면 첫 wrapper 가 살아있고, `??=` 는 작동하지 않아 leak 없음. LHS 할당 전 throw 라 `_mutex == null` 이면 `??=` 가 두 번째 wrapper 를 생성. 어느 쪽이든 wrapper 1개만 살아있고 OnProcessExit 의 `_mutex?.Dispose()` 가 정리. **leak 없음**.

### 위험 R3 — `_mutex.Dispose()` 호출이 ProcessExit 에서 한 번만

기존 OnProcessExit 코드는 `_mutex?.Dispose()` 1회. catch 경로에서 `_mutex` 가 새로 할당되든 기존 할당이 유지되든 단일 객체 → 단일 Dispose. **변경 없음**.

### 위험 R4 — Logger 초기화 전에 Warning 호출

`TryAcquireMutex` 는 `MainImpl` 의 step 1, `Logger.Initialize` 는 step 4. Warning 호출 시점에 Logger 는 default state (Console + 미초기화 파일 sink). 기존 코드도 같은 위치에서 `Logger.Info("Another instance already running")` 호출하고 있어 동일 환경 가정 — **변경 없음**.

## 측정 계획

본 변경의 핵심 시그널은 로그의 `[WARN] Mutex was abandoned by previous crashed instance:` 출현 빈도. 운영 중 사용자 PC 의 로그 파일에서 이 라인이 보이면:

1. 직전 KoEnVue 인스턴스가 정상 종료되지 않았다는 증거 — 별도 crash 로그 (`koenvue_crash.txt`) 또는 시스템 이벤트 (`taskkill`/사용자 강제 종료) 와 상관 분석
2. 빈도가 높으면 (예: 부팅 10회당 1회 이상) 사이드 채널로 OS / 다른 앱이 KoEnVue 를 죽이고 있는 패턴 의심 → 추가 조사 PR

본 PR 단독으로는 측정용 카운터 / 텔레메트리 추가 없음. 로그 라인만으로 충분.

## 관련 자료

- improvement-plan: [PR-00](../improvement-plan/PR-00-mutex-abandoned.md)
- CHANGELOG: `[Unreleased] / 수정`
- 코드: [Program.Bootstrap.cs:33](../../Program.Bootstrap.cs#L33) `TryAcquireMutex`
