using System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace CycleArc.UI;

/// <summary>
/// Gives the native tray and widget menus the app's Dark or Light ground. Light keeps the
/// standard Windows menu look; Dark uses the popup's colors, with check marks and submenu
/// arrows drawn in the menu's text color so they stay visible.
/// </summary>
internal static class TrayMenuTheme
{
    private static readonly ToolStripRenderer Dark = new DarkRenderer();

    public static void Apply(ToolStripDropDown menu, bool dark)
    {
        if (dark)
        {
            if (!ReferenceEquals(menu.Renderer, Dark)) menu.Renderer = Dark;
            menu.BackColor = DarkColors.Ground;
        }
        else if (menu.RenderMode != ToolStripRenderMode.ManagerRenderMode)
        {
            menu.RenderMode = ToolStripRenderMode.ManagerRenderMode;
            menu.ResetBackColor();
        }
        foreach (ToolStripItem item in menu.Items)
        {
            if (dark) item.ForeColor = item.Enabled ? DarkColors.Text : DarkColors.Disabled;
            else item.ResetForeColor();
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent) Apply(parent.DropDown, dark);
        }
    }

    internal static class DarkColors
    {
        public static readonly DrawingColor Ground = DrawingColor.FromArgb(30, 30, 30);
        public static readonly DrawingColor Text = DrawingColor.FromArgb(238, 238, 238);
        public static readonly DrawingColor Disabled = DrawingColor.FromArgb(150, 150, 150);
        public static readonly DrawingColor Highlight = DrawingColor.FromArgb(55, 55, 55);
        public static readonly DrawingColor Line = DrawingColor.FromArgb(58, 58, 58);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override DrawingColor ToolStripDropDownBackground => DarkColors.Ground;
        public override DrawingColor ImageMarginGradientBegin => DarkColors.Ground;
        public override DrawingColor ImageMarginGradientMiddle => DarkColors.Ground;
        public override DrawingColor ImageMarginGradientEnd => DarkColors.Ground;
        public override DrawingColor MenuBorder => DarkColors.Line;
        public override DrawingColor MenuItemBorder => DarkColors.Highlight;
        public override DrawingColor MenuItemSelected => DarkColors.Highlight;
        public override DrawingColor MenuItemSelectedGradientBegin => DarkColors.Highlight;
        public override DrawingColor MenuItemSelectedGradientEnd => DarkColors.Highlight;
        public override DrawingColor MenuItemPressedGradientBegin => DarkColors.Highlight;
        public override DrawingColor MenuItemPressedGradientEnd => DarkColors.Highlight;
        public override DrawingColor SeparatorDark => DarkColors.Line;
        public override DrawingColor SeparatorLight => DarkColors.Line;
        public override DrawingColor CheckBackground => DarkColors.Ground;
        public override DrawingColor CheckSelectedBackground => DarkColors.Highlight;
        public override DrawingColor CheckPressedBackground => DarkColors.Highlight;
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColorTable()) => RoundedEdges = false;

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? DarkColors.Disabled : DarkColors.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The default check is a black bitmap; draw a light tick in the same box instead.
            var box = e.ImageRectangle;
            using var pen = new System.Drawing.Pen(e.Item.Enabled ? DarkColors.Text : DarkColors.Disabled, 1.6f);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawLines(pen, new[]
            {
                new System.Drawing.PointF(box.Left + box.Width * 0.2f, box.Top + box.Height * 0.52f),
                new System.Drawing.PointF(box.Left + box.Width * 0.42f, box.Top + box.Height * 0.74f),
                new System.Drawing.PointF(box.Left + box.Width * 0.8f, box.Top + box.Height * 0.28f)
            });
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkColors.Text : DarkColors.Disabled;
            base.OnRenderItemText(e);
        }
    }
}
