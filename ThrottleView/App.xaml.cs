using System;
using System.Reflection;
using System.Windows;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using ThrottleView.Models;
using ThrottleView.Services;
using ThrottleView.UI;
using StartupSvc = ThrottleView.Services.Startup; // App inherits Application.Startup (an event), so the service is aliased

namespace ThrottleView
{
    /// <summary>
    /// Application entry point and process-wide host. ThrottleView lives in the system tray,
    /// samples the machine once a second through <see cref="Monitor"/>, and opens a dark dashboard
    /// window on demand. The tray tooltip reflects the current delivered-performance reading so the
    /// state is visible without opening anything.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutex = "ThrottleView.SingleInstance.{7E1B4C90}";

        private System.Threading.Mutex _mutex;
        private bool _ownsMutex;
        private WinForms.NotifyIcon _tray;
        private WinForms.ToolStripMenuItem _startupItem;
        private MainWindow _window;

        public static SettingsStore Settings { get; private set; }
        public static Monitor Monitor { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Init();
            Log.Info("ThrottleView starting. args=[" + string.Join(" ", e.Args) + "]");

            try
            {
                if (!ClaimSingleInstance())
                {
                    Log.Info("Another ThrottleView instance is already running. Exiting.");
                    Shutdown();
                    return;
                }

                Settings = new SettingsStore();
                Settings.Load();
                ReconcileStartup();

                Monitor = new Monitor(Settings);
                Monitor.SampleReady += OnSampleReady;
                Monitor.Start();

                CreateTrayIcon();

                bool showOnLaunch = !HasFlag(e.Args, "--tray");
                if (showOnLaunch) ShowMainWindow();

                Log.Info("ThrottleView running.");
            }
            catch (Exception ex)
            {
                Log.Error("Fatal during startup", ex);
                Shutdown();
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (var a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool ClaimSingleInstance()
        {
            _mutex = new System.Threading.Mutex(true, SingleInstanceMutex, out _ownsMutex);
            return _ownsMutex;
        }

        // Keep the HKCU Run entry in step with the saved preference. The installer sets it; if the
        // user later disabled it elsewhere we respect the saved value as the source of truth.
        private void ReconcileStartup()
        {
            try
            {
                bool want = Settings.Current.StartWithWindows;
                if (StartupSvc.IsEnabled() != want) StartupSvc.SetEnabled(want);
            }
            catch (Exception ex) { Log.Error("startup reconcile", ex); }
        }

        // ---- Tray ------------------------------------------------------------

        private void CreateTrayIcon()
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "ThrottleView",
                Visible = true
            };
            _tray.MouseClick += (s, a) =>
            {
                if (a.Button == WinForms.MouseButtons.Left) Dispatcher.Invoke(ShowMainWindow);
            };
            _tray.DoubleClick += (s, a) => Dispatcher.Invoke(ShowMainWindow);

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open ThrottleView", null, (s, a) => Dispatcher.Invoke(ShowMainWindow));
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _startupItem = new WinForms.ToolStripMenuItem("Start with Windows")
            {
                Checked = Settings.Current.StartWithWindows,
                CheckOnClick = true
            };
            _startupItem.CheckedChanged += (s, a) => Dispatcher.Invoke(() => SetStartWithWindows(_startupItem.Checked));
            menu.Items.Add(_startupItem);

            // Accurate CPU/GPU temperatures need the optional sensor library plus a kernel driver that
            // only loads when elevated. Offer a one-click relaunch as administrator for that.
            if (IsElevated())
                menu.Items.Add(new WinForms.ToolStripMenuItem("Running as administrator") { Enabled = false });
            else
                menu.Items.Add("Restart as administrator (full temps)", null, (s, a) => Dispatcher.Invoke(RestartElevated));

            menu.Items.Add("Reset history", null, (s, a) => Dispatcher.Invoke(ResetHistory));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, a) => Dispatcher.Invoke(Shutdown));
            _tray.ContextMenuStrip = menu;
        }

        private void SetStartWithWindows(bool on)
        {
            try
            {
                StartupSvc.SetEnabled(on);
                Settings.Current.StartWithWindows = on;
                Settings.Save();
            }
            catch (Exception ex) { Log.Error("set startup", ex); }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Relaunch elevated so the optional sensor library's kernel driver can load and report real
        // CPU/GPU temperatures. The lock is released first so the elevated copy can claim it; if the
        // user dismisses the UAC prompt we re-take the lock and carry on unelevated.
        private void RestartElevated()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly().Location;
                try { if (_ownsMutex) { _mutex.ReleaseMutex(); _ownsMutex = false; } _mutex?.Dispose(); _mutex = null; } catch { }
                var psi = new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" };
                System.Diagnostics.Process.Start(psi);
                Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("restart elevated (likely UAC dismissed)", ex);
                try { _mutex = new System.Threading.Mutex(true, SingleInstanceMutex, out _ownsMutex); } catch { }
            }
        }

        private void ResetHistory()
        {
            var r = WinForms.MessageBox.Show(
                "Clear all recorded throttle history (live buffer, minute rollups, and episodes)?",
                "ThrottleView", WinForms.MessageBoxButtons.OKCancel, WinForms.MessageBoxIcon.Warning);
            if (r == WinForms.DialogResult.OK)
            {
                Monitor.History.Reset();
                _window?.RefreshHistory();
            }
        }

        private void OnSampleReady(object sender, Sample s)
        {
            // Tray tooltip is the at-a-glance state. NotifyIcon.Text caps at 127 chars.
            try
            {
                if (_tray == null) return;
                string state = s.PrimaryCause == ThrottleCause.None
                    ? "OK"
                    : "Throttled: " + UiFormat.CauseShort(s.PrimaryCause);
                string text = "ThrottleView  " + Math.Round(s.DeliveredPct) + "% delivered  "
                              + s.EffectiveGhz.ToString("0.0") + " GHz  " + state;
                if (text.Length > 127) text = text.Substring(0, 127);
                Dispatcher.BeginInvoke(new Action(() => { if (_tray != null) _tray.Text = text; }));
            }
            catch { }
        }

        // ---- Window ----------------------------------------------------------

        private void ShowMainWindow()
        {
            if (_window == null)
            {
                _window = new MainWindow(Monitor, Settings);
                _window.Closed += (s, a) => _window = null;
            }
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
        }

        private Drawing.Icon LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/throttleview.ico", UriKind.Absolute);
                var info = GetResourceStream(uri);
                if (info != null) using (var st = info.Stream) return new Drawing.Icon(st);
            }
            catch (Exception ex) { Log.Error("load packed icon", ex); }
            try
            {
                var path = Assembly.GetEntryAssembly().Location;
                var ico = Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null) return ico;
            }
            catch (Exception ex) { Log.Error("extract icon", ex); }
            return Drawing.SystemIcons.Application;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { Monitor?.Stop(); } catch (Exception ex) { Log.Error("monitor stop", ex); }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_ownsMutex) { _mutex.ReleaseMutex(); } _mutex?.Dispose(); } catch { }
            Log.Info("ThrottleView exited.");
            base.OnExit(e);
        }
    }
}
