# KoEnVue

Windows 한/영 IME 상태 인디케이터 — 드래그 가능한 플로팅 오버레이로 현재 입력 모드를 실시간 표시.

> **한글** 모드면 "한", **영문** 모드면 "En", **비한국어 IME**면 "EN" 라벨이 화면 위에 떠 있어 타이핑 중 시선 이동 없이 입력 상태를 파악할 수 있습니다.

---

## 특징

- **플로팅 오버레이** — 항상 최상위에 떠 있는 투명 창. 마우스로 원하는 위치에 자유롭게 배치
- **앱별 위치 기억** — 프로세스별 인디케이터 위치를 자동 저장. 앱마다 다른 위치에 고정 가능
- **자석 스냅** — 드래그 시 인근 창 엣지·작업 영역 가장자리에 자석처럼 달라붙음 (토글 가능)
- **Shift 드래그 축 잠금** — Shift 누른 채 드래그하면 우세 축(가로/세로)으로만 이동
- **멀티 모니터 + DPI 완전 대응** — 모니터 간 드래그 시 실시간 DPI 재계산, 모서리 anchor 방식으로 해상도 변경에도 안정적
- **6개 테마 + 커스텀 색상** — Dracula / Solarized Dark / Nord / Monokai / Catppuccin / One Dark + 색상 직접 지정
- **크기 배율 1.0~5.0배** — 0.1 단위, 트레이 메뉴 "직접 지정" 대화상자로 설정
- **61개 설정 필드** — 트레이 "상세 설정" 스크롤 다이얼로그로 전체 설정 편집 가능
- **핫 리로드** — `config.json` 저장 즉시 앱 재시작 없이 자동 반영 (삭제/원자적 교체 안전)
- **완전 포터블** — NativeAOT 단일 exe (~4.7 MB), .NET 런타임 설치 불필요, 설치 과정 없음

---

## 요구 사항

- Windows 10 / 11 (x64)
- 한국어 IME (Microsoft 한국어 IME)
- 관리자 권한 (`app.manifest requireAdministrator`)

---

## 설치 및 실행

1. [Releases](../../releases)에서 `KoEnVue.exe` 다운로드
2. 원하는 폴더에 저장 후 실행
3. 시스템 트레이에 아이콘이 나타나고 인디케이터가 화면에 표시됨
4. 트레이 아이콘 우클릭 → **시작 프로그램 등록**으로 Windows 로그온 시 자동 시작 설정

**완전 삭제**: 트레이 → "시작 프로그램 등록" 해제 → 앱 종료 → exe 폴더 삭제.  
`config.json`과 `koenvue.log`가 exe 옆에 있으므로 폴더 하나만 지우면 완전 제거됩니다.

---

## 트레이 메뉴

```
투명도 ▸          진하게 / 보통 / 연하게
크기 ▸            1배 / 2배 / 3배 / 4배 / 5배 / 직접 지정
☑ 창에 자석처럼 붙이기
───
☑ 시작 프로그램 등록
───
기본 위치 ▸       현재 위치로 설정 / 초기화
미사용 위치 데이터 정리
───
상세 설정
───
종료
```

| 항목 | 설명 |
|------|------|
| **투명도** | 인디케이터 배경 불투명도 3단계 빠른 설정 |
| **크기** | 1~5배 정수 프리셋 + 소수점 직접 입력 다이얼로그 |
| **자석 스냅** | 드래그 중 창 엣지 스냅 on/off 토글 |
| **시작 프로그램 등록** | Windows 작업 스케줄러(schtasks) 기반 ONLOGON 등록 |
| **기본 위치** | 저장 위치가 없는 앱에 처음 표시될 기본 위치 설정 |
| **미사용 위치 데이터 정리** | 더 이상 실행되지 않는 프로세스의 저장 위치를 체크박스 다이얼로그로 선택 삭제 |
| **상세 설정** | 61개 설정 필드를 13개 섹션으로 구성한 스크롤 다이얼로그 |

---

## 주요 설정 (`config.json`)

exe 폴더의 `config.json`을 직접 편집하거나 트레이 **상세 설정** 대화상자를 사용하세요.  
변경 사항은 파일 저장 즉시 자동 반영됩니다. 첫 실행 시 기본값으로 자동 생성됩니다.

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

---

## 핫키

| 단축키 | 기능 |
|--------|------|
| `Ctrl+Alt+H` | 인디케이터 표시/숨기기 토글 |

트레이 상세 설정에서 변경 가능합니다.

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

## 기술 스택

- **언어/런타임**: C# 14 / .NET 10, NativeAOT
- **외부 패키지**: 없음 (.NET 10 BCL + Windows API P/Invoke 전용)
- **P/Invoke**: User32, Imm32, Shell32, Gdi32, Kernel32, Shcore, Dwmapi
- **렌더링**: GDI DIB section + UpdateLayeredWindow (premultiplied alpha)
- **설정**: System.Text.Json source generation
