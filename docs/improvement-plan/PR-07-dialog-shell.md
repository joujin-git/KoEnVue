# PR-07: DialogShell 추출 + a11y baseline

**Status**: ⏳ pending
**Branch**: feat/pr-07-dialog-shell
**Base**: main (PR-04 후 권장)
**Risk**: Medium (다이얼로그 epilogue 미묘한 차이 + 수동 smoke 필수)
**Estimated session size**: L (반나절+, 2-3세션 가능)

## 1. 목적 (Why)

3개 다이얼로그(SettingsDialog/CleanupDialog/ScaleInputDialog)가 같은 패턴을 3번 손으로 작성:

- Show() prologue (IsActive guard → DPI → font → register → create): **~50줄 × 3**
- Show() epilogue (ShowWindow → ModalDialogLoop.Run → DestroyWindow): **~15줄 × 3**
- WM_COMMAND IDOK/IDCANCEL: **~15줄 × 3**
- 뷰포트 WndProc (SettingsDialog/CleanupDialog): **95% 동일**
- `int borderW = 16` 매직 처리가 셋 다 다른 방식

**부가 (H4-b)**: a11y baseline — `WS_GROUP`/`WS_TABSTOP` 일관 적용 + UIA `LabeledBy` 자동 연결 (STATIC을 EDIT 직전).

### 검증된 epilogue 차이 (4-라운드 검증 결과)
- SettingsDialog: `ShowWindow → SetForegroundWindow → (SetFocus 없음)`
- CleanupDialog: `ShowWindow → (SetForegroundWindow 없음)` ← **누락 추정**
- ScaleInputDialog: `ShowWindow → SetForegroundWindow → SetFocus(_hwndScaleEdit)`

→ 셸의 epilogue 후크: `bool bringToForeground = true`, `Action? onAfterShow`

## 2. 변경 범위 (What)

### 코드 — 신규 셸
- [ ] [Core/Windowing/DialogShell.cs](../../Core/Windowing/) 신규 — 다이얼로그 라이프사이클 공통 골격
  ```csharp
  internal static class DialogShell
  {
      internal sealed class DialogContext { /* DPI, font, hwndDialog, hwndViewport, etc. */ }

      public static bool Run(
          IntPtr hwndOwner,
          string className,
          string title,
          int widthLogical,
          int heightLogical,
          bool useViewport,                  // true = SettingsDialog/CleanupDialog
          bool bringToForeground,            // CleanupDialog의 누락분 일치화
          Action<DialogContext> buildChildren,
          Action<DialogContext>? onAfterShow,
          Func<DialogContext, bool> onOk);
  }
  ```
- [ ] [Core/Windowing/DialogShell.WndProc.cs](../../Core/Windowing/) 신규 (partial) — 공통 WM_COMMAND/WM_VSCROLL/WM_MOUSEWHEEL/WM_CLOSE 핸들러

### 코드 — 3 다이얼로그 리팩토링
- [ ] [App/UI/Dialogs/SettingsDialog.cs](../../App/UI/Dialogs/SettingsDialog.cs) — Show()를 `DialogShell.Run(...)` 호출로 축약. 자기 책임은 `buildChildren`(필드 빌드) + `onOk`(저장 로직)만.
- [ ] [App/UI/Dialogs/CleanupDialog.cs](../../App/UI/Dialogs/CleanupDialog.cs) — 동일 패턴. `bringToForeground: true`로 통일(누락 수정).
- [ ] [App/UI/Dialogs/ScaleInputDialog.cs](../../App/UI/Dialogs/ScaleInputDialog.cs) — 동일. `onAfterShow: ctx => User32.SetFocus(_hwndScaleEdit)`.
- [ ] [App/UI/Dialogs/SettingsDialog.Scroll.cs](../../App/UI/Dialogs/SettingsDialog.Scroll.cs) — 뷰포트 WndProc 로직을 DialogShell로 흡수. 호출자에 hook만 남김.
- [ ] partial class 파편화 정리: SettingsDialog의 3개 partial 파일을 1-2개로 축소 검토

### 코드 — H4-b a11y
- [ ] DialogShell.AddRow 등의 helper에서 STATIC을 EDIT/COMBOBOX 직전 위치 + WS_GROUP/WS_TABSTOP 일관 적용
- [ ] 모든 input control에 ID 부여 (UIA `AutomationId` 대용)

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed
- [ ] `docs/architecture.md` Core/Windowing 모듈 목록에 DialogShell 추가
- [ ] `docs/implementation-notes.md` 다이얼로그 섹션 신설 — 셸 사용법 + a11y 패턴
- [ ] (선택) `docs/dev-notes/2026-05-21-dialog-shell-extraction.md` — 3-다이얼로그 epilogue 차이 분석 + bringToForeground 후크 도입 근거

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "ModalDialogLoop.Run" App/UI/Dialogs/` 3 매치 이하 (각 다이얼로그가 DialogShell 호출 한 번씩이거나 셸이 단일 진입점)
- [ ] `git grep "CreateWindowExW" App/UI/Dialogs/` 0 매치 (셸이 처리)
- [ ] `wc -l App/UI/Dialogs/SettingsDialog.cs` < 200 (현재 440)
- [ ] `wc -l App/UI/Dialogs/CleanupDialog.cs` < 200 (현재 377)
- [ ] `wc -l App/UI/Dialogs/ScaleInputDialog.cs` < 150 (현재 274)
- [ ] `git grep "WS_TABSTOP\|WS_GROUP" Core/Windowing/DialogShell.cs` 1+ 매치

### Tier 3 — 수동 smoke (필수, 다이얼로그별)
**SettingsDialog**:
- [ ] 트레이 → 설정 → 다이얼로그 정상 표시 (DPI 정상, 폰트 정상)
- [ ] 38개 필드 모두 표시
- [ ] 스크롤 정상 (휠 + 스크롤바)
- [ ] Tab/Shift+Tab으로 순회
- [ ] Enter = OK, Esc = Cancel
- [ ] OK 저장 후 인디 즉시 반영

**CleanupDialog**:
- [ ] 트레이 → 저장 위치 정리 → 다이얼로그 표시
- [ ] **새로 SetForegroundWindow 호출됨 — 트레이 메뉴 닫힌 후 다이얼로그가 자동 포어그라운드**
- [ ] 체크박스 선택 + OK → 항목 삭제

**ScaleInputDialog**:
- [ ] 트레이 → 크기 → 사용자 정의 → 다이얼로그 표시
- [ ] EDIT에 포커스 (onAfterShow 동작)
- [ ] 잘못된 값 → 에러 메시지 + 재포커스

## 4. 사이드 이펙트 / 위험

- **위험 1 (큼)**: CleanupDialog의 SetForegroundWindow가 새로 호출됨. 실제 사용자 체감 변화는 거의 없음 (ShowWindow가 어차피 활성 윈도우). 코멘트로 명시.
- **위험 2**: 셸이 너무 generic해지면 각 다이얼로그의 특수 로직(예: SettingsDialog의 필드 빌드 시 enum combo 처리)이 callback 폭증. **결정**: 셸은 라이프사이클만, 컨트롤 빌드는 호출자가.
- **위험 3**: partial class 정리 시 정적 필드(_fields, _fieldInputs, _scrollChildren, …)의 ownership 명확화. DialogContext 안에 흡수 권장.
- **위험 4**: 셸이 ModalDialogLoop와 어떻게 협력하는지 명확화. `ModalDialogLoop.IsActive` 가드는 셸 내부로 흡수.
- **위험 5**: a11y 변경이 기존 keyboard nav 동작 깨뜨리지 않는지 — 수동 Tab 순회 smoke 필수.

## 5. 롤백 절차

- 단순 revert 가능 (Y) — 단 변경 큼. 단일 commit 또는 squash 권장.
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)
