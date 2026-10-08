using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TranscriptionOverlay.Services;

public sealed class TrayIconBadgeRenderer
{
    public Icon CreateIcon(Icon baseIcon, Color badgeColor)
    {
        using var canvas = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var renderedBaseIcon = new Icon(baseIcon, new Size(32, 32));
            graphics.DrawIcon(renderedBaseIcon, new Rectangle(0, 0, 32, 32));

            const int outerDiameter = 16;
            const int innerDiameter = 12;
            const int badgeLeft = 15;
            const int badgeTop = 15;

            using var outerBrush = new SolidBrush(Color.FromArgb(235, 248, 244, 236));
            using var innerBrush = new SolidBrush(badgeColor);
            using var badgeOutlinePen = new Pen(Color.FromArgb(190, 33, 27, 21), 1f);

            graphics.FillEllipse(outerBrush, badgeLeft, badgeTop, outerDiameter, outerDiameter);
            graphics.DrawEllipse(badgeOutlinePen, badgeLeft, badgeTop, outerDiameter - 1, outerDiameter - 1);
            graphics.FillEllipse(innerBrush, badgeLeft + 2, badgeTop + 2, innerDiameter, innerDiameter);
        }

        var iconHandle = canvas.GetHicon();
        try
        {
            using var handleIcon = Icon.FromHandle(iconHandle);
            return (Icon)handleIcon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
