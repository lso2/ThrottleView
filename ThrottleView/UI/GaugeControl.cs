using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ThrottleView.UI
{
    /// <summary>
    /// The headline dial: a 240-degree speedometer that fills in proportion to delivered performance
    /// and is colored by health (green near peak, red when badly throttled). The big number in the
    /// middle is the live delivered-performance percentage measured by the probe.
    /// </summary>
    public sealed class GaugeControl : FrameworkElement
    {
        private double _value = 0;     // 0..100 throttle level (0 = not throttled, needle at rest)
        private Color _color = UiFormat.Ok;
        private string _big = "--";
        private string _sub = "";

        private const double StartDeg = 150;
        private const double SweepDeg = 240;

        private static readonly Typeface TfSemi = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface TfNormal = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public void Set(double valuePct, Color color, string big, string sub)
        {
            _value = Math.Max(0, Math.Min(100, valuePct));
            _color = color; _big = big; _sub = sub;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;

            var center = new Point(w / 2, h * 0.60);
            double radius = Math.Min(w * 0.5, h * 0.66) - 12;
            if (radius < 8) return;
            double ring = Math.Max(6, radius * 0.16);

            // Throttle zones, like a speedometer: green when mild, through amber to red when severe.
            // The arc is always shown; the needle, not the fill, indicates the level.
            DrawZone(dc, center, radius, ring, 0.00, 0.40, UiFormat.Ok);
            DrawZone(dc, center, radius, ring, 0.40, 0.70, UiFormat.Warn);
            DrawZone(dc, center, radius, ring, 0.70, 1.00, UiFormat.Danger);

            // Tick marks inside the dial, longer at 0 / 50 / 100, for a real-instrument feel.
            double tickTop = radius - ring * 0.62;
            for (int t = 0; t <= 10; t++)
            {
                double ta = StartDeg + SweepDeg * (t / 10.0);
                bool major = (t % 5 == 0);
                double len = major ? ring * 0.55 : ring * 0.30;
                var tickPen = new Pen(UiFormat.Frozen(UiFormat.WithA(Colors.White, major ? (byte)115 : (byte)55)),
                                      major ? Math.Max(1.5, ring * 0.10) : Math.Max(1, ring * 0.06));
                dc.DrawLine(tickPen, AnglePoint(center, tickTop, ta), AnglePoint(center, tickTop - len, ta));
            }

            // Needle from the centre out to the rim. Its inner half is masked by the centre cap below,
            // so it reads as a pointer emerging from under the hub and never crosses the number.
            double frac = _value / 100.0;
            double needleDeg = StartDeg + SweepDeg * frac;
            Point tip = AnglePoint(center, radius - ring * 0.2, needleDeg);
            var needlePen = new Pen(UiFormat.Frozen(UiFormat.WithA(Colors.White, 225)), Math.Max(2, ring * 0.18)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(needlePen, center, tip);

            // Centre cap: a disc in the card colour that hides the needle's inner half and backs the
            // number, with a faint ring so it still reads as an instrument hub.
            double capR = radius * 0.60;
            dc.DrawEllipse(UiFormat.Frozen(UiFormat.Panel), new Pen(UiFormat.Frozen(UiFormat.WithA(Colors.White, 28)), 1), center, capR, capR);

            double dpi = Dpi();
            var big = Text(_big, radius * 0.40, FontWeights.SemiBold, UiFormat.Frozen(_color), dpi);
            double bigY = string.IsNullOrEmpty(_sub) ? center.Y - big.Height * 0.5 : center.Y - big.Height * 0.74;
            dc.DrawText(big, new Point(center.X - big.Width / 2, bigY));
            if (!string.IsNullOrEmpty(_sub))
            {
                var sub = Text(_sub, radius * 0.16, FontWeights.Normal, UiFormat.Frozen(UiFormat.TextSecondary), dpi);
                dc.DrawText(sub, new Point(center.X - sub.Width / 2, center.Y + capR * 0.38));
            }
        }

        private void DrawZone(DrawingContext dc, Point c, double r, double ring, double f0, double f1, Color color)
        {
            var pen = new Pen(UiFormat.Frozen(color), ring) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Arc(c, r, StartDeg + SweepDeg * f0, StartDeg + SweepDeg * f1));
        }

        private static Geometry Arc(Point c, double r, double startDeg, double endDeg)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(AnglePoint(c, r, startDeg), false, false);
                bool large = Math.Abs(endDeg - startDeg) > 180;
                ctx.ArcTo(AnglePoint(c, r, endDeg), new Size(r, r), 0, large, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        private static Point AnglePoint(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        private FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi)
        {
            var tf = weight == FontWeights.SemiBold ? TfSemi : TfNormal;
            return new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                tf, Math.Max(8, size), brush, dpi);
        }

        private double Dpi()
        {
            try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { return 1.0; }
        }
    }
}
