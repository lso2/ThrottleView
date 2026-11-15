using System;
using System.Reflection;
using Microsoft.Win32;

namespace ThrottleView.Services
{
    /// <summary>
    /// Start-with-Windows toggle, backed by the per-user HKCU Run key (no admin needed). The login
    /// launch passes --tray so the app comes up quietly in the tray instead of popping the window,
    /// which keeps history recording continuous from boot.
    /// </summary>
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ThrottleView";

        private static string LaunchCommand()
        {
            string exe = Assembly.GetEntryAssembly().Location;
            return "\"" + exe + "\" --tray";
        }

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var v = key?.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch (Exception ex) { Log.Error("startup read", ex); return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(ValueName, LaunchCommand(), RegistryValueKind.String);
                    else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
                Log.Info("Start with Windows = " + enabled);
            }
            catch (Exception ex) { Log.Error("startup write", ex); }
        }
    }
}
