# KoEnVue

Windows 한/영 IME 상태 인디케이터 — 드래그 가능한 플로팅 오버레이로 현재 입력 모드를 실시간 표시.

> **한글** 모드면 "한", **영문** 모드면 "En", **비한국어 IME**면 "EN" 라벨이 화면 위에 떠 있어 타이핑 중 시선 이동 없이 입력 상태를 파악할 수 있습니다. CAPS LOCK이 켜지면 라벨 좌우에 세로 막대가 함께 표시됩니다.

**완전 포터블** — NativeAOT 단일 exe (~4.7 MB), .NET 런타임 설치 불필요.

---

## 사용자 문서

사용 방법·트레이 메뉴·단축키·FAQ 는 **[docs/User_Guide.md](docs/User_Guide.md)** 를 참고하세요.

설계·스펙 문서는 **[docs/KoEnVue_PRD.md](docs/KoEnVue_PRD.md)** 에 있습니다.

---

## 다운로드

[Releases](../../releases) 에서 `KoEnVue.exe` 를 받아 원하는 폴더에 두고 실행하면 됩니다. Windows 10/11 x64, 관리자 권한이 필요합니다 (`app.manifest requireAdministrator`).

첫 공개 릴리스는 **v0.8.9.0** (2026-04-14) 입니다. 이 빌드부터 부팅 시 GitHub Releases 에서 새 버전을 1회 자동 확인해 트레이 메뉴 최상단에 "새 버전 있음" 항목을 띄웁니다(자동 설치는 아니며 사용자가 직접 새 exe 로 교체). 싫으면 `config.json` 에서 `update_check_enabled: false`.

---

## 빌드

```bash
# 요구 사항: .NET 10 SDK, Windows x64

# 디버그 빌드
dotnet build

# NativeAOT 릴리스 (단일 exe)
dotnet publish -r win-x64 -c Release
```

결과물: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`

---

## 릴리즈 (Releasing)

**유지보수자 전용** — 새 릴리스를 내릴 때의 순서입니다. 일반 사용자는 건너뛰세요.

버전 문자열은 **두 곳에서 동일하게** 관리됩니다. 둘 중 하나만 올리면 Windows 파일 속성(PE 헤더)과 런타임 업데이트 체크 값이 어긋나므로 **반드시 함께** 수정하세요.

1. **버전 bump (두 파일 동시 수정)**
   - [App/Config/DefaultConfig.cs](App/Config/DefaultConfig.cs) 의 `AppVersion` 상수 — `UpdateChecker` 가 GitHub `tag_name` 과 비교
   - [KoEnVue.csproj](KoEnVue.csproj) 의 `<Version>` 요소 — PE 헤더의 `AssemblyVersion` / `FileVersion` / `InformationalVersion` 3종에 박힘 (Windows 파일 속성 → 자세히 탭에서 보임)
   - 형식: `major.minor.build.revision` (4-part, 예: `0.8.9.0`). `System.Version.TryParse` 가 2~4-part 를 받아들이지만 4-part 로 맞추면 모든 PE 헤더 필드가 동일해져 혼동이 없음
2. **빌드 — 디버그와 릴리스 둘 다**
   ```bash
   dotnet build
   dotnet publish -r win-x64 -c Release
   ```
3. **GitHub 릴리스 작성** — 웹 UI `Releases → Draft a new release`
   - Tag: `vX.Y.Z.W` (예: `v0.8.9.0`) — 태그에 `v` 접두어 필수 (`UpdateChecker.NormalizeVersion` 이 벗겨냄)
   - Title: 자유 (예: `KoEnVue v0.8.9.0`)
   - Attach: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`
   - **"Set as a pre-release" 체크 해제** — 0.x.x 버전이라고 GitHub 가 자동으로 권장하지만, 체크하면 `release.prerelease=true` 로 `UpdateChecker` 가 건너뛰어 사용자에게 노출되지 않음. 정식 릴리스로 내보낼 때는 반드시 해제
   - Publish release
4. **검증 (선택)** — 이전 버전 exe 를 실행하면 트레이 메뉴 최상단에 "새 버전 있음 (vX.Y.Z.W) — 다운로드" 가 뜨는지 확인

> PE 헤더의 `InformationalVersion` 에 `+{gitHash}` 자동 접미어가 붙지 않도록 csproj 에 `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` 가 설정되어 있습니다. 태그로 빌드를 식별하므로 해시 중복 불필요.

---

## `config.json` 주요 키

exe 폴더의 `config.json` 을 직접 편집하거나 트레이 **상세 설정** 대화상자를 사용하세요. 저장 즉시 핫 리로드됩니다.

| 키 | 기본값 | 설명 |
|----|--------|------|
| `display_mode` | `"always"` | `"always"` 항상 표시 / `"on_event"` 이벤트 시만 |
| `indicator_scale` | `1.0` | 크기 배율 1.0~5.0 (0.1 단위) |
| `opacity` | `0.85` | 활성 불투명도 |
| `idle_opacity` | `0.4` | 유휴 불투명도 (Always 모드) |
| `position_mode` | `"window"` | `"fixed"` 화면 고정 / `"window"` 창 기준 상대 위치 |
| `snap_to_windows` | `true` | 드래그 중 창 엣지 자석 스냅 |
| `snap_gap_px` | `2` | 창 엣지 스냅 시 간격 (px, 0 = 밀착) |
| `drag_modifier` | `"none"` | 드래그 활성 키. `"none"` 항상 드래그 (클릭 소비) / `"ctrl"`·`"alt"`·`"ctrl_alt"` 해당 키 누른 상태에서만 드래그, 평상시 클릭·휠은 아래 창으로 투과 |
| `theme` | `"custom"` | `custom` / `minimal` / `vivid` / `pastel` / `dark` / `system` (6 프리셋) |
| `poll_interval_ms` | `80` | IME 감지 폴링 간격 (ms) |
| `hide_in_fullscreen` | `true` | 전체화면 앱에서 숨김 |
| `hide_when_no_focus` | `true` | 포커스 없는 창에서 숨김 |
| `tray_tooltip` | `true` | 트레이 아이콘 호버 툴팁 표시 |
| `update_check_enabled` | `true` | 부팅 시 GitHub Releases 에서 새 버전 1회 확인 (발견 시 트레이 메뉴 최상단에 "새 버전 있음" 항목 표시) |

전체 62개 설정 필드는 트레이 "상세 설정" 다이얼로그에서 13개 섹션으로 편집할 수 있습니다.

---

## 기술 스택

- **언어/런타임**: C# 14 / .NET 10, NativeAOT
- **외부 패키지**: 없음 (.NET 10 BCL + Windows API P/Invoke 전용)
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi, WinHttp
- **렌더링**: GDI DIB section + UpdateLayeredWindow (premultiplied alpha)
- **설정**: System.Text.Json source generation

---

## 개발자 문서

| 문서 | 내용 |
|------|------|
| [CLAUDE.md](CLAUDE.md) | 프로젝트 진입점 — P1-P6 하드 제약, 검증 invariants, 빌드 |
| [docs/architecture.md](docs/architecture.md) | Core/App 레이어 분리, 재사용 가능한 Core 모듈, 파사드 패턴 |
| [docs/conventions.md](docs/conventions.md) | 코드 스타일, silent catch 정책, .NET 10 / NativeAOT 호환 노트 |
| [docs/implementation-notes.md](docs/implementation-notes.md) | 인디케이터 렌더링·드래그·애니메이션·설정 핫리로드 등 구현 세부 |
| [docs/KoEnVue_PRD.md](docs/KoEnVue_PRD.md) | 제품 요구사항 문서 |

---

## 라이선스

[MIT License](LICENSE) — 자유로운 사용·수정·재배포 허용, 무보증(As-Is). 저작권 고지만 유지해 주세요.

`koenvue.ico` 아이콘은 본 저장소 소유자가 직접 제작한 것으로 MIT 적용 범위에 포함됩니다. 외부 라이선스 의존성은 없습니다 (.NET 10 BCL + Windows SDK P/Invoke 만 사용, NuGet 패키지 0개).
