using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// SettingsDialog 레이아웃 메트릭 — 96 DPI 기준 logical 상수를 DPI 스케일한 값과,
/// 거기서 1회 계산한 파생 좌표(뷰포트 지오메트리·열 위치·clamp 된 컨트롤 폭)를 묶는다.
/// BuildChildren 분해(IMP-2)의 결합점을 명시적 struct 로 박제해 픽셀 회귀를 방어하고,
/// SettingsLayoutTests 가 파생식(clamp/Max)을 헤드리스로 자동 검증할 수 있게 한다.
/// </summary>
internal readonly record struct SettingsLayout(
    int Pad, int DescH, int DescGap, int ContentPadInner,
    int LabelColW, int ColGap, int ControlColW, int RowH, int RowGap,
    int SectionHeadH, int SectionHeadGap, int SectionTopGap, int SectionSepPostGap,
    int BtnW, int BtnH, int BtnAreaH, int ComboDropExtra, int ViewportScrollReserve,
    int ClientW, int ClientH,
    int VpX, int VpY, int VpW, int VpH,
    int LabelX, int ControlX, int SectionContentW);

internal static partial class SettingsDialog
{
    /// <summary>
    /// 레이아웃 상수를 DPI 스케일하고 파생 좌표를 1회 계산해 <see cref="SettingsLayout"/> 으로 묶는다.
    /// 순수 산술(HWND 무의존) — BuildChildren 의 결합점을 시그니처로 박제한다. test-visible(internal).
    /// </summary>
    internal static SettingsLayout BuildLayout(DialogShellContext ctx)
    {
        int pad = ctx.Pad;
        int descH = ctx.Scale(DescH);
        int descGap = ctx.Scale(DescGap);
        int contentPadInner = ctx.Scale(ContentPadInner);
        int labelColW = ctx.Scale(LabelColW);
        int colGap = ctx.Scale(ColGap);
        int controlColW = ctx.Scale(ControlColW);
        int rowH = ctx.Scale(RowH);
        int rowGap = ctx.Scale(RowGap);
        int sectionHeadH = ctx.Scale(SectionHeadH);
        int sectionHeadGap = ctx.Scale(SectionHeadGap);
        int sectionTopGap = ctx.Scale(SectionTopGap);
        int sectionSepPostGap = ctx.Scale(SectionSepPostGap);
        int btnW = ctx.Scale(BtnW);
        int btnH = ctx.Scale(BtnH);
        int btnAreaH = ctx.Scale(BtnAreaH);
        int comboDropExtra = ctx.Scale(ComboDropExtra);
        int viewportScrollReserve = ctx.Scale(ViewportScrollReserve);

        int clientW = ctx.ClientW;
        int clientH = ctx.ClientH;

        // 뷰포트 지오메트리 (원 BuildChildren: vpX/vpY/vpW/vpH)
        int vpX = pad;
        int vpY = pad + descH + descGap;
        int vpW = clientW - pad * 2;
        int vpH = clientH - vpY - btnAreaH - pad;

        // 행 배치 파생 (원 BuildChildren: labelX/controlX/controlColW clamp/sectionContentW)
        int labelX = contentPadInner;
        int controlX = contentPadInner + labelColW + colGap;
        int innerContentW = vpW - contentPadInner * 2 - viewportScrollReserve;
        controlColW = Math.Max(1, Math.Min(controlColW, innerContentW - labelColW - colGap));
        int sectionContentW = Math.Max(innerContentW, labelColW + colGap + controlColW);

        return new SettingsLayout(
            Pad: pad, DescH: descH, DescGap: descGap, ContentPadInner: contentPadInner,
            LabelColW: labelColW, ColGap: colGap, ControlColW: controlColW, RowH: rowH, RowGap: rowGap,
            SectionHeadH: sectionHeadH, SectionHeadGap: sectionHeadGap, SectionTopGap: sectionTopGap,
            SectionSepPostGap: sectionSepPostGap,
            BtnW: btnW, BtnH: btnH, BtnAreaH: btnAreaH, ComboDropExtra: comboDropExtra,
            ViewportScrollReserve: viewportScrollReserve,
            ClientW: clientW, ClientH: clientH,
            VpX: vpX, VpY: vpY, VpW: vpW, VpH: vpH,
            LabelX: labelX, ControlX: controlX, SectionContentW: sectionContentW);
    }
}
