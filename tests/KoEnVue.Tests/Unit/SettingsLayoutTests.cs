using KoEnVue.App.UI.Dialogs;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// SettingsDialog.BuildLayout 의 DPI 스케일·파생 좌표 산술 회귀 박제 (IMP-2 분해).
/// BuildChildren 분해 시 가장 깨지기 쉬운 clamp/Max 파생식을 헤드리스로 자동 차단한다.
/// 다이얼로그 시각 배치(행 y 누적·실제 렌더)는 GDI 의존이라 수동 smoke 영역으로 남는다.
/// </summary>
public class SettingsLayoutTests
{
    // DialogShellContext 를 HWND 없이 구성 — BuildLayout 은 순수 산술이라 동작한다.
    // Pad 는 호출자(DialogShell)가 이미 DPI 스케일해 Metrics.Pad 로 주입하므로 여기서도
    // 스케일된 값(96 DPI 면 16, 150% 면 24)을 직접 넘긴다. NonClient* 는 0 으로 단순화.
    private static DialogShellContext MakeCtx(double dpiScale, int pad, int dlgWidth, int dlgHeight)
    {
        var metrics = new DialogShellMetrics(
            HMonitor: IntPtr.Zero,
            CursorPos: default,
            DpiScale: dpiScale,
            DpiY: 96,
            RawDpi: 96,
            NonClientH: 0,
            NonClientW: 0,
            Pad: pad,
            DlgWidth: dlgWidth);
        return new DialogShellContext
        {
            HwndOwner = IntPtr.Zero,
            HwndDialog = IntPtr.Zero,
            HFont = IntPtr.Zero,
            DlgHeight = dlgHeight,
            Metrics = metrics,
        };
    }

    // 표준 96 DPI 다이얼로그 (DlgWidth=600, DlgHeight=700, Pad=16).
    private static SettingsLayout StdLayout() => SettingsDialog.BuildLayout(MakeCtx(1.0, 16, 600, 700));

    [Fact]
    public void LabelX_EqualsContentPadInner()
    {
        Assert.Equal(12, StdLayout().LabelX);                 // ContentPadInner@96
    }

    [Fact]
    public void ControlX_IsContentPadInnerPlusLabelColPlusGap()
    {
        Assert.Equal(12 + 220 + 14, StdLayout().ControlX);    // ContentPadInner + LabelColW + ColGap
    }

    [Fact]
    public void ControlColW_UnclampedWhenViewportIsWide()
    {
        // inner = 568-24-24 = 520; Min(260, 520-220-14=286)=260 → clamp 미발동
        Assert.Equal(260, StdLayout().ControlColW);
    }

    [Fact]
    public void ControlColW_ClampedToInnerWidthWhenNarrow()
    {
        // DlgWidth=400 → vpW=368 → inner=320 → Min(260, 320-220-14=86)=86
        var m = SettingsDialog.BuildLayout(MakeCtx(1.0, 16, 400, 700));
        Assert.Equal(86, m.ControlColW);
    }

    [Fact]
    public void ControlColW_FloorIsOneWhenInnerTooSmall()
    {
        // DlgWidth=250 → vpW=218 → inner=170 → 170-220-14=-64 → Max(1, -64)=1
        var m = SettingsDialog.BuildLayout(MakeCtx(1.0, 16, 250, 700));
        Assert.Equal(1, m.ControlColW);
    }

    [Fact]
    public void SectionContentW_IsAtLeastInnerWidth()
    {
        // inner=520; labelCol+gap+control = 220+14+260=494 → Max(520, 494)=520
        Assert.Equal(520, StdLayout().SectionContentW);
    }

    [Fact]
    public void Viewport_GeometrySubtractsHeaderButtonAreaAndPads()
    {
        var m = StdLayout();
        Assert.Equal(16, m.VpX);                              // Pad
        Assert.Equal(16 + 20 + 12, m.VpY);                    // Pad + DescH + DescGap = 48
        Assert.Equal(600 - 16 * 2, m.VpW);                    // ClientW - Pad*2 = 568
        Assert.Equal(700 - 48 - 50 - 16, m.VpH);              // ClientH - VpY - BtnAreaH - Pad = 586
    }

    [Fact]
    public void At150Dpi_ScalesLogicalMetricsButNotInjectedPad()
    {
        // DpiScale=1.5, Pad 는 호출자가 스케일한 24(=16*1.5) 주입.
        var m = SettingsDialog.BuildLayout(MakeCtx(1.5, 24, 900, 1050));
        Assert.Equal(36, m.RowH);                             // 24 * 1.5
        Assert.Equal(330, m.LabelColW);                       // 220 * 1.5
        Assert.Equal(24, m.Pad);                              // 주입값 그대로 (재스케일 안 함)
    }
}
