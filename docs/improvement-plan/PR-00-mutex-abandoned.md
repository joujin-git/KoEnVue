# PR-00: AbandonedMutex 캐치

**Status**: ⏳ pending
**Branch**: feat/pr-00-mutex-abandoned
**Base**: main
**Risk**: Low
**Estimated session size**: S (≤30분)

## 1. 목적 (Why)

이전 인스턴스가 비정상 종료 시 mutex가 `WAIT_ABANDONED` 상태로 남는다. 현 코드는 `AbandonedMutexException`을 캐치하지 않아 두 번째 인스턴스의 `Main`에서 unhandled exception → 앱 영구 실행 불가 (deadlock 형태).

`new Mutex(true, name, out createdNew)` 자체는 ABANDONED 시 createdNew=false로 분기 타지만, 일부 .NET 런타임 시나리오에서 `WaitOne` 호출 시점에 `AbandonedMutexException`이 throw됨. 본 코드는 `WaitOne`은 호출 안 하지만 안전망이 없는 상태.

## 2. 변경 범위 (What)

### 코드
- [ ] [Program.Bootstrap.cs:34-45](../../Program.Bootstrap.cs#L34) `TryAcquireMutex` — `AbandonedMutexException` 캐치 추가. 캐치 시: `Logger.Warning` 1줄 + `_mutex` 보유한 채로 정상 분기로 진행 (mutex 인수 의도).

### 코드 — 예시 변경

```csharp
private static bool TryAcquireMutex()
{
    try
    {
        _mutex = new Mutex(true, DefaultConfig.MutexName, out bool createdNew);
        return createdNew;
    }
    catch (AbandonedMutexException ex)
    {
        // 이전 인스턴스가 비정상 종료. mutex는 .NET이 자동 인수했으므로 createdNew=true와 동일 의미.
        Logger.Warning($"Mutex was abandoned by previous crashed instance: {ex.Message}");
        // _mutex는 catch 시점에 이미 owned 상태로 .NET 런타임이 처리.
        // 일부 런타임 버전은 _mutex 필드 set 후 throw하므로 null 체크.
        _mutex ??= new Mutex(true, DefaultConfig.MutexName, out _);
        return true;
    }
}
```

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Fixed에 항목 추가
- [ ] `docs/dev-notes/2026-05-21-mutex-abandoned-handling.md` 신규 — 재현 시나리오 + 결정 근거

## 3. 검증 기준 (Done When)

### Tier 1 — 자동
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] CLAUDE.md invariant 4종 0 매치 유지

### Tier 2 — PR별 grep
- [ ] `git grep "AbandonedMutexException" Program.Bootstrap.cs` 1+ 매치 (변경 적용 확인)

### Tier 3 — 수동 smoke (선택)
- [ ] 정상 부팅 → 트레이 아이콘 표시 확인
- [ ] (선택) 첫 인스턴스를 `taskkill /f /pid <PID>`로 강제 종료 후 재실행 → 두 번째가 deadlock 없이 부팅

## 4. 사이드 이펙트 / 위험

- **위험 1**: 캐치 후 `_mutex` 객체 상태가 런타임 버전마다 다름. .NET 7+에서는 `AbandonedMutexException` throw 시점에 이미 mutex.GetSafeWaitHandle()은 valid. 본 코드는 `??=` 폴백으로 방어.
- **위험 2**: 캐치 안에서 다시 throw되면 외부 `Main`의 catch가 흡수 → `koenvue_crash.txt`. 정상 경로.
- **사이드 이펙트**: 정상 종료 후 재실행은 영향 없음 (createdNew=true 분기).

## 5. 롤백 절차

- 단순 revert로 충분 (Y)
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)
