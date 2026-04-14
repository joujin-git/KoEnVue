# KoEnVue

Windows 한/영 IME 상태 인디케이터 — 드래그 가능한 플로팅 오버레이로 현재 입력 모드를 실시간 표시.

> **한글** 모드면 "한", **영문** 모드면 "En", **비한국어 IME**면 "EN" 라벨이 화면 위에 떠 있어 타이핑 중 시선 이동 없이 입력 상태를 파악할 수 있습니다. CAPS LOCK이 켜지면 라벨 좌우에 세로 막대가 함께 표시됩니다.

**완전 포터블** — NativeAOT 단일 exe (~4.9 MB), .NET 런타임 설치 불필요.

---

## 사용자 문서

사용 방법·트레이 메뉴·단축키·FAQ 는 **[docs/사용설명서.md](docs/사용설명서.md)** 를 참고하세요.

설계·스펙 문서는 **[docs/KoEnVue_PRD.md](docs/KoEnVue_PRD.md)** 에 있습니다.

---

## 다운로드

[Releases](../../releases) 에서 `KoEnVue.exe` 를 받아 원하는 폴더에 두고 실행하면 됩니다. Windows 10/11 x64, 관리자 권한이 필요합니다 (`app.manifest requireAdministrator`).

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

## `config.json` 주요 키

exe 폴더의 `config.json` 을 직접 편집하거나 트레이 **상세 설정** 대화상자를 사용하세요. 저장 즉시 핫 리로드됩니다.

| 키 | 기본값 | 설명 |
|----|--------|------|
| `display_mode` | `"always"` | `"always"` 항상 표시 / `"on_event"` 이벤트 시만 |
| `indicator_scale` | `1.0` | 크기 배율 1.0~5.0 (0.1 단위) |
| `opacity` | `0.85` | 활성 불투명도 |
| `idle_opacity` | `0.4` | 유휴 불투명도 (Always 모드) |
| `snap_to_windows` | `true` | 드래그 중 창 엣지 자석 스냅 |
| `theme` | `"custom"` | `dracula` / `solarized_dark` / `nord` / `monokai` / `catppuccin` / `one_dark` / `custom` |
| `poll_interval_ms` | `80` | IME 감지 폴링 간격 (ms) |
| `hide_in_fullscreen` | `true` | 전체화면 앱에서 숨김 |
| `hide_when_no_focus` | `true` | 포커스 없는 창에서 숨김 |
| `tray_tooltip` | `true` | 트레이 아이콘 호버 툴팁 표시 |
| `update_check_enabled` | `true` | 부팅 시 GitHub Releases 에서 새 버전 1회 확인 (발견 시 트레이 메뉴 최상단에 "새 버전 있음" 항목 표시) |

전체 59개 설정 필드는 트레이 "상세 설정" 다이얼로그에서 13개 섹션으로 편집할 수 있습니다.

---

## 기술 스택

- **언어/런타임**: C# 14 / .NET 10, NativeAOT
- **외부 패키지**: 없음 (.NET 10 BCL + Windows API P/Invoke 전용)
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Ole32, OleAut32, Dwmapi, WinHttp
- **렌더링**: GDI DIB section + UpdateLayeredWindow (premultiplied alpha)
- **설정**: System.Text.Json source generation

아키텍처 (Core/App 레이어 분리, P1-P6 하드 제약, 재사용 가능한 Core 모듈 목록)는 `CLAUDE.md` 참고.
