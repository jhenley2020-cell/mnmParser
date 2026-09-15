using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using MnmDamageParser.Core.Xp;

namespace MnmDamageParser.App;

/// <summary>Grabs the calibrated XP-bar rectangle off the screen and turns
/// it into a fill fraction (0..1). No game memory/files -- just pixels.
/// The region is tiny (~170x20), so a plain GetPixel loop is plenty fast
/// at the ~1.5s sample interval.</summary>
[SupportedOSPlatform("windows")]
internal static class ScreenXpReader
{
    /// <summary>The bar's sub-pixel fill fraction (0..1), or null if the
    /// capture failed. NaN if the calibration has too little colour contrast
    /// to trust (the caller shows "recalibrate"). Each read is the median of
    /// <see cref="XpBarConfig.SamplesPerRead"/> quick captures so a one-frame
    /// overlay (tooltip, floating text, particle) can't skew it.</summary>
    public static double? ReadFraction(XpBarConfig cfg)
    {
        if (!cfg.IsCalibrated) return null;

        var n = Math.Clamp(cfg.SamplesPerRead, 1, 9);
        var reads = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            var cols = Capture(cfg.Region);
            if (cols is null) continue;
            var f = FillMeter.FractionPrecise(cols, cfg.FillRgb, cfg.EmptyRgb, cfg.EdgeTrimPx);
            if (double.IsNaN(f)) return double.NaN; // contrast problem -- one read is enough to know
            reads.Add(f);
            if (n > 1 && i < n - 1) Thread.Sleep(35);
        }
        if (reads.Count == 0) return null;
        reads.Sort();
        return reads[reads.Count / 2];
    }

    /// <summary>Per-column (R,G,B), averaged down the region's height, or
    /// null if the capture failed. Used by the reader and the calibrator.</summary>
    public static (double R, double G, double B)[]? Capture(ScreenRect r)
    {
        if (r.Width < 1 || r.Height < 1) return null;
        try
        {
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);

            var w = bmp.Width;
            var h = bmp.Height;
            var cols = new (double, double, double)[w];
            for (var x = 0; x < w; x++)
            {
                double sr = 0, sg = 0, sb = 0;
                for (var y = 0; y < h; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    sr += c.R; sg += c.G; sb += c.B;
                }
                cols[x] = (sr / h, sg / h, sb / h);
            }
            return cols;
        }
        catch
        {
            return null;
        }
    }
}
