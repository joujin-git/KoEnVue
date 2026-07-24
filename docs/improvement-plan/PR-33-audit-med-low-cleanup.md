# PR-33: AUDIT Med/Low cleanup (FG guard · I18n · tests · AppMessages · Program partial)

**Status**: ✅ implemented (build/test/publish PASS — 142 tests)
**Risk**: Low–Medium
**Estimated**: M–L
**Depends on**: DetectionService extract, InternalsVisibleTo, terminology rename `d1b4541`

## Scope

1. DetectionService / Program FG HWND=0 가드 대칭화 (커서 HWND non-zero 비교)
2. I18n `TrayPositionUnavailable` → 플로팅 배지 / floating badge
3. 단위 테스트: `UpdateChecker` · `MatchProfile`/`MergeProfile` · `ThemePresets.Apply` 백업/복원
4. `CursorRenderer` P3 단위구간 const
5. `AppMessages` → `App/Messaging/` (Detector/Config → UI 역의존 제거)
6. `Program` partial: OverlayDrag / SystemEvents / Timers
7. DetectionLoop stale 문서 앵커 정리

## Non-goals

DECISIONS.md 영구 보류(UIA, 글로벌 단축키, 코드 서명, 외부 LogFilePath, OverlayAnimator 추가 분해).

## Verification

```bash
dotnet build
dotnet test tests\KoEnVue.Tests\KoEnVue.Tests.csproj
dotnet publish -r win-x64 -c Release
git grep "using KoEnVue.App.UI" App/Detector App/Config   # AppMessages 용 0
git grep "DllImport"
```
