namespace KoEnVue.App.UI;

/// <summary>
/// 커서 이동 중 시인성 저하(PR-29)의 순수 상태 전이 — Win32/엔진 무의존.
/// enter/exit 이 같은 이동량(δ)을 쓰고 exit 만 settle 틱 히스테리시스를 둔다
/// (PR-28 B안 9Hz 리미트 사이클 회피).
/// </summary>
internal static class CursorMotionDim
{
    /// <summary>창 SourceConstantAlpha Full — 딤은 셰이더 원별 알파로만.</summary>
    public const byte FullAlpha = 255;

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

    /// <summary>팝 중·비활성·비딤 → 0. 딤이면 softness(두께 보간) 그대로.</summary>
    public static double EffectiveSoftness(bool dimActive, bool enabled, bool popActive, double softness)
    {
        if (popActive || !enabled || !dimActive)
            return 0.0;
        if (softness < 0.0) return 0.0;
        if (softness > 1.0) return 1.0;
        return softness;
    }

    /// <summary>
    /// 원별 픽셀 알파. 딤 OFF/팝 → (1,1,1).
    /// 딤 ON → 세 원에 거의 균일(안개) — motionAlpha×배수.
    /// </summary>
    public static (double Inner, double Middle, double Outer) RingAlphas(
        bool dimActive, bool enabled, bool popActive, double motionAlpha,
        double innerFactor, double middleFactor, double outerFactor,
        double minAlpha = 0.04)
    {
        if (popActive || !enabled || !dimActive)
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
