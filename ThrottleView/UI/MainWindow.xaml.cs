using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ThrottleView.Models;
using ThrottleView.Services;

namespace ThrottleView.UI
{
    public partial class MainWindow : Window
    {
        private readonly Monitor _monitor;
        private readonly SettingsStore _settings;
        private const int WindowSecs = 90;
        private static readonly string Deg = ((char)0x00B0).ToString();
        private int _lastProcSeq = -1;
        private const string GitHubUrl = "https://github.com/lso2/ThrottleView";
        private const string DonateUrl = "https://plexpixel.com/donate";

        public MainWindow(Monitor monitor, SettingsStore settings)
        {
            _monitor = monitor;
            _settings = settings;
            InitializeComponent();

            // Footer version comes from AssemblyVersion in Properties\AssemblyInfo.cs.
            var version = typeof(MainWindow).Assembly.GetName().Version;
            FooterName.Text = "ThrottleView v" + version.Major + "." + version.Minor + "." + version.Build;

            Width = settings.Current.WindowWidth;
            Height = settings.Current.WindowHeight;

            History.Attach(monitor);

            _monitor.SampleReady += OnSampleReady;
            _monitor.EpisodeClosed += OnEpisodeClosed;

            Loaded += (s, a) => { if (_monitor.Latest != null) UpdateLive(_monitor.Latest); };
            Closing += OnClosing;
        }

        private void OnSampleReady(object sender, Sample s)
        {
            Dispatcher.BeginInvoke(new Action(() => { try { UpdateLive(s); } catch (Exception ex) { Log.Error("update live", ex); } }));
        }

        private void OnEpisodeClosed(object sender, ThrottleEpisode e)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (History.Visibility == Visibility.Visible) History.Reload(); }));
        }

        // ---- Live update -----------------------------------------------------

        private void UpdateLive(Sample s)
        {
            // No live-UI work when minimized or when the History tab is showing; the dashboard is not
            // visible then, so rendering it would burn CPU for nothing.
            if (!IsVisible || WindowState == WindowState.Minimized || LivePanel.Visibility != Visibility.Visible) return;
            // Left gauge is a usage meter: CPU load from idle (needle at rest, green) to maxed (red).
            double load = s.CpuPct;
            LoadGauge.Set(load, UiFormat.HealthColor(100 - load), Math.Round(load) + "%", "");

            // Right gauge is the throttle meter: 0 when nothing holds the machine back, climbing into
            // amber and red as the throttle worsens. The clock and performance detail live in the cards.
            double throttleLevel = s.IsThrottled ? s.Severity : 0;
            ThrottleGauge.Set(throttleLevel, UiFormat.HealthColor(100 - throttleLevel), Math.Round(throttleLevel) + "%", "");

            if (s.IsThrottled)
            {
                StateText.Text = UiFormat.CauseLabel(s.PrimaryCause);
                StateText.Foreground = UiFormat.Frozen(UiFormat.CauseColor(s.PrimaryCause));
            }
            else
            {
                StateText.Text = "No throttling";
                StateText.Foreground = UiFormat.Frozen(UiFormat.Ok);
            }
            DetailText.Text = s.CauseDetail ?? "";

            BannerClock.Text = s.EffectiveGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz";
            BannerClockBase.Text = "base " + s.BaseGhz.ToString("0.0", CultureInfo.InvariantCulture) + " GHz  /  " + Math.Round(s.ClockPct) + "% perf";
            BannerTemp.Text = s.TempC.HasValue ? Math.Round(s.TempC.Value) + Deg + "C" : "n/a";
            BannerTempSrc.Text = s.TempC.HasValue ? s.TempSource : "no sensor";
            BannerTopApp.Text = string.IsNullOrEmpty(s.TopCpuProc) ? "none" : s.TopCpuProc;
            BannerTopAppSub.Text = s.TopCpuProcPct > 0 ? Math.Round(s.TopCpuProcPct) + "% CPU" : "";

            CardClock.Text = s.EffectiveGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz";
            CardClockSub.Text = Math.Round(s.ClockPct) + "% of base";
            CardClockBar.ScaleX = Clamp01(s.ClockPct / 100.0);

            CardDelivered.Text = Math.Round(throttleLevel) + "%";
            CardDeliveredSub.Text = s.IsThrottled ? UiFormat.CauseShort(s.PrimaryCause) : "none";
            CardDeliveredBar.ScaleX = Clamp01(throttleLevel / 100.0);
            CardDeliveredFill.Background = UiFormat.Frozen(UiFormat.HealthColor(100 - throttleLevel));

            CardMem.Text = Math.Round(s.RamUsedPct) + "%";
            CardMemSub.Text = Math.Round(s.CommitPct) + "% commit / " + UiFormat.Gb(s.AvailMB) + " free";
            CardMemBar.ScaleX = Clamp01(s.RamUsedPct / 100.0);

            CardDisk.Text = Math.Round(s.DiskLatencyMs) + " ms";
            CardDiskSub.Text = Math.Round(s.DiskActivePct) + "% active / q" + s.DiskQueue.ToString("0.0", CultureInfo.InvariantCulture);
            CardDiskBar.ScaleX = Clamp01(s.DiskActivePct / 100.0);

            CardTemp.Text = s.TempC.HasValue ? Math.Round(s.TempC.Value) + Deg + "C" : "n/a";
            CardTempSub.Text = s.TempC.HasValue ? s.TempSource : "no sensor";
            CardTempBar.ScaleX = s.TempC.HasValue ? Clamp01(s.TempC.Value / 100.0) : 0;

            UpdateGraph();
            if (_monitor.ProcSeq != _lastProcSeq)
            {
                _lastProcSeq = _monitor.ProcSeq;
                BuildTopApps(_monitor.LatestTopProcs());
            }
        }

        private void UpdateGraph()
        {
            var recent = _monitor.History.GetRecentSamples(WindowSecs);
            if (recent.Length == 0) return;
            int n = recent.Length;

            var cpu = new float[n];
            var ram = new float[n];
            var delivered = new float[n];
            var temp = new float[n];
            bool anyTemp = false;
            float lastTemp = 0;
            for (int i = 0; i < n; i++)
            {
                var s = recent[i];
                cpu[i] = s.CpuPct;
                ram[i] = s.RamUsedPct;
                delivered[i] = s.IsThrottled ? Clamp(s.Severity, 0, 100) : 0; // throttle level, 0 when fine
                if (s.TempC.HasValue) { lastTemp = s.TempC.Value; anyTemp = true; }
                temp[i] = lastTemp;
            }

            var series = new List<Series>();
            series.Add(new Series { Name = "Throttle", Color = UiFormat.Delivered, Values = delivered, Fill = true });
            series.Add(new Series { Name = "CPU", Color = UiFormat.Cpu, Values = cpu });
            series.Add(new Series { Name = "RAM", Color = UiFormat.Ram, Values = ram });
            if (anyTemp) series.Add(new Series { Name = "Temp", Color = UiFormat.Temp, Values = temp });

            Graph.SetData(series, 100, WindowSecs);
            Timeline.SetData(recent, WindowSecs);
        }

        private void BuildTopApps(List<ProcUsage> top)
        {
            TopAppsPanel.Children.Clear();
            if (top == null || top.Count == 0)
            {
                TopAppsPanel.Children.Add(new TextBlock { Text = "Gathering process data...", Foreground = UiFormat.Frozen(UiFormat.TextSecondary), FontSize = 12 });
                return;
            }
            float topCpu = Math.Max(1f, top[0].CpuPct);
            int rows = Math.Min(6, top.Count);
            for (int i = 0; i < rows; i++)
            {
                var p = top[i];
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock { Text = p.Name, FontSize = 12.5, Foreground = UiFormat.Frozen(Color.FromRgb(0xE8, 0xEA, 0xED)), TextTrimming = TextTrimming.CharacterEllipsis };
                double memMb = p.WorkingSetBytes / (1024.0 * 1024.0);
                var stat = new TextBlock { Text = Math.Round(p.CpuPct) + "% CPU  /  " + Math.Round(memMb) + " MB", FontSize = 11.5, Foreground = UiFormat.Frozen(UiFormat.TextSecondary), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(stat, 1);
                g.Children.Add(name); g.Children.Add(stat);

                var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 0, 0), Background = UiFormat.Frozen(UiFormat.WithA(UiFormat.Track, 160)) };
                var fill = new Border { CornerRadius = new CornerRadius(2), Background = UiFormat.Frozen(UiFormat.Cpu), HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransformOrigin = new Point(0, 0.5) };
                fill.RenderTransform = new ScaleTransform(Clamp01(p.CpuPct / topCpu), 1);
                track.Child = fill;

                sp.Children.Add(g); sp.Children.Add(track);
                TopAppsPanel.Children.Add(sp);
            }
        }

        // ---- Chrome ----------------------------------------------------------

        private void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (LivePanel == null || History == null) return;
            bool live = LiveTab.IsChecked == true;
            LivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            History.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            if (!live) History.Reload();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_settings) { Owner = this };
            w.ShowDialog();
        }

        private void Min_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void GitHub_Click(object sender, RoutedEventArgs e) { OpenLink(GitHubUrl); }

        private void Donate_Click(object sender, RoutedEventArgs e) { OpenLink(DonateUrl); }

        private static void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open link " + url, ex); }
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                _monitor.SampleReady -= OnSampleReady;
                _monitor.EpisodeClosed -= OnEpisodeClosed;
                if (WindowState == WindowState.Normal)
                {
                    _settings.Current.WindowWidth = ActualWidth;
                    _settings.Current.WindowHeight = ActualHeight;
                    _settings.Save();
                }
            }
            catch (Exception ex) { Log.Error("window closing", ex); }
        }

        public void RefreshHistory() { try { History.Reload(); } catch { } }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
