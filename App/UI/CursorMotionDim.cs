using KoEnVue.App.Config;
using KoEnVue.App.Models;

namespace KoEnVue.App.UI;

/// <summary>
/// 커서 표시 방식(PR-29/30/31)의 순수 상태 전이 — Win32/엔진 무의존.
/// Motion 모드는 enter/exit 동일 δ + exit settle 히스테리시스 (PR-28 B안 회피).
/// Soft/Sharp 는 이동 무관 고정. 딤 전용 δ 임계는 호출측에서 분류.
/// </summary>
internal static class CursorMotionDim
{
    /// <summary>창 SourceConstantAlpha Full — 딤은 셰이더 원별 알파로만.</summary>
    public const byte FullAlpha = 255;

    /// <summary>
    /// 표시 모드에 따라 딤 활성 여부. Soft=항상 ON, Sharp=항상 OFF,
    /// Motion=이동 즉시 ON / 연속 정지 settle 후 OFF.
    /// </summary>
    public static bool AdvanceForMode(
        CursorDisplayMode mode,
        ref int stillPolls,
        bool moving,
        bool wasDimActive,
        int settlePolls)
    {
        switch (mode)
        {
            case CursorDisplayMode.Sharp:
                stillPolls = 0;
                return false;
            case CursorDisplayMode.Motion:
                return AdvanceDimActive(ref stillPolls, moving, wasDimActive, settlePolls);
            case CursorDisplayMode.Soft:
            default:
                stillPolls = 0;
                return true;
        }
    }

    /// <summary>
    /// 이동 중이면 즉시 딤. 정지면 <paramref name="stillPolls"/> 를 누적해
    /// <paramref name="settlePolls"/> 도달 시에만 딤 해제. 그 전에는 딤 유지.
    /// </summary>
    public static bool AdvanceDimActive(ref int stillPolls, bool moving, bool wasDimActive, int settlePolls)
    {
        if (moving)
        {
            stillPolls = 0;
            return true;
        }

        if (!wasDimActive)
        {
            stillPolls = 0;
            return false;
        }

        stillPolls++;
        if (stillPolls >= settlePolls)
        {
            stillPolls = 0;
            return false;
        }

        return true;
    }

    /// <summary>비딤 → 0. 딤이면 softness 그대로 (IME 팝 중에도 Soft/딤 안개 유지).</summary>
    public static double EffectiveSoftness(bool dimActive, double softness)
    {
        if (!dimActive)
            return 0.0;
        if (softness < 0.0) return 0.0;
        if (softness > 1.0) return 1.0;
        return softness;
    }

    /// <summary>
    /// 원별 픽셀 알파. 비딤 → (1,1,1).
    /// 딤 ON → 세 원에 거의 균일(안개) — motionAlpha×배수. 팝과 무관.
    /// </summary>
    public static (double Inner, double Middle, double Outer) RingAlphas(
        bool dimActive, double motionAlpha,
        double innerFactor, double middleFactor, double outerFactor,
        double minAlpha = DefaultConfig.MinCursorMotionAlpha)
    {
        if (!dimActive)
            return (1.0, 1.0, 1.0);

        double a = motionAlpha;
        if (a < minAlpha) a = minAlpha;
        if (a > 1.0) a = 1.0;
        return (Clamp01(a * innerFactor), Clamp01(a * middleFactor), Clamp01(a * outerFactor));
    }

    private static double Clamp01(double v)
    {
        if (v <= 0.0) return 0.0;
        if (v >= 1.0) return 1.0;
        return v;
    }
}
