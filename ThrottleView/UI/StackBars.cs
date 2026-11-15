using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ThrottleView.UI
{
    /// <summary>One bar: the fraction of the bucket's time spent throttled, split by cause.</summary>
    public sealed class HistoryBucket
    {
        public float Cpu;     // fraction of covered time, 0..1
        public float Ram;
        public float Disk;
        public string Label;
        public bool HasData;
        public float Total { get { return Cpu + Ram + Disk; } }
    }

    /// <summary>
    /// Stacked bar chart of "percent of time throttled" per time bucket, colored by cause. The
    /// backbone of the history view, so the eye lands on when throttling clustered and what drove it.
    /// </summary>
    public sealed class StackBars : FrameworkElement
    {
        private List<HistoryBucket> _buckets = new List<HistoryBucket>();

        public void SetData(List<HistoryBucket> buckets) { _buckets = buckets ?? new List<HistoryBucket>(); InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 8 || h <= 8) return;

            double padL = 34, padR = 8, padT = 8, padB = 20;
            double plotL = padL, plotR = w - padR, plotT = padT, plotB = h - padB;
            double plotW = plotR - plotL, plotH = plotB - plotT;
            if (plotW <= 4 || plotH <= 4) return;
            double dpi = Dpi();

            var gridPen = new Pen(UiFormat.Frozen(UiFormat.WithA(UiFormat.Grid, 150)), 1); gridPen.Freeze();
            var labelBrush = UiFormat.Frozen(UiFormat.TextSecondary);
            for (int p = 0; p <= 100; p += 25)
            {
                double y = plotB - (p / 100.0) * plotH;
                dc.DrawLine(gridPen, new Point(plotL, y), new Point(plotR, y));
                var t = Label(p + "%", 10.5, labelBrush, dpi);
                dc.DrawText(t, new Point(plotL - t.Width - 6, y - t.Height / 2));
            }

            int n = _buckets.Count;
            if (n == 0) return;
            double slot = plotW / n;
            double barW = Math.Max(2, Math.Min(26, slot * 0.66));
            int labelEvery = Math.Max(1, (int)Math.Ceiling(n / (plotW / 64.0)));

            var cpuB = UiFormat.Frozen(UiFormat.Cpu);
            var ramB = UiFormat.Frozen(UiFormat.Ram);
            var diskB = UiFormat.Frozen(UiFormat.Disk);
            var emptyB = UiFormat.Frozen(UiFormat.WithA(UiFormat.Track, 120));

            for (int i = 0; i < n; i++)
            {
                var b = _buckets[i];
                double cx = plotL + slot * (i + 0.5);
                double left = cx - barW / 2;
                if (!b.HasData)
                {
                    dc.DrawRectangle(emptyB, null, new Rect(left, plotB - 2, barW, 2));
                }
                else
                {
                    double yBottom = plotB;
                    yBottom = StackSeg(dc, cpuB, left, yBottom, barW, b.Cpu, plotH);
                    yBottom = StackSeg(dc, ramB, left, yBottom, barW, b.Ram, plotH);
                    yBottom = StackSeg(dc, diskB, left, yBottom, barW, b.Disk, plotH);
                }
                if (i % labelEvery == 0 && !string.IsNullOrEmpty(b.Label))
                {
                    var t = Label(b.Label, 10, labelBrush, dpi);
                    double tx = cx - t.Width / 2;
                    if (tx < plotL) tx = plotL;
                    if (tx + t.Width > plotR) tx = plotR - t.Width;
                    dc.DrawText(t, new Point(tx, plotB + 5));
                }
            }
        }

        private static double StackSeg(DrawingContext dc, Brush brush, double left, double yBottom, double barW, float frac, double plotH)
        {
            if (frac <= 0) return yBottom;
            double segH = frac * plotH;
            double yTop = yBottom - segH;
            dc.DrawRectangle(brush, null, new Rect(left, yTop, barW, segH));
            return yTop;
        }

        private FormattedText Label(string s, double size, Brush brush, double dpi)
        {
            return new FormattedText(s ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, dpi);
        }

        private double Dpi() { try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { return 1.0; } }
    }
}
