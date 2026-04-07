# Phase 06: 설정 시스템

## 목표
config.json 로드/저장/핫 리로드/마이그레이션/검증 + 앱별 프로필 + I18n을 구현한다.
이 단계가 완료되면 사용자가 config.json을 편집하고 최대 5초 이내 자동 반영되어야 한다.

## 선행 조건
- Phase 01 완료 (Models/AppConfig, DefaultConfig, Native)
- Phase 03 완료 (Program.cs — WM_CONFIG_CHANGED 핸들러)

## 팀 구성
- **이온-시스템**: Settings.cs + I18n.cs (순차 구현)
- mode: "plan" — 계획 제출 후 리드 승인

## 병렬 실행 계획
- Phase 04/05와 **병렬 실행 가능** (서로 다른 파일, 독립적 기능)
- Settings.cs 내부 작업은 순차.

---

## 구현 명세

### Config/Settings.cs — 설정 관리

#### 설정 파일 탐색 순서

```csharp
public static AppConfig Load()
{
    // 1순위: %APPDATA%\KoEnVue\config.json (사용자 설정, 최우선)
    string appDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KoEnVue", "config.json");
    if (File.Exists(appDataPath))
        return LoadFromFile(appDataPath);

    // 2순위: exe 디렉토리\config.json (포터블 모드)
    string exeDirPath = Path.Combine(
        AppContext.BaseDirectory, "config.json");
    if (File.Exists(exeDirPath))
        return LoadFromFile(exeDirPath);

    // 3순위: 코드 내 DEFAULT_CONFIG (파일 자동 생성 안 함)
    // 최초 트레이 메뉴 설정 변경 시에만 config.json 생성
    return DefaultConfig.CreateDefault();
}
```

- exe 디렉토리에 config.json이 존재하고 %APPDATA% 파일이 없을 때 **포터블 모드** (F-46)
- 트레이 메뉴에 현재 모드(포터블/설치) 표시

#### 설정 적용 우선순위

```
1. 앱별 프로필 (app_profiles) — 최우선 (해당 키만 오버라이드, 나머지 상속)
2. config.json 글로벌 설정
3. 코드 내 DEFAULT_CONFIG — 최하위 (누락 키 보충)
```

#### NativeAOT JSON 처리

```csharp
// AppConfig를 immutable record로 정의 (Phase 01에서 이미 생성)
record AppConfig(IndicatorStyle IndicatorStyle, DisplayMode DisplayMode, ...);

// Source Generator 필수 (리플렉션 차단됨 — NativeAOT)
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]  // app_profiles 동적 파싱
partial class AppConfigJsonContext : JsonSerializerContext { }

// 로드
static AppConfig LoadFromFile(string path)
{
    string json = File.ReadAllText(path, Encoding.UTF8);
    // UTF-8 BOM 감지 → 제거 → 파싱
    if (json.Length > 0 && json[0] == '\uFEFF')
        json = json[1..];

    try
    {
        // Source-generated context 사용 시 options는 context 내부에서 설정됨
        // AppConfigJsonContext의 생성자에서 PropertyNameCaseInsensitive, ReadCommentHandling,
        // AllowTrailingCommas를 설정하거나, 별도 static 속성으로 제공
        var config = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig)
            ?? DefaultConfig.CreateDefault();

        config = Migrate(config);
        config = Validate(config);
        return config;
    }
    catch
    {
        Logger.Warning("Failed to parse config.json, using DEFAULT_CONFIG");
        return DefaultConfig.CreateDefault();
    }
}

// 저장
static void Save(AppConfig config, string path)
{
    try
    {
        string json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json, Encoding.UTF8);
    }
    catch (Exception ex)
    {
        // NF-25: 저장 실패 시 로그 경고 + 인메모리 유지 + 다음 저장 재시도
        Logger.Warning($"Failed to save config.json: {ex.Message}");
    }
}
```

#### 설정 동시 접근 안전성

```csharp
// Immutable 객체 교체 방식 — 레이스 컨디션 방지
volatile AppConfig _config = DefaultConfig.CreateDefault();

void UpdateConfig(Action<AppConfigBuilder> modifier)
{
    var builder = new AppConfigBuilder(_config);
    modifier(builder);
    var updated = builder.Build();
    updated = Validate(updated);
    _config = updated;  // 원자적 참조 교체
}
```

- 감지 스레드(읽기)와 메인 스레드(쓰기)가 설정 객체를 공유
- 락 불필요 — volatile 참조 교체로 충분

#### 핫 리로드 (5초 mtime 체크)

```csharp
// 감지 스레드의 폴링 루프 내에서 ~5초(약 62폴링×80ms)마다 호출 (PRD §2.8.3)
// 변경 감지 시 PostMessage(WM_CONFIG_CHANGED)로 메인 스레드에 통보
static void CheckConfigFileChange()
{
    if (_configFilePath == null) return;

    DateTime mtime = File.GetLastWriteTimeUtc(_configFilePath);
    if (mtime == _lastConfigMtime) return;

    _lastConfigMtime = mtime;
    try
    {
        var newConfig = LoadFromFile(_configFilePath);
        _config = newConfig;
        PostMessage(_hwndMain, AppMessages.WM_CONFIG_CHANGED, 0, 0);
    }
    catch
    {
        // 실패 시 이전 설정 유지
        Logger.Warning("Config reload failed, keeping previous config");
    }
}
```

- 트레이 메뉴 변경 시: 즉시 반영 + config.json 저장
- 외부 편집 감지: 5초마다 mtime 체크 → 변경 시 리로드

#### 설정 값 검증

```csharp
static AppConfig Validate(AppConfig config)
{
    // 범위를 벗어나면 유효값으로 조용히 클램핑 (에러 아님)
    return config with
    {
        PollIntervalMs = Clamp(config.PollIntervalMs, 50, 500),
        CaretPollIntervalMs = Clamp(config.CaretPollIntervalMs, 30, 500),
        EventDisplayDurationMs = Clamp(config.EventDisplayDurationMs, 500, 10000),
        AlwaysIdleTimeoutMs = Clamp(config.AlwaysIdleTimeoutMs, 1000, 30000),
        Opacity = Clamp(config.Opacity, 0.1, 1.0),
        IdleOpacity = Clamp(config.IdleOpacity, 0.1, 1.0),
        ActiveOpacity = Clamp(config.ActiveOpacity, 0.1, 1.0),
        CaretBoxOpacity = Clamp(config.CaretBoxOpacity, 0.1, 1.0),
        CaretBoxIdleOpacity = Clamp(config.CaretBoxIdleOpacity, 0.1, 1.0),
        CaretBoxActiveOpacity = Clamp(config.CaretBoxActiveOpacity, 0.1, 1.0),
        CaretBoxMinOpacity = Clamp(config.CaretBoxMinOpacity, 0.1, 1.0),
        HighlightScale = Clamp(config.HighlightScale, 1.0, 2.0),
        FadeInMs = Clamp(config.FadeInMs, 0, 2000),
        FadeOutMs = Clamp(config.FadeOutMs, 0, 2000),
        HighlightDurationMs = Clamp(config.HighlightDurationMs, 0, 2000),
        SlideSpeedMs = Clamp(config.SlideSpeedMs, 0, 2000),
        FontSize = Clamp(config.FontSize, 8, 36),
        CaretDotSize = Clamp(config.CaretDotSize, 4, 32),
        CaretSquareSize = Clamp(config.CaretSquareSize, 4, 32),
        CaretUnderlineWidth = Clamp(config.CaretUnderlineWidth, 8, 64),
        CaretUnderlineHeight = Clamp(config.CaretUnderlineHeight, 1, 16),
        CaretVbarWidth = Clamp(config.CaretVbarWidth, 1, 16),
        CaretVbarHeight = Clamp(config.CaretVbarHeight, 4, 64),
        LabelWidth = Clamp(config.LabelWidth, 16, 128),
        LabelHeight = Clamp(config.LabelHeight, 12, 96),
        LabelBorderRadius = Clamp(config.LabelBorderRadius, 0, 48),
        BorderWidth = Clamp(config.BorderWidth, 0, 8),
        ScreenEdgeMargin = Clamp(config.ScreenEdgeMargin, 0, 50),
        LogMaxSizeMb = Clamp(config.LogMaxSizeMb, 1, 100),
    };
}
```

#### 설정 마이그레이션

```csharp
static AppConfig Migrate(AppConfig config)
{
    // config_version으로 스키마 버전 관리
    int version = config.ConfigVersion;

    // 버전별 변환 함수를 체인으로 적용
    if (version < 2) config = MigrateV1ToV2(config);
    if (version < 3) config = MigrateV2ToV3(config);
    // ... 향후 확장

    return config with { ConfigVersion = CurrentVersion };
}
```

- `config_version: 1` 필드로 스키마 버전 관리
- 알 수 없는 키는 무시 (JsonSerializerOptions로 처리)
- 누락된 키는 기본값으로 채움 (record default)
- 버전 체인: v1→v2, v2→v3 순차 적용 → 어떤 버전에서든 최신으로

#### UTF-8 인코딩

- `InvariantGlobalization: true` → CP949/EUC-KR 미지원
- **UTF-8만 지원**: BOM 감지 → 제거 → UTF-8 파싱
- 파싱 실패 시 DEFAULT_CONFIG + 로그 경고

---

### config.json 전체 키 구조

```jsonc
{
  "config_version": 1,

  // [표시 모드]
  "display_mode": "on_event",           // "on_event" | "always"
  "event_display_duration_ms": 1500,
  "always_idle_timeout_ms": 3000,
  "event_triggers": { "on_focus_change": true, "on_ime_change": true },

  // [위치]
  "position_mode": "caret",             // "caret" | "mouse" | "fixed"
  "fixed_position": { "x": 100, "y": 100, "anchor": "top_right", "monitor": "primary" },
  "caret_offset": { "x": -2, "y": 0 },
  "mouse_offset": { "x": 20, "y": 25 },
  "caret_placement": "left",            // "left" | "above" | "below" | "right"
  "caret_placement_auto_flip": true,
  "screen_edge_margin": 8,

  // [외관 — 스타일]
  "indicator_style": "caret_dot",
  "caret_dot_size": 8, "caret_square_size": 8,
  "caret_underline_width": 24, "caret_underline_height": 3,
  "caret_vbar_width": 3, "caret_vbar_height": 16,
  "label_shape": "rounded_rect",        // "rounded_rect" | "circle" | "pill"
  "label_width": 28, "label_height": 24, "label_border_radius": 6,
  "border_width": 0, "border_color": "#000000",
  "shadow_enabled": false,

  // [외관 — 색상]
  "hangul_bg": "#16A34A", "hangul_fg": "#FFFFFF",
  "english_bg": "#D97706", "english_fg": "#FFFFFF",
  "non_korean_bg": "#6B7280", "non_korean_fg": "#FFFFFF",
  "opacity": 0.85, "idle_opacity": 0.4, "active_opacity": 0.95,
  "caret_box_opacity": 0.95, "caret_box_idle_opacity": 0.65,
  "caret_box_active_opacity": 1.0, "caret_box_min_opacity": 0.5,

  // [외관 — 텍스트]
  "font_family": "맑은 고딕", "font_size": 12, "font_weight": "bold",
  "hangul_label": "한", "english_label": "En", "non_korean_label": "EN",
  "label_style": "text",                // "text" | "dot" | "icon"

  // [테마]
  "theme": "custom",                    // "custom" | "minimal" | "vivid" | "pastel" | "dark" | "system"

  // [애니메이션]
  "animation_enabled": true,
  "fade_in_ms": 150, "fade_out_ms": 400,
  "change_highlight": true, "highlight_scale": 1.3, "highlight_duration_ms": 300,
  "slide_animation": false, "slide_speed_ms": 100,

  // [감지]
  "poll_interval_ms": 80, "caret_poll_interval_ms": 50,
  "detection_method": "auto",           // "auto" | "ime_default" | "ime_context" | "keyboard_layout"
  "caret_method": "auto",              // "auto" | "gui_thread" | "uia" | "mouse"
  "non_korean_ime": "hide",            // "hide" | "show" | "dim"
  "hide_in_fullscreen": true, "hide_when_no_focus": true,
  "hide_on_lock_screen": true,
  "system_hide_classes": ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"],
  "system_hide_classes_user": [],
  "app_method_cache_size": 50,

  // [앱별 프로필]
  "app_profiles": {
    "code.exe": { "caret_method": "gui_thread", "caret_offset": { "x": 0, "y": 2 } },
    "excel.exe": { "position_mode": "mouse" },
    "mstsc.exe": { "enabled": false }
  },
  "app_profile_match": "process",       // "process" | "title" | "class"
  "app_filter_mode": "blacklist",       // "blacklist" | "whitelist"
  "app_filter_list": [],

  // [핫키]
  "hotkeys_enabled": true,
  "hotkey_toggle_visibility": "Ctrl+Alt+H",
  "hotkey_cycle_style": "Ctrl+Alt+I",
  "hotkey_cycle_position": "Ctrl+Alt+P",
  "hotkey_cycle_display": "Ctrl+Alt+D",
  "hotkey_open_settings": "Ctrl+Alt+S",

  // [트레이]
  "tray_enabled": true, "tray_icon_style": "caret_dot",
  "tray_tooltip": true,
  "tray_click_action": "toggle",
  "tray_show_notification": false,
  "tray_quick_opacity_presets": [0.95, 0.85, 0.6],

  // [시스템]
  "startup_with_windows": false, "startup_minimized": true,
  "single_instance": true,
  "log_level": "WARNING", "language": "ko",
  "log_to_file": false, "log_file_path": "", "log_max_size_mb": 10,

  // [다중 모니터]
  "multi_monitor": "follow_caret",
  "per_monitor_scale": true,
  "clamp_to_work_area": true,
  "prevent_cross_monitor": true,

  // [고급]
  "advanced": {
    "force_topmost_interval_ms": 5000,
    "uia_timeout_ms": 200,
    "uia_cache_ttl_ms": 500,
    "skip_uia_for_processes": [],
    "ime_fallback_chain": ["ime_default_wnd", "ime_context", "keyboard_layout"],
    "caret_fallback_chain": ["gui_thread_info", "uia_text_pattern", "focus_window_rect", "mouse_cursor"],
    "overlay_class_name": "KoEnVueOverlay",
    "prevent_sleep": false,
    "debug_overlay": false
  }
}
```

> 전체 약 80개 키. 사용자는 아래 9개 필수 키만으로 핵심 동작 커스터마이즈 가능:
> `config_version`, `indicator_style`, `display_mode`, `hangul_bg`, `english_bg`,
> `hangul_label`, `english_label`, `opacity`, `startup_with_windows`

#### 구현 우선순위별 설정 키

| 우선순위 | 시점 | 키 |
|----------|------|-----|
| MVP (Phase 01-03) | Week 1-2 | config_version, display_mode, indicator_style, opacity, hangul/english/non_korean bg/fg, labels, font_*, poll_*, animation_*, system_hide_classes, hide_*, tray_enabled, startup_with_windows, language, log_level |
| Standard (Phase 04-06) | Week 3 | event_display_duration_ms, always_*, event_triggers, position_mode, offsets, placement, screen_edge_margin, caret_box_* opacity, non_korean_ime, app_profiles, app_filter_*, hotkeys_* |
| Advanced (Phase 07) | Week 4+ | fixed_position, label_shape, border_*, theme, label_style, slide_*, tray_icon_style, multi_monitor, per_monitor_scale, advanced.* |

---

### 앱별 프로필 매칭 (F-41)

```csharp
public static AppConfig ResolveForApp(AppConfig global, IntPtr hwnd)
{
    // 매칭 방식
    string key = global.AppProfileMatch switch
    {
        "process" => GetProcessName(hwnd).ToLowerInvariant(),
        "title" => GetWindowTitle(hwnd),  // 정규식(Regex) 패턴 매칭 지원
        "class" => GetClassName(hwnd),
        _ => GetProcessName(hwnd).ToLowerInvariant()
    };
    // "title" 모드에서는 app_profiles 키를 정규식 패턴으로 취급:
    //   foreach (var (pattern, profile) in global.AppProfiles)
    //     if (Regex.IsMatch(key, pattern, RegexOptions.IgnoreCase))
    //       return global.MergeWith(profile);

    // app_profiles 딕셔너리에서 검색
    if (!global.AppProfiles.TryGetValue(key, out var profile))
        return global;

    // 매칭된 프로필의 키만 오버라이드 (나머지 상속)
    return global.MergeWith(profile);
    // enabled: false → 해당 앱에서 인디케이터 비활성화
}
```

- process 매칭: GetWindowThreadProcessId → OpenProcess → QueryFullProcessImageName → 파일명
- 캐싱: Dictionary + LRU (최대 50개, AppMethodCacheSize 설정값).
  - LRU 만료: 캐시 크기 초과 시 가장 오래된 항목 제거 (LinkedList 기반).
  - 설정 리로드 시 캐시 전체 무효화 (WM_CONFIG_CHANGED 핸들러에서 Clear()).
  - enabled: false인 프로필도 캐시하여 매번 재검색 방지.

---

### Utils/I18n.cs — 한글/영문 UI 텍스트

```csharp
static class I18n
{
    private static Dictionary<string, string> _texts = new();

    public static void Load(string language)
    {
        // language: "ko" (기본) | "en" | "auto"
        // "auto" → Windows 시스템 언어. 한국어 아니면 영문.

        _texts = language switch
        {
            "en" => LoadEnglish(),
            "ko" => LoadKorean(),
            "auto" => IsSystemKorean() ? LoadKorean() : LoadEnglish(),
            _ => LoadKorean()
        };
    }

    // 트레이 메뉴 텍스트
    public static string IndicatorStyle => _texts["indicator_style"];
    // "인디케이터 스타일" / "Indicator Style"
    public static string DisplayMode => _texts["display_mode"];
    // "표시 모드" / "Display Mode"
    // ... 기타 UI 텍스트
}
```

---

## 검증 기준

- [ ] %APPDATA% → exe dir → DEFAULT_CONFIG 순서로 설정 파일 탐색
- [ ] [JsonSerializable] source generator 사용 (리플렉션 없음)
- [ ] UTF-8 BOM 제거 후 파싱
- [ ] 파싱 실패 시 DEFAULT_CONFIG 유지 + 로그 경고
- [ ] 모든 검증 범위 (~20개)가 Validate()에 구현됨
- [ ] volatile AppConfig 참조 교체 패턴
- [ ] 5초 mtime 핫 리로드
- [ ] 저장 실패 시 인메모리 유지 + 재시도 (NF-25)
- [ ] config_version 기반 마이그레이션 체인
- [ ] 앱별 프로필 매칭 + 오버라이드 + 캐싱
- [ ] 전체 ~80개 config 키가 AppConfig record에 매핑됨
- [ ] I18n이 language 설정에 따라 한글/영문 전환

## 산출물
```
Config/Settings.cs     # 로드/저장/핫리로드/마이그레이션/검증
Utils/I18n.cs          # 한글/영문 UI 텍스트 관리
```
