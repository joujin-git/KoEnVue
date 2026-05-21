# `config.json` 전체 키 레퍼런스

KoEnVue 의 `config.json` 에서 사용 가능한 **모든** 설정 키 — 84 항목. 트레이 메뉴의 "상세 설정" 다이얼로그가 대부분을 GUI 로 제공하지만, **앱별 프로필** (`app_profiles`) 처럼 GUI 미노출 키는 직접 편집해야 합니다.

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
| `slide_animation` | bool | `false` | — | 위치 변경 시 미끄러지듯 이동 (기본 OFF) |
| `slide_speed_ms` | int | `100` | 0 ~ 2000 | 슬라이드 지속 (ms) |

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
| `tray_quick_opacity_presets` | double[] | `[0.95, 0.85, 0.6]` | 0.1 ~ 1.0 | 트레이 메뉴 "빠른 투명도" 서브메뉴에 노출할 프리셋 3개 |
| `user_hidden` | bool | `false` | — | 사용자가 명시 숨긴 상태. `true` 면 트레이 아이콘에 취소선 + 감지 이벤트로 인디 복원 차단. 재기동에도 유지 |

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
| `default_indicator_position_relative` | object? | `null` | (창 기준 모드) 저장 위치 없는 앱의 기본 위치. `null` = 하드코딩 폴백 (창 TopRight) |
| `default_indicator_position_relative.corner` | enum | `"TopRight"` | 창 DWM visible frame 의 4 모서리 |
| `default_indicator_position_relative.delta_x` | int | `-50` | 앵커에서 X 오프셋 (논리 px). 창의 모니터 DPI 스케일로 변환 후 적용 |
| `default_indicator_position_relative.delta_y` | int | `10` | 앵커에서 Y 오프셋 (논리 px) |
| `snap_to_windows` | bool | `true` | 드래그 중 다른 창 엣지 + 모니터 작업영역 엣지에 자석처럼 붙음 |
| `snap_gap_px` | int | `10` | 0 ~ 10 | 창 엣지 스냅 시 간격 (논리 px). 0 = 밀착, 양수 = 경계선 겹침 방지 여백. 화면 엣지에는 적용 안 됨 |
| `drag_modifier` | enum | `"none"` | `none` / `ctrl` / `alt` / `ctrl_alt` | 드래그 개시 게이트. `none` = 항상 드래그 / 모디파이어 = 해당 키를 정확히 일치하는 조합으로 누른 상태에서만 개시 (크로스 프로세스 투과는 미구현) |

## 고급 (Advanced)

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `advanced.force_topmost_interval_ms` | int | `5000` | TopmostWatchdog 가 인디를 `HWND_TOPMOST` 로 재고정하는 주기. 0 = 비활성. 일부 풀스크린 / 게임 모드 / 가상 데스크톱 잔재가 z-order 를 깨는 케이스 방어 |
| `advanced.overlay_class_name` | string | `"KoEnVueOverlay"` | 오버레이 윈도우의 Win32 클래스명. 1~255자 ASCII 영문/숫자/언더스코어만 허용. [Settings.Validate](../App/Config/Settings.cs#L184) 에서 비정상 값은 디폴트로 폴백 |

---

## 부록 — 단일 진실원 매트릭스

| 진실원 | 어디서 참조 |
|---|---|
| `AppConfig` init 디폴트 6쌍 ([App/Models/AppConfig.cs](../App/Models/AppConfig.cs)) | `DefaultConfig.{FadeInMs, FadeOutMs, HighlightScale, HighlightDurationMs, AlwaysIdleTimeoutMs, PollIntervalMs}` const |
| `Min/MaxX` 16쌍 ([App/Config/DefaultConfig.cs](../App/Config/DefaultConfig.cs)) | `Settings.Validate` clamp 18개 인자 + `SettingsDialog.Fields.cs` Int/Dbl min/max 13개 인자 |
| `KoEnVue.csproj` `<Version>` | PE 헤더 3종 (Assembly/File/Informational) + `Directory.Build.targets` 가 emit 하는 `Version.g.cs` 의 `DefaultConfig.AppVersion` (PR-11 D6) |
| `ThemeColors` 4 preset ([App/Config/ThemePresets.cs](../App/Config/ThemePresets.cs)) | `record ThemeColors(HBg,HFg,EBg,EFg,NBg,NFg)` + `Dictionary<Theme, ThemeColors>` 정적 사전 |

값 변경 시 위 표의 단일 진실원만 손대면 전 경로가 일관 갱신됩니다.
