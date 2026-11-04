using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.UI;
using KoEnVue.Core.Color;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 트레이 아이콘 실제 렌더 — 헤일로 안개 고리(2026-11-04) 로 바뀐 뒤에도 좌클릭 순환 4단계가
/// 픽셀로 구별되는지. <see cref="Tray.GetShapes"/> 검사(TrayLeftClickToggleTests)는 도형 선택만 보므로
/// 그림 자체를 여기서 고정한다.
/// </summary>
public class TrayIconPaintTests
{
    private const int BytesPerPixel = 4;

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void FourStages_RenderDistinctly(int side)
    {
        var (bgR, bgG, bgB) = ColorHelper.ColorRefToRgb(ColorHelper.HexToColorRef(DefaultConfig.DefaultHangulBg));
        byte[] both = Paint(side, IndicatorVisibility.Both);
        byte[] badgeOnly = Paint(side, IndicatorVisibility.BadgeOnly);
        byte[] haloOnly = Paint(side, IndicatorVisibility.CursorOnly);
        byte[] none = Paint(side, IndicatorVisibility.None);

        int mid = side / 2;
        int ringX = mid + (int)Math.Round(side * 0.34);
        // 모두 숨김: 배경 안 픽셀은 전부 배경색
        Assert.True(IsColor(none, side, mid, mid, bgR, bgG, bgB));
        Assert.True(IsColor(none, side, ringX, mid, bgR, bgG, bgB));
        // 헤일로만: 고리 위치는 밝고, 고리 안쪽(중심)은 그보다 뚜렷이 어둡다 — 작은 아이콘은 안개가 넓어(σ 0.10)
        // 중심까지 살짝 번지므로 「정확히 배경색」 대신 고리와의 밝기 차로 본다.
        Assert.True(Brightness(haloOnly, side, ringX, mid) > Brightness(none, side, ringX, mid) + 100, "고리가 보여야 함");
        Assert.True(Brightness(haloOnly, side, ringX, mid) > Brightness(haloOnly, side, mid, mid) + 100, "고리 안쪽이 비어 있어야 함");
        // 배지만: 중심은 배지(흰색), 고리 위치는 배경색
        Assert.True(Brightness(badgeOnly, side, mid, mid) > 3 * 250, "배지가 보여야 함");
        Assert.True(IsColor(badgeOnly, side, ringX, mid, bgR, bgG, bgB), "배지만 단계에 고리가 그려짐");
        // 둘 다: 배지 + 고리
        Assert.True(Brightness(both, side, mid, mid) > 3 * 250);
        Assert.True(Brightness(both, side, ringX, mid) > Brightness(none, side, ringX, mid) + 100);
        // 둥근 모서리 바깥(왼쪽 위 꼭짓점)은 투명
        Assert.Equal(0, both[3]);
        // 네 단계 모두 서로 다른 그림
        var all = new[] { both, badgeOnly, haloOnly, none };
        for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
                Assert.False(all[i].AsSpan().SequenceEqual(all[j]), $"단계 {i} 와 {j} 가 같은 그림");
    }

    private static byte[] Paint(int side, IndicatorVisibility visibility)
    {
        int n = side * side * BytesPerPixel;
        IntPtr buf = Marshal.AllocHGlobal(n);
        try
        {
            TrayIcon.PaintIcon(buf, side, side,
                ColorHelper.HexToColorRef(DefaultConfig.DefaultHangulFg),
                ColorHelper.HexToColorRef(DefaultConfig.DefaultHangulBg), visibility);
            var bytes = new byte[n];
            Marshal.Copy(buf, bytes, 0, n);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // DIB 는 bottom-up 이지만 검사 지점이 가운데 행이라 상하 반전과 무관하다.
    private static int Offset(int side, int x, int y) => (y * side + x) * BytesPerPixel;

    private static bool IsColor(byte[] px, int side, int x, int y, byte r, byte g, byte b)
    {
        int o = Offset(side, x, y);
        return px[o] == b && px[o + 1] == g && px[o + 2] == r;
    }

    private static int Brightness(byte[] px, int side, int x, int y)
    {
        int o = Offset(side, x, y);
        return px[o] + px[o + 1] + px[o + 2];
    }
}
