namespace MnmDamageParser.Core.Xp;

/// <summary>
/// Works out the "filled" and "empty" reference colours for a progress bar
/// straight from a capture of it, instead of assuming the fill is in the
/// left 10% and empty in the right 10% (which produces garbage when the bar
/// happens to be nearly full or nearly empty at calibration time).
///
/// Two-means clustering over the per-column colours: the bar only really has
/// two colours, so the columns fall into two groups. The group whose
/// columns sit further to the LEFT is the fill (bars fill left-to-right),
/// regardless of which one is brighter.
/// </summary>
public static class BarCalibration
{
    public sealed record Result(int[] FillRgb, int[] EmptyRgb, double ContrastSq, double Fraction)
    {
        /// <summary>True when the two reference colours are far enough apart
        /// for a reliable reading.</summary>
        public bool Usable => ContrastSq >= FillMeter.MinContrastSq;
    }

    public static Result? Detect(IReadOnlyList<(double R, double G, double B)> columns, int edgeTrimPx = 2)
    {
        var n = columns.Count;
        if (n < 8) return null;
        int lo = 0, hi = n;
        if (edgeTrimPx > 0 && n > 2 * edgeTrimPx + 4) { lo = edgeTrimPx; hi = n - edgeTrimPx; }
        var count = hi - lo;

        // Seed the two centroids with the darkest and brightest columns.
        double Lum((double R, double G, double B) c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        (double R, double G, double B) c0 = columns[lo], c1 = columns[lo];
        double minL = double.MaxValue, maxL = double.MinValue;
        for (var i = lo; i < hi; i++)
        {
            var l = Lum(columns[i]);
            if (l < minL) { minL = l; c0 = columns[i]; }
            if (l > maxL) { maxL = l; c1 = columns[i]; }
        }

        var assign = new int[count];
        for (var iter = 0; iter < 15; iter++)
        {
            double s0r = 0, s0g = 0, s0b = 0, s1r = 0, s1g = 0, s1b = 0;
            int n0 = 0, n1 = 0;
            for (var i = 0; i < count; i++)
            {
                var c = columns[lo + i];
                var k = Dist2(c, c0) <= Dist2(c, c1) ? 0 : 1;
                assign[i] = k;
                if (k == 0) { s0r += c.R; s0g += c.G; s0b += c.B; n0++; }
                else { s1r += c.R; s1g += c.G; s1b += c.B; n1++; }
            }
            if (n0 == 0 || n1 == 0) break; // everything collapsed to one colour
            c0 = (s0r / n0, s0g / n0, s0b / n0);
            c1 = (s1r / n1, s1g / n1, s1b / n1);
        }

        // Which cluster is the fill? The one sitting further left.
        double sx0 = 0, sx1 = 0;
        int cnt0 = 0, cnt1 = 0;
        for (var i = 0; i < count; i++)
        {
            if (assign[i] == 0) { sx0 += i; cnt0++; }
            else { sx1 += i; cnt1++; }
        }
        if (cnt0 == 0 || cnt1 == 0)
            return new Result(ToRgb(c0), ToRgb(c1), Dist2(c0, c1), double.NaN);

        var fillIsC0 = sx0 / cnt0 <= sx1 / cnt1;
        var fill = fillIsC0 ? c0 : c1;
        var empty = fillIsC0 ? c1 : c0;

        var fillRgb = ToRgb(fill);
        var emptyRgb = ToRgb(empty);
        var frac = FillMeter.FractionPrecise(columns, fillRgb, emptyRgb, edgeTrimPx);
        return new Result(fillRgb, emptyRgb, Dist2(fill, empty), frac);
    }

    private static double Dist2((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private static int[] ToRgb((double R, double G, double B) c) => new[]
    {
        (int)Math.Round(Math.Clamp(c.R, 0, 255)),
        (int)Math.Round(Math.Clamp(c.G, 0, 255)),
        (int)Math.Round(Math.Clamp(c.B, 0, 255)),
    };
}
