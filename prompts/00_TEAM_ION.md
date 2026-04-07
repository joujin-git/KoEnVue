# 이온 (Ion) — 에이전트 팀 구성 가이드

> KoEnVue 프로젝트의 구현/검증/개선을 위한 Claude Code 에이전트 팀.
> 모든 단계별 프롬프트 파일(01~07)을 실행하기 전에 이 문서를 먼저 읽으라.

---

## 1. 팀 생성

```
claude team create 이온
```

## 2. 팀원 역할 정의

| 팀원 | 역할 | 담당 범위 |
|------|------|-----------|
| **이온-리드** | 팀 리드 (사용자 또는 메인 에이전트) | 계획 승인, 코드 리뷰, 통합, 충돌 해결 |
| **이온-파운데이션** | P/Invoke + 공통 인프라 | Native/*.cs, Win32Types, SafeGdiHandles, Models, DefaultConfig |
| **이온-감지** | 감지 엔진 | ImeStatus, CaretTracker, SystemFilter, UiaClient |
| **이온-렌더** | 렌더링 + 애니메이션 | Overlay, Animation, DpiHelper, ColorHelper, 위치 계산 |
| **이온-시스템** | 시스템 통합 | Tray, TrayIcon, Settings, Hotkey, Startup, Lifecycle |
| **이온-QA** | 검증 + 품질 | 체크리스트 검증, NF 요구사항 확인, 코드 리뷰, 빌드 테스트 |

## 3. 워크플로우 — 계획 선행 필수

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ 1. 계획 제출  │───▶│ 2. 리드 승인  │───▶│ 3. 구현      │───▶│ 4. QA 검증   │
│ (plan 모드)  │    │ (리뷰+피드백) │    │ (승인 후)    │    │ (체크리스트)  │
└─────────────┘    └─────────────┘    └─────────────┘    └─────────────┘
```

### 3.1 계획 제출 (필수)
- 모든 팀원은 `mode: "plan"`으로 스폰한다.
- 팀원은 구현에 앞서 **계획서**를 제출해야 한다:
  - 생성/수정할 파일 목록
  - 각 파일의 public API (메서드 시그니처, enum 값)
  - 사용할 공통 모듈 목록
  - 다른 팀원 산출물과의 의존 관계
- **계획 없이 코드 작성 금지**.

### 3.2 리드 승인
- 리드는 계획을 검토하여:
  - 중복 구현 여부 확인
  - 공통 모듈 활용 여부 확인
  - 다른 팀원과의 인터페이스 정합성 확인
- 승인 후에만 구현 단계로 진행.
- 수정이 필요하면 피드백 후 재제출.

### 3.3 구현
- 승인된 계획대로만 구현.
- 계획 범위를 벗어나는 변경은 리드에게 보고 후 승인 필요.

### 3.4 QA 검증
- 이온-QA가 각 단계 완료 후 검증:
  - 구현 체크리스트 대조
  - NF 요구사항 확인
  - P1-P5 원칙 준수 확인
  - 빌드 가능 여부 확인

## 4. 병렬 실행 규칙

### 병렬 가능 조건
- 서로 다른 파일을 생성하는 작업
- 공통 모듈(Phase 01)이 이미 완성된 상태
- 인터페이스(메서드 시그니처, enum)가 사전 합의된 상태

### 병렬 불가 조건
- 같은 파일을 수정하는 작업 → 순차 실행
- 공통 모듈을 아직 생성하지 않은 상태에서 그것을 사용하는 작업
- 한 팀원의 출력이 다른 팀원의 입력인 경우

### 단계별 병렬 실행 맵

```
Phase 01 (Foundation):
  이온-파운데이션: Native/*.cs + Win32Types + SafeGdiHandles
  이온-감지:       Models/*.cs + AppMessages.cs        ← 병렬
  이온-렌더:       Utils/*.cs (DpiHelper, ColorHelper, Logger)  ← 병렬
  이온-시스템:     Config/DefaultConfig.cs             ← 병렬

Phase 02 (Detection):
  이온-감지: ImeStatus.cs + CaretTracker.cs + SystemFilter.cs  ← 순차 (내부 의존)

Phase 03 (Core Loop):
  이온-리드: Program.cs (메인 루프 + 스레드 관리)  ← 단독 (통합 작업)

Phase 04 + 05 (병렬 가능):
  이온-렌더: Overlay.cs + Animation.cs   ← Phase 04
  이온-시스템: Tray.cs + TrayIcon.cs     ← Phase 05, 04와 병렬 가능

Phase 06 (Config):
  이온-시스템: Settings.cs + I18n.cs     ← 단독

Phase 07 (Advanced):
  이온-감지: UiaClient.cs + CaretTracker 통합  ← 병렬
  이온-시스템: Startup.cs + 라이프사이클 고급   ← 병렬
```

## 5. 하드 제약 조건 (P1-P5) — 모든 팀원 필독

```
P1 — 외부 패키지 의존성 제로
  NuGet 외부 패키지 사용 금지.
  .NET 10 기본 라이브러리 + Windows API만 사용.
  P/Invoke: User32, Imm32, Shell32, GDI32, Kernel32, Shcore, Ole32, OleAut32.
  UIAutomation은 COM ([GeneratedComInterface] + CoCreateInstance)으로 접근 — P/Invoke 대상 아님.

P2 — 한글 표시 우선
  UI 텍스트(라벨, 트레이, 툴팁) = 한글 기본.
  인디케이터 라벨: "한" / "En" / "EN".
  로그, config.json 키 = 영문.

P3 — 하드코딩 금지
  매직 넘버 → const/enum 또는 config.json.
  스타일명/상태 → C# enum (문자열 비교 금지).
  색상 → config.json + ColorHelper.cs.
  DPI 기준 96 → DpiHelper.BASE_DPI.

P4 — 공통모듈 사용 강제
  DPI → DpiHelper.cs 1곳.
  색상 → ColorHelper.cs 1곳.
  GDI 핸들 → SafeGdiHandles.cs SafeHandle 래퍼.
  설정 → AppConfig record volatile 참조.
  P/Invoke → Native/ 폴더 DLL별 1파일.
  Win32 구조체 → Win32Types.cs 1회 정의.

P5 — 관리자 권한
  app.manifest UAC requireAdministrator.
  Task Scheduler 경유 → UAC 프롬프트 회피.
```

## 6. 아키텍처 요약

### 6.1 기술 스택
- C# 14 / .NET 10 + NativeAOT (단일 exe ~3MB)
- `[LibraryImport]` source generator (`[DllImport]` 금지)
- `[GeneratedComInterface]` source generator (COM)
- `[JsonSerializable]` source generator (System.Text.Json)

### 6.2 3-스레드 모델
```
메인 스레드 (UI):     메시지 루프 + 렌더링 + 트레이 + WM_TIMER 애니메이션
감지 스레드 (BG):     80ms 폴링 → PostMessage로 메인에 통보
UIA 스레드 (BG):      COM STA + IUIAutomation 전용
```

### 6.3 프로젝트 폴더 구조
```
KoEnVue/
├── KoEnVue.csproj / app.manifest / Program.cs
├── Models/        ImeState, IndicatorStyle, Placement, DisplayMode (enum), AppConfig (record)
├── Detector/      ImeStatus.cs, CaretTracker.cs, SystemFilter.cs, UiaClient.cs
├── UI/            Overlay.cs, Tray.cs, TrayIcon.cs, Animation.cs
├── Config/        Settings.cs, DefaultConfig.cs
├── Utils/         Startup.cs, I18n.cs, DpiHelper.cs, ColorHelper.cs, Logger.cs
├── Native/        User32/Imm32/Shell32/Gdi32/Kernel32/Shcore/Ole32/OleAut32
│                  + Win32Types.cs, AppMessages.cs, SafeGdiHandles.cs
```

### 6.4 파일 소유권 규칙
- **한 파일은 한 팀원만 생성/수정**한다.
- 다른 팀원의 파일을 수정해야 하면 리드에게 요청.
- 공통 모듈(Phase 01)은 이온-파운데이션이 소유. 수정 시 리드 승인 필수.

## 7. 단계별 프롬프트 파일 목록

| 파일 | 단계 | 목표 |
|------|------|------|
| `01_FOUNDATION.md` | 프로젝트 셋업 | .csproj + 매니페스트 + P/Invoke + 모델 + 유틸 |
| `02_DETECTION.md` | 감지 엔진 | IME 3-tier + 캐럿 4-tier + 시스템 필터 |
| `03_CORE_LOOP.md` | 메인 루프 | Program.cs + 3-스레드 + 이벤트 파이프라인 |
| `04_RENDERING.md` | 렌더링 | 오버레이 + 애니메이션 + 위치 안정성 |
| `05_SYSTEM_UI.md` | 시스템 UI | 트레이 + 핫키 + GDI 텍스트 |
| `06_CONFIG.md` | 설정 시스템 | Settings + 검증 + 마이그레이션 + 앱 프로필 |
| `07_FINAL.md` | 마감 | UIA + 고급 기능 + 빌드 + 테스트 |

**실행 순서**: 반드시 01 → 02 → 03 → 04(+05 병렬) → 06 → 07 순서로 진행.

## 8. 커밋 규칙

- 각 단계 완료 시 커밋.
- 커밋 메시지: `phase-XX: 단계 설명`
- 예: `phase-01: project setup - P/Invoke, models, utils`

## 9. 개별 구현 / 중복 구현 금지 체크리스트

매 구현 전 확인:
- [ ] 이 기능이 이미 다른 파일에 구현되어 있지 않은가?
- [ ] 공통 모듈(DpiHelper, ColorHelper, SafeGdiHandles)을 활용하고 있는가?
- [ ] P/Invoke 선언이 Native/ 폴더에 있는가? (다른 곳에 중복 선언하지 않았는가?)
- [ ] Win32 상수/구조체가 Win32Types.cs에 있는가? (다른 곳에 중복 정의하지 않았는가?)
- [ ] AppConfig를 직접 JSON에서 읽지 않고 volatile 참조를 통해 접근하는가?
