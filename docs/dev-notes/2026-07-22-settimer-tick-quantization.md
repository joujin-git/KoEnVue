# 2026-07-22 `SetTimer` 틱 양자화 실측 — `uElapse=16` 은 60fps 가 아니다

## 결론

Windows `SetTimer` 의 `uElapse` 는 **약 15.625ms 시스템 클럭 격자로 양자화**된다. 실배달 주기는

```
실배달 주기 ≈ ceil(uElapse / 15.625) × 15.625 ms
```

즉 **16 은 15 보다 정확히 한 틱 더 느리다.** 16ms 를 요청하면 실제로는 약 **31ms(≈32fps)** 마다 배달된다.
조사 당시 `AnimationFrameMs = 16` / `CursorAlwaysPollMs = 16` 에 달린 **"~60fps" 주석은 사실과 달랐다.**
**(2026-07-23)** 두 상수 디폴트는 **15** 로 변경됨 — 실배달 ~15.6ms(≈64fps). 주석도 양자화 설명으로 교체.

이 문서는 그 사실을 **실측으로** 박제한다. 추론이 아니라 재현 가능한 측정이며, 벤치 프로그램 전문을
아래에 통째로 보존한다 — "16 이냐 15 냐" 논쟁은 값이 1 차이라 눈으로는 절대 판별되지 않으므로, 재발할
때마다 다시 재려면 프로그램이 남아 있어야 한다.

## 실측 결과

측정 조건: **창 타이머**(`SetTimer(hwnd, …)` — KoEnVue 가 쓰는 방식과 동일), message-only 윈도우,
케이스당 120 샘플, warmup 10, 간격은 `Stopwatch.GetTimestamp()` 델타.

| `uElapse` | median | mean | min | p95 | median / 15.625 |
|---|---|---|---|---|---|
| 8 | 15.49ms | 15.59ms | 14.55ms | 16.09ms | 0.99 틱 |
| 15 | 15.56ms | 16.61ms | 14.61ms | 29.91ms | 1.00 틱 |
| **16** | **30.78ms** | 27.52ms | 14.79ms | 31.65ms | **1.97 틱** |
| 17 | 30.99ms | 31.29ms | 29.31ms | 32.17ms | 1.98 틱 |
| 50 | 62.03ms | 62.62ms | 60.38ms | 63.90ms | 3.97 틱 |

읽는 법: `15 → 16` 으로 **1 올리면 배달 주기가 15.56 → 30.78ms 로 두 배가 된다.** 격자를 한 칸 넘겼기
때문이다. 반대로 `16 → 15` 로 1 내리면 주기가 절반이 된다 — 이 저장소에서 가장 값싼 개선 후보가
여기 있는 이유다.

### 추가 확인 (전부 실측)

- **`timeBeginPeriod(1)` 은 효과가 없다.** 호출 전후 수치가 동일했다(`uElapse=16` → 30.78ms /
  30.70ms). `NtQueryTimerResolution` 이 `current=1.0000ms` 를 보고하는데도 `SetTimer` 는 15.625ms
  격자를 유지한다. 이는 "이미 해상도가 1ms 라서 더 못 올린다"가 아니라 **USER 타이머가 시스템 타이머
  해상도와 애초에 다른 축**이라는 뜻이다. 따라서 "`timeBeginPeriod` 를 쓰면 부드러워지지만 배터리를
  먹는다"는 트레이드오프 자체가 이 앱에는 **성립하지 않는다** — 먹기만 하고 얻는 게 없다.
- **`uElapse=8` 도 15.49ms.** `SetTimer` 로는 15.625ms 보다 빠르게 갈 수 없다. 8ms 프레임을 원하면
  `SetTimer` 를 버려야 한다(별개 축, 본 문서 범위 밖).
- **`Thread.Sleep` 은 이 격자와 무관하다** — 시스템 타이머 해상도를 따르는 다른 메커니즘이다. 감지
  스레드의 폴링([`Program.cs:1207`](../../Program.cs) `Thread.Sleep(_config.PollIntervalMs + backoffMs)`)은
  본 문서의 영향을 **받지 않는다.**

## 영향받는 상수 (조사 당시 선언값 → 현재)

네 상수 모두 창 타이머(`User32.SetTimer(hwnd, …)`)로 등록된다 — 전부 위 격자의 적용 대상이다.

| 상수 | 위치 | 조사 당시 | 현재 디폴트 | 실배달 (선언 기준) | 주석 |
|---|---|---|---|---|---|
| `CursorAlwaysPollMs` | [`App/Config/DefaultConfig.cs`](../../App/Config/DefaultConfig.cs) | 16 | **15** ✅ | 15 → **~15.6ms (≈64fps)** / (구)16 → ~31ms | 양자화 설명으로 정정됨 |
| `AnimationFrameMs` | 동상 | 16 | **15** ✅ | 동일 | 양자화 설명으로 정정됨 |
| `CursorMotionPollMs` | 동상 | 50 | 50 | ~62ms | "정지 검출 모드 — 50ms" (실제 4 틱) |
| `CapsLockPollMs` | 동상 | 200 | 200 | ~203ms | 무해 (13 틱, 오차 1.5%) |

`AnimationFrameMs` 는 **메인 인디 애니메이션 전체의 프레임 상수**다 —
[`Core/Animation/OverlayAnimator.cs`](../../Core/Animation/OverlayAnimator.cs) 의 Fade 4 지점 + Highlight + Slide, 그리고
[`App/UI/CursorOverlay.cs`](../../App/UI/CursorOverlay.cs) 의 커서 IME 전환 팝.
**15 적용 후** 실효 ≈64fps (이전 16 시절 ≈32fps).

## 재현 방법

별도 콘솔 프로젝트 하나면 된다. csproj 는 **`net10.0` + `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
만** 있으면 충분하다(`unsafe` 는 `MSG*`/`WNDCLASSEXW*` 포인터와 `[UnmanagedCallersOnly]` WndProc 때문).
NuGet 0 — 본 저장소의 P1 과 동일 조건이며, `[DllImport]` 대신 `[LibraryImport]` 를 쓰는 것도 동일.

이 프로그램은 **릴리스 산출물이 아니라 일회성 계측 도구**다. 저장소에 프로젝트로 편입하지 않고 이
문서에 소스만 박제한다.

```csharp
// WM_TIMER 실배달 주기 측정 — SetTimer(uElapse) 가 실제로 몇 ms 마다 배달되는지.
// KoEnVue 의 CursorAlwaysPollMs=16 / AnimationFrameMs=16 이 정말 ~60fps 인지 재현 검증용.
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static unsafe partial class Program
{
    private const uint WM_TIMER = 0x0113;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const nuint TIMER_ID = 1;

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static partial int GetMessageW(MSG* lpMsg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(MSG* lpMsg, IntPtr hWnd, uint min, uint max, uint removeMsg);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProcW(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static partial IntPtr CreateWindowExW(uint exStyle, IntPtr className, IntPtr windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint uPeriod);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryTimerResolution")]
    private static partial int NtQueryTimerResolution(uint* min, uint* max, uint* cur);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam;
        public uint time; public int ptX; public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(IntPtr h, uint m, nuint w, nint l) => DefWindowProcW(h, m, w, l);

    private static IntPtr CreateMessageWindow()
    {
        IntPtr name = Marshal.StringToHGlobalUni("KoEnVueTimerBench");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, nuint, nint, nint>)&WndProc,
            lpszClassName = name,
        };
        ushort atom = RegisterClassExW(&wc);
        if (atom == 0) throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");
        IntPtr hwnd = CreateWindowExW(0, name, IntPtr.Zero, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    /// <summary>메시지 큐에 남은 잔여 메시지를 모두 배출 — 이전 측정의 WM_TIMER 가 다음 측정을 오염시키지 않도록.</summary>
    private static void DrainQueue()
    {
        MSG m;
        while (PeekMessageW(&m, IntPtr.Zero, 0, 0, 0x0001 /* PM_REMOVE */)) { }
    }

    /// <summary>uElapse 로 타이머를 걸고 WM_TIMER 배달 간격을 sampleCount 회 측정.</summary>
    private static double[] Measure(IntPtr hwnd, uint uElapse, int sampleCount, int warmup)
    {
        DrainQueue();
        var deltas = new List<double>(sampleCount);
        nuint id = SetTimer(hwnd, TIMER_ID, uElapse, IntPtr.Zero);
        if (id == 0) throw new InvalidOperationException($"SetTimer failed: {Marshal.GetLastWin32Error()}");

        long prev = 0;
        int seen = 0;
        MSG msg;
        double freq = Stopwatch.Frequency;

        while (deltas.Count < sampleCount)
        {
            int r = GetMessageW(&msg, IntPtr.Zero, 0, 0);
            if (r <= 0) break;
            if (msg.message != WM_TIMER) continue;

            long now = Stopwatch.GetTimestamp();
            seen++;
            if (seen > warmup && prev != 0)
                deltas.Add((now - prev) * 1000.0 / freq);
            prev = now;
        }

        // hWnd=NULL 이면 SetTimer 가 nIDEvent 를 무시하고 새 ID 를 발급하므로 반환값으로 죽여야 한다.
        KillTimer(hwnd, hwnd == IntPtr.Zero ? id : TIMER_ID);
        DrainQueue();
        return deltas.ToArray();
    }

    private static (double median, double mean, double min, double p95) Stats(double[] d)
    {
        var s = (double[])d.Clone();
        Array.Sort(s);
        double median = s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
        double mean = 0; foreach (double v in s) mean += v; mean /= s.Length;
        return (median, mean, s[0], s[(int)(s.Length * 0.95)]);
    }

    private static void PrintResolution(string label)
    {
        uint min = 0, max = 0, cur = 0;
        NtQueryTimerResolution(&min, &max, &cur);
        Console.WriteLine($"  [{label}] 타이머 해상도: current={cur / 10000.0:F4}ms  min={min / 10000.0:F4}ms  max={max / 10000.0:F4}ms");
    }

    private static void Main()
    {
        uint[] cases = [8, 15, 16, 17, 50];
        const int Samples = 120;
        const int Warmup = 10;

        Console.WriteLine($"OS={Environment.OSVersion.VersionString}  Stopwatch.Frequency={Stopwatch.Frequency}");
        Console.WriteLine();

        IntPtr hwnd = CreateMessageWindow();
        Console.WriteLine($"message-only window = 0x{hwnd:X}");
        Console.WriteLine();

        foreach (bool boostPeriod in new[] { false, true })
        {
            if (boostPeriod) TimeBeginPeriod(1);
            string tag = boostPeriod ? "timeBeginPeriod(1) 호출 후" : "기본 상태 (timeBeginPeriod 미호출)";
            Console.WriteLine($"=== {tag} ===");
            PrintResolution(boostPeriod ? "after" : "before");

            // KoEnVue 는 창 타이머(SetTimer(hwnd, ...))만 쓰므로 창 타이머만 측정한다.
            foreach (uint ms in cases)
            {
                var d = Measure(hwnd, ms, Samples, Warmup);
                var (median, mean, min, p95) = Stats(d);
                double ticks = median / 15.625;
                Console.WriteLine(
                    $"  창 타이머  uElapse={ms,2}  →  median={median,6:F2}ms  mean={mean,6:F2}ms  min={min,6:F2}ms  p95={p95,6:F2}ms   (median/15.625 = {ticks:F2} 틱)");
            }

            if (boostPeriod) TimeEndPeriod(1);
            Console.WriteLine();
        }
    }
}
```

### 측정 중 밟은 함정 — 스레드 타이머의 ID 재발급

`SetTimer(NULL, …)` (**스레드 타이머**)는 `nIDEvent` 를 **무시하고 새 ID 를 발급**한다. 따라서
`KillTimer(NULL, 원래ID)` 는 실패하고 타이머가 죽지 않은 채 누적된다 → 이후 모든 케이스의 측정이
이전 타이머들의 `WM_TIMER` 로 오염된다. **median 0.05ms 처럼 물리적으로 불가능한 값이 나오면 이걸
의심할 것.** 반드시 `SetTimer` 의 **반환값**으로 죽여야 한다(위 코드의 `KillTimer(hwnd, hwnd ==
IntPtr.Zero ? id : TIMER_ID)`).

**창 타이머(`SetTimer(hwnd, …)`)는 해당 없다** — `nIDEvent` 가 그대로 유지된다. KoEnVue 는 창
타이머만 쓰므로 이 함정은 앱 코드와 무관하고, 계측 프로그램에만 해당한다.

## 권고 → 적용 (2026-07-23)

1. **`CursorAlwaysPollMs` 16 → 15, `AnimationFrameMs` 16 → 15.** ✅ 적용됨.
   효과: 배달 주기 30.78ms → 15.56ms(약 2배 개선). 800px/s 로 마우스를 움직일 때 커서 링이 화면상
   **약 12px 더 붙는다.** 신규 config 키 0, [`Core/`](../../Core) 변경 0줄, 값 리터럴 2개 수정.
2. **위 표의 "사실과 다름" 주석 2곳 정정** — `DefaultConfig` XML 주석. ✅ 같은 변경과 함께 처리됨.

## 남는 한계 — 폴링을 고쳐도 사라지지 않는 것

**DWM 합성 1프레임(~16.7ms)** 은 그대로다. 마우스 포인터는 **하드웨어 커서 플레인**에서 GPU 가 직접
그려 합성을 우회하지만, 레이어드 윈도우인 커서 인디는 DWM 합성을 거친다. 따라서 폴링을 아무리
촘촘히 해도 **원리적으로 완전 추종은 불가능**하다. 폴링 정렬은 "31ms 지각 지연을 15.6ms 로 줄인다"는
것이지 "0 으로 만든다"가 아니다.

## 참고

- 흐림/투명도로 이 지연을 *지각적으로* 가리려던 시도의 전면 기각 기록:
  [PR-28](../improvement-plan/PR-28-cursor-lag-perceptual-masking.md).
- 커서 인디 엔진이 왜 메인 인디와 별개인지 / `WH_MOUSE_LL`·`WM_INPUT` 를 왜 거부했는지:
  [2026-05-27-cursor-indicator.md](2026-05-27-cursor-indicator.md).
