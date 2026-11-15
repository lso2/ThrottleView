using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ThrottleView.UI
{
    /// <summary>One line on the graph.</summary>
    public sealed class Series
    {
        public string Name;
        public Color Color;
        public float[] Values;
        public bool Fill;
    }

    /// <summary>
    /// A right-anchored multi-series line graph drawn directly for speed and full control of the
    /// look. New data scrolls in from the right like Task Manager, on a 0..MaxY axis (percent, and
    /// degrees Celsius for the temperature line, which share the same 0..100 range). Render resources
    /// are cached because this redraws once a second while the window is open.
    /// </summary>
    public sealed class LiveGraph : FrameworkElement
    {
        private static readonly Typeface Tf = new Typeface("Segoe UI");
        private static readonly Pen GridPen = FrozenPen(UiFormat.WithA(UiFormat.Grid, 150), 1);
        private static readonly Brush LabelBrush = UiFormat.Frozen(UiFormat.TextSecondary);

        private List<Series> _series = new List<Series>();
        private double _maxY = 100;
        private int _window = 90;

        // Cached y-axis labels; rebuilt only when the scale or DPI changes.
        private FormattedText[] _yLabels;
        private double _yLabelsMax = -1, _yLabelsDpi = -1;

        public void SetData(List<Series> series, double maxY, int windowPoints)
        {
            _series = series ?? new List<Series>();
            _maxY = maxY <= 0 ? 100 : maxY;
            _window = Math.Max(10, windowPoints);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 8 || h <= 8) return;

            double padL = 34, padR = 8, padT = 8, padB = 18;
            double plotL = padL, plotR = w - padR, plotT = padT, plotB = h - padB;
            double plotW = plotR - plotL, plotH = plotB - plotT;
            if (plotW <= 4 || plotH <= 4) return;

            double dpi = Dpi();
            EnsureLabels(dpi);

            for (int i = 0; i <= 4; i++)
            {
                double y = plotB - (i / 4.0) * plotH;
                dc.DrawLine(GridPen, new Point(plotL, y), new Point(plotR, y));
                var t = _yLabels[i];
                dc.DrawText(t, new Point(plotL - t.Width - 6, y - t.Height / 2));
            }

            double step = plotW / (_window - 1);

            foreach (var s in _series)
            {
                if (s == null || s.Values == null || s.Values.Length < 2 || !s.Fill) continue;
                var fill = BuildFill(s, plotL, plotR, plotB, plotT, plotH, step);
                if (fill != null) dc.DrawGeometry(UiFormat.Frozen(UiFormat.WithA(s.Color, 36)), null, fill);
            }

            foreach (var s in _series)
            {
                if (s == null || s.Values == null || s.Values.Length < 2) continue;
                var pen = new Pen(UiFormat.Frozen(s.Color), 2) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                Geometry g = BuildLine(s, plotR, plotB, plotT, plotH, step);
                if (g != null) dc.DrawGeometry(null, pen, g);
            }
        }

        private void EnsureLabels(double dpi)
        {
            if (_yLabels != null && _yLabelsMax == _maxY && _yLabelsDpi == dpi) return;
            _yLabels = new FormattedText[5];
            for (int i = 0; i <= 4; i++)
            {
                int val = (int)(_maxY * (i / 4.0));
                _yLabels[i] = new FormattedText(val.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Tf, 10.5, LabelBrush, dpi);
            }
            _yLabelsMax = _maxY; _yLabelsDpi = dpi;
        }

        private Point Map(int i, int n, float v, double plotR, double plotB, double plotT, double plotH, double step)
        {
            double x = plotR - (n - 1 - i) * step;
            double frac = v / _maxY; if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            double y = plotB - frac * plotH;
            return new Point(x, y);
        }

        private Geometry BuildLine(Series s, double plotR, double plotB, double plotT, double plotH, double step)
        {
            int n = s.Values.Length;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Map(0, n, s.Values[0], plotR, plotB, plotT, plotH, step), false, false);
                var pts = new List<Point>(n - 1);
                for (int i = 1; i < n; i++) pts.Add(Map(i, n, s.Values[i], plotR, plotB, plotT, plotH, step));
                ctx.PolyLineTo(pts, true, false);
            }
            g.Freeze();
            return g;
        }

        private Geometry BuildFill(Series s, double plotL, double plotR, double plotB, double plotT, double plotH, double step)
        {
            int n = s.Values.Length;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                Point first = Map(0, n, s.Values[0], plotR, plotB, plotT, plotH, step);
                ctx.BeginFigure(new Point(first.X, plotB), true, true);
                ctx.LineTo(first, true, false);
                var pts = new List<Point>(n - 1);
                for (int i = 1; i < n; i++) pts.Add(Map(i, n, s.Values[i], plotR, plotB, plotT, plotH, step));
                ctx.PolyLineTo(pts, true, false);
                ctx.LineTo(new Point(pts.Count > 0 ? pts[pts.Count - 1].X : first.X, plotB), true, false);
            }
            g.Freeze();
            return g;
        }

        private static Pen FrozenPen(Color c, double thickness)
        {
            var p = new Pen(UiFormat.Frozen(c), thickness);
            p.Freeze();
            return p;
        }

        private double Dpi() { try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { return 1.0; } }
    }
}
