# 02 — Stage 1: 중복 핫스팟 6건 제거

← Previous: [01 — Stage 0](01-stage0-baseline.md) | → Next: [03 — Stage 2](03-stage2-relocation.md)

## 목표

파일 이동 전에 "지금 당장 고칠 수 있는 중복 구현 6건"을 먼저 해소. 폴더 이동과 뒤섞이지 않도록 반드시 Stage 2보다 먼저 완료. 각 작업은 별도 커밋(C1~C5)으로 떨어져 revert 단위가 명확하다.

## 에이전트 구성

- **구성**: mixed, 1x Plan + 5x general-purpose (Wave 단위 직렬·부분 병렬)

## 파일 충돌 매트릭스

| 작업 | 편집 파일 |
|---|---|
| A — ColorHelper.TryNormalizeHex | `Utils/ColorHelper.cs`, `UI/SettingsDialog.cs` |
| B — 다이얼로그 SafeFontHandle | `UI/Tray.cs`, `UI/SettingsDialog.cs` (+분할 후 `UI/Dialogs/*.cs`) |
| C — CleanupDialog IsDialogMessageW | `UI/Tray.cs` 또는 분할 후 `UI/Dialogs/CleanupDialog.cs` |
| D — Tray.cs 분할 | `UI/Tray.cs` → `UI/Dialogs/CleanupDialog.cs` + `UI/Dialogs/ScaleInputDialog.cs` |
| E — Config→Detector 해소 | `Detector/SystemFilter.cs`, `Config/Settings.cs`, 신규 `Utils/WindowProcessInfo.cs` |

충돌: B∩C∩D는 모두 Tray.cs를 건드리고, A∩B는 SettingsDialog.cs를 건드린다. **무조건 병렬 실행 불가**.

## 실행 스케줄

**Plan 에이전트 1대**가 먼저 5개 작업의 경계·헬퍼 시그니처를 검증하고 아래 스케줄을 확정.

| Wave | 작업 | 실행 방식 | 커밋 |
|---|---|---|---|
| 1 | D (Tray.cs 분할) + E (Config→Detector) | 병렬 | C1, C2 |
| 2 | C (CleanupDialog IsDialogMessageW) | 직렬 (after D) | C3 |
| 3 | B (SafeFontHandle 3 dialogs) | 직렬 (after C) | C4 |
| 4 | A (ColorHelper.TryNormalizeHex) | 직렬 (after B) | C5 |

각 Wave 완료 직후 `dotnet build` 빠른 검증 통과 → 다음 Wave 진입.

## 에이전트 브리핑

### Wave 1-D: Tray.cs 분할

- **편집 파일**: `UI/Tray.cs` → `UI/Dialogs/CleanupDialog.cs` + `UI/Dialogs/ScaleInputDialog.cs` 생성, `Utils/Win32DialogHelper.cs` 헬퍼 추가
- CleanupDialog 관련 멤버 전체(대략 [Tray.cs:656-909](../../UI/Tray.cs#L656-L909))를 신규 파일로 이동 — `ShowCleanupDialog`, `CleanupDlgProc`, private static 상태 필드(`_dlgResult`/`_dlgClosed`/`_hwndDialog`/`_checkboxHandles`/`_selectAllState`), Win32 윈도우 클래스 이름 문자열 `"KoEnVueCleanupDlg"` (C# 클래스가 아니라 `RegisterClassExW`에 넘기는 문자열임에 주의)
- ScaleInputDialog 관련 멤버(대략 [Tray.cs:910-1155](../../UI/Tray.cs#L910-L1155)) 동일 처리 — `ShowScaleInputDialog`, `ScaleDlgProc`, `TryCommitScaleInput`, `IsIntegerScale`, IDC_SCALE_EDIT/OK/CANCEL 상수, 상태 필드(`_scaleDlg*`), 윈도우 클래스 이름 `"KoEnVueScaleDlg"`
- **`ApplyFont` ([Tray.cs:851](../../UI/Tray.cs#L851)) 공용 헬퍼 처리**: CleanupDialog 5곳 + ScaleInputDialog 5곳에서 호출되므로 **어느 한쪽으로 이동 불가**. `Utils/Win32DialogHelper.cs`에 `public static void ApplyFont(IntPtr hwnd, IntPtr hFont)`로 이관하고, 두 분리된 다이얼로그 파일과 SettingsDialog(Wave 3-B에서 SafeFontHandle 적용 시 동일 호출)가 모두 `Win32DialogHelper.ApplyFont` 경유. P4 "Win32 dialog metrics → Win32DialogHelper" 원칙의 자연스러운 확장.
- Tray.cs 잔여 = 1169 − 약 495 = **약 670 라인** (NotifyIcon 라이프사이클, 메뉴 구성, 명령 분기, schtasks 동기화 + `RunSchtasks` 헬퍼). 500라인대가 목표라면 Wave 1-D에서 추가 분할 필요 (예: `Tray/StartupSync.cs`로 [Tray.cs:472-574](../../UI/Tray.cs#L472-L574)의 `SyncStartupPathCore`/`QueryRegisteredTaskCommand`/`ExtractCommandFromXml`/`PathsEqual` 묶음 추출). Stage 1에서는 **Tray.cs ≤ 700 라인**을 현실 목표로 삼고 추가 분할은 Stage 6 이후로 미뤄도 무방.
- `[UnmanagedCallersOnly]` WndProc 함수명 충돌 방지: `CleanupDlgProc`/`ScaleDlgProc` 유지
- 이동 대상은 **Tray의 내부 static 멤버 묶음**이지 C# 클래스가 아니다. 두 신규 파일 모두 자체 `internal static class CleanupDialog` / `internal static class ScaleInputDialog`로 감싸되, Tray.cs가 `CleanupDialog.Show(...)` / `ScaleInputDialog.Show(...)` 형태로 호출하도록 리팩터
- **Show 메서드 시그니처 (크로스파일 의존성 명시)**: 현재 `ShowCleanupDialog`/`ShowScaleInputDialog`는 `_hwndMain`(Tray의 private static)을 직접 읽는다. 분할 후에는 Tray가 소유권을 유지해야 하므로 파라미터 주입:
  - `CleanupDialog.Show(IntPtr hwndMain, List<string> items) → List<string>?`
  - `ScaleInputDialog.Show(IntPtr hwndMain, double initialValue) → double?`
- **Tray.cs에서 분리 파일로 함께 이동해야 하는 보조 심볼**:
  - CleanupDialog: `IDC_SELECT_ALL`/`IDC_CHECK_BASE`/`IDC_BTN_OK`/`IDC_BTN_CANCEL` 컨트롤 ID 상수, `CleanupDlgWidth`/`CleanupDlgRowH`/관련 치수 상수, `KoEnVueCleanupDlg` 클래스 이름
  - ScaleInputDialog: `IDC_SCALE_EDIT`/`IDC_SCALE_OK`/`IDC_SCALE_CANCEL` 상수, `ScaleDlgWidth`/`ScaleDlgPad`/`ScaleMinValue`/`ScaleMaxValue`/`ScaleTolerance`, `KoEnVueScaleDlg` 클래스 이름
  - Tray.cs에 남아야 하는 건 메뉴/NotifyIcon 관련 ID(`IDM_*`)와 `_hwndMain`/`_initialized` 등 트레이 고유 상태뿐
- **빌드 에러 기반 정리 전략**: 분할 후 첫 `dotnet build` 에러 리스트를 읽고, 각 `CS0103`(정의되지 않은 이름) / `CS0122`(보호 수준) 건을 위 목록에 따라 이동 대상/파라미터 주입으로 해소

### Wave 1-E: Config→Detector 레이어 위반 해소

- **편집 파일**: `Detector/SystemFilter.cs`, `Config/Settings.cs`, 신규 `Utils/WindowProcessInfo.cs`
- `SystemFilter.GetProcessName(hwnd)`와 `SystemFilter.GetClassName(hwnd)`를 `Utils/WindowProcessInfo.cs`로 이동
- Settings.cs:425-426과 SystemFilter 내부 호출부를 모두 새 경로로 변경
- `using KoEnVue.Detector;` 라인이 Config/Settings.cs에서 사라졌는지 확인

### Wave 2-C: CleanupDialog IsDialogMessageW 일관화

- **편집 파일**: `UI/Dialogs/CleanupDialog.cs` (Wave 1-D 이후 존재)
- 기존 `GetMessageW → TranslateMessage → DispatchMessageW` 루프 (원래 [Tray.cs:815-821](../../UI/Tray.cs#L815-L821))를 `IsDialogMessageW` 선처리 패턴으로 수정
- 참조 구현: ScaleDialog 루프 (원래 [Tray.cs:1060-1070](../../UI/Tray.cs#L1060-L1070) — Wave 1-D 이후 ScaleInputDialog.cs), SettingsDialog 루프 ([UI/SettingsDialog.cs:376-386](../../UI/SettingsDialog.cs#L376-L386))
- 수정 후 Tab/ESC 동작 수동 확인

### Wave 3-B: 다이얼로그 SafeFontHandle 적용

- **편집 파일**: `UI/Dialogs/CleanupDialog.cs`, `UI/Dialogs/ScaleInputDialog.cs`, `UI/SettingsDialog.cs`, 필요 시 `Native/SafeGdiHandles.cs`
- 3개 다이얼로그의 수동 `CreateFontW` + `DeleteObject` 쌍을 `SafeFontHandle`로 대체 (사전 분할 위치: `Tray.cs:706` CleanupDialog, `Tray.cs:965` ScaleInputDialog, `SettingsDialog.cs:176`. Wave 1-D 완료 후에는 `UI/Dialogs/CleanupDialog.cs`와 `UI/Dialogs/ScaleInputDialog.cs`로 옮겨져 있으므로 신규 파일에서 찾아 대체)
- 핸들 라이프타임: 다이얼로그 종료 시 `using` 스코프로 자동 해제 (`Show` 메서드 프레임 전체를 `using var hFont = new SafeFontHandle(Gdi32.CreateFontW(...), ownsHandle: true);`로 감쌈). 기존 `if (hFont != IntPtr.Zero) DeleteObject(hFont)` 패턴 완전 제거. `Win32DialogHelper.ApplyFont`에는 `hFont.DangerousGetHandle()` 전달 — HFONT가 dialog 수명 내내 유효해야 하므로 `using` 종료는 반드시 모달 루프 + DestroyWindow 후에 일어나야 함
- Overlay.cs의 기존 SafeFontHandle 사용 패턴을 참조 구현으로 삼음

### Wave 4-A: ColorHelper.TryNormalizeHex 추가

- **편집 파일**: `Utils/ColorHelper.cs`, `UI/SettingsDialog.cs`
- `SettingsDialog.TryNormalizeHexColor()` 로직을 `ColorHelper.TryNormalizeHex(string input, out string normalized)`로 이관
- 기존 구현은 `#RRGGBB` 또는 bare `RRGGBB` 6자 헥스만 지원 (`0x` 프리픽스 미지원). 새 공용 헬퍼도 동일 입력 스펙 유지해 동작 변경 없음
- `SettingsDialog` 호출부를 공용 헬퍼로 교체
- `#`/6자 헥스 정규화 패턴을 전역 grep (`grep -rn "TryNormalizeHex\|TryNormalizeHexColor"`) 으로 확인해 중복 없음 증명

## 검증 게이트 (Stage 1 종료 시)

- `dotnet build` + `dotnet publish -r win-x64 -c Release` 둘 다 경고 증가 없이 성공
- 런타임 스모크:
  - CleanupDialog 열기 → Tab 키로 포커스 순환, ESC로 닫힘 확인
  - ScaleInputDialog 열기 → 값 입력 후 Enter 저장 확인
  - SettingsDialog 열기 → 헥스 색상 필드에 `XYZ123` 입력 → 공용 `ColorHelper.TryNormalizeHex` 경유 검증 실패 메시지 표시 확인
  - IME 한/영 토글 → 오버레이 정상 업데이트
- `git grep "using KoEnVue\.Detector" Config/` → 0건

## 커밋 출력

| # | Wave | 커밋 제목 |
|---|---|---|
| C1 | 1-D | `refactor: split Tray.cs into Dialogs/CleanupDialog + ScaleInputDialog` |
| C2 | 1-E | `refactor: extract WindowProcessInfo to break Config→Detector layer violation` |
| C3 | 2-C | `fix: wrap CleanupDialog modal loop with IsDialogMessageW for Tab/ESC` |
| C4 | 3-B | `refactor: replace manual CreateFontW/DeleteObject with SafeFontHandle in dialogs` |
| C5 | 4-A | `refactor: move TryNormalizeHexColor to shared ColorHelper.TryNormalizeHex` |

---

← Back to [README](README.md)
