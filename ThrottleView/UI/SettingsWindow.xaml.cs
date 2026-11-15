using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ThrottleView.Services;

namespace ThrottleView.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsStore _settings;

        public SettingsWindow(SettingsStore settings)
        {
            _settings = settings;
            InitializeComponent();
            LoadFields();
        }

        private void LoadFields()
        {
            var c = _settings.Current;
            ChkStartup.IsChecked = c.StartWithWindows;
            FClockBelow.Text = Str(c.ClockThrottleBelowPct);
            FCpuBusy.Text = Str(c.CpuBusyPct);
            FCpuSat.Text = Str(c.CpuSaturatedPct);
            FCommit.Text = Str(c.CommitHighPct);
            FAvail.Text = Str(c.AvailLowMB);
            FPaging.Text = Str(c.PagesInputHigh);
            FDiskLat.Text = Str(c.DiskLatencyHighMs);
            FTempHot.Text = Str(c.TempHotC);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var c = _settings.Current;
            c.ClockThrottleBelowPct = Num(FClockBelow, c.ClockThrottleBelowPct);
            c.CpuBusyPct = Num(FCpuBusy, c.CpuBusyPct);
            c.CpuSaturatedPct = Num(FCpuSat, c.CpuSaturatedPct);
            c.CommitHighPct = Num(FCommit, c.CommitHighPct);
            c.AvailLowMB = Num(FAvail, c.AvailLowMB);
            c.PagesInputHigh = Num(FPaging, c.PagesInputHigh);
            c.DiskLatencyHighMs = Num(FDiskLat, c.DiskLatencyHighMs);
            c.TempHotC = Num(FTempHot, c.TempHotC);
            c.StartWithWindows = ChkStartup.IsChecked == true;

            _settings.Save(); // clamps to safe ranges

            try { Startup.SetEnabled(c.StartWithWindows); } catch (Exception ex) { Log.Error("apply startup", ex); }

            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
        }

        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        private static string Str(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        private static double Num(TextBox box, double fallback)
        {
            if (box != null && double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) return v;
            return fallback;
        }
    }
}
