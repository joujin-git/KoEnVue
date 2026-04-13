# KoEnVue 기능별 재구성 계획 — 인덱스

원본 플랜: `C:\Users\joujin\.claude\plans\partitioned-fluttering-sparrow.md`
이 폴더는 원본 플랜을 단계별로 분리한 실행 레퍼런스입니다.

## 진행 체크리스트

- [ ] [00 — Context & Target Structure](00-context-and-structure.md)
- [ ] [01 — Stage 0: 기준선 고정](01-stage0-baseline.md)
- [ ] [02 — Stage 1: 중복 핫스팟 6건 제거](02-stage1-dedupe.md)
- [ ] [03 — Stage 2: 파일 재배치](03-stage2-relocation.md)
- [ ] [04 — Stage 3: AppConfig 시그니처 좁히기](04-stage3-narrowing.md)
- [ ] [05 — Stage 4: Core 기반 추출](05-stage4-core-extraction.md)
- [ ] [06 — Stage 5: 전체 빌드 & 런타임 검증](06-stage5-verification.md)
- [ ] [07 — Stage 6: 문서 현행화](07-stage6-docs-sync.md)
- [ ] [08 — Stage 7: 전체 최종 검증](08-stage7-final-gate.md)
- [ ] [09 — Risks, Reuse, Exit Criteria](09-risks-and-reuse.md)

## 커밋 경계 (총 14 커밋)

| # | Stage | 커밋 제목 |
|---|---|---|
| C0 | 0 | `chore: capture refactor baseline metrics` |
| C1 | 1-D | `refactor: split Tray.cs into Dialogs/CleanupDialog + ScaleInputDialog` |
| C2 | 1-E | `refactor: extract WindowProcessInfo to break Config→Detector layer violation` |
| C3 | 1-C | `fix: wrap CleanupDialog modal loop with IsDialogMessageW for Tab/ESC` |
| C4 | 1-B | `refactor: replace manual CreateFontW/DeleteObject with SafeFontHandle in dialogs` |
| C5 | 1-A | `refactor: move TryNormalizeHexColor to shared ColorHelper.TryNormalizeHex` |
| C6 | 2 | `refactor: move files to Core/App folder structure and rewrite namespaces` |
| C7 | 3-A | `refactor: narrow Overlay/Animation/Logger signatures off AppConfig` |
| C8 | 3-B | `refactor: narrow ImeStatus.Detect signature to DetectionMethod` |
| C9 | 4-A | `feat: extract LayeredOverlayBase and OverlayStyle to Core` |
| C10 | 4-B | `feat: extract OverlayAnimator state machine to Core` |
| C11 | 4-C | `feat: extract JsonSettingsManager<T> to Core` |
| C12 | 4-D | `feat: extract NotifyIconManager and ModalDialogLoop to Core` |
| C13 | 6 | `docs: sync CLAUDE.md and PRD with Core/App split` |

## 읽는 순서

1. 먼저 [00](00-context-and-structure.md)으로 **왜**와 **무엇을 만드는가**를 파악
2. [09](09-risks-and-reuse.md)에서 위험/재사용 규칙을 먼저 훑어두면 Stage 중 판단이 쉬움
3. Stage 0 → 7 순서대로 [01](01-stage0-baseline.md) ~ [08](08-stage7-final-gate.md) 실행
