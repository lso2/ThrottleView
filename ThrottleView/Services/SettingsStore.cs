using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ThrottleView.Services
{
    /// <summary>
    /// Tunable thresholds and preferences. Everything the analyzer uses to decide "throttled" lives
    /// here so it can be adjusted in Settings without recompiling. Defaults are applied in
    /// <see cref="Settings.SetDefaults"/>, which runs both for a fresh object and (via the
    /// OnDeserializing hook) before JSON is layered on, so a missing key falls back to its default
    /// rather than to zero.
    /// </summary>
    [DataContract]
    public sealed class Settings
    {
        [DataMember(Name = "startWithWindows")] public bool StartWithWindows;
        [DataMember(Name = "probeEnabled")] public bool ProbeEnabled;
        [DataMember(Name = "probeBaselineOpsPerMs")] public double ProbeBaselineOpsPerMs;

        [DataMember(Name = "cpuBusyPct")] public double CpuBusyPct;
        [DataMember(Name = "clockThrottleBelowPct")] public double ClockThrottleBelowPct;
        [DataMember(Name = "cpuSaturatedPct")] public double CpuSaturatedPct;
        [DataMember(Name = "deliveredThrottleBelowPct")] public double DeliveredThrottleBelowPct;

        [DataMember(Name = "commitHighPct")] public double CommitHighPct;
        [DataMember(Name = "availLowMB")] public double AvailLowMB;
        [DataMember(Name = "pagesInputHigh")] public double PagesInputHigh;

        [DataMember(Name = "diskLatencyHighMs")] public double DiskLatencyHighMs;
        [DataMember(Name = "diskBusyPct")] public double DiskBusyPct;
        [DataMember(Name = "diskQueueHigh")] public double DiskQueueHigh;

        [DataMember(Name = "tempHotC")] public double TempHotC;

        [DataMember(Name = "liveSeconds")] public int LiveSeconds;
        [DataMember(Name = "rollupDays")] public int RollupDays;
        [DataMember(Name = "windowWidth")] public double WindowWidth;
        [DataMember(Name = "windowHeight")] public double WindowHeight;

        public Settings() { SetDefaults(new StreamingContext()); }

        [OnDeserializing]
        private void SetDefaults(StreamingContext _)
        {
            StartWithWindows = true;
            ProbeEnabled = true;
            ProbeBaselineOpsPerMs = 0;          // 0 = not yet calibrated

            CpuBusyPct = 50;                    // demand floor before clock-below-base counts as throttle
            ClockThrottleBelowPct = 92;         // % Processor Performance under this, while busy = throttle
            CpuSaturatedPct = 93;               // cores effectively pinned
            DeliveredThrottleBelowPct = 90;     // probe: under this share of peak = observed slowdown

            CommitHighPct = 88;
            AvailLowMB = 1024;
            PagesInputHigh = 12;                // hard-fault reads/sec that confirm real paging

            DiskLatencyHighMs = 25;
            DiskBusyPct = 80;
            DiskQueueHigh = 2.0;

            TempHotC = 90;

            LiveSeconds = 3600;                 // 1 hour of second-resolution detail in memory
            RollupDays = 90;
            WindowWidth = 1120;
            WindowHeight = 740;
        }

        public void Clamp()
        {
            CpuBusyPct = Bound(CpuBusyPct, 0, 100);
            ClockThrottleBelowPct = Bound(ClockThrottleBelowPct, 50, 100);
            CpuSaturatedPct = Bound(CpuSaturatedPct, 50, 100);
            DeliveredThrottleBelowPct = Bound(DeliveredThrottleBelowPct, 30, 100);
            CommitHighPct = Bound(CommitHighPct, 50, 100);
            AvailLowMB = Bound(AvailLowMB, 64, 65536);
            PagesInputHigh = Bound(PagesInputHigh, 0, 100000);
            DiskLatencyHighMs = Bound(DiskLatencyHighMs, 1, 2000);
            DiskBusyPct = Bound(DiskBusyPct, 10, 100);
            DiskQueueHigh = Bound(DiskQueueHigh, 0.5, 100);
            TempHotC = Bound(TempHotC, 50, 110);
            if (LiveSeconds < 300) LiveSeconds = 300;
            if (RollupDays < 1) RollupDays = 1;
            if (WindowWidth < 760) WindowWidth = 760;
            if (WindowHeight < 520) WindowHeight = 520;
        }

        private static double Bound(double v, double lo, double hi)
        {
            if (double.IsNaN(v) || v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }

    public sealed class SettingsStore
    {
        private string _path;
        public Settings Current { get; private set; }
        public event EventHandler Changed;

        public SettingsStore() { Current = new Settings(); }

        private string FilePath()
        {
            if (_path != null) return _path;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThrottleView");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "settings.json");
            return _path;
        }

        public void Load()
        {
            try
            {
                var p = FilePath();
                if (File.Exists(p))
                {
                    var bytes = File.ReadAllBytes(p);
                    if (bytes.Length > 0)
                    {
                        var ser = new DataContractJsonSerializer(typeof(Settings));
                        using (var ms = new MemoryStream(bytes))
                            Current = (Settings)ser.ReadObject(ms);
                    }
                }
                else
                {
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Error("settings load, using defaults", ex);
                Current = new Settings();
            }
            Current.Clamp();
        }

        public void Save()
        {
            try
            {
                Current.Clamp();
                var ser = new DataContractJsonSerializer(typeof(Settings));
                using (var ms = new MemoryStream())
                {
                    ser.WriteObject(ms, Current);
                    File.WriteAllBytes(FilePath(), Indent(ms.ToArray()));
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { Log.Error("settings save", ex); }
        }

        // DataContractJsonSerializer emits one dense line; expand to one key per line so the file is
        // hand-editable. This is cosmetic only and parsing ignores the whitespace on the way back in.
        private static byte[] Indent(byte[] raw)
        {
            try
            {
                string s = Encoding.UTF8.GetString(raw);
                var sb = new StringBuilder(s.Length + 64);
                bool inStr = false;
                int depth = 0;
                foreach (char c in s)
                {
                    if (c == '"') inStr = !inStr;
                    if (!inStr && (c == '{'))
                    {
                        sb.Append(c); depth++; NewLine(sb, depth); continue;
                    }
                    if (!inStr && (c == '}'))
                    {
                        depth--; NewLine(sb, depth); sb.Append(c); continue;
                    }
                    if (!inStr && c == ',') { sb.Append(c); NewLine(sb, depth); continue; }
                    sb.Append(c);
                }
                return Encoding.UTF8.GetBytes(sb.ToString());
            }
            catch { return raw; }
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }
    }
}
