using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// AC_SRC_OVER + AC_SRC_ALPHA premultiplied 모드의 <see cref="User32.UpdateLayeredWindow"/>
/// 호출 한 줄 wrapper. <see cref="LayeredOverlayBase"/> 의 두 호출 site
/// (UpdateOverlayDuringDrag / UpdateOverlay) 와 <see cref="LayeredCursorBase"/> 의 한 호출 site
/// 가 동일하게 들고 있던 ptDst/size/ptSrc/blend 5 객체 구성 + UpdateLayeredWindow 호출을
/// 합친다. premultiply 후처리는 엔진별 의미 차이 (overlay = AA 엣지 보존 / cursor = 외곽 잡티
/// 제거) 가 있어 본 helper 의 책임이 아니며, caller 가 사전 적용한다.
/// </summary>
internal static class LayeredWindowBlit
{
    /// <summary>
    /// <paramref name="memDC"/> 의 (0, 0) ~ (<paramref name="width"/>, <paramref name="height"/>)
    /// 영역을 <paramref name="hwnd"/> 의 (<paramref name="x"/>, <paramref name="y"/>) 부터
    /// premultiplied alpha 블렌딩으로 BLT 한다. <paramref name="alpha"/> 는 SourceConstantAlpha
    /// (0..255) — fade 보간 결과 적용용. 호출자는 사전에 ApplyPremultipliedAlpha 로 RGB 채널을
    /// alpha 와 곱해둔다.
    /// </summary>
    public static bool Blit(IntPtr hwnd, IntPtr memDC, int x, int y, int width, int height, byte alpha)
    {
        var ptDst = new POINT(x, y);
        var size = new SIZE(width, height);
        var ptSrc = new POINT(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32Constants.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = Win32Constants.AC_SRC_ALPHA
        };

        return User32.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref ptDst, ref size,
            memDC, ref ptSrc, 0, ref blend, Win32Constants.ULW_ALPHA);
    }
}
