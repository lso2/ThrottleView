using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThrottleView.Models;
using ThrottleView.Services;

namespace ThrottleView.UI
{
    public partial class HistoryView : UserControl
    {
        private Monitor _monitor;
        private static readonly string Deg = ((char)0x00B0).ToString();

        public HistoryView() { InitializeComponent(); }

        public void Attach(Monitor monitor) { _monitor = monitor; }

        private void Range_Changed(object sender, RoutedEventArgs e) { Reload(); }

        private TimeSpan Window()
        {
            if (Range1h != null && Range1h.IsChecked == true) return TimeSpan.FromHours(1);
            if (Range7d != null && Range7d.IsChecked == true) return TimeSpan.FromDays(7);
            return TimeSpan.FromHours(24);
        }

        private int BucketCount()
        {
            if (Range1h != null && Range1h.IsChecked == true) return 12;
            if (Range7d != null && Range7d.IsChecked == true) return 28;
            return 24;
        }

        private string WindowName()
        {
            if (Range1h != null && Range1h.IsChecked == true) return "hour";
            if (Range7d != null && Range7d.IsChecked == true) return "7 days";
            return "24 hours";
        }

        public void Reload()
        {
            if (_monitor == null || !IsLoaded) return;
            try
            {
                var window = Window();
                var sum = _monitor.History.GetSummary(window);
                FillSummary(sum, window);
                FillBars(window);
                FillEpisodes(window);
                FillApps(window);
            }
            catch (Exception ex) { Log.Error("history reload", ex); }
        }

        private void FillSummary(HistorySummary sum, TimeSpan window)
        {
            double pct = sum.ThrottledFraction * 100.0;
            SummaryPct.Text = pct.ToString(pct < 10 ? "0.0" : "0") + "%";
            SummaryPct.Foreground = UiFormat.Frozen(pct < 2 ? UiFormat.Ok : (pct < 15 ? UiFormat.Warn : UiFormat.Danger));
            SummaryCaption.Text = "of the last " + WindowName() + " throttled";

            double cpu = sum.SecCpu, ram = sum.SecRam, disk = sum.SecDisk;
            double thr = cpu + ram + disk;
            double free = Math.Max(0, sum.SecondsCovered - thr);
            if (sum.SecondsCovered <= 0) { free = 1; cpu = ram = disk = 0; }
            BreakdownGrid.ColumnDefinitions[0].Width = new GridLength(cpu, GridUnitType.Star);
            BreakdownGrid.ColumnDefinitions[1].Width = new GridLength(ram, GridUnitType.Star);
            BreakdownGrid.ColumnDefinitions[2].Width = new GridLength(disk, GridUnitType.Star);
            BreakdownGrid.ColumnDefinitions[3].Width = new GridLength(free, GridUnitType.Star);

            double cov = Math.Max(1, sum.SecondsCovered);
            LegCpu.Text = "CPU " + (cpu / cov * 100).ToString("0.#") + "%";
            LegRam.Text = "RAM " + (ram / cov * 100).ToString("0.#") + "%";
            LegDisk.Text = "Disk " + (disk / cov * 100).ToString("0.#") + "%";

            StatEpisodes.Text = sum.Episodes.ToString();
            StatWorst.Text = (sum.SecondsCovered > 0 ? Math.Round(sum.WorstDeliveredPct) : 100) + "%";
            StatPeakTemp.Text = sum.PeakTempC > 0 ? Math.Round(sum.PeakTempC) + Deg + "C" : "n/a";
        }

        private void FillBars(TimeSpan window)
        {
            int nb = BucketCount();
            long now = Epoch.Now();
            long start = now - (long)window.TotalSeconds;
            long bucketSec = Math.Max(1, (long)window.TotalSeconds / nb);

            var cov = new double[nb];
            var cpu = new double[nb]; var ram = new double[nb]; var disk = new double[nb];
            var tempSum = new double[nb]; var tempCnt = new int[nb];

            foreach (var r in _monitor.History.GetRollupsSince(window))
            {
                int idx = (int)((r.MinuteUnix - start) / bucketSec);
                if (idx < 0 || idx >= nb) continue;
                cov[idx] += 60; cpu[idx] += r.SecCpu; ram[idx] += r.SecRam; disk[idx] += r.SecDisk;
                if (r.TempAvg > 0) { tempSum[idx] += r.TempAvg; tempCnt[idx]++; }
            }

            var buckets = new List<HistoryBucket>(nb);
            var tempVals = new float[nb];
            bool anyTemp = false; float lastTemp = 0;
            for (int i = 0; i < nb; i++)
            {
                var b = new HistoryBucket { HasData = cov[i] > 0, Label = BucketLabel(start + i * bucketSec, window) };
                if (b.HasData)
                {
                    b.Cpu = (float)(cpu[i] / cov[i]);
                    b.Ram = (float)(ram[i] / cov[i]);
                    b.Disk = (float)(disk[i] / cov[i]);
                }
                buckets.Add(b);
                if (tempCnt[i] > 0) { lastTemp = (float)(tempSum[i] / tempCnt[i]); anyTemp = true; }
                tempVals[i] = lastTemp;
            }
            Bars.SetData(buckets);

            if (anyTemp)
            {
                TempEmpty.Visibility = Visibility.Collapsed;
                TempGraph.Visibility = Visibility.Visible;
                var s = new List<Series> { new Series { Name = "Temp", Color = UiFormat.Temp, Values = tempVals, Fill = true } };
                TempGraph.SetData(s, 100, nb);
            }
            else
            {
                TempGraph.SetData(new List<Series>(), 100, nb);
                TempGraph.Visibility = Visibility.Collapsed;
                TempEmpty.Visibility = Visibility.Visible;
            }
        }

        private string BucketLabel(long unix, TimeSpan window)
        {
            var dt = Epoch.ToLocal(unix);
            if (window.TotalHours <= 1) return dt.ToString("HH:mm");
            if (window.TotalHours <= 24) return dt.ToString("HH:mm");
            return dt.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        }

        private void FillEpisodes(TimeSpan window)
        {
            EpisodesPanel.Children.Clear();
            long cutoff = Epoch.Now() - (long)window.TotalSeconds;
            var eps = _monitor.History.GetRecentEpisodes(40);
            int shown = 0;
            foreach (var ep in eps)
            {
                if (ep.EndUnix < cutoff) continue;
                EpisodesPanel.Children.Add(BuildEpisodeRow(ep));
                if (++shown >= 14) break;
            }
            if (shown == 0)
                EpisodesPanel.Children.Add(Muted("No throttle episodes recorded in this range."));
        }

        private FrameworkElement BuildEpisodeRow(ThrottleEpisode ep)
        {
            var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(Chip(ep.DominantCause));
            var when = new TextBlock
            {
                Text = ep.Start.ToString("MMM d, HH:mm") + "  (" + UiFormat.Duration(ep.Duration) + ")",
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            };
            line1.Children.Add(when);
            outer.Children.Add(line1);

            string detail = string.IsNullOrEmpty(ep.Detail) ? UiFormat.CauseLabel(ep.DominantCause) : ep.Detail;
            if (!string.IsNullOrEmpty(ep.TopProcName)) detail += "   /   " + ep.TopProcName + " " + Math.Round(ep.TopProcCpuPct) + "% CPU";
            outer.Children.Add(new TextBlock { Text = detail, Foreground = UiFormat.Frozen(UiFormat.TextSecondary), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

            return outer;
        }

        private void FillApps(TimeSpan window)
        {
            HistoryAppsPanel.Children.Clear();
            var apps = _monitor.History.GetTopAppsDuringThrottle(window, 6);
            if (apps.Count == 0)
            {
                HistoryAppsPanel.Children.Add(Muted("No throttling has been attributed to a specific app in this range."));
                return;
            }
            int top = Math.Max(1, apps[0].Value);
            foreach (var kv in apps)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = kv.Key, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                var dur = new TextBlock { Text = UiFormat.Duration(TimeSpan.FromSeconds(kv.Value)) + " throttled", FontSize = 11.5, Foreground = UiFormat.Frozen(UiFormat.TextSecondary), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(dur, 1); g.Children.Add(dur);

                var track = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(0, 5, 0, 0), Background = UiFormat.Frozen(UiFormat.WithA(UiFormat.Track, 160)) };
                var fill = new Border { CornerRadius = new CornerRadius(2.5), Background = UiFormat.Frozen(UiFormat.Cpu), HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform((double)kv.Value / top, 1) };
                track.Child = fill;

                sp.Children.Add(g); sp.Children.Add(track);
                HistoryAppsPanel.Children.Add(sp);
            }
        }

        private Border Chip(ThrottleCause cause)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                Background = UiFormat.CauseBrush(cause),
                Child = new TextBlock { Text = UiFormat.CauseLabel(cause), Foreground = UiFormat.Frozen(UiFormat.TextOnFill), FontSize = 11.5, FontWeight = FontWeights.SemiBold }
            };
        }

        private TextBlock Muted(string text)
        {
            return new TextBlock { Text = text, Foreground = UiFormat.Frozen(UiFormat.TextSecondary), FontSize = 12 };
        }
    }
}
