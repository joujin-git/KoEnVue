# `config.json` 전체 키 레퍼런스

KoEnVue 의 `config.json` 에서 사용 가능한 **모든** 설정 키 — 101 항목 (커서 인디 16 키 = 동심원 10 + 이동 딤 3 + 전환 효과 3, PR-15 `admin_elevation` 포함). 트레이 메뉴의 "상세 설정" 다이얼로그가 대부분을 GUI 로 제공하지만, **앱별 프로필** (`app_profiles`) 처럼 GUI 미노출 키는 직접 편집해야 합니다.

`config.json` 의 위치: `%LOCALAPPDATA%\KoEnVue\config.json` (기본) 또는 exe 폴더 (writable 일 때). 자세한 결정 절차는 [README §다운로드](../README.md) 의 권장 설치 위치 절을 참고하세요. 저장 즉시 **핫 리로드** 됩니다 (메인 스레드 mtime 폴링, ~5초 간격).

> 키 표기: JSON 은 `snake_case`, C# 소스 (`AppConfig`) 는 `PascalCase`. 본 문서는 JSON 표기 기준. 클램프 범위는 [App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs) 의 `Min/MaxX` const 16쌍이 단일 진실원 — [App/Config/Settings.cs:107](../App/Config/Settings.cs#L107) `Validate` 가 범위를 벗어나면 silent 보정합니다.

---

## 표시 모드 (Display)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `display_mode` | enum | `"always"` | `always` / `on_event` | `always` 는 인디케이터를 항상 표시 (유휴 시 `idle_opacity`). `on_event` 는 IME / 포커스 변경 이벤트 시에만 페이드인 → 유지 → 페이드아웃 |
| `event_display_duration_ms` | int | `2000` | 500 ~ 10000 | `on_event` 모드의 유지 시간 (ms) |
| `always_idle_timeout_ms` | int | `3000` | 1000 ~ 30000 | `always` 모드에서 유휴 전환 (`idle_opacity` 로 dim) 까지 대기 시간 (ms). 0 이면 즉시 dim |
| `event_triggers.on_focus_change` | bool | `true` | — | 포그라운드 윈도우 변경 시 인디 표시 트리거 |
| `event_triggers.on_ime_change` | bool | `true` | — | IME 상태 변경 (한/영 전환 등) 시 인디 표시 트리거 |

## 외관 — 스타일 (Appearance / Layout)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `label_width` | int | `28` | 16 ~ 128 | 라벨 박스 폭 (DPI 스케일링 + `indicator_scale` 배율 적용 전 px) |
| `label_height` | int | `24` | 12 ~ 96 | 라벨 박스 높이 (px) |
| `label_border_radius` | int | `6` | 0 ~ 48 | 라벨 박스 모서리 둥글기 (px). 0 = 직각, 큰 값 = 캡슐 |
| `border_width` | int | `0` | 0 ~ 8 | 라벨 박스 외곽선 두께 (px). 0 = 외곽선 없음 |
| `border_color` | string | `"#000000"` | `#RRGGBB` | 외곽선 색상 |
| `indicator_scale` | double | `2.0` | 1.0 ~ 5.0 (0.1 단위) | 인디 전체 크기 배율 — 라벨 폭/높이/폰트/모서리/외곽선/패딩에 곱해짐. DPI 스케일링과 독립 적용 |

## 외관 — 색상 (Colors)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `hangul_bg` | string | `"#16A34A"` | `#RRGGBB` | 한글 모드 배경색 |
| `hangul_fg` | string | `"#FFFFFF"` | `#RRGGBB` | 한글 모드 글자색 |
| `english_bg` | string | `"#D97706"` | `#RRGGBB` | 영문 모드 배경색 |
| `english_fg` | string | `"#FFFFFF"` | `#RRGGBB` | 영문 모드 글자색 |
| `non_korean_bg` | string | `"#6B7280"` | `#RRGGBB` | 비한국어 IME 모드 배경색 |
| `non_korean_fg` | string | `"#FFFFFF"` | `#RRGGBB` | 비한국어 IME 모드 글자색 |
| `opacity` | double | `0.85` | 0.1 ~ 1.0 | `on_event` 모드 표시 시 불투명도 |
| `idle_opacity` | double | `0.55` | 0.1 ~ 1.0 | `always` 모드 유휴 (`always_idle_timeout_ms` 경과 후) 불투명도 |
| `active_opacity` | double | `0.95` | 0.1 ~ 1.0 | `always` 모드 활성 (이벤트 직후) 불투명도 |

## 외관 — 텍스트 (Text)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `font_family` | string | `"맑은 고딕"` | — | 라벨 텍스트 폰트 패밀리 |
| `font_size` | int | `12` | 8 ~ 36 | 라벨 텍스트 폰트 크기 (pt). DPI + `indicator_scale` 와 합쳐 적용 |
| `font_weight` | enum | `"Bold"` | `Normal` / `Bold` | 폰트 굵기 |
| `hangul_label` | string | `"한"` | — | 한글 모드에 표시할 라벨 문자열 |
| `english_label` | string | `"En"` | — | 영문 모드 라벨 |
| `non_korean_label` | string | `"EN"` | — | 비한국어 IME 모드 라벨 |

## 외관 — 테마 (Theme)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `theme` | enum | `"custom"` | `custom` / `minimal` / `vivid` / `pastel` / `dark` / `system` | 6 프리셋. `custom` 외의 값은 위 6개 색상 키를 자동으로 덮어씀. `system` 은 Windows 강조색 + 고대비 모드 자동 추적 (PR-14, [DwmGetColorizationColor] + `COLOR_HIGHLIGHT` 폴백) |
| `custom_backup_hangul_bg` | string? | `null` | — | 프리셋 적용 직전 `hangul_bg` 백업. `theme` 을 다시 `custom` 으로 돌리면 자동 복원. 사용자가 직접 편집할 일 없음 |
| `custom_backup_hangul_fg` | string? | `null` | — | 동상, `hangul_fg` 백업 |
| `custom_backup_english_bg` | string? | `null` | — | 동상, `english_bg` 백업 |
| `custom_backup_english_fg` | string? | `null` | — | 동상, `english_fg` 백업 |
| `custom_backup_non_korean_bg` | string? | `null` | — | 동상, `non_korean_bg` 백업 |
| `custom_backup_non_korean_fg` | string? | `null` | — | 동상, `non_korean_fg` 백업 |

## 애니메이션 (Animation)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `animation_enabled` | bool | `true` | — | `false` 면 fade/highlight/slide 모든 애니메이션 비활성 (즉시 표시/숨김) |
| `fade_in_ms` | int | `150` | 0 ~ 2000 | 페이드인 지속 (ms). 0 = 즉시 |
| `fade_out_ms` | int | `400` | 0 ~ 2000 | 페이드아웃 지속 (ms) |
| `change_highlight` | bool | `true` | — | IME 전환 시 잠깐 확대되는 강조 효과 |
| `highlight_scale` | double | `1.3` | 1.0 ~ 2.0 | 강조 시 확대 배율 |
| `highlight_duration_ms` | int | `300` | 0 ~ 2000 | 강조 → 원래 크기 복귀 시간 (ms) |
| `slide_animation` | bool | `true` | — | 같은 모니터 내 위치 변경 시 미끄러지듯 이동 (모니터 간 이동은 DPI 변동으로 슬라이드 생략·즉시 이동) |
| `slide_speed_ms` | int | `500` | 0 ~ 2000 | 슬라이드 지속 (ms) |

## 동작 — 감지 (Detection)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `poll_interval_ms` | int | `80` | 50 ~ 500 | IME 감지 폴링 간격 (ms). 낮을수록 반응 빠름·CPU 부담 |
| `detection_method` | enum | `"auto"` | `auto` / `ime_default` / `ime_context` / `keyboard_layout` | IME 상태 감지 경로 선택. `auto` 는 3-tier 폴백 (`ime_default` → `ime_context` → `keyboard_layout`). 특정 앱에서 미감지 시 단일 tier 강제 |
| `non_korean_ime` | enum | `"hide"` | `hide` / `show` / `dim` | 비한국어 IME (일/중 등) 활성 시 행동. `hide` 숨김 / `show` 그대로 표시 / `dim` 50% 투명도로 표시 |
| `hide_in_fullscreen` | bool | `true` | — | 전체화면 독점 앱 (게임 등) 에서 인디 숨김 |
| `hide_when_no_focus` | bool | `true` | — | 키보드 포커스 없는 창에서 인디 숨김 |
| `hide_on_lock_screen` | bool | `true` | — | 잠금 화면 (WTS 이벤트) 에서 인디 즉시 숨김. 잠금 해제 후 첫 포커스 변경 시 복원 |

## 동작 — 시스템 창 필터 (System Window Filter)

`SystemFilter` 의 클래스명/프로세스명 블랙리스트 — 8조건 단락 평가 중 4·5. `*_user` 변형은 사용자 추가용 (기본 리스트와 합쳐서 매칭).

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `system_hide_classes` | string[] | `["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow_WASDK", "TopLevelWindowForOverflowXamlIsland", "ControlCenterWindow"]` | 바탕화면 + 작업 표시줄 + Win11 트레이 오버플로 + Quick Settings (Win+A) 클래스명. 일반 사용자가 편집할 일은 거의 없음 |
| `system_hide_classes_user` | string[] | `[]` | 사용자 추가 클래스명 |
| `system_hide_processes` | string[] | `["ShellExperienceHost"]` | 작업 표시줄/바탕화면 우클릭 컨텍스트 메뉴 같은 Win11 팝업 (포그라운드 + null owner) |
| `system_hide_processes_user` | string[] | `[]` | 사용자 추가 프로세스명 (확장자 없음) |

## 앱별 프로필 + 필터 (App Profiles + Filter)

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `app_profiles` | object | `{}` | 프로세스명 (또는 정규식, `app_profile_match` 에 따라) 키 → 부분 AppConfig override 값. 매칭 시 글로벌 `AppConfig` 와 deep merge 후 적용. GUI 미노출 — config.json 직접 편집. 사용 예: `{"chrome": {"opacity": 0.5, "theme": "dark"}, "notepad": {"enabled": false}}` |
| `app_profile_match` | enum | `"process"` | `process` / `title` / `class_name`. 매칭 키 종류 |
| `app_filter_mode` | enum | `"blacklist"` | `blacklist` / `whitelist`. `app_filter_list` 의 의미 결정 |
| `app_filter_list` | string[] | `[]` | 프로세스명 리스트. blacklist 모드: 리스트의 앱에서 숨김. whitelist 모드: 리스트의 앱에서만 표시 |

## 시스템 트레이 (System Tray)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `tray_enabled` | bool | `true` | — | 트레이 아이콘 표시. `false` 면 메뉴 접근 불가 — config.json 직접 편집 + 재기동만 가능 |
| `tray_tooltip` | bool | `true` | — | 트레이 아이콘 호버 시 툴팁 표시 |
| `tray_click_action` | enum | `"toggle"` | `toggle` / `settings` | 트레이 좌클릭 동작. `toggle` = `user_hidden` 토글, `settings` = 설정 파일 열기 |
| `tray_quick_opacity_presets` | double[] | `[0.95, 0.85, 0.6]` | 0.1 ~ 1.0 | 트레이 메뉴 "빠른 투명도" 서브메뉴에 노출할 프리셋 3개. 기본값은 `DefaultConfig.TrayQuickOpacity1/2/3` const + `TrayQuickOpacityPresets` property 단일 진실원에서 derive (감사 High ④, 2026-06-01 — 값 불변) |
| `user_hidden` | bool | `false` | — | 사용자가 명시 숨긴 상태. `true` 면 트레이 아이콘에 취소선 + 감지 이벤트로 인디 복원 차단. 재기동에도 유지 |

## 시스템 — 권한 (Privileges)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `admin_elevation` | bool | `false` | — | UIPI 우회용 관리자 권한 실행 옵션 (PR-15). `false` (default) — 매니페스트 `asInvoker` (PR-03) 그대로, UAC 0. `true` — (a) 단일 실행은 부팅 시 [AdminElevation.TryRelaunchAsAdmin](../App/Bootstrap/AdminElevation.cs) 가 `ShellExecuteW("runas")` 로 자기 재실행 (UAC 1회), (b) 부팅 자동 시작은 schtasks `<RunLevel>HighestAvailable</RunLevel>` 으로 등록 (등록 시 UAC 1회, 부팅마다 0). UAC 거부 시 안내 다이얼로그 후 일반 권한 계속 (관리자 콘솔의 한/영 표시 미작동). 트레이 메뉴 "관리자 권한으로 실행" + Settings 다이얼로그 "시스템" 섹션 양쪽에서 토글 가능. 토글 즉시 schtasks 등록 상태도 자동 재등록 (`ReregisterIfAdminChanged`). 자세한 설계: [dev-notes/2026-05-27-admin-elevation.md](dev-notes/2026-05-27-admin-elevation.md) |

## 시스템 — 로깅 (Logging)

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `log_level` | enum | `"INFO"` | `DEBUG` / `INFO` / `WARNING` / `ERROR` | 로그 레벨. DEBUG 는 상세 (시작 메뉴 ↔ 검색 창 전환 등 80ms 폴링 결과 포함) |
| `language` | enum | `"auto"` | `auto` / `ko` / `en` | UI 언어 (트레이 메뉴 / 다이얼로그 / 툴팁). `auto` = Windows 시스템 언어 자동 감지 |
| `log_to_file` | bool | `true` | — | `koenvue.log` 파일 쓰기. `false` = Trace 만 (디버거 부착 시 가시) |
| `log_file_path` | string | `""` | — | 사용자 지정 로그 경로. 빈 문자열 = 기본 (`exe폴더\koenvue.log` 또는 `%LOCALAPPDATA%\KoEnVue\koenvue.log`). [PortablePath.SanitizeLogPath](../App/Config/PortablePath.cs) 가 허용 루트 외 값은 거절 |
| `log_max_size_mb` | int | `10` | 1 ~ 100 | 로그 파일 최대 크기 (MB). 도달 시 `koenvue.log.1` 로 단일 회전 |

## 업데이트 (Update)

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `update_check_enabled` | bool | `true` | 부팅 시 GitHub Releases API 1회 조회. 새 버전 발견 시 트레이 메뉴 헤더 라벨이 `KoEnVue v{cur} → {newTag} — 다운로드` 로 자동 전환. `false` 면 네트워크 호출 자체 안 함 (오프라인/사내망 친화) |

## 인디케이터 위치 (Indicator Position)

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `position_mode` | enum | `"window"` | `fixed` / `window`. `fixed` = 화면 작업영역 기준 절대 좌표, `window` = 포그라운드 창 DWM 프레임 기준 상대 오프셋 (창 이동 시 인디도 따라감) |
| `indicator_positions` | object | `{}` | (고정 모드) 프로세스명 → `[x, y]` 절대 좌표. 드래그 종료 시 자동 저장 |
| `indicator_positions_relative` | object | `{}` | (창 기준 모드) 프로세스명 → `[corner, deltaX, deltaY]`. `corner`: 0=TopLeft / 1=TopRight / 2=BottomLeft / 3=BottomRight |
| `default_indicator_position` | object? | `null` | (고정 모드) 저장 위치 없는 앱의 기본 위치. `{"corner": "TopRight", "delta_x": -200, "delta_y": 10}` 형식. `null` = 하드코딩 폴백 (work area 우상단) |
| `default_indicator_position.corner` | enum | `"TopRight"` | `TopLeft` / `TopRight` / `BottomLeft` / `BottomRight`. 작업영역 모서리 앵커 |
| `default_indicator_position.delta_x` | int | `-200` | 앵커에서 X 오프셋 (음수 = 왼쪽 / 위쪽). 논리 px |
| `default_indicator_position.delta_y` | int | `10` | 앵커에서 Y 오프셋 (양수 = 오른쪽 / 아래쪽). 논리 px |
| `default_indicator_position_relative` | object? | `{corner: "BottomRight", delta_x: -69, delta_y: -58}` | (창 기준 모드) 저장 위치 없는 앱의 기본 위치. init 디폴트는 `DefaultConfig.DefaultRelative{Corner,OffsetX,OffsetY}` 3 const 를 직접 참조하는 객체이며, 사용자가 명시적으로 `null` 로 저장한 경우에도 Overlay 가 동일 const 로 폴백 (두 경로 단일 진실원) |
| `default_indicator_position_relative.corner` | enum | `"BottomRight"` | 창 DWM visible frame 의 4 모서리 |
| `default_indicator_position_relative.delta_x` | int | `-69` | 앵커에서 X 오프셋 (논리 px). 창의 모니터 DPI 스케일로 변환 후 적용 |
| `default_indicator_position_relative.delta_y` | int | `-58` | 앵커에서 Y 오프셋 (논리 px) |
| `snap_to_windows` | bool | `true` | 드래그 중 다른 창 엣지 + 모니터 작업영역 엣지에 자석처럼 붙음 |
| `snap_gap_px` | int | `10` | 0 ~ 10 | 창 엣지 스냅 시 간격 (논리 px). 0 = 밀착, 양수 = 경계선 겹침 방지 여백. 화면 엣지에는 적용 안 됨 |
| `drag_modifier` | enum | `"none"` | `none` / `ctrl` / `alt` / `ctrl_alt` | 드래그 개시 게이트. 짧은 좌클릭은 항상 일시 숨김(포커스·IME 변경 시 재표시). `none` = 임계 초과 시 드래그 / 모디파이어 = 해당 키를 정확히 누른 채 임계 초과 시에만 드래그 |

## 커서 추종 인디케이터 (Cursor Indicator)

마우스 커서 주변에 동심원 3개 (Inner / Middle / Outer) + 헤일로를 표시하는 보조 인디케이터. 메인 라벨 인디 (`한`/`En`/`EN`) 와는 **별개 윈도우** (`WS_EX_TRANSPARENT` 영구 ON — 클릭은 항상 아래 창으로 통과) 로 동작하며 트레이 메뉴 "커서 인디케이터 숨김" 체크박스로 즉시 토글 가능. 기본 ON + 항상 표시 모드 (`cursor_always_show = true`) 라 첫 부팅부터 가시. `cursor_indicator_enabled = false` 로 끈 동안에는 메모리 / CPU 비용 0 (lazy 해제 + 재활성화 시 lazy 생성).

색상 정책: Inner/Middle 은 현재 IME 색상 (한글/영문/비한국어 중 하나). CAPS LOCK ON 시 보이는 Outer 원은 "영문 IME → 한글 색상, 한글/비한글 IME → 영문 색상" — CAPS 토글이 한 눈에 보이도록.

아래 10 키는 트레이 메뉴 "상세 설정" 다이얼로그의 **"커서 인디 — 동심원"** 섹션 (PR-21 재배치 후 12 번째) 에서도 GUI 로 노출됩니다 — config.json 직접 편집 / GUI 편집 둘 다 가능. Min/Max 는 두 경로 모두 `DefaultConfig.MinCursor*` / `MaxCursor*` 상수 단일 진실원에서 클램프. IME 전환 스케일 팝 3 키는 아래 [전환 효과](#전환-효과-cursor-transition) 절 참조.

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `cursor_indicator_enabled` | bool | `true` | — | 커서 추종 인디 활성. 트레이 메뉴 "커서 인디케이터 숨김" 체크박스와 동일 — 기본 ON, 끄려면 메뉴 체크 또는 본 키를 `false` 로 |
| `cursor_always_show` | bool | `true` | — | `true` (기본) = 항상 표시 모드 (`CursorAlwaysPollMs=15` 폴링 위치 추종 — `SetTimer` ~15.625ms 격자 양자화로 실배달 ~15.6ms≈64fps; 이전 16은 실효 ~31ms≈32fps. [dev-notes](dev-notes/2026-07-22-settimer-tick-quantization.md)). 숨김 안 함. `false` = 정지 검출 모드 (마우스 정지 → `cursor_idle_delay_ms` 후 표시, 이동 시 즉시 숨김) |
| `cursor_outer_radius` | int | `45` | 8 ~ 96 | 외측 동심원 반지름 (논리 px, DPI 미적용). CAPS LOCK ON 시에만 보임 |
| `cursor_middle_radius` | int | `35` | 6 ~ 80 | 중간 동심원 반지름 (논리 px) |
| `cursor_inner_radius` | int | `30` | 4 ~ 64 | 내측 동심원 반지름 (논리 px) |
| `cursor_core_thickness` | int | `1` | 1 ~ 8 | 코어 (사용자 색상) 두께 (논리 px). 양옆 0.5px 분석적 AA 적용 |
| `cursor_halo_thickness` | int | `2` | 0 ~ 12 | 헤일로 (흰색) 두께 (논리 px). 0 = 헤일로 끔. 코어 양옆 `(halo - core) / 2` 씩 외부 확장 |
| `cursor_halo_opacity` | double | `0.5` | 0.0 ~ 1.0 | 헤일로 흰색 불투명도. 코어와 alpha 비교해 큰 쪽이 채택됨 |
| `cursor_idle_delay_ms` | int | `100` | 0 ~ 2000 | (정지 검출 모드) 마우스 정지 후 인디 표시까지 대기 시간 (ms). 0 = 즉시 |
| `cursor_motion_threshold_px` | int | `5` | 1 ~ 32 | (정지 검출 모드) 직전 폴링 좌표와 맨해튼 거리 임계 (px). 초과 시 "이동" 으로 분류 — 인디 즉시 숨김 |

### 이동 중 시인성 (Motion Dim, PR-29)

항상 표시 모드(`cursor_always_show = true`)에서 커서 이동 중 인디 distraction↓ — **세 원(Inner/Middle/Outer) 공통 가우시안 안개**: soft&gt;0 이면 하드 코어 없음·σ≈헤일로반폭×14·색+흰색 혼합·알파≈0.22 균일(배수 Inner/Middle/Outer = 1.00/0.97/0.94). CAPS OFF 면 Outer 미표시(기존). DIB `MotionFogPadLogicalPx=28`. 창 `SourceConstantAlpha` 는 Full. IME 팝 중 soft=0·원별 α=1. Settings「이동 중 옅게 / 안개 농도 / 안개 강도」+ 트레이.

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `cursor_motion_dim_enabled` | bool | `true` | — | 이동 중 딤 on/off. 트레이 메뉴 "커서 이동 중 옅게" 체크박스와 동일 (체크 = ON). Settings Bool 도 노출 |
| `cursor_motion_alpha` | double | `0.22` | **0.04 ~ 1.0** | 이동 중 안개 알파 기준. Middle/Outer 배수 0.97 / 0.94 |
| `cursor_motion_softness` | double | `1.0` | 0.0 ~ 1.0 | 안개 강도. 1=가우시안 σ≈14×헤일로반폭(하드 코어 없음) |

### 전환 효과 (Cursor Transition)

IME 한↔영 전환 시 동심원이 잠깐 확대됐다 복귀하는 스케일 팝 효과 (PR-21) — 메인 인디의 `change_highlight` / `highlight_scale` / `highlight_duration_ms` 와 평행. `cursor_change_highlight` on/off 는 **트레이 메뉴 "커서 변경 시 강조"** 토글 전용이라 SettingsDialog 에는 미노출 (메인 `change_highlight` 와 동일 정책). 나머지 2 키 (`cursor_highlight_scale` / `cursor_highlight_duration_ms`) 는 다이얼로그 **"커서 인디 — 전환 효과"** 섹션 (13 번째) 에서 GUI 노출. 팝 진행 중에도 DIB 는 `CursorStyle.MaxHighlightScale` (=`2.0`, `MaxCursorHighlightScale` 상한) 기준으로 고정 확대돼 재생성 0 — 그래서 `cursor_highlight_scale` 상한이 `2.0` 으로 잠긴다.

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `cursor_change_highlight` | bool | `true` | — | IME 전환 시 스케일 팝 on/off. 트레이 메뉴 "커서 인디케이터 변경 강조" 체크박스와 동일 (체크 = ON). SettingsDialog 미노출 — 메뉴 또는 본 키 직접 편집. **트레이 "애니메이션 사용"(`animation_enabled`) 마스터에 종속** — 마스터 OFF 면 본 키가 ON 이어도 커서 팝 정지(색만 즉시 갱신), 메인 인디 `change_highlight` 와 동형 AND 게이팅 (PR-22 후속). 강조는 **IME 상태가 실제 바뀔 때만** 발생 — 앱 포커스 변경만으로는 트리거 안 됨 (동일 IME 앱 사이 전환은 강조 없음, 다른 IME 앱 전환은 강조 있음 — 메인 인디와 일관) |
| `cursor_highlight_scale` | double | `1.3` | 1.0 ~ 2.0 | 팝 시작 배율 (확대 정점). `2.0` 상한은 DIB bbox 고정 기준 (`CursorStyle.MaxHighlightScale`) 과 일치 |
| `cursor_highlight_duration_ms` | int | `300` | 0 ~ 2000 | 확대 정점 → 원래 크기 복귀 시간 (ms). 0 = 즉시 복귀 (팝 없음) |

## 고급 (Advanced)

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `advanced.force_topmost_interval_ms` | int | `5000` | TopmostWatchdog 가 인디를 `HWND_TOPMOST` 로 재고정하는 주기. 0 = 비활성. 일부 풀스크린 / 게임 모드 / 가상 데스크톱 잔재가 z-order 를 깨는 케이스 방어 |
| `advanced.overlay_class_name` | string | `"KoEnVueOverlay"` | 오버레이 윈도우의 Win32 클래스명. 1~255자 ASCII 영문/숫자/언더스코어만 허용. [Settings.Validate](../App/Config/Settings.cs#L184) 에서 비정상 값은 디폴트로 폴백 |

---

## 부록 — 단일 진실원 매트릭스

| 진실원 | 어디서 참조 |
|---|---|
| `AppConfig` 의 모든 numeric init 디폴트 ([App/Models/AppConfig.cs](../App/Models/AppConfig.cs)) | `DefaultConfig.*` 동명 const — PR-05 D7 가 6쌍 (`FadeInMs`/`FadeOutMs`/`HighlightScale`/`HighlightDurationMs`/`AlwaysIdleTimeoutMs`/`PollIntervalMs`) 도입 + PR-17 (v0.9.5.0) 가 14 필드 추가 (외관 6 + 투명도 3 + 슬라이드 1 + 동작/시스템/고급 4). 검증: `git grep -nE "\}\s*=\s*-?[0-9]+(\.[0-9]+)?\s*;" App/Models/AppConfig.cs` → 0 매치 |
| `AppConfig` 의 색상/문자열/배열 init 디폴트 + `Settings.EnsureSubObjects`/`ValidateAdvanced` 폴백 ([App/Models/AppConfig.cs](../App/Models/AppConfig.cs) + [App/Config/Settings.cs](../App/Config/Settings.cs)) | `DefaultConfig` 의 색상 7 const (`DefaultHangulBg` 등) + 폰트 (`DefaultIndicatorFontFamily`) + 라벨 3 + 클래스명 (`DefaultOverlayClassName`) const + 숨김 배열 2 property (`DefaultSystemHideClasses`/`DefaultSystemHideProcesses`) — AUDIT 묶음 2 (DUP-1) 가 PR-17 numeric 단일화의 비-numeric 축 완성. 검증: `git grep '"#16A34A"\|"맑은 고딕"\|"KoEnVueOverlay"\|"Progman"\|"ShellExperienceHost"' App/Models/AppConfig.cs App/Config/Settings.cs` → 0 (배열/문자열은 정규식 미포착 — 수동 확인 병기) |
| `AppConfig.DefaultIndicatorPositionRelative` init 디폴트 + Overlay null 폴백 ([App/Models/AppConfig.cs](../App/Models/AppConfig.cs) + [App/UI/Overlay.cs](../App/UI/Overlay.cs)) | `DefaultConfig.{DefaultRelativeCorner, DefaultRelativeOffsetX, DefaultRelativeOffsetY}` 3 const — record init 디폴트 객체와 null 폴백 경로가 동일 const 참조 (두 경로 단일 진실원 일치) |
| `Min/MaxX` 18쌍 ([App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs)) | `Settings.Validate` clamp 20개 인자 + `SettingsDialog.Fields.cs` Int/Dbl min/max 15개 인자. PR-21 이 커서 전환 효과 2쌍 추가 (`Min/MaxCursorHighlightScale` 1.0/2.0 + `Min/MaxCursorHighlightDurationMs` 0/2000) — `MaxCursorHighlightScale` 는 `CursorStyle.MaxHighlightScale` (Core) 를 참조 (App→Core 단일 진실원, P6 정방향 — DIB bbox 팝 상한과 clamp 상한 동기화) |
| `KoEnVue.csproj` `<Version>` | PE 헤더 3종 (Assembly/File/Informational) + `Directory.Build.targets` 가 emit 하는 `Version.g.cs` 의 `DefaultConfig.AppVersion` (PR-11 D6) |
| `ThemeColors` 4 preset ([App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs)) | `record ThemeColors(HBg,HFg,EBg,EFg,NBg,NFg)` + `Dictionary<Theme, ThemeColors>` 정적 사전 |

값 변경 시 위 표의 단일 진실원만 손대면 전 경로가 일관 갱신됩니다.
