using System.Diagnostics;
using KoEnVue.App.Localization;
using KoEnVue.App.Models;
using KoEnVue.Core.Logging;

namespace KoEnVue.App.Config;

/// <summary>
/// <c>indicator_positions</c> 정리 작업의 비-UI 비즈니스 로직.
/// Tray.cs 에서 분리(PR-04) — 다이얼로그 렌더링(<see cref="UI.Dialogs.CleanupDialog"/>)은 호출자가
/// 담당하고, 본 서비스는 (1) 정리 대상 키 목록 + 실행 중 접미사 합성, (2) 사용자 선택 결과를
/// 두 dict (<c>IndicatorPositions</c> / <c>IndicatorPositionsRelative</c>) 에서 제거한 새 AppConfig 생성
/// 까지만 책임진다.
/// </summary>
internal static class PositionCleanupService
{
    /// <summary>
    /// 정리 대상 키(양쪽 dict 의 합집합) 의 표시 라벨 + 원본 키 매핑을 반환.
    /// 실행 중인 프로세스는 라벨에 <see cref="I18n.RunningSuffix"/> 접미사가 붙는다.
    /// 두 리스트는 같은 인덱스가 동일 항목을 가리키므로 사용자 선택 mapping 에 그대로 활용.
    /// 대상이 0개면 두 리스트가 모두 빈 상태로 반환되며 호출자는 "비어있음" 안내를 띄운다.
    /// </summary>
    internal static (List<string> DisplayItems, List<string> OriginalNames) Compute(AppConfig config)
    {
        var allKeys = new HashSet<string>(config.IndicatorPositions.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in config.IndicatorPositionsRelative.Keys)
            allKeys.Add(k);

        var displayItems = new List<string>(allKeys.Count);
        var originalNames = new List<string>(allKeys.Count);

        if (allKeys.Count == 0)
            return (displayItems, originalNames);

        var running = CollectRunningProcessNames();
        string suffix = I18n.RunningSuffix;
        foreach (string name in allKeys)
        {
            originalNames.Add(name);
            displayItems.Add(running.Contains(name) ? name + suffix : name);
        }
        return (displayItems, originalNames);
    }

    /// <summary>
    /// 사용자가 선택한 표시 라벨 집합을 원본 키로 환산해 두 dict 에서 제거한 새 AppConfig 를 반환.
    /// displayItems / originalNames 는 <see cref="Compute"/> 가 만든 짝이어야 한다.
    /// </summary>
    internal static AppConfig RemoveSelected(
        AppConfig config,
        IReadOnlyList<string> displayItems,
        IReadOnlyList<string> originalNames,
        IReadOnlyCollection<string> selectedDisplay)
    {
        var selectedOriginal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < displayItems.Count; i++)
        {
            if (selectedDisplay.Contains(displayItems[i]))
                selectedOriginal.Add(originalNames[i]);
        }

        var cleanedFixed = new Dictionary<string, int[]>(config.IndicatorPositions);
        var cleanedRelative = new Dictionary<string, int[]>(config.IndicatorPositionsRelative);
        foreach (string name in selectedOriginal)
        {
            cleanedFixed.Remove(name);
            cleanedRelative.Remove(name);
        }

        Logger.Info($"Cleaned {selectedOriginal.Count} position(s): {string.Join(", ", selectedOriginal)}");
        return config with
        {
            IndicatorPositions = cleanedFixed,
            IndicatorPositionsRelative = cleanedRelative,
        };
    }

    private static HashSet<string> CollectRunningProcessNames()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try { running.Add(proc.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException
                                             or System.ComponentModel.Win32Exception)
                {
                    Logger.Debug($"PositionCleanup: failed to read process name: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"PositionCleanup: Process.GetProcesses enumeration failed: {ex.Message}");
        }
        return running;
    }
}
