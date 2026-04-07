# Phase 04: 오버레이 렌더링 + 애니메이션 + 위치 안정성

## 목표

오버레이 윈도우를 생성하고, 5종 인디케이터 스타일을 GDI 기반으로 렌더링하며, 페이드/강조 애니메이션을 구현하고, 위치 안정성(F-S01~F-S06)을 보장하며, Per-Monitor DPI 인식 배치를 완성한다.

## 선행 조건

- Phase 01 완료: Native/*.cs, Win32Types.cs, SafeGdiHandles.cs, AppMessages.cs, Models/*.cs, DpiHelper.cs, ColorHelper.cs, DefaultConfig.cs
- Phase 02 완료: ImeStatus.cs, CaretTracker.cs, SystemFilter.cs
- Phase 03 완료: Program.cs (메인 메시지 루프 + 3-스레드 모델)
- 공통 모듈(DpiHelper, ColorHelper, SafeGdiHandles)이 Phase 01에서 이미 구현되어 있어야 한다.
- app.manifest에 PerMonitorV2 DPI Awareness가 선언되어 있어야 한다 (Phase 01 또는 Phase 03에서 설정).

## 팀 구성

| 팀원 | 역할 | 담당 파일 |
|------|------|-----------|
| **이온-렌더** | 오버레이 + 애니메이션 + 위치 | `UI/Overlay.cs`, `UI/Animation.cs` |
| **이온-QA** | 체크리스트 검증 | 검증 기준 대조 |

## 병렬 실행 계획

```
이온-렌더: Overlay.cs (순차 1단계)
  │
  ▼
이온-렌더: Animation.cs (순차 2단계 — Overlay.cs의 DIB + UpdateLayeredWindow에 의존)
  │
  ▼
이온-QA: 검증
```

- Animation.cs는 Overlay.cs의 UpdateLayeredWindow 파이프라인, DIB 버퍼, OverlayState에 의존하므로 **순차 실행 필수**.
- Phase 05(시스템 트레이)와는 **병렬 실행 가능**.

---

## 구현 명세

### `UI/Overlay.cs`

#### 오버레이 윈도우 생성

- `CreateWindowExW`로 오버레이 윈도우를 생성한다.
- 확장 스타일 (dwExStyle):
  ```
  WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
  ```
  - `WS_EX_LAYERED`: per-pixel alpha 제어를 위한 레이어드 윈도우
  - `WS_EX_TRANSPARENT`: 마우스 클릭 투과 (인디케이터가 클릭을 가로채지 않음)
  - `WS_EX_TOPMOST`: 모든 창 위에 표시
  - `WS_EX_TOOLWINDOW`: 작업표시줄/Alt+Tab에 표시 안 됨
  - `WS_EX_NOACTIVATE`: 포커스를 빼앗지 않음
- 윈도우 스타일 (dwStyle): `WS_POPUP`
- 윈도우 클래스명: `config.Advanced.OverlayClassName` (기본값 `"KoEnVueOverlay"`)

#### TOPMOST 재적용

- 다른 TOPMOST 앱과의 경쟁에 대비하여, 매 `config.Advanced.ForceTopmostIntervalMs` (기본 5000ms)마다 `SetWindowPos`를 호출하여 TOPMOST를 재적용한다.
  ```
  SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
               SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
  ```
- WM_TIMER 기반으로 주기적 호출.

#### 렌더링 파이프라인 (7단계)

**반드시 `UpdateLayeredWindow` + 32-bit PARGB DIB 방식을 사용한다. `SetLayeredWindowAttributes`는 사용하지 않는다.**
- `SetLayeredWindowAttributes`는 윈도우 전체 알파만 지원하여 per-pixel 투명도(둥근 모서리 등)를 처리할 수 없다.
- `UpdateLayeredWindow`는 비트맵의 알파 채널을 직접 제어하므로, `label_shape`(rounded_rect/circle/pill)의 둥근 모서리와 페이드 애니메이션을 **단일 파이프라인**으로 처리한다.

```
단계 1: CreateCompatibleDC(NULL)
  - 화면 DC와 호환되는 메모리 DC 생성.

단계 2: BITMAPINFOHEADER (32bpp, BI_RGB) -> CreateDIBSection -> HBITMAP + 픽셀 버퍼 포인터 획득
  - biWidth = 인디케이터 너비
  - biHeight = -인디케이터 높이 (top-down DIB: 음수로 지정)
  - biBitCount = 32
  - biCompression = BI_RGB
  - CreateDIBSection으로 HBITMAP과 픽셀 버퍼 포인터(IntPtr ppvBits)를 동시에 얻는다.

단계 3: SelectObject(memDC, hBitmap)
  - 메모리 DC에 DIB 비트맵을 선택.

단계 4: 픽셀 버퍼에 도형 렌더링 (스타일별):
  - label:
    - label_shape에 따라:
      - "rounded_rect": GDI RoundRect 호출. 모서리 반경 = config.LabelBorderRadius (기본 6px, DPI 스케일).
      - "circle": GDI Ellipse로 원형 배경 렌더링.
      - "pill": 좌우 끝이 완전히 둥근 알약형. border_radius = height / 2.
    - 배경 채운 후 DrawTextW로 텍스트 렌더링. (DrawTextW는 User32 P/Invoke — gdi32.dll 아님!)
  - caret_dot:
    - FillEllipse (원형). 크기: 기본 8x8px + DPI 스케일링.
  - caret_square:
    - FillRect (정사각형). 크기: 기본 8x8px + DPI 스케일링.
  - caret_underline:
    - 얇은 수평 사각형 (FillRect). 크기: 기본 24x3px + DPI 스케일링.
  - caret_vbar:
    - 세로 사각형 (FillRect). 크기: 기본 3x16px + DPI 스케일링.

단계 5: UpdateLayeredWindow 호출
  UpdateLayeredWindow(hwnd, NULL, &pos, &size, memDC, &srcPoint, 0, &blendFunc, ULW_ALPHA)
  - BLENDFUNCTION 구조체:
    - BlendOp = AC_SRC_OVER
    - BlendFlags = 0
    - SourceConstantAlpha = targetAlpha (초기값: (byte)(effectiveOpacity * 255), 페이드 시 조절)
      // label: config.Opacity(0.85) → 217, caret_box: config.CaretBoxOpacity(0.95) → 242
      // 절대 255 하드코딩 금지 — Animation.cs의 targetAlpha 사용
    - AlphaFormat = AC_SRC_ALPHA (per-pixel alpha)
  - pos: POINT 구조체 (스크린 좌표)
  - size: SIZE 구조체 (인디케이터 크기)
  - srcPoint: POINT(0, 0)

단계 6: 페이드 처리
  - SourceConstantAlpha 값(0~255)만 변경하여 UpdateLayeredWindow를 재호출.
  - **매 프레임 픽셀 버퍼를 순회하지 않는다** -- CPU 부담 최소화의 핵심.
  - premultiplied 비트맵은 그대로 유지한 채, 윈도우 전체 투명도만 조절.

단계 7: 강조(Highlight) 처리
  - UpdateLayeredWindow의 size 파라미터에 확대된 크기를 전달.
  - 중심점 기준 확대 (F-S04). pos도 중심 유지 수식으로 재계산.
  - **DIB 재생성 불필요** -- UpdateLayeredWindow가 비트맵을 자동 스케일.
```

#### Premultiplied Alpha 처리

- `UpdateLayeredWindow`는 **premultiplied alpha**를 요구한다.
- 공식: 각 채널 값 = 원본 값 x (alpha / 255)
  ```csharp
  // 의사 코드: DIB 픽셀 버퍼 순회
  for (int i = 0; i < pixelCount; i++)
  {
      byte b = buffer[i * 4 + 0];
      byte g = buffer[i * 4 + 1];
      byte r = buffer[i * 4 + 2];
      byte a = buffer[i * 4 + 3];
      buffer[i * 4 + 0] = (byte)(b * a / 255);
      buffer[i * 4 + 1] = (byte)(g * a / 255);
      buffer[i * 4 + 2] = (byte)(r * a / 255);
      // alpha 채널은 그대로 유지
  }
  ```
- GDI DrawTextW / FillRect 출력 후 DIB 픽셀 버퍼를 직접 순회하며 premultiply 적용.
- premultiply된 비트맵은 그대로 유지, **페이드 시 재생성하지 않는다**.
- **색상 변경(한/영 전환) 시에만** 비트맵 픽셀을 갱신하고 premultiply를 재적용한다.

#### DIB 버퍼 재사용 전략

- 인디케이터 크기가 변하지 않으면 DIB(HBITMAP + 메모리 DC)를 재생성하지 않고 **픽셀만 갱신**.
- DIB 재생성 조건 (이 3가지뿐):
  1. DPI 변경 (모니터 전환)
  2. 인디케이터 스타일 변경
  3. 인디케이터 크기 변경
- highlight scale 적용 시에는 UpdateLayeredWindow의 size 파라미터로 처리 -> DIB 재생성 불필요.
- 메모리 DC와 HBITMAP은 오버레이 윈도우 수명과 함께 관리한다.
  - 오버레이 윈도우 생성 시 할당, 파괴 시 해제.
  - SafeBitmapHandle 래퍼 사용 (SafeGdiHandles.cs).

#### 5종 인디케이터 스타일 렌더링 상세

| 스타일 | 렌더링 내용 | 기본 크기 | 크기 결정 |
|--------|------------|-----------|-----------|
| `label` | label_shape(rounded_rect/circle/pill) 배경 + DrawTextW 텍스트 | 28x24px | F-S01 고정 너비 x label_height |
| `caret_dot` | 원형 FillEllipse | 8x8px | 고정 크기 (DPI 스케일링만 적용) |
| `caret_square` | 정사각형 FillRect | 8x8px | 고정 크기 (DPI 스케일링만 적용) |
| `caret_underline` | 수평 얇은 바 FillRect | 24x3px | 고정 너비 x 고정 높이 (DPI 스케일링) |
| `caret_vbar` | 수직 바 FillRect | 3x16px | 고정 너비 x 고정 높이 (DPI 스케일링) |

- label의 `label_shape` 3종:
  - `rounded_rect`: GDI `RoundRect` 사용. `label_border_radius` 설정값(기본 6px)으로 모서리 둥글기 결정.
  - `circle`: GDI `Ellipse`로 원형 배경 렌더링.
  - `pill`: 좌우 끝이 완전히 둥근 알약형. border_radius = height / 2.
- 모든 캐럿 박스 스타일은 **고정 크기**로 앱 폰트에 의존하지 않음.
- 크기 설정값:
  - `caret_dot_size`: 기본 8 (지름, px)
  - `caret_square_size`: 기본 8 (한 변, px)
  - `caret_underline_width`: 기본 24, `caret_underline_height`: 기본 3 (px)
  - `caret_vbar_width`: 기본 3, `caret_vbar_height`: 기본 16 (px)
  - `label_width`: 기본 28 (최소 너비), `label_height`: 기본 24 (px)

#### GDI 텍스트 렌더링 (label 스타일 전용)

**HFONT 생성 -- DPI 인식 크기 변환:**
- `CreateFontW` 호출 시 폰트 크기(pt)를 px로 변환:
  ```
  -MulDiv(fontSize_pt, dpiY, 72)
  ```
- 기본 폰트: `"맑은 고딕"`, 12pt, bold.
- fontWeight: "normal" = FW_NORMAL(400), "bold" = FW_BOLD(700).

**HFONT 수명 관리:**
- HFONT는 **앱 시작 시 한 번 생성하고 재사용**. 폴링마다 CreateFontW/DeleteObject 반복 금지.
- HFONT 재생성 조건 (이 2가지뿐):
  - 폰트 설정 변경 (font_family, font_size, font_weight)
  - 모니터 전환으로 DPI 변경 시 (현재 모니터 DPI를 캐시하고, 변경 감지 시 DeleteObject -> CreateFontW)
- `SafeFontHandle` 래퍼 사용 (SafeGdiHandles.cs).

**텍스트 렌더링 절차 (7단계):**
```
1. CreateFontW(-MulDiv(pt, dpiY, 72), fontFamily, fontWeight) -> HFONT
   (또는 캐시된 HFONT 재사용)
2. SelectObject(memDC, hFont) -> oldFont 보관
3. SetBkMode(memDC, TRANSPARENT)
4. SetTextColor(memDC, fgColor)
   - fgColor: ColorHelper.HexToColorRef(config.HangulFg 또는 EnglishFg 또는 NonKoreanFg)
5. DrawTextW(memDC, text, -1, &rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE)
   - text: config.HangulLabel("한") / EnglishLabel("En") / NonKoreanLabel("EN")
   - rect: label 영역 전체 (고정 너비 x 높이)
6. DIB 픽셀 버퍼에서 텍스트 영역 premultiplied alpha 보정
7. SelectObject(memDC, oldFont) -- 이전 폰트 복원
```

#### 색상 체계

- 한글(Hangul): 배경 `#16A34A` (초록), 전경 `#FFFFFF`
- 영문(English): 배경 `#D97706` (앰버), 전경 `#FFFFFF`
- 비한국어(NonKorean): 배경 `#6B7280` (회색), 전경 `#FFFFFF`
- 모든 색상은 config.json에서 로드.
- `ColorHelper.cs`로 HEX -> COLORREF(0x00BBGGRR) 변환. 직접 변환 코드 작성 금지.

#### OverlayState 클래스

```csharp
class OverlayState
{
    public Placement? CurrentPlacement { get; private set; }
    public bool IsVisible { get; private set; }

    // 이벤트 발생 시 호출 -- 배치 방향 결정/유지
    public (int x, int y) OnEvent(int caretX, int caretY, int caretH,
                                   int indW, int indH, RECT workArea)
    {
        if (!IsVisible)
        {
            // 새 표시 주기: 배치 방향 재계산
            CurrentPlacement = CalculatePlacement(caretX, caretY, caretH, indW, indH, workArea);
            IsVisible = true;
        }
        // 기존 표시 중: CurrentPlacement 유지 (F-S02)
        return ApplyPlacement(CurrentPlacement!.Value, caretX, caretY, caretH, indW, indH);
    }

    // 페이드아웃 완료 시 호출
    public void OnFadeOutComplete()
    {
        CurrentPlacement = null;
        IsVisible = false;
    }
}
```

---

### `UI/Animation.cs`

#### 타이밍 파라미터 (config에서 읽어옴)

| 파라미터 | config 키 | 기본값 | 용도 |
|----------|-----------|--------|------|
| 애니메이션 활성화 | `animation_enabled` | true | false 시 모든 애니메이션 비활성화 (즉시 표시/숨김) |
| 페이드인 | `fade_in_ms` | 150ms | 표시 시 페이드인 |
| 페이드아웃 | `fade_out_ms` | 400ms | 숨김 시 페이드아웃 |
| 유지 시간 | `event_display_duration_ms` | 1500ms | 표시 유지 시간 (hold) |
| 강조 활성화 | `change_highlight` | true | false 시 IME 전환 강조 확대 비활성화 |
| 확대 배율 | `highlight_scale` | 1.3 | IME 전환 시 확대 배율 |
| 확대 복귀 | `highlight_duration_ms` | 300ms | 확대 -> 원래 크기 복귀 시간 |

- DefaultConfig.cs에도 동일한 기본 상수를 정의:
  ```
  FadeInDurationMs     = 150
  HoldDurationMs       = 1500
  FadeOutDurationMs    = 400
  ScaleFactor          = 1.3
  ScaleReturnMs        = 300
  ```

#### 페이드 애니메이션 -- SourceConstantAlpha 방식

- 페이드인/페이드아웃은 `BLENDFUNCTION.SourceConstantAlpha` 값(0~255)만 변경하여 `UpdateLayeredWindow`를 재호출.
- **매 프레임 픽셀 버퍼를 순회하지 않는다** -- CPU 부담 최소화의 핵심.
- 비트맵은 premultiplied 상태로 유지한 채, 윈도우 전체 투명도만 조절.
- `animation_enabled: false` 시: 페이드 없이 즉시 표시(alpha=targetAlpha)/숨김(alpha=0). 타이머 불필요.
  - targetAlpha = (byte)(effectiveOpacity * 255). 캐럿 박스 스타일은 caret_box_opacity(0.95), label은 opacity(0.85) 사용.
- 보간: 시작 alpha에서 목표 alpha까지 타이머 경과 비율에 따라 선형 보간.
  ```
  elapsed = 현재시각 - 시작시각
  ratio = Clamp(elapsed / duration, 0.0, 1.0)
  alpha = (byte)(startAlpha + (endAlpha - startAlpha) * ratio)
  ```

#### 강조(Highlight) 애니메이션 -- 중심 기준 1.3배 확대

- `change_highlight: false` 시: 확대 효과 없이 색상만 변경.
- IME 상태 변경 시 (`change_highlight: true`): 인디케이터를 1.3배로 확대 -> `highlight_duration_ms`(기본 300ms)에 걸쳐 원래 크기로 복귀.
- **반드시 중심점 기준 확대** (F-S04):
  ```
  newW = (int)Math.Round(w * scale)
  newH = (int)Math.Round(h * scale)
  newX = x - (newW - w) / 2    // 중심 X 유지
  newY = y - (newH - h) / 2    // 중심 Y 유지
  ```
- **좌상단 기준 확대는 절대 사용 금지** -- 오른쪽+아래로 밀려 보이는 시각적 결함 발생.
- UpdateLayeredWindow의 `size` 파라미터에 확대된 크기를 전달 (DIB 재생성 불필요).
- scale 보간: 1.3에서 1.0까지 타이머 경과 비율에 따라 선형 보간.
  ```
  elapsed = 현재시각 - 시작시각
  ratio = Clamp(elapsed / highlightDuration, 0.0, 1.0)
  currentScale = highlightScale - (highlightScale - 1.0) * ratio
  ```
- 강조 확대/축소의 중간 결과도 정수로 반올림한 뒤 사용 (F-S05).

#### WM_TIMER 기반 애니메이션 루프

- 모든 애니메이션은 Win32 `SetTimer` / `WM_TIMER` 기반으로 구동.
- 메인 스레드의 메시지 루프에서 `WM_TIMER`를 처리.
- 타이머 종류 및 ID:
  - **페이드 타이머** (TIMER_ID_FADE): ~16ms 간격 (약 60fps). SourceConstantAlpha를 보간하여 UpdateLayeredWindow 호출.
  - **유지 타이머** (TIMER_ID_HOLD): `event_display_duration_ms`(기본 1500ms) 후 1회 발화하여 페이드아웃 시작.
  - **강조 타이머** (TIMER_ID_HIGHLIGHT): ~16ms 간격. `highlight_duration_ms`(기본 300ms) 동안 scale을 1.3 -> 1.0으로 보간.
- 타이머 ID는 AppMessages.cs 또는 Overlay/Animation 내부 상수로 정의.

#### 이벤트 중첩 처리

- **새 이벤트 발생 시 유지 타이머를 리셋** (KillTimer + SetTimer로 1500ms 재시작).
- **이미 표시 중(OverlayState.IsVisible == true)이면 페이드인을 생략**.
- **유지 중 새 이벤트**: 유지 타이머 리셋 (1500ms 재시작) + 새 캐럿 위치로 재계산.

#### 상태 전환 시퀀스

```
[이벤트 발생 -- 포커스 변경 또는 IME 상태 변경]
  -> 숨김 상태에서 시작:
     targetAlpha = (byte)(effectiveOpacity * 255)
       // label: config.Opacity(0.85) → 217, 캐럿 박스: config.CaretBoxOpacity(0.95) → 242
     FadeIn (SourceConstantAlpha: 0 -> targetAlpha, 150ms)
     -> Hold (1500ms, 유지 타이머)
     -> FadeOut (SourceConstantAlpha: targetAlpha -> 0, 400ms)
     -> Hidden (OverlayState.OnFadeOutComplete())

[IME 상태 변경 시 추가 -- 확대 강조]
  -> 1.3배 확대 (즉시, highlight_scale 적용)
  -> 원래 크기 복귀 (300ms, 페이드인과 동시 진행)
  -> 강조 타이머 완료 시 KillTimer

[유지 중 새 이벤트]
  -> 유지 타이머 리셋 (1500ms 재시작)
  -> 이미 표시 중이면 페이드인 생략
  -> 새 캐럿 위치로 재계산

[색상 변경 (한/영 전환) -- F-S06]
  -> 페이드 없이 즉시 반영
  -> 비트맵 픽셀만 새 색상으로 갱신 + premultiply 재적용
  -> UpdateLayeredWindow 즉시 호출
  -> 페이드인/아웃은 표시/숨김에만 사용
```

#### always 모드 동작

- `display_mode: "always"` 시: 인디케이터 항상 표시.
- 유휴 시 `idle_opacity`, 활성 시 `active_opacity` 사용.
  - 캐럿 박스 스타일: `caret_box_idle_opacity`(기본 0.65), `caret_box_active_opacity`(기본 1.0) 사용.
  - label 스타일: `idle_opacity`(기본 0.4), `active_opacity`(기본 0.95) 사용.
  - `caret_box_min_opacity`(기본 0.5) 하한 클램핑: **모든 caret_box 투명도에 적용** (always 모드뿐 아니라 on_event 모드 포함).
    - `effectiveOpacity = Math.Max(config.CaretBoxMinOpacity, rawOpacity)` — 이 아래로 내려가지 않음.
- 이벤트 없이 `always_idle_timeout_ms`(기본 3000ms) 경과 시 유휴 투명도로 복귀.
- **유휴->활성 전환 트리거**: 포커스 변경 또는 IME 상태 변경.
- 유휴<->활성 전환도 SourceConstantAlpha를 사용한 페이드로 전환.
- always 모드에서는 FadeOut으로 숨기지 않고, idle_opacity까지만 내려간다.

---

### 위치 안정성 (F-S01 ~ F-S06)

> **핵심 원칙**: 한/영 전환 시 인디케이터가 움직여 보이지 않아야 한다.
> 색상만 바뀌고 위치/크기는 그대로 유지되어야 전환을 자연스럽게 인지할 수 있다.

#### F-S01: label 고정 너비

- `label` 스타일은 텍스트("한"/"En"/"EN")에 관계없이 **고정 너비** 사용.
- 앱 시작 시 (또는 폰트/DPI 변경 시) 모든 후보 텍스트를 측정하여 최대 너비를 사전 계산.
- 고정 너비 계산:
  ```csharp
  int CalculateFixedLabelWidth(string fontFamily, int fontSize, bool bold)
  {
      string[] candidates = { "한", "En", "EN" };  // 표시 가능한 모든 텍스트
      int maxWidth = 0;
      foreach (var text in candidates)
      {
          int w = MeasureTextWidth(text, fontFamily, fontSize, bold);
          maxWidth = Math.Max(maxWidth, w);
      }
      return maxWidth + LABEL_PADDING_X * 2;
  }
  ```
- `LABEL_PADDING_X`는 DefaultConfig.cs에 정의 (기본 4px, DPI 스케일링 적용).
- `label_width` 설정값(기본 28px)보다 자동 계산값이 크면 자동 계산값 사용.
- 텍스트는 고정 너비 라벨 내 **중앙 정렬** (DT_CENTER).

#### F-S02: 배치 방향 고정

- 한/영 전환 시 배치 방향을 변경하지 않는다.
- 이벤트 발생 시점에 결정된 배치 방향(left/above/below 등)을 **해당 표시 주기 동안 유지**.
- 페이드아웃 완료 후 다음 이벤트에서만 재계산.
- 구현: `OverlayState.CurrentPlacement`를 보관. `IsVisible`이 true인 동안 변경 불가.

#### F-S03: 캐럿 박스 고정 크기

- caret_dot / caret_square / caret_underline / caret_vbar는 한/영 상태와 무관하게 **동일 크기 유지**.
- 색상만 변경한다.

#### F-S04: 강조 효과 중심 고정

- 반드시 중심점 기준 확대 수식 적용:
  ```
  newW = (int)Math.Round(w * scale)
  newH = (int)Math.Round(h * scale)
  newX = x - (newW - w) / 2
  newY = y - (newH - h) / 2
  ```
- 좌상단 기준 확대는 절대 사용 금지.

#### F-S05: 서브픽셀 정렬 방지

- 인디케이터 좌표를 항상 **정수(px)로 `Math.Round`** 처리.
- 소수점 좌표는 렌더링 시 흔들림(jitter) 유발.
- 강조 확대/축소의 중간 결과도 정수로 반올림한 뒤 사용.
- DPI 스케일 적용: `(int)Math.Round(value * dpiScale)` -- 절삭 `(int)(value * dpiScale)` 절대 금지 (계통적 과소 스케일링 유발).

#### F-S06: 색상 전환 즉시 반영

- 한/영 전환 시 색상 변경은 **페이드 없이 즉시** 반영.
- 처리 순서:
  1. 비트맵 픽셀을 새 색상으로 갱신
  2. premultiply 재적용
  3. `UpdateLayeredWindow` 즉시 호출
- 페이드인/아웃은 **표시/숨김에만** 사용.

---

### 인디케이터 배치 (포지셔닝)

#### 5가지 스타일과 정확한 배치 좌표

```
[label] 기본 28x24px (F-S01 자동 계산으로 고정 너비 결정)
  배치: 캐럿 왼쪽 (수직 중앙 정렬). left->above->below->mouse 자동 전환.
  gap = config.LabelGap (기본 DefaultConfig.LabelGap = 2px)

[caret_dot] 기본 8x8px (원형)
  배치: (caret_x + CaretBoxGapX, caret_y - dot_size + CaretBoxGapY)
  CaretBoxGapX = DefaultConfig.CaretBoxGapX = 2px
  CaretBoxGapY = DefaultConfig.CaretBoxGapY = 2px

[caret_square] 기본 8x8px (사각)
  배치: caret_dot과 동일 좌표.
  (caret_x + CaretBoxGapX, caret_y - square_size + CaretBoxGapY)

[caret_underline] 기본 24x3px (수평 바)
  배치: (caret_x - bar_w/2, caret_y + caret_h + UnderlineGap)
  UnderlineGap = DefaultConfig.UnderlineGap = 1px

[caret_vbar] 기본 3x16px (수직 바)
  배치: (caret_x - VbarOffsetX, caret_y)
  VbarOffsetX = DefaultConfig.VbarOffsetX = 1px
```

#### 배치 상수 (DefaultConfig.cs에 정의)

```
LabelGap       = 2    캐럿-라벨 간격 (px)
FocusWindowGap = 4    포커스 윈도우 fallback 시 윈도우 하단 간격 (px)
CaretBoxGapX   = 2    caret_dot/square X 오프셋 (px)
CaretBoxGapY   = 2    caret_dot/square Y 오프셋 (px)
UnderlineGap   = 1    underline 캐럿 아래 간격 (px)
VbarOffsetX    = 1    vbar X 오프셋 (px)
LABEL_PADDING_X = 4   label 텍스트 좌우 패딩 (px)
```

- 모든 오프셋은 DPI 스케일링 적용: `DpiHelper.Scale(value, dpiScale)`.

#### 라벨 4방향 자동 전환 (label 스타일 전용)

`caret_placement` 설정에 따라 선호 방향과 fallback 체인이 결정된다.

**`caret_placement="left"` (기본) fallback 체인:**
```
1순위: 캐럿 왼쪽 (Placement.Left)
  x = caret_x - indW - gap
  y = caret_y + (caret_h - indH) / 2     // 수직 중앙 정렬
  조건: x >= workArea.Left + margin

2순위: 캐럿 위 (Placement.Above) -- 왼쪽 공간 부족 시
  x = caret_x
  y = caret_y - indH - gap
  조건: y >= workArea.Top + margin

3순위: 캐럿 아래 (Placement.Below)
  x = caret_x
  y = caret_y + caret_h + gap

4순위: 마우스 커서 부근 -- 위 3방향 모두 실패 시
  GetCursorPos(out POINT pt)
  x = pt.X + config.MouseOffset.X, y = pt.Y + config.MouseOffset.Y
  // 기본값: MouseOffset.X=20, MouseOffset.Y=25 (PRD §6.1)
```

**`caret_placement="right"` fallback 체인:**
```
1순위: 캐럿 오른쪽 (Placement.Right)
  x = caret_x + caret_w + gap
  y = caret_y + (caret_h - indH) / 2
  조건: x + indW <= workArea.Right - margin
2순위: Above -> 3순위: Below -> 4순위: 마우스 커서 부근 (GetCursorPos + config.MouseOffset)
```

**`caret_placement="above"` fallback 체인:**
```
1순위: Above -> 2순위: Below -> 3순위: Left -> 4순위: 마우스 커서 부근
```

**`caret_placement="below"` fallback 체인:**
```
1순위: Below -> 2순위: Above -> 3순위: Left -> 4순위: 마우스 커서 부근
```

- `caret_placement_auto_flip: true`이면 위 fallback 체인이 활성화.
- false이면 1순위 방향만 사용 (공간 부족이면 클램핑으로 처리).

#### 통합 배치 로직 (CalculateIndicatorPosition)

```csharp
(int x, int y, Placement placement, double dpiScale) CalculateIndicatorPosition(
    int caretX, int caretY, int caretH,
    int indW, int indH, IndicatorStyle style, AppConfig config)
{
    // 0. position_mode 분기 (PRD §6.1: "caret" | "mouse" | "fixed")
    int anchorX = caretX, anchorY = caretY;
    switch (config.PositionMode)
    {
        case "mouse":
            User32.GetCursorPos(out POINT mpt);
            anchorX = mpt.X + config.MouseOffset.X;
            anchorY = mpt.Y + config.MouseOffset.Y;
            caretH = 0;  // 마우스 모드에서는 캐럿 높이 무관
            break;
        case "fixed":
            // config.FixedPosition + 앵커 계산 (PRD §6.1 fixed_position 참조)
            anchorX = config.FixedPosition.X;
            anchorY = config.FixedPosition.Y;
            caretH = 0;
            // 아래 DPI/클램핑 로직은 동일하게 적용
            break;
        // case "caret": 기본값 — anchorX/Y = caretX/Y 유지
    }

    // 1. 앵커 좌표가 속한 모니터의 작업 영역 획득
    IntPtr hMonitor = MonitorFromPoint(new POINT(anchorX, anchorY), MONITOR_DEFAULTTONEAREST);
    RECT workArea = DpiHelper.GetWorkArea(hMonitor);

    // 2. 해당 모니터의 DPI 스케일링 적용
    double dpiScale = DpiHelper.GetScale(hMonitor);
    int margin = DpiHelper.Scale(config.ScreenEdgeMargin, dpiScale);
    int sIndW = DpiHelper.Scale(indW, dpiScale);
    int sIndH = DpiHelper.Scale(indH, dpiScale);

    int x, y;
    Placement placement;

    // 3. 스타일별 기본 위치 계산 (매직 넘버 없음, 모두 config/DefaultConfig 상수 사용)
    switch (style)
    {
        case IndicatorStyle.Label:
            (x, y, placement) = CalcLabelPosition(
                anchorX, anchorY, caretH, sIndW, sIndH, workArea, margin, config);
            // CalcLabelPosition 내부: left→above→below→mouse 4방향 자동 전환
            // 4순위 mouse fallback: GetCursorPos + config.MouseOffset
            break;
        case IndicatorStyle.CaretDot:
        case IndicatorStyle.CaretSquare:
            x = anchorX + DpiHelper.Scale(DefaultConfig.CaretBoxGapX, dpiScale);
            y = anchorY - sIndH + DpiHelper.Scale(DefaultConfig.CaretBoxGapY, dpiScale);
            placement = Placement.CaretTopRight;
            break;
        case IndicatorStyle.CaretUnderline:
            x = anchorX - sIndW / 2;
            y = anchorY + caretH + DpiHelper.Scale(DefaultConfig.UnderlineGap, dpiScale);
            placement = Placement.CaretBelow;
            break;
        case IndicatorStyle.CaretVbar:
            x = anchorX - DpiHelper.Scale(DefaultConfig.VbarOffsetX, dpiScale);
            y = anchorY;
            placement = Placement.CaretOverlap;
            break;
        default:
            (x, y, placement) = (anchorX, anchorY, Placement.Left);
            break;
    }

    // 4. 모니터 작업 영역 내로 클램핑
    x = Math.Max(workArea.Left + margin, Math.Min(x, workArea.Right - sIndW - margin));
    y = Math.Max(workArea.Top + margin, Math.Min(y, workArea.Bottom - sIndH - margin));

    return (x, y, placement, dpiScale);
}
```

---

### Per-Monitor DPI 스케일링 (DpiHelper.cs 활용)

DpiHelper.cs는 Phase 01에서 이미 구현되어 있다. Overlay.cs에서 다음과 같이 활용:

#### DPI 조회

```
IntPtr hMonitor = MonitorFromPoint(caretPoint, MONITOR_DEFAULTTONEAREST)
Shcore.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY)
double dpiScale = dpiX / (double)DpiHelper.BASE_DPI    // BASE_DPI = 96
```

#### 스케일 적용 (DpiHelper.Scale 메서드)

```
(int)Math.Round(value * dpiScale)
```

- **절대 금지**: `(int)(value * dpiScale)` -- 절삭은 계통적 과소 스케일링 유발.
- 반드시 `Math.Round` 사용.

#### DPI별 스케일 예시

| 모니터 DPI | scale | 8px dot -> | 28x24 label -> |
|-----------|-------|-----------|---------------|
| 96 (100%) | 1.0 | 8px | 28x24px |
| 120 (125%) | 1.25 | 10px | 35x30px |
| 144 (150%) | 1.5 | 12px | 42x36px |
| 192 (200%) | 2.0 | 16px | 56x48px |

#### HFONT DPI 적용

```
-MulDiv(fontSize_pt, dpiY, 72)
```
- `MulDiv`은 Kernel32 P/Invoke (gdi32.dll 아님!).
- `dpiY`는 GetDpiForMonitor에서 얻은 Y축 DPI.
- 모니터 전환으로 DPI 변경 감지 시 HFONT 재생성.

---

### rcWork 클램핑 (모니터 작업 영역 경계 보정)

#### 모니터 특정 및 작업 영역 획득

```csharp
IntPtr hMonitor = MonitorFromPoint(caretPoint, MONITOR_DEFAULTTONEAREST);
var monitorInfo = new MONITORINFOEXW();
monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
GetMonitorInfoW(hMonitor, ref monitorInfo);
RECT workArea = monitorInfo.rcWork;   // 작업표시줄 제외된 실제 사용 영역
```

#### 클램핑 수식

```
x = Math.Max(workArea.Left + margin, Math.Min(x, workArea.Right - indW - margin))
y = Math.Max(workArea.Top + margin, Math.Min(y, workArea.Bottom - indH - margin))
```

- `margin` = `config.ScreenEdgeMargin` (기본 8px) DPI 스케일 적용.

#### 금지 사항

- `Screen.Bounds` 같은 가상 데스크톱 전체 영역 사용 금지. **rcWork** 기준만 사용.
- rcWork를 캐시하지 않는다 -- 매 폴링 사이클마다 `GetMonitorInfoW` 호출 (경량 API, 성능 부담 없음).
- 보조 모니터의 **음수 좌표** 정상 처리 필수.
  - 예: 보조 모니터가 주 모니터 왼쪽에 위치 시 rcWork = (-1920, 0, -1, 1040).

#### 비표준 모니터 배치 예시

```
보조 모니터              주 모니터
(-1920,0)------(0,0)-------(1920,0)
|             ||              |
|  rcWork:    ||  rcWork:     |
|  (-1920,0)  ||  (0,40)      | <- 작업표시줄 40px
|  ~(-1,1040) ||  ~(1919,1080)|
|             ||              |
(-1920,1080)--(0,1080)--(1920,1080)

캐럿이 (-500, 300)에 있으면:
  -> MonitorFromPoint -> 보조 모니터
  -> rcWork = (-1920, 0, -1, 1040)
  -> 이 영역 내에서 경계 보정
```

#### 시스템 메시지 처리 (Program.cs 메인 루프에서)

- `WM_DISPLAYCHANGE`: 모니터 연결/분리, 해상도 변경 시 -> DPI/rcWork 재조회 + HFONT/DIB 재생성 여부 판단.
- `WM_SETTINGCHANGE`: 작업표시줄 위치/크기 변경 시 -> rcWork 재조회.
- 이 메시지 처리는 Program.cs(Phase 03)의 WndProc에서 수행하고, Overlay에 재계산을 요청하는 구조.

---

## 검증 기준

### 렌더링 파이프라인
- [ ] UpdateLayeredWindow + 32-bit PARGB DIB 방식 사용 확인 (SetLayeredWindowAttributes 사용 금지)
- [ ] 5종 인디케이터 스타일이 모두 정상 렌더링되는지 확인
- [ ] label_shape 3종(rounded_rect, circle, pill) 모두 정상 렌더링
- [ ] premultiplied alpha가 올바르게 적용되어 둥근 모서리가 깔끔한지 확인
- [ ] 페이드 시 픽셀 버퍼를 재순회하지 않고 SourceConstantAlpha만 변경하는지 확인

### DIB 재사용
- [ ] 색상 변경(한/영 전환) 시 DIB를 재생성하지 않고 픽셀만 갱신 + premultiply 재적용
- [ ] DPI/스타일/크기 변경 시에만 DIB 재생성
- [ ] highlight scale 시 DIB 재생성 없이 UpdateLayeredWindow size 파라미터만 변경

### 위치 안정성
- [ ] F-S01: label 고정 너비 -- "한"/"En"/"EN" 전환 시 너비 변동 없음
- [ ] F-S02: 표시 중 배치 방향 변경 안 됨 (한/영 전환으로 인한 방향 점프 없음)
- [ ] F-S03: 캐럿 박스 스타일 한/영 전환 시 크기 동일 (색상만 변경)
- [ ] F-S04: 강조 확대 시 중심점 기준 (좌상단 기준 아님)
- [ ] F-S05: 모든 좌표가 Math.Round 정수 (서브픽셀 jitter 없음)
- [ ] F-S06: 색상 전환 즉시 반영 (페이드 없음). 비트맵 갱신 + premultiply + UpdateLayeredWindow

### 애니메이션
- [ ] 페이드인 0->targetAlpha (150ms) -> 유지 (1500ms) -> 페이드아웃 targetAlpha->0 (400ms) -> 숨김
  - targetAlpha = (byte)(effectiveOpacity * 255). label: opacity(0.85)→217, 캐럿 박스: caret_box_opacity(0.95)→242
- [ ] IME 전환 시 1.3x 확대 즉시 -> 1.0 복귀 (300ms, 페이드인과 동시)
- [ ] 유지 중 새 이벤트 -> 유지 타이머 리셋, 이미 표시 중이면 페이드인 생략
- [ ] always 모드: idle_opacity <-> active_opacity 전환 정상 동작
- [ ] WM_TIMER 기반 (~16ms, ~60fps)

### DPI 스케일링
- [ ] DpiHelper.Scale 사용 (Math.Round, 절삭 금지)
- [ ] HFONT: -MulDiv(pt, dpiY, 72) 사용
- [ ] 100%/125%/150%/200% DPI에서 인디케이터 크기 정확

### rcWork 클램핑
- [ ] MonitorFromPoint -> GetMonitorInfoW -> rcWork 기준 (Screen.Bounds 사용 금지)
- [ ] rcWork 캐시 안 함 (매 폴링마다 조회)
- [ ] 보조 모니터 음수 좌표 정상 처리
- [ ] 작업표시줄 제외 영역 내에서 인디케이터 위치

### GDI 리소스 관리
- [ ] SafeFontHandle, SafeBitmapHandle 래퍼 사용
- [ ] HFONT 앱 시작 시 1회 생성, DPI/폰트 변경 시에만 재생성
- [ ] memDC + HBITMAP 수명 = 오버레이 윈도우 수명

### P1-P5 원칙
- [ ] P1: NuGet 외부 패키지 없음
- [ ] P2: 라벨 텍스트 한글 기본 ("한"/"En"/"EN")
- [ ] P3: 매직 넘버 없음 (모두 DefaultConfig/config 상수)
- [ ] P4: DpiHelper, ColorHelper, SafeGdiHandles 공통 모듈 사용
- [ ] P5: (Phase 04에서는 직접 해당 없음)

## 산출물

| 파일 | 설명 |
|------|------|
| `UI/Overlay.cs` | 오버레이 윈도우 생성 + UpdateLayeredWindow 렌더링 파이프라인 + 5종 스타일 + GDI 텍스트 + 위치 계산 + OverlayState |
| `UI/Animation.cs` | WM_TIMER 기반 페이드/강조 애니메이션 + always 모드 투명도 관리 |
