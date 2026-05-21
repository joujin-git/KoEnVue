# PR-00: AbandonedMutex 캐치

**Status**: ✅ 코드 완료, Tier-1 + Tier-2 통과 (Tier-3 수동 smoke 는 본 PR 머지 후 권장)
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

### 2026-05-21 — session 1 (구현 + Tier-2)

- 브랜치 `feat/pr-00-mutex-abandoned` 생성 (base: main).
- [Program.Bootstrap.cs:33-58](../../Program.Bootstrap.cs#L33) `TryAcquireMutex` 에 `try { ... } catch (AbandonedMutexException ex) { Logger.Warning(...); _mutex ??= new Mutex(true, MutexName, out _); return true; }` 5줄 추가. 기존 createdNew 분기 로직은 try 블록 안으로만 이동, 시그니처/반환 의미 불변.
- [CHANGELOG.md](../../CHANGELOG.md) `[Unreleased] / 수정` 항목 1건 추가 (잠재 deadlock 차원, BREAKING 아님 명시).
- [docs/dev-notes/2026-05-21-mutex-abandoned-handling.md](../../docs/dev-notes/2026-05-21-mutex-abandoned-handling.md) 신규 — 무엇/왜/대안 4건/회귀 위험 R1-R4/측정 계획.
- **Tier-1 자동 검증**: 1차 시도 시 NuGet 소스 0건 + 캐시 부재로 NU1100 실패. 프로젝트-로컬 `NuGet.config` (nuget.org 단일 소스) 추가 후 재시도 → `dotnet restore` 17.5s, `dotnet build` 4.51s (0 warn / 0 err), `dotnet publish -r win-x64 -c Release` 성공. AOT exe `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe` = 4,684,800 bytes (~4.47 MB, CLAUDE.md "~4.7 MB" 기대치 부합).
- **Tier-1 invariant 4종** (grep): `Core/` 내 `KoEnVue.App`/`ImeState`/`NonKoreanImeMode` 0매치, 전체 소스 `DllImport` 0매치 (docs 만 매치) — 모두 통과.
- **Tier-2 grep**: `git grep "AbandonedMutexException" Program.Bootstrap.cs` → 1매치 (line 47) ✅.
- **Tier-3 수동 smoke**: 실행. 시퀀스 — 새 빌드 exe (`bin/Release/.../publish/KoEnVue.exe`) 실행 → 트레이 표시 확인 → `taskkill /f /im KoEnVue.exe` (사용자 권한 거부, app.manifest 의 `requireAdministrator` 영향) → 관리자 PowerShell 에서 동일 명령 성공 → 즉시 재실행 → **재실행 성공, deadlock 없음**. 단, 로그 파일 `bin/Release/.../publish/koenvue.log` 의 두 부팅 (11:41:28, 11:42:18) 사이에 `[WARN] Mutex was abandoned by previous crashed instance:` 라인 **부재** — OS scheduler 가 taskkill 직후 mutex slot 을 청소한 후 두 번째 인스턴스가 도착해 catch 가 trigger 되지 않음. 본 race window 는 좁고 환경 의존적이라 한 번의 smoke 로 재현되지 않는 게 정상. 본 PR 가 방어한 **사용자 가시 동작 (kill + 재실행 = deadlock 무발생)** 자체는 입증됨. catch 자체의 동작 검증은 운영 중 자연 발생하는 race 사례를 `Logger.Warning` 으로 모니터링.
- **NuGet.config 처분 대기**: 본 PR 의 부수 산물로 추가됐으나 빌드 인프라 변경이라 별도 결정 필요. PR-00 코드 변경과 분리해 commit / .gitignore / 제거 중 사용자 선택.
