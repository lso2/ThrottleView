using System;
using System.Windows;
using System.Windows.Media;
using ThrottleView.Models;

namespace ThrottleView.UI
{
    /// <summary>
    /// A thin strip under the graph. Each second is a sliver colored by what was throttling then,
    /// or left as faint track when nothing was, so a glance shows the rhythm of throttling and which
    /// cause dominated. Right-anchored like the graph. Brushes are cached because this redraws every
    /// second while the window is open.
    /// </summary>
    public sealed class TimelineRibbon : FrameworkElement
    {
        private static readonly Brush[] CauseBrush = BuildCauseBrushes();
        private static readonly Brush TrackBrush = UiFormat.Frozen(UiFormat.WithA(UiFormat.Track, 140));

        private Sample[] _samples = new Sample[0];
        private int _window = 90;

        public void SetData(Sample[] samples, int windowPoints)
        {
            _samples = samples ?? new Sample[0];
            _window = Math.Max(10, windowPoints);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;

            var clip = new RectangleGeometry(new Rect(0, 0, w, h), 6, 6);
            clip.Freeze();
            dc.PushClip(clip);
            dc.DrawRectangle(TrackBrush, null, new Rect(0, 0, w, h));

            int n = _samples.Length;
            double step = w / _window;
            for (int i = 0; i < n; i++)
            {
                var s = _samples[i];
                if (s == null || !s.IsThrottled) continue;
                double left = w - (n - i) * step;
                double width = step + 0.6; // overlap a hair to avoid seams
                int idx = (int)s.PrimaryCause;
                Brush b = (idx >= 0 && idx < CauseBrush.Length) ? CauseBrush[idx] : CauseBrush[0];
                dc.DrawRectangle(b, null, new Rect(left, 0, width, h));
            }

            dc.Pop();
        }

        private static Brush[] BuildCauseBrushes()
        {
            var arr = new Brush[6];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = UiFormat.Frozen(UiFormat.WithA(UiFormat.CauseColor((ThrottleCause)i), 215));
            return arr;
        }
    }
}
