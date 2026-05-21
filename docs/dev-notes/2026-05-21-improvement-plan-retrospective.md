# 2026-05-21 — Improvement Plan Retrospective: 13-PR 작업 회고

**Context**: 2026-05-21 시작된 4-라운드 코드베이스 리뷰의 결과로 13개 PR (PR-00 ~ PR-12 + 신설 PR-13/14, 총 15 PR) 의 개선 계획을 수립·실행. 본 문서는 PR-12 머지 시점의 retrospective — 다음 작업자 (또는 미래의 자기 자신) 가 본 문서만 읽고 13-PR 의 결과·근거·잔존 결정을 파악할 수 있도록 self-contained 작성.

`docs/improvement-plan/` 디렉토리의 명세 파일 (PR-00 ~ PR-14) + `INDEX.md` 의 Sessions log 가 진행 로그의 단일 진실원이다. 본 문서는 그 위 한 계층 — "전체 그림".

---

## 0. 전/후 비교 한 줄 요약

| 축 | 전 (v0.9.x) | 후 (v0.10.0 후보) |
|---|---|---|
| **보안 토큰** | `requireAdministrator` (매 부팅 UAC) | `asInvoker` + `%LOCALAPPDATA%` fallback |
| **테스트 / CI** | 0개 / 0개 | xUnit 40/40 + GitHub Actions windows-latest |
| **AppDomain crash trace** | `Main` 의 try/catch 만 | `UnhandledException` + `UnobservedTaskException` + `koenvue_crash.txt` |
| **Core / App 분리** | IME / 한국어 폰트 어휘가 Core 누출 | P6 게이트 회복 (9 IME 상수 → App, "맑은 고딕" → App) |
| **버전 진실원** | 3 곳 hand-sync (csproj + DefaultConfig + docs) | csproj `<Version>` 1 곳 (MSBuild Target generated) |
| **per-app rendering override** | 비대상 — 시각 키 30+ 모두 무동작 | 18 렌더 호출처에 `ResolveCurrent()` 배선 (PR-13) |
| **Win11 강조색 즉시 반영** | `GetSysColor(COLOR_HIGHLIGHT)` (제목표시줄 옵션 OFF 시 미반영) | `DwmGetColorizationColor` 우선 + 폴백 + WM_DWMCOLORIZATIONCOLORCHANGED |
| **다이얼로그 a11y** | WS_TABSTOP / WS_GROUP 누락 | DialogShell 통합 + 20 매치 |
| **Tray.cs 라인** | 1156 줄 (god class) | 575 줄 (4 모듈 + Menu partial 분리) |
| **DefaultConfig 단일 진실원** | clamp / dialog / theme 4축 drift | `Min/MaxX` 16쌍 + `ThemeColors` record + 고대비 분기 |
| **분석기** | (없음) | AOT/Trim/SingleFile 3종 + manifest Win10/11 compatibility |
| **AOT exe 크기** | 4.47 MB | 4.81 MB (+0.34 MB — 신규 기능 누적, Size 최적화 유지) |

핵심 효과는 **신뢰성** 과 **개발 속도** 양면. UAC 가 사라지고 사용자 가시 결함 (per-app 무동작 / 강조색 미반영 / cleanup 실삭 안 됨) 이 모두 해소되고, 매 변경마다 xUnit + GitHub Actions 가 회귀를 자동 감지하는 인프라가 깔렸다.

## 1. PR 목록 + 한 줄 결과

| PR | 항목 (기존 리뷰 코드) | 한 줄 결과 |
|---|---|---|
| 00 | AbandonedMutex catch | `Mutex` 초기화에 `AbandonedMutexException` 가드 + 폴백 |
| 01 | A1+A4+B3+H5 | MergeProfile 파이프라인 + WM_THEMECHANGED + Tier-3 에서 PR-13 신설 근거 발견 |
| 02 | G2+G3 | AOT/Trim/SingleFile 분석기 활성 (위반 0건) + manifest Win10/11 compatibility GUID |
| 03 | (BREAKING) | `requireAdministrator → asInvoker` + schtasks LeastPrivilege + `PortablePath` fallback + B1/B2 sanitize. Tier-3 D 회귀 (schtasks LogonTrigger UserId) 후속 fix |
| 04 | C1+C2+D9+D10 | Tray.cs 1156→575 줄 분해 (4 신규 모듈 + Menu partial) |
| 05 | D2+D5+N3+D7+H4-c | DefaultConfig Min/Max 16쌍 + AppConfig 디폴트 ↔ Validate ↔ Dialog 4축 단일 진실원 + ThemeColors record + 고대비 분기 |
| 06 | D3+D4 | I18n `Dictionary<I18nKey, (Ko, En)>` + `AppLanguage` enum + Tier-3 ④ 즉시반영 fix |
| 07 | C3+H4-b | DialogShell 추출 + 3 다이얼로그 통합 + a11y baseline + CleanupDialog tryCommit 회귀 fix |
| 08 | C4+C6+C5(부분)+E1+E2+E3 | Core 재사용 contract 회복 — WindowSnapHelper / TopmostWatchdog / ImeConstants 분리 + MeasureLabels 일반화 + 폰트 파라미터화 |
| 09 | E4+E5+F1+F3+F4+F5 | ILogSink + LogProvider + pre-Init 버퍼 + LogLevel converter App 이전 + PR-06 ④ 잔재 해소 |
| 10 | G1+G5 | xUnit 40/40 + GitHub Actions + AppDomain unhandled + CONTRIBUTING.md. Tier-3 CI build path 회귀 1건 fix |
| 11 | D6+G4 | csproj `<Version>` 단일 진실원 + SHA256 자동 emit + NativeAOT 비결정성 발견 + Tier-3 회귀 2건 fix (Targets condition / pwsh) |
| 12 | H1+H2+H3+H4 | 본 PR — config-reference.md / 9→8 / CHANGELOG 표준 안내 / CLAUDE.md final + retrospective |
| **13** (신설) | (PR-01 Tier-3 발견) | `ResolveCurrent()` + 18 렌더 호출처에 per-app resolved 배선 — `app_profiles` 시각 override 의 무동작 해소 |
| **14** (신설) | (PR-01 Tier-3 ④ 해소) | `DwmGetColorizationColor` 우선 + GetSysColor 폴백 + WM_DWMCOLORIZATIONCOLORCHANGED 핫 리로드 |

## 2. 잘 된 점 (What worked)

- **Tier-1/2/3 게이트 + invariant grep**: 모든 PR 이 `dotnet build` clean + 신규 grep 가드 + 4종 invariant 0매치 + Tier-3 수동 smoke 의 4단 검증을 통과해야 머지. 회귀 패턴 (PR-03 D, PR-07 CleanupDialog, PR-10 CI, PR-11 Targets/pwsh) 이 모두 이 게이트에서 자가 검출 후 fix.
- **PR 마다 self-contained 명세 (`PR-NN-*.md`)**: 1차 작성 시점의 의도 + Tier-3 에서 추가된 발견을 같은 파일 §6 에 누적 — 다음 작업자가 "왜 이렇게 했는가" 를 한 문서에서 파악.
- **Sessions log 표 (INDEX.md)**: 매 작업 단계의 결과를 한 줄 누적. 작업 중단/재개 시 자기 자신과 다음 작업자 모두 같은 진실원 참조.
- **메모리 1 파일 시스템**: `improvement-plan.md` 한 파일이 매 세션 첫 라인 자동 로드 — 컨텍스트 비용 없이 진행 상태를 다음 세션이 즉시 인계.
- **dev-notes 의 "왜 보류" 기록**: OverlayAnimator 4-트랙 분해 / 코드 사인 / NativeAOT 비결정성 같은 결정은 단순 "안 함" 이 아니라 *근거 + 재검토 트리거* 까지 박제 — 미래에 같은 함정에 다시 빠지지 않게 보호.
- **CI 의 사후 발견**: PR-10 push 후 첫 CI 실행이 build path 회귀를 즉시 검출. PR-11 push 후엔 Directory.Build.targets 의 sibling 충돌 + GH runner 의 `powershell.exe` 5.1 특이성을 동시 검출. 로컬 테스트만으로는 가려졌을 결함을 인프라가 자동 노출.

## 3. 어려웠던 점 (What was hard)

- **NativeAOT 비결정성 (PR-11)**: `<Deterministic>true</Deterministic>` 가 C# 컴파일러만 잡고 ILC codegen 은 별개임을 spec 작성 시점에 알지 못함 — Tier-3 3 회 publish 실측으로 확인. SHA256 의 의미를 "재현 빌드" → "발행본 변조 검증" 으로 좁히고 문서 4종 정정. **함의**: AOT 기반 프로젝트의 자동 사인 워크플로 설계 시 hash 의 의미를 명시할 것.
- **Directory.Build.* 의 sibling 충돌 (PR-11)**: tests 프로젝트도 같은 Target 을 받아 obj/Version.g.cs 가 메인 DefaultConfig 와 type conflict. 로컬은 PR-10 시절 캐시로 가려졌고 CI fresh 빌드가 노출. fix: `MSBuildProjectName == 'KoEnVue'` 조건. **함의**: 워크스페이스 단위 MSBuild 항목은 명시 게이트 필수.
- **GH Actions windows-latest 의 `powershell.exe` 5.1 (PR-11)**: -NoProfile + Get-FileHash 가 "term not recognized" — module 자동 로드 실패로 추정. 로컬 PS 5.1 은 정상. fix: `pwsh` 로 전환. **함의**: CI 환경에선 PS 7 (`pwsh`) 만 사용.
- **CleanupDialog tryCommit 타이밍 (PR-07)**: 1차 리팩토링에서 결과 수집을 `DialogShell.Run` 반환 *후* HWND 에서 BM_GETCHECK 로 수행 — finally-DestroyWindow 가 HWND 무효화 후라 빈 선택 반환. 사용자 가시 결과는 "삭제 후에도 인디 위치 그대로". fix: WM_COMMAND IDOK 안 (모달, HWND 유효) 에 정적 필드 박는 tryCommit 콜백 패턴. **함의**: HWND 의존 결과 수집은 반드시 모달 활성 구간 안에서.
- **per-app rendering 의 무동작 (PR-01 → PR-13)**: 감지 스레드가 만든 `resolved` AppConfig 가 메인 스레드의 `Animation.TriggerShow` / `Overlay.UpdateColor` / `BuildStyle` 에 도달하지 않아 `app_profiles` 시각 키 30+ 가 silent 무동작. Tier-3 사용자 가시 검증으로 발견 → PR-13 신설. **함의**: 2-스레드 모델에서 *어디서 만든 객체가 어디까지 도달하는지* 의 데이터 플로우 전수 점검 가치.

## 4. 보류 항목 (Deferred — with reasons + retest triggers)

| 보류 항목 | 근거 | 재검토 트리거 |
|---|---|---|
| `OverlayAnimator` 의 fade/hold/highlight/slide 4 트랙 분해 (PR-08 C5) | 공유 상태 `_phase` / `_currentAlpha` + `TriggerShow` 분기 타이밍 의존이 강해 분해 시 사실상 single-class fragmentation. dev-notes 2 개의 fragile 영역 지목. | 타이머 mock 도입 + 픽셀-단위 비교 도구 마련 + 600줄 재돌파. [docs/dev-notes/2026-05-21-animator-decomposition-deferral.md](2026-05-21-animator-decomposition-deferral.md) 참고 |
| EV/OV 코드 사인 (PR-11 G4) | 연간 $200~700 + 발급 절차 + 평판 학습 필요. 무서명 + SHA256 게시로 변조 검증만 보장. | (1) 다운로드 수 1,000+/년, (2) 잘 알려진 AV 가 false positive 분류, (3) 기업/학교 환경 배포 요청, (4) 유료화. [2026-05-21-signing-decision.md](2026-05-21-signing-decision.md) §재검토 트리거 |
| 인디케이터 UIA + 글로벌 핫키 (H4 부분) | 채택한 부분 (다이얼로그 keyboard nav + UIA 라벨 PR-07, 고대비 감지 PR-05) 외 인디 UIA / 글로벌 핫키는 사용자 요구 부재. | 접근성 사용자 보고 또는 keyboard-only 사용 시나리오 명시 요청 |
| CHANGELOG 기존 항목 → Keep a Changelog 표준 재작성 | 13-PR 의 사후 단락형 기록은 정보 밀도가 높아 표준 형식 (짧은 bullet) 로 압축 시 손실. | v0.10.0 후 새 릴리스부터 표준 적용 — 기존 보존 |
| README config 표를 전체 84 키로 확장 | UX 부담 — 신규 사용자에게 한 화면 분량의 표는 압도적. | 별 파일 `docs/config-reference.md` 분리 (PR-12) — README 는 13 키만 |
| NativeAOT ILC deterministic 모드 (PR-11) | 현재 ILC 가 노출 안 함. C# 컴파일러 deterministic 만으로는 부족. | .NET 11+ 또는 ILC 에 deterministic flag 추가 시 README/release-procedure 의 "정본 hash" → "재현 가능 hash" 강화 |

## 5. 향후 작업 후보 (Future work)

본 13-PR 의 범위 밖이지만, 본 작업 중 인접 가능성으로 관찰된 항목 — 우선순위는 사용자 가시 효과 + 회귀 위험으로 정렬:

1. **v0.10.0 릴리스 cut**: 본 13-PR 의 변경 누적 + asInvoker BREAKING 을 정식 태그 + GitHub Releases 본문에 SHA256 첨부. [docs/release-procedure.md](../release-procedure.md) 절차 따름.
2. **`app_profiles` GUI 편집기**: 현재 config.json 직접 편집만 가능. Settings 다이얼로그에 "앱별 프로필" 섹션 추가 — 프로세스명 / 매칭 키 / override 6 키 (theme/색상/투명도/위치) 최소 노출.
3. **글로벌 핫키 (Win+Shift+K 같은)**: 인디 토글 + 강제 표시. `RegisterHotKey` Win32 API. v0.9.x 부터 사용자 요구 잠재.
4. **CleanupDialog 양방향 정렬**: 현재 알파벳 정렬 고정. 사용 빈도순 / 최근 사용순 옵션. (`indicator_positions` dict 가 입력 → 정렬 키만 추가)
5. **다국어 (3rd language)**: 현재 ko/en. I18n 의 `Dictionary<I18nKey, (Ko, En)>` 를 3-tuple 또는 `Dictionary<I18nKey, Dictionary<Lang, string>>` 로 확장 (PR-06 회고에서 이미 미리 설계됨).
6. **PR-13 의 cross-thread per-app override 마샬링 검증**: 감지 스레드가 `ResolveForApp` 결과를 그대로 PostMessage 로 메인에 전달하는 현 구조의 성능 측정 (LRU 캐시 50 슬롯의 hit rate, JSON merge 비용). 4 주 데이터로 마이그레이션 필요 여부 재결정.
7. **dotnet 10 -> 11 SDK 마이그레이션**: `EnableAotAnalyzer` / `EnableTrimAnalyzer` / `EnableSingleFileAnalyzer` 분석기의 신규 경고 검수 + AOT 사이즈 재측정.

## 6. 작업 완료 후 후속 정리 (이 PR 머지 후)

PR-12 (본 PR) 머지 + v0.10.0 릴리스 후:

1. ✅ `docs/improvement-plan/` 디렉토리 **보존** — 미래의 retrospective 참조 + PR-NN-*.md 명세는 self-contained 한 작업 단위 사례집
2. ⏳ `memory/improvement-plan.md` 메모리 **삭제** (사용자가 새 메모리로 대체 또는 다음 작업 컨텍스트로 전환할 때)
3. ⏳ README `프로젝트 마일스톤` 또는 유사 섹션에 본 retrospective 링크 (선택)
4. ⏳ v0.10.0 태그 + GitHub Release (asInvoker BREAKING 명시 + SHA256 첨부 — PR-11 산출물 활용)
