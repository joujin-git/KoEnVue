# 09 — Risks, Reuse, Exit Criteria

← Previous: [08 — Stage 7](08-stage7-final-gate.md)

## Risks & Mitigations

### Risk 1 — 소스 생성기 참조 끊김 (`[LibraryImport]`, `[JsonSerializable]`)

- **현상**: 네임스페이스 이동 후 source gen이 old 이름을 캐시할 수 있음
- **완화**: Stage 2는 단일 커밋·단일 빌드로 모든 끊김을 컴파일러가 드러내게 함. 중간 커밋 금지. SYSLIB1051 서스펜션이 기존 NoWarn으로 유지되는지 확인

### Risk 2 — NativeAOT ILC 트리밍/루팅 회귀

- **현상**: Core/App 분리 후 ILC가 특정 코드를 드롭할 가능성. 런타임 `Settings.Load` 예외로만 드러남(`JsonSerializerIsReflectionEnabledByDefault=false`)
- **완화**: Stage 2/3/4/5 매 단계 `dotnet publish` 후 스모크 실행. 핫-리로드와 `Corner` enum(`[JsonStringEnumMemberName]`) 직렬화 테스트를 회귀 가드로 사용

### Risk 3 — SafeFontHandle 도입 후 다이얼로그 폰트 수명 회귀

- **현상**: 기존 수동 `DeleteObject`와 수명 타이밍이 달라 조기 해제 시 `DrawTextW` 크래시
- **완화**: Stage 1 직후 각 다이얼로그를 개폐 사이클로 반복 테스트. `GetGuiResources`로 GDI 핸들 누수 확인. SafeHandle 필드는 다이얼로그 인스턴스 스코프 유지, `WM_NCDESTROY`에서 명시 해제

### Risk 4 — ImeState가 Core로 누출

- **현상**: Stage 4에서 LayeredOverlayBase / OverlayAnimator를 추출할 때 실수로 `using KoEnVue.App.Models;`가 들어갈 수 있음
- **완화**: Stage 7의 Explore 에이전트가 `git grep "ImeState" Core/`를 하드 실패 조건으로 검증. LayeredOverlayBase는 `OverlayStyle.LabelText`만 받고, OverlayAnimator는 state key를 추상 식별자(int/string)로 취급

### Risk 5 — Config→Detector 레이어 위반 재발

- **현상**: Stage 1에서 해소하더라도 차후 app-profile 기능 확장 시 재도입 가능
- **완화**: CLAUDE.md 신규 P6 규칙에 "Config/는 Detector/를 참조 불가" 한 줄 명시 + Stage 7 grep 검증 항목으로 고정

### Risk 6 — Tray.cs 분할에 따른 `[UnmanagedCallersOnly]` 함수명 충돌

- **현상**: 3개 WndProc가 서로 다른 파일에 있어도 NativeAOT unmanaged export 등록은 이름 기반 — 실수로 동일 헬퍼 이름이 생기면 링커 에러
- **완화**: `CleanupDlgProc`/`ScaleDlgProc`/`SettingsDlgProc` 명명 유지. 헬퍼 함수는 각 파일 내부 private static으로 국한

## Reused Existing Functions & Helpers

재구성 중 **새로 작성하지 말고 기존을 재사용**해야 할 항목:

- **SafeGdiHandles** (`Native/SafeGdiHandles.cs`)
  - 다이얼로그 폰트 관리에 확장 사용, 새 SafeHandle 만들지 말 것

- **Win32DialogHelper** (`Utils/Win32DialogHelper.cs`)
  - `CalculateNonClientHeight/Width`, `CalculateFontHeightPx` 이미 공용화되어 있음
  - 다이얼로그별 수동 계산 금지
  - Stage 1 Wave 1-D에서 `ApplyFont(hwnd, hFont)` (WM_SETFONT 래퍼)를 여기에 흡수 — CleanupDialog/ScaleInputDialog 분할 시 어느 한쪽에도 귀속될 수 없는 공용 3-line 헬퍼

- **ColorHelper** (`Utils/ColorHelper.cs`)
  - HEX↔COLORREF↔RGB 변환
  - 새 파싱 로직 금지 (Stage 1 A에서 TryNormalizeHex 통합)

- **DpiHelper** (`Utils/DpiHelper.cs`)
  - `GetScale`, `GetWorkArea`, `GetRawDpi`, `GetMonitorFromPoint` 기존 API 사용

- **Logger** (`Utils/Logger.cs`)
  - 비동기 큐/드레인 스레드/회전 이미 구현
  - 로깅 방식 추가하지 말 것

- **Native/Dwmapi.cs**의 `TryGetVisibleFrame`과 `IsCloaked`
  - 드래그/스냅 경로에서 재사용, `GetWindowRect` 직접 호출 금지

- **Native/Win32Types.cs**의 struct/constant
  - 새 const 선언하지 말고 이 파일에 추가 (P4)

- **Settings의 파이프라인** (`MergeWithDefaults` / `EnsureSubObjects` / `CheckConfigFileChange`)
  - Stage 4에서 JsonSettingsManager\<T\>로 이관될 때 로직 변경 없이 그대로 옮김

## Exit Criteria (전체 완료 조건)

1. Stage 0~7 모두 검증 게이트 통과
2. CLAUDE.md와 docs/KoEnVue_PRD.md가 새 구조를 정확히 기술
3. Core 독립성 증명:
   - `git grep "KoEnVue\.App" Core/` = 0
   - `git grep "ImeState" Core/` = 0
   - `git grep "Detector\." App/Config/` = 0
4. publish된 exe가 Stage 5 스모크 매트릭스 10개 항목 모두 정상 동작
5. 경고 증가 0, exe 크기 델타 ≤ +100KB
6. 추가 NuGet 패키지 0건 (P1 유지)
7. app.manifest requireAdministrator 유지 (P5)

## Critical Files Summary (Stage별)

### Stage 1 (in-place, Pre-relocation)
- `Utils/ColorHelper.cs` — TryNormalizeHex 추가
- `UI/Tray.cs` — 분할 + IsDialogMessageW 수정 + SafeFontHandle
- `UI/SettingsDialog.cs` — TryNormalizeHexColor 제거 + SafeFontHandle
- `Detector/SystemFilter.cs` — GetProcessName/GetClassName 추출
- `Config/Settings.cs` — WindowProcessInfo 경유로 변경
- 신규: `UI/Dialogs/CleanupDialog.cs`, `UI/Dialogs/ScaleInputDialog.cs`, `Utils/WindowProcessInfo.cs`

### Stage 2 (전수 파일 이동)
- 모든 `.cs` 파일 (Program.cs 제외)

### Stage 3 (시그니처 좁힘)
- `App/UI/Overlay.cs`, `App/UI/Animation.cs`, `Core/Logging/Logger.cs`
- `App/Detector/ImeStatus.cs`, `App/Config/Settings.cs`, `Program.cs`

### Stage 4 (Core 추출)
- 신규: `Core/Windowing/LayeredOverlayBase.cs`, `Core/Windowing/OverlayStyle.cs`, `Core/Animation/OverlayAnimator.cs`, `Core/Config/JsonSettingsFile.cs`, `Core/Config/JsonSettingsManager.cs`, `Core/Tray/NotifyIconManager.cs`, `Core/Windowing/ModalDialogLoop.cs`
- 편집: `App/UI/Overlay.cs`, `App/UI/Animation.cs`, `App/Config/Settings.cs`, `App/UI/Tray.cs`, 3개 다이얼로그 파일

### Stage 6 (문서)
- `CLAUDE.md`, `docs/KoEnVue_PRD.md`

---

← Back to [README](README.md)
