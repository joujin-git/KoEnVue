using KoEnVue.App.Models;

namespace KoEnVue.App.Detector;

/// <summary>
/// 감지 루프가 Program 정적 상태에 직접 의존하지 않도록 주입하는 호스트 콜백 묶음.
/// </summary>
internal sealed class DetectionHost
{
    public required Func<AppConfig> GetConfig { get; init; }
    public required Func<IntPtr> GetHwndMain { get; init; }
    public required Func<IntPtr> GetHwndOverlay { get; init; }
    public required Func<IntPtr> GetHwndCursorOverlay { get; init; }
    public required Func<bool> IsIndicatorVisible { get; init; }
    public required Func<bool> IsSessionLocked { get; init; }
    public required Func<bool> IsStopping { get; init; }
}
