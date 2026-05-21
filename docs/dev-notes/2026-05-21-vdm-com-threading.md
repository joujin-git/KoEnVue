# VDM COM cross-thread 호출 — 측정 단계 (2026-05-21)

> **결과**: `SystemFilter._vdmFailCount` static 카운터 추가, 1000회마다 `Logger.Debug("VDM COM failure count: …")` 1줄. 4주 후 (≈ 2026-06-18) 운영 데이터로 cross-thread COM 마이그레이션 필요 여부 재결정. PR-01 ([docs/improvement-plan/PR-01-merge-profile-pipeline.md](../improvement-plan/PR-01-merge-profile-pipeline.md)) 의 A4 항목.

## 무엇 (What)

`SystemFilter._vdm` 인스턴스(`IVirtualDesktopManager`) 는 메인 스레드 STA 의 `static SystemFilter()` 생성자에서 `Ole32.CoCreateInstance` 로 만들어진다. 호출 경로는 감지 스레드(`DetectionLoop`) 의 80ms 핫패스에서 `IsOnCurrentVirtualDesktop(hwnd)` → `_vdm.IsWindowOnCurrentVirtualDesktop(hwnd, …)` — STA 객체를 MTA 스레드에서 cross-apartment 호출하는 패턴.

`IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop` 자체는 `[PreserveSig]` 라 .NET 런타임이 자동 `COMException` throw 를 막아 HRESULT 가 int 로 반환된다. 그러나 RCW 상태 이상(`InvalidComObjectException`), cross-apartment 마샬링 오류, 드물게 `NullReferenceException` 등은 wide catch 로 포획되어 "숨기지 않음" 안전 폴백.

본 PR 는 wide catch 안에 누적 카운터 + 1000회당 1줄 Debug 로깅을 추가했다. 함수 동작/시그니처는 불변.

## 왜 (Why)

### 4-라운드 코드베이스 리뷰 (2026-05-21) 의 위험 식별

리뷰에서 cross-thread COM 호출이 잠재 결함으로 식별됐다. 한쪽 의견은 STA 객체를 MTA 호출하면 .NET runtime 이 proxy 를 통해 마샬링하므로 안전하다는 입장 — 다른 쪽은 `IVirtualDesktopManager` 의 일부 메서드가 cross-apartment proxy 에서 일관되게 동작하지 않는 사례(특정 Win11 빌드의 fast-user-switch / virtual desktop 전환 직후 race) 가 보고된 적이 있다는 입장.

**결정**: 측정 없이 마이그레이션 하지 않는다. 마이그레이션 후보는 (A) STA 호출자(메인 스레드) 가 감지 결과를 캐시 → 감지 스레드가 캐시 읽기, (B) 매 호출마다 메인 스레드 message queue 로 marshalling, (C) `IServiceProvider::QueryService(_serviceProvider, &SID_VirtualDesktopManager, &IID_IVirtualDesktopManager)` 로 free-threaded marshaler 재취득 — 셋 다 코드 복잡도/지연시간 비용이 0 이 아니라 데이터 없이 들이기엔 부담.

### 카운터 설계

- **`static int _vdmFailCount`** — 감지 스레드 단일 라이터. 메인 스레드에서 읽지 않으므로 `Interlocked` 불요. 32비트 int 라 2.1 G회 이후 overflow 하지만 80ms 폴링 기준 일일 1M회 호출 × 365일 = 3.65억 회 < 2.1G — 1년 단위로 카운터 리셋 안 함
- **1000회당 1줄 Debug 로깅** — 정상 상황(0 또는 매우 낮은 실패율) 에서는 며칠~몇 주 한 번 로그 출현. 비정상 폭증 시 0.1% 가량의 비율로만 디스크 쓰기. `Logger.Debug` 라 기본 로그 레벨(`Info`) 사용자에게는 파일에 안 남고, 진단 시 `log_level: debug` 로 전환해 수집

### 4주 데이터 수집 기간 선정

- **너무 짧으면**(1주) Windows Update / 가상 데스크톱 전환 빈도가 낮은 사용자의 데이터가 부족
- **너무 길면**(3개월) 다른 PR (PR-04 Tray decomposition 등) 진행 중 이 데이터 의존성이 깔려 비효율
- 4주 — VDM 전환을 비교적 자주 하는 사용자(개발자/리뷰어)는 충분히 노출, 거의 안 쓰는 사용자도 7일+ 운영 데이터 확보 가능

### 4주 후 (≈ 2026-06-18) 의사결정 기준

| 4주간 누적 카운트 | 결정 |
|------------------|------|
| < 100 | 현 상태 유지. wide catch 로 충분 |
| 100 ~ 1,000 | 카운터를 Info 레벨로 승격, 1주 추가 관측 |
| > 1,000 | 마이그레이션 진행. 옵션 (A)/(C) 우선 평가 |

이 표는 본 dev-note 의 4주 후 회고 시 의사결정 트리거로 활용.

## 대안 (Alternatives considered)

### A. 즉시 마이그레이션 (STA marshalling)

매 호출마다 메인 스레드 message queue 로 `PostMessage` + `Wait` → 응답 회수. 동기화 비용 + 메인 스레드 message queue 점유율 증가 → 메시지 처리 응답성 저하. 80ms 핫패스에서 매 호출 동기 wait 하면 감지 루프 자체 지연. 측정 없이는 들이기 부담.

### B. 카운터 없이 wide catch 유지

현 상태 그대로 wide catch + Debug 로깅 1건/실패. 정상 동작 시 노이즈 0 인 장점이 있으나 트렌드 (실패 빈도) 데이터가 영구히 수집 안 됨. 다음 4-라운드 리뷰 (2026-08-21 가량) 에서 같은 토론이 다시 발생할 위험. 기각.

### C. `IServiceProvider::QueryService` 로 free-threaded marshaler

`ImmersiveShell` 의 `IServiceProvider` 를 통해 VDM 을 재취득하면 free-threaded marshaler 가 자동 결합되어 cross-apartment 호출 안전성이 보장된다는 보고가 있음. 단, ImmersiveShell COM 객체 자체가 IID 비공개 (`a5cd92ff-...` 외 ImmersiveShell 측 IID 들은 미공개) 라 재현/유지 부담. 측정 후 마이그레이션이 필요하다고 판정되면 1순위로 평가.

## 회고 (Lessons learned)

- **잠재 결함 식별 후 측정 단계를 명시적으로 끼우는 것의 가치**. 리뷰에서 "위험" 으로 식별됐다는 이유만으로 마이그레이션을 들이면 복잡도가 부풀어 오른다. 4주 데이터 수집 → 의사결정 트리거 표 → 재결정 의 3단계가 작아 보여도 다음 5개 PR 동안 같은 토론을 끄집어내지 않는 효과
- **카운터의 비용 < 데이터의 가치**. `static int` + 1000회당 Debug 로깅 1줄 = 메모리 4 bytes + 1년 누적 수십 KB 로그. 마이그레이션 결정의 근거 데이터로는 명백한 흑자

## 4주 후 회고 자리 (TBD ≈ 2026-06-18)

(추후 갱신 — 실측 데이터 + 결정 기록)
