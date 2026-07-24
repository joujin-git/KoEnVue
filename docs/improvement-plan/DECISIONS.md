# Improvement Plan — Decisions Record

본 문서는 2026-05-21에 수행된 4-라운드 코드베이스 리뷰의 결정 사항 요약. 각 PR은 본 결정을 베이스로 작성됨. 변경 시 본 문서 갱신 + 영향 받은 PR의 §1·§2 동기화 필요.

## 1. Scope

10.5k LOC의 KoEnVue (Windows IME 상태 표시 오버레이). 13개 PR로 구조·중복·정합성·보안·인프라 정리.

## 2. 주요 결정 (보류 항목 결정 포함)

### 2-1. A1 — `MergeProfile` 파이프라인 완성
프로필 머지가 `Migrate`/`Validate`/`ApplyTheme`을 우회([Settings.cs:331-397](../../App/Config/Settings.cs#L331)). 잠재 visual bug. **결정**: 3 훅 모두 추가 + `Settings.ClearProfileCache()`를 `WM_SETTINGCHANGE`와 `WM_THEMECHANGED`에서 호출.

### 2-2. A4 — VDM COM cross-thread: **현 상태 유지 + 측정 추가**
detection 스레드가 main STA proxy를 호출하는 패턴([SystemFilter.cs:151-169](../../App/Detector/SystemFilter.cs#L151)). 코멘트가 wide catch로 인정. **결정**: 변경 안 함. PR-01에서 Debug 카운터(1000회당 1줄) 추가해 production 데이터 수집. 4주 후 재결정.

### 2-3. B-series 보안 — **PR-03 asInvoker 채택으로 자연 해소**
앱이 실제로 Admin 권한 필요 없음. `requireAdministrator` 제거 시:
- B1 LogFilePath sanitize: 잔여 (외부 경로 허용 정책 결정 필요)
- B2 schtasks symlink: 자연 해소 (Admin 토큰 없음)
- B3 OverlayClassName 검증: PR-01 Validate에 흡수
- B4 portable vs Admin: **`asInvoker` 채택**. `%LOCALAPPDATA%\KoEnVue\` 자동 fallback
- B5 Admin notepad: 자연 해소

### 2-4. C5 — `OverlayAnimator` 분해: **부분 (TopmostWatchdog만)**
dev-notes 2개의 fragile 영역. fade/hold/highlight/slide/dim는 보류. **결정**: PR-08에서 TopmostWatchdog만 분리(~30줄, 독립). 나머지는 `docs/dev-notes/2026-05-21-animator-decomposition-deferral.md`에 보류 근거 기록.

### 2-5. G4 — Code signing: **무서명 + SHA256**
EV/OV cert ROI 낮음. **결정**: GitHub Releases body에 SHA256 게시, README에 검증 가이드. PR-11에 통합. 사용자 베이스 확장 시 재검토.

### 2-6. H4 — Accessibility: **부분 채택**
- H4-a 인디케이터 UIA: **보류** (ephemeral UI라 스크린리더 방해)
- H4-b 다이얼로그 키보드 nav + UIA 라벨: **채택**, PR-07에 흡수
- H4-c 고대비 감지 + system color fallback: **채택**, PR-05에 흡수
- H4-d Global hotkey: **별도 PRD, 보류**

### 2-7. CLAUDE.md P5 invariant 갱신 — PR-03 머지 직후
현 P5는 `requireAdministrator`. asInvoker 채택 후 갱신:
```
| **P5** | app.manifest UAC asInvoker. system-위치 설치 시 %LOCALAPPDATA% 자동 fallback |
```
새 verification: `git grep "requireAdministrator" app.manifest` → 0 매치.

### 2-8. BEH-2 커서 팝 — **방향 A (2026-07-24)**
동일 IME 앱 전환 시 커서 헤일로 스케일 팝이 없는 것은 **정상**. IME가 실제로 바뀔 때만 팝(플로팅 배지와 일관). 코드 변경 0 · User_Guide / config-reference 명문화. 포커스 전환에도 팝(방향 B)은 **비채택**. 근거: [AUDIT-2026-06-02 §BEH-2](AUDIT-2026-06-02-codebase-review.md).

## 3. 철회된 발견 (False positives or weakened)

1. **TrayIcon vs Overlay의 IME→fallback 색 발산**: `ImeState` enum 3-value, dead path. 우선순위 무시 가능.
2. **`ShowMenu` popup workaround test 부재**: 코멘트로 의도 명확. 과한 비판.
3. **`EnsureSubObjects`가 AppConfig defaults 전체 재나열**: 참조 타입 null 폴백 17개만. 비판 약화. 단 array/dict 기본값은 진짜 hand-sync — PR-05에 흡수.
4. **Mutex ABANDONED state 의심**: createdNew 분기로 정상 처리. 단 N2 (AbandonedMutexException 캐치)는 별 시나리오, PR-00로 진행.
5. **GDI `hOldBitmap` 누수 의심**: stock GDI object라 deletion 불필요. 실제 leak 아님.
6. **PostDeserializeFixup의 `Count == 0` 가드**: STJ deterministic, 발생 가능성 낮음. P3 강등, 보류.

## 4. 신규 발견 (Round 3-4에서)

- **N1**: ILogSink 도입 시 `Settings.Load`이 `Logger.Initialize` 전. sink 미설정 시 silent loss. → PR-09에서 부트 순서 강제.
- **N2**: AbandonedMutexException 미처리 → 잠재 deadlock. → PR-00.
- **N3**: `DefaultConfig` vs `AppConfig` 진짜 중복 6개의 이름 불일치 (FadeInDurationMs↔FadeInMs 등). → PR-05에서 통합 + 이름 일치화.

## 5. 영구 보류 (별 PRD 또는 spike 필요)

- C5 나머지 트랙 분해 (fade/hold/highlight/slide/dim)
- H4-a 인디케이터 UIA
- H4-d Global hotkey
- EV/OV code signing 도입
- B4 외부 LogFilePath 경로 허용 여부 (PR-03 마이그레이션 후 결정)

## 6. 갱신 절차

본 결정이 PR 진행 중 뒤집힐 경우:
1. 본 문서의 해당 §에 변경 사유 명시 (날짜 + 무엇이 바뀜)
2. 영향 받는 `PR-NN-*.md` §1·§2 동기화
3. `memory/improvement-plan.md` 한 줄 갱신
4. INDEX.md의 Sessions log에 한 줄
