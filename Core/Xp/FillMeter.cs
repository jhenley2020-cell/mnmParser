namespace MnmDamageParser.Core.Xp;

/// <summary>
/// Estimates how full a left-to-right progress bar is (0..1). Each column
/// of the bar is classified "filled" or "empty" by whichever reference
/// colour it is closer to; the fraction of filled columns is the fill
/// level. Tolerates gradients and anti-aliasing reasonably well.
///
/// Direct port of the Python reader's <c>fill_fraction</c>. The caller
/// (the screen-capture layer) supplies each column already averaged over
/// the region's height, so this stays free of any bitmap type.
/// </summary>
public static class FillMeter
{
    /// <summary>Below this squared distance between the fill and empty
    /// reference colours the calibration carries essentially no signal --
    /// <see cref="FractionPrecise"/> returns NaN so the caller can say
    /// "recalibrate" instead of reporting noise as XP.</summary>
    public const double MinContrastSq = 12.0 * 12.0;

    /// <summary>
    /// Sub-pixel fill level (0..1). Instead of counting each column as fully
    /// filled or fully empty, every column gets a fractional "fill-ness" =
    /// its colour projected onto the empty→fill axis and clamped to [0,1];
    /// the mean of those is the fill level. The one partially-lit column at
    /// the fill edge then contributes its true fraction, so the resolution
    /// is a small fraction of a pixel rather than ±1 pixel (≈±0.5% of a
    /// level on a ~190px bar). Returns NaN if the calibration has almost no
    /// colour contrast (see <see cref="MinContrastSq"/>).
    /// </summary>
    public static double FractionPrecise(
        IReadOnlyList<(double R, double G, double B)> columns,
        IReadOnlyList<int> fillRgb, IReadOnlyList<int> emptyRgb, int edgeTrimPx = 2)
    {
        if (columns.Count < 4 || fillRgb.Count < 3 || emptyRgb.Count < 3) return 0.0;

        var (lo, hi) = TrimBounds(columns.Count, edgeTrimPx);

        double er = emptyRgb[0], eg = emptyRgb[1], eb = emptyRgb[2];
        double ax = fillRgb[0] - er, ay = fillRgb[1] - eg, az = fillRgb[2] - eb;
        var axisLenSq = ax * ax + ay * ay + az * az;
        if (axisLenSq < MinContrastSq) return double.NaN;

        double sum = 0;
        var n = hi - lo;
        for (var i = lo; i < hi; i++)
        {
            var (r, g, b) = columns[i];
            var t = ((r - er) * ax + (g - eg) * ay + (b - eb) * az) / axisLenSq;
            sum += t < 0 ? 0 : t > 1 ? 1 : t;
        }
        return n > 0 ? sum / n : 0.0;
    }

    private static (int Lo, int Hi) TrimBounds(int count, int edgeTrimPx)
    {
        if (edgeTrimPx > 0 && count > 2 * edgeTrimPx + 2)
            return (edgeTrimPx, count - edgeTrimPx);
        return (0, count);
    }

    /// <param name="columns">One (R,G,B) triple per pixel column of the
    /// bar, left to right, each averaged down the column. Values 0..255.</param>
    public static double Fraction(
        IReadOnlyList<(double R, double G, double B)> columns,
        IReadOnlyList<int> fillRgb, IReadOnlyList<int> emptyRgb, int edgeTrimPx = 2)
    {
        if (columns.Count < 4 || fillRgb.Count < 3 || emptyRgb.Count < 3) return 0.0;

        var lo = 0;
        var hi = columns.Count;
        if (edgeTrimPx > 0 && columns.Count > 2 * edgeTrimPx + 2)
        {
            lo = edgeTrimPx;
            hi = columns.Count - edgeTrimPx;
        }

        double fr = fillRgb[0], fg = fillRgb[1], fb = fillRgb[2];
        double er = emptyRgb[0], eg = emptyRgb[1], eb = emptyRgb[2];

        var filled = 0;
        var total = hi - lo;
        for (var i = lo; i < hi; i++)
        {
            var (r, g, b) = columns[i];
            var dFill = Dist(r, g, b, fr, fg, fb);
            var dEmpty = Dist(r, g, b, er, eg, eb);
            if (dFill < dEmpty) filled++;
        }
        return total > 0 ? (double)filled / total : 0.0;
    }

    private static double Dist(double r, double g, double b, double r2, double g2, double b2)
    {
        var dr = r - r2;
        var dg = g - g2;
        var db = b - b2;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }
}
