using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using ThrottleView.Models;

namespace ThrottleView.Services
{
    /// <summary>One minute of aggregated history. The backbone of the 24 hour and 7 day views.</summary>
    public sealed class MinuteRollup
    {
        public long MinuteUnix;
        public float CpuAvg, CpuMax;
        public float ClockAvg, ClockMin;
        public float DeliveredAvg, DeliveredMin;
        public float RamAvg, RamMax;
        public float DiskLatMax;
        public float TempAvg, TempMax;     // TempMax = -1 when no temperature was read in the minute
        public byte SecondsThrottled;      // 0..60
        public byte SecCpu, SecRam, SecDisk;
    }

    /// <summary>Rolled-up answer to "how often, and from what" over a window.</summary>
    public struct HistorySummary
    {
        public int SecondsCovered;
        public int SecondsThrottled;
        public int SecCpu, SecRam, SecDisk;
        public float WorstDeliveredPct;
        public float PeakTempC;            // -1 when unknown
        public int Episodes;
        public double ThrottledFraction { get { return SecondsCovered > 0 ? (double)SecondsThrottled / SecondsCovered : 0; } }
    }

    /// <summary>
    /// Records history at two resolutions. A second-resolution ring in memory feeds the live graph
    /// and the precise last-hour view; minute rollups persisted to disk feed the longer windows so
    /// days of history cost only kilobytes. Throttle episodes are appended as one JSON line each.
    /// All access is guarded because the sampler writes from a background thread while the UI reads.
    /// </summary>
    public sealed class HistoryStore
    {
        private const int RecSize = 56;
        private readonly object _gate = new object();
        private readonly Sample[] _ring;
        private int _ringHead;     // next write index
        private int _ringCount;

        private readonly List<MinuteRollup> _rollups = new List<MinuteRollup>();
        private readonly List<ThrottleEpisode> _episodes = new List<ThrottleEpisode>();
        private readonly int _rollupCap;
        private int _appendsSinceCompact;

        private string _dir, _rollupPath, _episodePath;

        // Current minute accumulator.
        private long _accMinute = -1;
        private double _cpuSum, _clockSum, _delSum, _ramSum, _tempSum;
        private float _cpuMax, _clockMin, _delMin, _ramMax, _diskLatMax, _tempMax;
        private int _accCount, _tempCount, _secThr, _secCpu, _secRam, _secDisk;

        public HistoryStore(SettingsStore settings)
        {
            int live = Math.Max(300, settings.Current.LiveSeconds);
            _ring = new Sample[live];
            _rollupCap = Math.Max(1, settings.Current.RollupDays) * 1440;
            InitPaths();
            LoadRollups();
            LoadEpisodes();
        }

        private void InitPaths()
        {
            try
            {
                _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThrottleView", "history");
                Directory.CreateDirectory(_dir);
                _rollupPath = Path.Combine(_dir, "rollups.bin");
                _episodePath = Path.Combine(_dir, "episodes.jsonl");
            }
            catch (Exception ex) { Log.Error("history paths", ex); }
        }

        // ---- Ingest ----------------------------------------------------------

        public void AddSample(Sample s)
        {
            lock (_gate)
            {
                _ring[_ringHead] = s;
                _ringHead = (_ringHead + 1) % _ring.Length;
                if (_ringCount < _ring.Length) _ringCount++;

                long unix = Epoch.FromLocal(s.Time);
                long minute = unix - (unix % 60);
                if (_accMinute == -1) StartMinute(minute);
                else if (minute != _accMinute) { FlushMinute(); StartMinute(minute); }
                Accumulate(s);
            }
        }

        private void StartMinute(long minute)
        {
            _accMinute = minute;
            _cpuSum = _clockSum = _delSum = _ramSum = _tempSum = 0;
            _cpuMax = _ramMax = _diskLatMax = 0;
            _clockMin = 999; _delMin = 999; _tempMax = -1;
            _accCount = _tempCount = _secThr = _secCpu = _secRam = _secDisk = 0;
        }

        private void Accumulate(Sample s)
        {
            _accCount++;
            _cpuSum += s.CpuPct; if (s.CpuPct > _cpuMax) _cpuMax = s.CpuPct;
            _clockSum += s.ClockPct; if (s.ClockPct < _clockMin) _clockMin = s.ClockPct;
            float del = s.ProbeValid ? s.DeliveredPct : 100f;
            _delSum += del; if (del < _delMin) _delMin = del;
            _ramSum += s.RamUsedPct; if (s.RamUsedPct > _ramMax) _ramMax = s.RamUsedPct;
            if (s.DiskLatencyMs > _diskLatMax) _diskLatMax = s.DiskLatencyMs;
            if (s.TempC.HasValue) { _tempSum += s.TempC.Value; _tempCount++; if (s.TempC.Value > _tempMax) _tempMax = s.TempC.Value; }
            if (s.IsThrottled)
            {
                _secThr++;
                switch (s.PrimaryCause)
                {
                    case ThrottleCause.CpuThermal:
                    case ThrottleCause.CpuPower:
                    case ThrottleCause.CpuSaturated: _secCpu++; break;
                    case ThrottleCause.Ram: _secRam++; break;
                    case ThrottleCause.Disk: _secDisk++; break;
                }
            }
        }

        private void FlushMinute()
        {
            if (_accCount == 0) return;
            var r = new MinuteRollup
            {
                MinuteUnix = _accMinute,
                CpuAvg = (float)(_cpuSum / _accCount), CpuMax = _cpuMax,
                ClockAvg = (float)(_clockSum / _accCount), ClockMin = (_clockMin > 900 ? 100 : _clockMin),
                DeliveredAvg = (float)(_delSum / _accCount), DeliveredMin = (_delMin > 900 ? 100 : _delMin),
                RamAvg = (float)(_ramSum / _accCount), RamMax = _ramMax,
                DiskLatMax = _diskLatMax,
                TempAvg = _tempCount > 0 ? (float)(_tempSum / _tempCount) : -1,
                TempMax = _tempMax,
                SecondsThrottled = (byte)Math.Min(60, _secThr),
                SecCpu = (byte)Math.Min(60, _secCpu),
                SecRam = (byte)Math.Min(60, _secRam),
                SecDisk = (byte)Math.Min(60, _secDisk)
            };
            _rollups.Add(r);
            AppendRollup(r);
            if (_rollups.Count > _rollupCap) _rollups.RemoveRange(0, _rollups.Count - _rollupCap);
        }

        public void AddEpisode(ThrottleEpisode ep)
        {
            lock (_gate)
            {
                _episodes.Add(ep);
                if (_episodes.Count > 5000) _episodes.RemoveRange(0, _episodes.Count - 5000);
                AppendEpisode(ep);
            }
        }

        /// <summary>Flush the partial minute so a clean exit does not lose the last under-60 seconds.</summary>
        public void Flush()
        {
            lock (_gate) { if (_accCount > 0) { FlushMinute(); _accMinute = -1; } }
        }

        // ---- Queries ---------------------------------------------------------

        public Sample[] GetRecentSamples(int seconds)
        {
            lock (_gate)
            {
                int n = Math.Min(seconds, _ringCount);
                var outp = new Sample[n];
                for (int i = 0; i < n; i++)
                {
                    int idx = (_ringHead - n + i + _ring.Length * 2) % _ring.Length;
                    outp[i] = _ring[idx];
                }
                return outp;
            }
        }

        public List<MinuteRollup> GetRollupsSince(TimeSpan window)
        {
            long cutoff = Epoch.Now() - (long)window.TotalSeconds;
            lock (_gate)
            {
                var outp = new List<MinuteRollup>();
                foreach (var r in _rollups) if (r.MinuteUnix >= cutoff) outp.Add(r);
                return outp;
            }
        }

        public HistorySummary GetSummary(TimeSpan window)
        {
            var sum = new HistorySummary { PeakTempC = -1, WorstDeliveredPct = 100 };
            long cutoff = Epoch.Now() - (long)window.TotalSeconds;
            lock (_gate)
            {
                if (window.TotalSeconds <= _ring.Length)
                {
                    for (int i = 0; i < _ringCount; i++)
                    {
                        int idx = (_ringHead - _ringCount + i + _ring.Length * 2) % _ring.Length;
                        var s = _ring[idx];
                        if (s == null || Epoch.FromLocal(s.Time) < cutoff) continue;
                        TallySecond(ref sum, s.IsThrottled, s.PrimaryCause,
                            s.ProbeValid ? s.DeliveredPct : 100f, s.TempC);
                    }
                }
                else
                {
                    foreach (var r in _rollups)
                    {
                        if (r.MinuteUnix < cutoff) continue;
                        sum.SecondsCovered += 60; // each stored minute represents 60 seconds
                        sum.SecondsThrottled += r.SecondsThrottled;
                        sum.SecCpu += r.SecCpu; sum.SecRam += r.SecRam; sum.SecDisk += r.SecDisk;
                        if (r.DeliveredMin < sum.WorstDeliveredPct) sum.WorstDeliveredPct = r.DeliveredMin;
                        if (r.TempMax > sum.PeakTempC) sum.PeakTempC = r.TempMax;
                    }
                }
                foreach (var e in _episodes) if (e.EndUnix >= cutoff) sum.Episodes++;
            }
            return sum;
        }

        private static void TallySecond(ref HistorySummary sum, bool thr, ThrottleCause cause, float delivered, float? temp)
        {
            sum.SecondsCovered++;
            if (thr)
            {
                sum.SecondsThrottled++;
                if (cause == ThrottleCause.Ram) sum.SecRam++;
                else if (cause == ThrottleCause.Disk) sum.SecDisk++;
                else sum.SecCpu++;
            }
            if (delivered < sum.WorstDeliveredPct) sum.WorstDeliveredPct = delivered;
            if (temp.HasValue && temp.Value > sum.PeakTempC) sum.PeakTempC = temp.Value;
        }

        public List<ThrottleEpisode> GetRecentEpisodes(int max)
        {
            lock (_gate)
            {
                var outp = new List<ThrottleEpisode>();
                for (int i = _episodes.Count - 1; i >= 0 && outp.Count < max; i--) outp.Add(_episodes[i]);
                return outp;
            }
        }

        /// <summary>Apps most responsible across throttle episodes in the window, by total throttled seconds.</summary>
        public List<KeyValuePair<string, int>> GetTopAppsDuringThrottle(TimeSpan window, int max)
        {
            long cutoff = Epoch.Now() - (long)window.TotalSeconds;
            var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var e in _episodes)
                {
                    if (e.EndUnix < cutoff || string.IsNullOrEmpty(e.TopProcName)) continue;
                    int dur = (int)e.Duration.TotalSeconds;
                    tally.TryGetValue(e.TopProcName, out int cur);
                    tally[e.TopProcName] = cur + dur;
                }
            }
            var list = new List<KeyValuePair<string, int>>(tally);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        public void Reset()
        {
            lock (_gate)
            {
                Array.Clear(_ring, 0, _ring.Length);
                _ringHead = _ringCount = 0;
                _rollups.Clear(); _episodes.Clear();
                _accMinute = -1;
                try { if (File.Exists(_rollupPath)) File.Delete(_rollupPath); } catch (Exception ex) { Log.Error("reset rollups", ex); }
                try { if (File.Exists(_episodePath)) File.Delete(_episodePath); } catch (Exception ex) { Log.Error("reset episodes", ex); }
            }
            Log.Info("History reset.");
        }

        // ---- Persistence -----------------------------------------------------

        private void AppendRollup(MinuteRollup r)
        {
            try
            {
                using (var fs = new FileStream(_rollupPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(r.MinuteUnix);
                    w.Write(r.CpuAvg); w.Write(r.CpuMax);
                    w.Write(r.ClockAvg); w.Write(r.ClockMin);
                    w.Write(r.DeliveredAvg); w.Write(r.DeliveredMin);
                    w.Write(r.RamAvg); w.Write(r.RamMax);
                    w.Write(r.DiskLatMax);
                    w.Write(r.TempAvg); w.Write(r.TempMax);
                    w.Write(r.SecondsThrottled); w.Write(r.SecCpu); w.Write(r.SecRam); w.Write(r.SecDisk);
                }
                if (++_appendsSinceCompact > 1440 && _rollups.Count >= _rollupCap) Compact();
            }
            catch (Exception ex) { Log.Error("append rollup", ex); }
        }

        private void Compact()
        {
            _appendsSinceCompact = 0;
            try
            {
                string tmp = _rollupPath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    foreach (var r in _rollups)
                    {
                        w.Write(r.MinuteUnix);
                        w.Write(r.CpuAvg); w.Write(r.CpuMax);
                        w.Write(r.ClockAvg); w.Write(r.ClockMin);
                        w.Write(r.DeliveredAvg); w.Write(r.DeliveredMin);
                        w.Write(r.RamAvg); w.Write(r.RamMax);
                        w.Write(r.DiskLatMax);
                        w.Write(r.TempAvg); w.Write(r.TempMax);
                        w.Write(r.SecondsThrottled); w.Write(r.SecCpu); w.Write(r.SecRam); w.Write(r.SecDisk);
                    }
                }
                File.Copy(tmp, _rollupPath, true);
                File.Delete(tmp);
            }
            catch (Exception ex) { Log.Error("compact rollups", ex); }
        }

        private void LoadRollups()
        {
            try
            {
                if (!File.Exists(_rollupPath)) return;
                var bytes = File.ReadAllBytes(_rollupPath);
                int count = bytes.Length / RecSize;
                int start = Math.Max(0, count - _rollupCap);
                using (var ms = new MemoryStream(bytes))
                using (var rd = new BinaryReader(ms))
                {
                    ms.Position = (long)start * RecSize;
                    for (int i = start; i < count; i++)
                    {
                        var r = new MinuteRollup
                        {
                            MinuteUnix = rd.ReadInt64(),
                            CpuAvg = rd.ReadSingle(), CpuMax = rd.ReadSingle(),
                            ClockAvg = rd.ReadSingle(), ClockMin = rd.ReadSingle(),
                            DeliveredAvg = rd.ReadSingle(), DeliveredMin = rd.ReadSingle(),
                            RamAvg = rd.ReadSingle(), RamMax = rd.ReadSingle(),
                            DiskLatMax = rd.ReadSingle(),
                            TempAvg = rd.ReadSingle(), TempMax = rd.ReadSingle(),
                            SecondsThrottled = rd.ReadByte(), SecCpu = rd.ReadByte(), SecRam = rd.ReadByte(), SecDisk = rd.ReadByte()
                        };
                        _rollups.Add(r);
                    }
                }
                Log.Info("Loaded " + _rollups.Count + " minute rollups.");
            }
            catch (Exception ex) { Log.Error("load rollups", ex); }
        }

        private void AppendEpisode(ThrottleEpisode ep)
        {
            try
            {
                var ser = new DataContractJsonSerializer(typeof(ThrottleEpisode));
                using (var ms = new MemoryStream())
                {
                    ser.WriteObject(ms, ep);
                    string line = Encoding.UTF8.GetString(ms.ToArray()) + "\n";
                    File.AppendAllText(_episodePath, line, Encoding.UTF8);
                }
            }
            catch (Exception ex) { Log.Error("append episode", ex); }
        }

        private void LoadEpisodes()
        {
            try
            {
                if (!File.Exists(_episodePath)) return;
                var lines = File.ReadAllLines(_episodePath);
                var ser = new DataContractJsonSerializer(typeof(ThrottleEpisode));
                int start = Math.Max(0, lines.Length - 5000);
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    try
                    {
                        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(lines[i])))
                            _episodes.Add((ThrottleEpisode)ser.ReadObject(ms));
                    }
                    catch { }
                }
                Log.Info("Loaded " + _episodes.Count + " episodes.");
            }
            catch (Exception ex) { Log.Error("load episodes", ex); }
        }
    }
}
