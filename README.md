# KoEnVue

[![build](https://github.com/joujin-git/KoEnVue/actions/workflows/build.yml/badge.svg)](https://github.com/joujin-git/KoEnVue/actions/workflows/build.yml)

Windows 한/영 IME 상태 인디케이터 — 드래그 가능한 플로팅 오버레이로 현재 입력 모드를 실시간 표시.

> **한글** 모드면 "한", **영문** 모드면 "En", **비한국어 IME**면 "EN" 라벨이 화면 위에 떠 있어 타이핑 중 시선 이동 없이 입력 상태를 파악할 수 있습니다. CAPS LOCK이 켜지면 라벨 좌우에 세로 막대가 함께 표시됩니다.

**완전 포터블** — NativeAOT 단일 exe (~4.7 MB), .NET 런타임 설치 불필요.

---

## 사용자 문서

사용 방법·트레이 메뉴·단축키·FAQ 는 **[docs/User_Guide.md](docs/User_Guide.md)** 를 참고하세요.

설계·스펙 문서는 **[docs/KoEnVue_PRD.md](docs/KoEnVue_PRD.md)** 에 있습니다.

---

## 다운로드

[Releases](../../releases) 에서 `KoEnVue.exe` 를 받아 원하는 폴더에 두고 실행하면 됩니다. Windows 10/11 x64. v0.10.0 부터 관리자 권한이 필요 없습니다 (`app.manifest asInvoker`) — UAC 프롬프트 없이 바로 실행됩니다.

**권장 설치 위치**: `%USERPROFILE%` 하위(예: 바탕화면, 문서 폴더), 또는 USB 같은 사용자가 쓰기 가능한 위치. `Program Files` 처럼 사용자 쓰기 불가 위치에 두면 `config.json` 과 `koenvue.log` 가 자동으로 `%LOCALAPPDATA%\KoEnVue\` 로 fallback 합니다(완전 포터블 시나리오를 원하면 user-writable 위치 권장).

첫 공개 릴리스는 **v0.8.9.0** (2026-04-14) 입니다. 이 빌드부터 부팅 시 GitHub Releases 에서 새 버전을 1회 자동 확인해, 새 버전이 있으면 트레이 메뉴 최상단 헤더 라벨이 평소 `KoEnVue v{ver} — GitHub` 에서 `KoEnVue v{cur} → v{new} — 다운로드` 로 자동 전환됩니다(자동 설치는 아니며 사용자가 직접 새 exe 로 교체). 싫으면 `config.json` 에서 `update_check_enabled: false`.

### 다운로드 검증 (SHA256)

KoEnVue 는 무서명 exe 입니다 — Windows SmartScreen 이 "확인되지 않은 게시자" 경고를 띄울 수 있어요. 다운로드한 파일이 GitHub 가 게시한 정본인지 PowerShell 한 줄로 확인할 수 있습니다.

```powershell
Get-FileHash -Algorithm SHA256 .\KoEnVue.exe
```

출력의 `Hash` 값이 [Releases](../../releases) 페이지 본문에 첨부된 `KoEnVue.exe.sha256.txt` 의 hex 문자열과 일치하면 다운로드는 변조되지 않았습니다. 비교 대상은 **유지보수자가 직접 빌드해 GitHub Releases 에 첨부한 정본 hash** 입니다 — 본인이 소스에서 다시 빌드한 exe 와는 hash 가 다를 수 있어요 (NativeAOT ILC 가 codegen 단계에서 비결정성을 가져 머신·빌드별로 hash 가 달라짐. C# 컴파일러는 `<Deterministic>true</Deterministic>` 로 결정적이지만 AOT 단계는 아직 보장 안 됨 — [docs/dev-notes/2026-05-21-signing-decision.md](docs/dev-notes/2026-05-21-signing-decision.md) 참고). 정본 hash 와 다운로드 hash 가 불일치하면 다운로드를 중단하고 Issue 로 보고해 주세요.

> EV/OV 코드 사인 인증서는 ROI 가 낮아 도입하지 않습니다. SmartScreen 평판은 다운로드 누적에 따라 자연 학습됩니다. [docs/dev-notes/2026-05-21-signing-decision.md](docs/dev-notes/2026-05-21-signing-decision.md) 에 결정 근거 + 재검토 트리거가 있습니다.

---

## 빌드

```bash
# 요구 사항: .NET 10 SDK, Windows x64

# 디버그 빌드
dotnet build

# 단위 테스트
dotnet test tests/KoEnVue.Tests/

# NativeAOT 릴리스 (단일 exe)
dotnet publish -r win-x64 -c Release
```

결과물: `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe`

코드 기여 / 개선 PR 절차는 [CONTRIBUTING.md](CONTRIBUTING.md) 를 참고하세요.

---

## 릴리즈 (Releasing)

**유지보수자 전용** — 새 릴리스를 내릴 때의 순서입니다. 일반 사용자는 건너뛰세요. 전체 절차는 **[docs/release-procedure.md](docs/release-procedure.md)** 에 정리돼 있고, 아래는 핵심 요약입니다.

버전 문자열은 PR-11 D6 이후 [KoEnVue.csproj](KoEnVue.csproj) 의 `<Version>` 하나가 단일 진실원입니다. `Directory.Build.targets` 의 `GenerateVersionConstants` Target 가 빌드 시점에 `obj/.../Version.g.cs` 로 `DefaultConfig.AppVersion` partial 조각을 자동 생성해 PE 헤더 (`AssemblyVersion` / `FileVersion` / `InformationalVersion` 3종) 와 런타임 비교 값이 정합 유지됩니다.

1. **버전 bump** — csproj `<Version>` 한 줄만 수정. 예: `<Version>0.10.0</Version>`. 형식은 `major.minor.build[.revision]` (2~4-part, `System.Version.TryParse` 가 받음).
2. **빌드 — 디버그와 릴리스 둘 다**
   ```bash
   dotnet build
   dotnet publish -r win-x64 -c Release
   ```
   `publish/KoEnVue.exe` 옆에 `KoEnVue.exe.sha256.txt` 가 자동 생성됩니다 (PR-11 G4, `Directory.Build.targets` 의 `EmitSha256` Target).
3. **GitHub 릴리스 작성** — 웹 UI `Releases → Draft a new release`
   - Tag: `vX.Y.Z[.W]` (예: `v0.10.0`) — 태그에 `v` 접두어 필수 (`UpdateChecker.NormalizeVersion` 이 벗겨냄)
   - Title: 자유 (예: `KoEnVue v0.10.0`)
   - Attach: `publish/KoEnVue.exe` + `publish/KoEnVue.exe.sha256.txt`
   - Body 에 SHA256 hash 값 인용 (사용자가 다운로드 후 `Get-FileHash` 로 비교)
   - **"Set as a pre-release" 체크 해제** — 0.x.x 버전이라고 GitHub 가 자동으로 권장하지만, 체크하면 `release.prerelease=true` 로 `UpdateChecker` 가 건너뛰어 사용자에게 노출되지 않음. 정식 릴리스로 내보낼 때는 반드시 해제
   - Publish release
4. **검증 (선택)** — 이전 버전 exe 를 실행하면 트레이 메뉴 최상단 헤더 라벨이 `KoEnVue v{old} → v{new} — 다운로드` 로 자동 전환되는지 확인

> PE 헤더의 `InformationalVersion` 에 `+{gitHash}` 자동 접미어가 붙지 않도록 csproj 에 `<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>` 가 설정되어 있습니다. 태그로 빌드를 식별하므로 해시 중복 불필요. `<Deterministic>true</Deterministic>` 는 C# 컴파일러 결정성용 — NativeAOT ILC codegen 은 별개로 비결정성을 가지므로 같은 머신에서 publish 를 반복해도 SHA256 이 달라집니다. GitHub Releases 에 첨부된 정본 hash 만 canonical 로 취급해 주세요.

---

## `config.json` 주요 키

exe 폴더의 `config.json` 을 직접 편집하거나 트레이 **상세 설정** 대화상자를 사용하세요. 저장 즉시 핫 리로드됩니다.

| 키 | 기본값 | 설명 |
|----|--------|------|
| `display_mode` | `"always"` | `"always"` 항상 표시 / `"on_event"` 이벤트 시만 |
| `indicator_scale` | `2.0` | 크기 배율 1.0~5.0 (0.1 단위) |
| `opacity` | `0.85` | 활성 불투명도 |
| `idle_opacity` | `0.55` | 유휴 불투명도 (Always 모드) |
| `position_mode` | `"window"` | `"fixed"` 화면 고정 / `"window"` 창 기준 상대 위치 |
| `snap_to_windows` | `true` | 드래그 중 창 엣지 자석 스냅 |
| `snap_gap_px` | `10` | 창 엣지 스냅 시 간격 (px, 0 = 밀착) |
| `drag_modifier` | `"none"` | 드래그 개시 게이트. `"none"` 항상 드래그 / `"ctrl"`·`"alt"`·`"ctrl_alt"` 해당 키 누른 상태에서만 드래그 개시 (나머지 클릭은 오버레이가 소비, 크로스 프로세스 투과는 미지원) |
| `theme` | `"custom"` | `custom` / `minimal` / `vivid` / `pastel` / `dark` / `system` (6 프리셋) |
| `poll_interval_ms` | `80` | IME 감지 폴링 간격 (ms) |
| `hide_in_fullscreen` | `true` | 전체화면 앱에서 숨김 |
| `hide_when_no_focus` | `true` | 포커스 없는 창에서 숨김 |
| `tray_tooltip` | `true` | 트레이 아이콘 호버 툴팁 표시 |
| `user_hidden` | `false` | 사용자가 인디케이터를 숨긴 상태. `true` 일 때 아이콘 위에 굵은 취소선 1줄 표시, 감지 이벤트가 인디를 다시 띄우지 못하게 차단. 재기동에도 유지. 토글 경로: 트레이 좌클릭(좌클릭 동작이 `toggle` 일 때) 또는 우클릭 메뉴 "인디케이터 숨김" |
| `update_check_enabled` | `true` | 부팅 시 GitHub Releases 에서 새 버전 1회 확인 (발견 시 트레이 메뉴 최상단 헤더 라벨이 `KoEnVue v{cur} → v{new} — 다운로드` 로 자동 전환) |

전체 설정 필드는 트레이 "상세 설정" 대화상자에서 섹션별로 편집할 수 있습니다 (표시 모드·외관·애니메이션·감지·앱별 프로필·트레이·시스템·업데이트·인디케이터 조작·위치·단축 작업·고급 등).

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
