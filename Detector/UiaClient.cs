using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using KoEnVue.Config;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.Detector;

/// <summary>
/// UIA 전용 STA 스레드 로직.
/// ConcurrentQueue + ManualResetEventSlim 기반 요청/응답 (NativeAOT 호환).
/// </summary>
internal static class UiaClient
{
    // ================================================================
    // COM
    // ================================================================

    private static readonly Guid CLSID_CUIAutomation =
        new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private static readonly StrategyBasedComWrappers _comWrappers = new();
    private static IUIAutomation? _automation;

    // ================================================================
    // 요청/응답 큐
    // ================================================================

    private static readonly ConcurrentQueue<UiaRequest> _requestQueue = new();
    private static readonly ManualResetEventSlim _signal = new(false);
    private static volatile bool _stopping;

    // 결과 캐시: hwnd + timestamp
    private static IntPtr _cachedHwnd;
    private static (int x, int y, int w, int h)? _cachedResult;
    private static long _cachedTimestamp;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// UIA 스레드에서 호출. CUIAutomation COM 인스턴스 생성.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Guid clsid = CLSID_CUIAutomation;
            Guid iid = typeof(IUIAutomation).GUID;
            int hr = Ole32.CoCreateInstance(ref clsid, IntPtr.Zero,
                Win32Constants.CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);

            if (hr == 0 && ppv != IntPtr.Zero)
            {
                _automation = (IUIAutomation)_comWrappers.GetOrCreateObjectForComInstance(
                    ppv, CreateObjectFlags.None);
                Marshal.Release(ppv);
                Logger.Info("UIA CUIAutomation COM initialized");
            }
            else
            {
                Logger.Warning($"UIA CoCreateInstance failed: 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            _automation = null;
            Logger.Warning($"UIA init exception: {ex.Message}");
        }
    }

    /// <summary>
    /// UIA 스레드 메인 루프. signal 대기 후 큐 처리.
    /// </summary>
    public static void ProcessRequests()
    {
        while (!_stopping)
        {
            _signal.Wait(TimeSpan.FromMilliseconds(DefaultConfig.UiaLoopIntervalMs));
            _signal.Reset();

            while (_requestQueue.TryDequeue(out UiaRequest? request))
            {
                var result = GetCaretBoundsInternal(request.HwndFocus);
                request.Completion.TrySetResult(result);
            }
        }
    }

    /// <summary>
    /// 감지 스레드에서 호출. UIA 캐럿 위치 조회 (타임아웃 포함).
    /// </summary>
    public static (int x, int y, int w, int h)? GetCaretBounds(IntPtr hwndFocus, int timeoutMs, int cacheTtlMs)
    {
        if (_automation is null) return null;

        // 캐시 확인
        long now = Environment.TickCount64;
        if (hwndFocus == _cachedHwnd && _cachedResult.HasValue
            && (now - _cachedTimestamp) < cacheTtlMs)
        {
            return _cachedResult;
        }

        var request = new UiaRequest(hwndFocus);
        _requestQueue.Enqueue(request);
        _signal.Set();

        if (request.Completion.Task.Wait(timeoutMs))
        {
            var result = request.Completion.Task.Result;
            // 캐시 갱신
            _cachedHwnd = hwndFocus;
            _cachedResult = result;
            _cachedTimestamp = now;
            return result;
        }

        return null;  // 타임아웃
    }

    /// <summary>
    /// 앱 종료 시 COM 정리.
    /// </summary>
    public static void Shutdown()
    {
        _stopping = true;
        _signal.Set();  // 대기 중인 루프 깨우기
        _automation = null;
    }

    // ================================================================
    // Internal — COM 호출 체인
    // ================================================================

    private static (int x, int y, int w, int h)? GetCaretBoundsInternal(IntPtr hwndFocus)
    {
        if (_automation is null) return null;

        nint elementPtr = 0, patternPtr = 0, rangePtr = 0, safeArrayPtr = 0;
        try
        {
            // 1. ElementFromHandle
            int hr = _automation.ElementFromHandle(hwndFocus, out elementPtr);
            if (hr != 0 || elementPtr == 0) return null;

            var element = (IUIAutomationElement)_comWrappers.GetOrCreateObjectForComInstance(
                elementPtr, CreateObjectFlags.None);

            // 2. GetCurrentPattern(TextPattern2Id)
            hr = element.GetCurrentPattern(Win32Constants.UIA_TextPattern2Id, out patternPtr);
            if (hr != 0 || patternPtr == 0) return null;

            var textPattern = (IUIAutomationTextPattern2)_comWrappers.GetOrCreateObjectForComInstance(
                patternPtr, CreateObjectFlags.None);

            // 3. GetCaretRange
            hr = textPattern.GetCaretRange(out _, out rangePtr);
            if (hr != 0 || rangePtr == 0) return null;

            var range = (IUIAutomationTextRange)_comWrappers.GetOrCreateObjectForComInstance(
                rangePtr, CreateObjectFlags.None);

            // 4. GetBoundingRectangles → SAFEARRAY(double)
            hr = range.GetBoundingRectangles(out safeArrayPtr);
            if (hr != 0 || safeArrayPtr == 0) return null;

            return ExtractBoundsFromSafeArray(safeArrayPtr);
        }
        catch (Exception ex)
        {
            Logger.Debug($"UIA GetCaretBounds failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (safeArrayPtr != 0) OleAut32.SafeArrayDestroy(safeArrayPtr);
            if (rangePtr != 0) Marshal.Release(rangePtr);
            if (patternPtr != 0) Marshal.Release(patternPtr);
            if (elementPtr != 0) Marshal.Release(elementPtr);
        }
    }

    /// <summary>
    /// SAFEARRAY(double)에서 (x, y, w, h) 추출.
    /// GetBoundingRectangles는 [x, y, width, height, ...] 패턴의 double 배열 반환.
    /// </summary>
    private static unsafe (int x, int y, int w, int h)? ExtractBoundsFromSafeArray(nint psa)
    {
        int hr = OleAut32.SafeArrayGetLBound((IntPtr)psa, 1, out int lBound);
        if (hr != 0) return null;

        hr = OleAut32.SafeArrayGetUBound((IntPtr)psa, 1, out int uBound);
        if (hr != 0) return null;

        int count = uBound - lBound + 1;
        if (count < 4) return null;  // 최소 x,y,w,h

        hr = OleAut32.SafeArrayAccessData((IntPtr)psa, out IntPtr ppvData);
        if (hr != 0) return null;

        try
        {
            double* data = (double*)ppvData;
            return ((int)data[0], (int)data[1], (int)data[2], (int)data[3]);
        }
        finally
        {
            OleAut32.SafeArrayUnaccessData((IntPtr)psa);
        }
    }

    // ================================================================
    // 요청 레코드
    // ================================================================

    private sealed record UiaRequest(IntPtr HwndFocus)
    {
        public TaskCompletionSource<(int x, int y, int w, int h)?> Completion { get; } = new();
    }
}
