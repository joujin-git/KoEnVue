using System.Runtime.InteropServices;
using KoEnVue.Config;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.Detector;

/// <summary>
/// 캐럿 위치 추적. 4-tier fallback + 앱별 LRU 캐싱.
/// DPI 스케일링은 적용하지 않음 — raw 스크린 좌표만 반환 (스케일링은 Overlay 담당).
/// </summary>
internal static class CaretTracker
{
    // --- Tier 식별 상수 (P3: 매직 넘버 금지) ---
    private const int TierGuiThread = 1;
    private const int TierUia = 2;
    private const int TierWindowRect = 3;
    private const int TierMouse = 4;

    // --- 디버그 오버레이용 방식 이름 ---
    private const string MethodGuiThread = "gui_thread";
    private const string MethodUia = "uia";
    private const string MethodWindowRect = "window_rect";
    private const string MethodMouse = "mouse";

    // --- Tier 1 재시도 (rcCaret==(0,0,0,0) 타이밍 이슈 대응) ---
    private const int MaxTier1Retries = 3;

    // --- 앱별 감지 방식 LRU 캐시 ---
    private static readonly Dictionary<string, int> _methodCache = new();
    private static readonly LinkedList<string> _lruOrder = new();

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 캐럿 위치를 4-tier fallback으로 감지한다.
    /// </summary>
    /// <returns>
    /// (x, y, w, h, method) 튜플. method는 디버그용 감지 방식 이름.
    /// 특정 tier 강제 모드에서 실패 시 null.
    /// auto 모드에서는 Tier 4(마우스)가 항상 성공하므로 null 불가.
    /// </returns>
    public static (int x, int y, int w, int h, string method)? GetCaretPosition(
        IntPtr hwndFocus, uint threadId, string processName, AppConfig config)
    {
        // 특정 방식 강제
        if (config.CaretMethod != "auto")
        {
            return config.CaretMethod switch
            {
                "gui_thread" => TryTier1WithRetry(hwndFocus, threadId, config),
                "uia" => TryTier2(hwndFocus, config),
                "mouse" => TryTier4(),
                _ => RunFullFallback(hwndFocus, threadId, processName, config),
            };
        }

        return RunFullFallback(hwndFocus, threadId, processName, config);
    }

    /// <summary>
    /// 설정 리로드 시 캐시 전체 무효화.
    /// </summary>
    public static void ClearCache()
    {
        _methodCache.Clear();
        _lruOrder.Clear();
        Logger.Debug("Caret method cache cleared");
    }

    // ================================================================
    // Fallback 실행
    // ================================================================

    private static (int x, int y, int w, int h, string method)? RunFullFallback(
        IntPtr hwndFocus, uint threadId, string processName, AppConfig config)
    {
        // 캐시 히트 → 해당 tier 먼저 시도
        if (_methodCache.TryGetValue(processName, out int cachedTier))
        {
            var cached = TryTier(cachedTier, hwndFocus, threadId, config);
            if (cached.HasValue) return cached.Value;
            // 캐시 실패 → 1순위부터 재시도
        }

        // Tier 1: GetGUIThreadInfo → rcCaret → ClientToScreen (with retry)
        var result = TryTier1WithRetry(hwndFocus, threadId, config);
        if (result.HasValue) { CacheMethod(processName, TierGuiThread); return result.Value; }

        // Tier 2: UIA (Phase 07 placeholder)
        result = TryTier2(hwndFocus, config);
        if (result.HasValue) { CacheMethod(processName, TierUia); return result.Value; }

        // Tier 3: 포커스 윈도우 영역 기반
        result = TryTier3(hwndFocus);
        if (result.HasValue) { CacheMethod(processName, TierWindowRect); return result.Value; }

        // Tier 4: 마우스 커서 (항상 성공)
        result = TryTier4();
        if (result.HasValue) { CacheMethod(processName, TierMouse); return result.Value; }

        return null;
    }

    private static (int x, int y, int w, int h, string method)? TryTier(
        int tier, IntPtr hwndFocus, uint threadId, AppConfig config)
    {
        return tier switch
        {
            TierGuiThread => TryTier1(hwndFocus, threadId),
            TierUia => TryTier2(hwndFocus, config),
            TierWindowRect => TryTier3(hwndFocus),
            TierMouse => TryTier4(),
            _ => null,
        };
    }

    // ================================================================
    // 4-tier 내부 구현
    // ================================================================

    /// <summary>
    /// Tier 1: GetGUIThreadInfo → rcCaret → ClientToScreen.
    /// 핵심: ClientToScreen에 gti.hwndCaret 전달 (hwndFocus 아님!).
    /// </summary>
    private static (int x, int y, int w, int h, string method)? TryTier1(
        IntPtr hwndFocus, uint threadId)
    {
        GUITHREADINFO gti = default;
        gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();

        if (!User32.GetGUIThreadInfo(threadId, ref gti))
            return null;

        // rcCaret == (0,0,0,0) → 유효하지 않음
        if (gti.rcCaret.Left == 0 && gti.rcCaret.Top == 0
            && gti.rcCaret.Right == 0 && gti.rcCaret.Bottom == 0)
            return null;

        POINT pt = new(gti.rcCaret.Left, gti.rcCaret.Top);
        User32.ClientToScreen(gti.hwndCaret, ref pt);  // hwndCaret 사용!

        int w = gti.rcCaret.Right - gti.rcCaret.Left;
        int h = gti.rcCaret.Bottom - gti.rcCaret.Top;

        return (pt.X, pt.Y, w, h, MethodGuiThread);
    }

    /// <summary>
    /// Tier 1 with retry: rcCaret==(0,0,0,0)이면 CaretPollIntervalMs 간격으로 최대 3회 재시도.
    /// GetGUIThreadInfo 자체 실패(API 에러)는 재시도 없이 즉시 null (타이밍 이슈가 아님).
    /// </summary>
    private static (int x, int y, int w, int h, string method)? TryTier1WithRetry(
        IntPtr hwndFocus, uint threadId, AppConfig config)
    {
        for (int attempt = 0; attempt <= MaxTier1Retries; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(config.CaretPollIntervalMs);

            GUITHREADINFO gti = default;
            gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();

            if (!User32.GetGUIThreadInfo(threadId, ref gti))
                return null;  // API 실패 → 재시도 불필요

            if (gti.rcCaret.Left == 0 && gti.rcCaret.Top == 0
                && gti.rcCaret.Right == 0 && gti.rcCaret.Bottom == 0)
                continue;  // 타이밍 이슈 → 재시도

            POINT pt = new(gti.rcCaret.Left, gti.rcCaret.Top);
            User32.ClientToScreen(gti.hwndCaret, ref pt);

            int w = gti.rcCaret.Right - gti.rcCaret.Left;
            int h = gti.rcCaret.Bottom - gti.rcCaret.Top;
            return (pt.X, pt.Y, w, h, MethodGuiThread);
        }
        return null;  // 모든 재시도 소진
    }

    /// <summary>
    /// Tier 2: UI Automation. Phase 07에서 UiaClient 연결 시 구현.
    /// </summary>
    private static (int x, int y, int w, int h, string method)? TryTier2(
        IntPtr hwndFocus, AppConfig config)
    {
        // Phase 07에서 UiaClient.GetCaretBounds 호출로 교체 예정
        return null;
    }

    /// <summary>
    /// Tier 3: 포커스 윈도우 영역 기반 fallback.
    /// </summary>
    private static (int x, int y, int w, int h, string method)? TryTier3(IntPtr hwndFocus)
    {
        if (!User32.GetWindowRect(hwndFocus, out RECT rect))
            return null;

        return (rect.Left, rect.Bottom + DefaultConfig.FocusWindowGap, 0, 0, MethodWindowRect);
    }

    /// <summary>
    /// Tier 4: 마우스 커서 위치 (최종 fallback — 항상 성공).
    /// </summary>
    private static (int x, int y, int w, int h, string method)? TryTier4()
    {
        User32.GetCursorPos(out POINT cursor);
        return (cursor.X, cursor.Y, 0, 0, MethodMouse);
    }

    // ================================================================
    // LRU 캐시
    // ================================================================

    /// <summary>
    /// 앱별 성공한 감지 방식을 LRU 캐시에 저장.
    /// </summary>
    private static void CacheMethod(string processName, int tier)
    {
        if (_methodCache.ContainsKey(processName))
        {
            _lruOrder.Remove(processName);
        }
        else if (_methodCache.Count >= DefaultConfig.AppMethodCacheMaxSize)
        {
            // LRU 제거: 가장 오래된 항목
            string oldest = _lruOrder.Last!.Value;
            _lruOrder.RemoveLast();
            _methodCache.Remove(oldest);
        }

        _methodCache[processName] = tier;
        _lruOrder.AddFirst(processName);
    }
}
