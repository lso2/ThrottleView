using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ThrottleView.Models;

namespace ThrottleView.Services
{
    /// <summary>
    /// The sampling heart. Once a second on a background thread it reads the performance counters,
    /// the active probe, the temperature, and (every couple of seconds) the top processes, assembles
    /// a <see cref="Sample"/>, asks the analyzer to classify it, threads it into a throttle-episode
    /// state machine, stores it, and raises <see cref="SampleReady"/>. The UI subscribes and marshals
    /// to its own thread; the monitor never touches WPF directly.
    /// </summary>
    public sealed class Monitor
    {
        private readonly SettingsStore _settings;
        private PerfMetrics _metrics;
        private TemperatureProvider _temp;
        private ProcessSampler _procs;
        private ThrottleAnalyzer _analyzer;

        private Thread _thread;
        private volatile bool _stop;
        private readonly object _gate = new object();

        private List<ProcUsage> _latestTop = new List<ProcUsage>();
        private DateTime _lastProcSample = DateTime.MinValue;
        private int _procSeq;

        // Episode state machine.
        private const int OpenAfter = 2;   // consecutive throttled seconds before an episode opens
        private const int CloseAfter = 4;  // consecutive clear seconds before it closes (merges flapping)
        private EpisodeBuilder _builder;
        private int _consecThrottled, _consecClear;

        public HistoryStore History { get; private set; }
        public Sample Latest { get; private set; }
        public int ProcSeq { get { return _procSeq; } } // bumps when the top-process list is refreshed
        public float BaseGhz { get { return _metrics != null ? _metrics.BaseGhz : 0; } }
        public int LogicalCount { get { return _metrics != null ? _metrics.LogicalCount : Environment.ProcessorCount; } }
        public string TempSource { get { return _temp != null ? _temp.Source : "Unavailable"; } }

        public event EventHandler<Sample> SampleReady;
        public event EventHandler<ThrottleEpisode> EpisodeClosed;

        public Monitor(SettingsStore settings)
        {
            _settings = settings;
            History = new HistoryStore(settings);
        }

        public List<ProcUsage> LatestTopProcs()
        {
            lock (_gate) { return new List<ProcUsage>(_latestTop); }
        }

        public ThrottleEpisode CurrentEpisode()
        {
            lock (_gate) { return _builder != null ? _builder.Snapshot() : null; }
        }

        public void Start()
        {
            _metrics = new PerfMetrics();
            _temp = new TemperatureProvider();
            _procs = new ProcessSampler(_metrics.LogicalCount);
            _analyzer = new ThrottleAnalyzer(_settings);

            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ThrottleView.Monitor" };
            _thread.Start();
            Log.Info("Monitor started. baseGhz=" + _metrics.BaseGhz.ToString("0.00") + " logical=" + _metrics.LogicalCount + " cpuPerf=" + _metrics.CpuPerfAvailable);
        }

        public void Stop()
        {
            _stop = true;
            try { _thread?.Join(2500); } catch { }
            lock (_gate) { if (_builder != null) CloseEpisode(); }
            try { History?.Flush(); } catch (Exception ex) { Log.Error("history flush", ex); }
            try { _metrics?.Dispose(); } catch { }
            try { _temp?.Dispose(); } catch { }
            Log.Info("Monitor stopped.");
        }

        private void Loop()
        {
            const int interval = 1000; // one sample per second
            while (!_stop)
            {
                var sw = Stopwatch.StartNew();
                try { Tick(); }
                catch (Exception ex) { Log.Error("monitor tick", ex); }
                int wait = interval - (int)sw.ElapsedMilliseconds;
                if (wait < 10) wait = 10;
                SleepInterruptible(wait);
            }
        }

        private void SleepInterruptible(int ms)
        {
            int slept = 0;
            while (slept < ms && !_stop) { Thread.Sleep(50); slept += 50; }
        }

        private void Tick()
        {
            var m = _metrics.Read();
            float? temp = _temp.Read();

            var s = new Sample
            {
                Time = DateTime.Now,
                CpuPct = m.CpuPct,
                ClockPct = m.ClockPct,
                BaseGhz = m.BaseGhz,
                EffectiveGhz = m.EffectiveGhz,
                ProbeValid = _metrics.CpuPerfAvailable,
                DeliveredPct = 100f,
                RamUsedPct = m.RamUsedPct,
                CommitPct = m.CommitPct,
                AvailMB = m.AvailMB,
                PagesInput = m.PagesInput,
                DiskActivePct = m.DiskActivePct,
                DiskLatencyMs = m.DiskLatencyMs,
                DiskQueue = m.DiskQueue,
                TempC = temp,
                TempSource = _temp.Source
            };

            // Top processes every few seconds; enumerating every process is the heaviest sample step.
            if ((DateTime.UtcNow - _lastProcSample).TotalSeconds >= 3)
            {
                _lastProcSample = DateTime.UtcNow;
                var top = _procs.Sample();
                if (top.Count > 0) lock (_gate) { _latestTop = top; _procSeq++; }
            }
            lock (_gate)
            {
                if (_latestTop.Count > 0)
                {
                    s.TopCpuProc = _latestTop[0].Name;
                    s.TopCpuProcPct = _latestTop[0].CpuPct;
                }
            }

            _analyzer.Classify(s);
            // Delivered performance: 100 when not throttled, dropping by the throttle severity, so a low
            // idle clock never reads as a slowdown and only a real throttle moves it off 100.
            s.DeliveredPct = s.IsThrottled ? Math.Max(0f, 100f - s.Severity) : 100f;
            UpdateEpisode(s);

            History.AddSample(s);
            Latest = s;
            SampleReady?.Invoke(this, s);
        }

        // ---- Episode state machine ------------------------------------------

        private void UpdateEpisode(Sample s)
        {
            lock (_gate)
            {
                if (s.IsThrottled)
                {
                    _consecClear = 0;
                    _consecThrottled++;
                    if (_builder == null && _consecThrottled >= OpenAfter)
                    {
                        _builder = new EpisodeBuilder(Epoch.FromLocal(s.Time) - (OpenAfter - 1));
                        Log.Info("Throttle episode opened: " + s.PrimaryCause + " (" + s.CauseDetail + ")");
                    }
                    _builder?.Add(s, _latestTop);
                }
                else
                {
                    _consecThrottled = 0;
                    if (_builder != null)
                    {
                        _consecClear++;
                        if (_consecClear >= CloseAfter) CloseEpisode();
                    }
                }
            }
        }

        private void CloseEpisode()
        {
            var ep = _builder.Build();
            _builder = null;
            History.AddEpisode(ep);
            Log.Info("Throttle episode closed: " + ep.DominantCause + " " + ep.Duration.TotalSeconds + "s top=" + ep.TopProcName);
            EpisodeClosed?.Invoke(this, ep);
        }

        /// <summary>Accumulates one throttle episode as it unfolds.</summary>
        private sealed class EpisodeBuilder
        {
            private readonly long _start;
            private long _end;
            private float _peakSev, _minDelivered = 100, _minClock = 100, _peakTemp = -1;
            private readonly int[] _secByCause = new int[6];
            private readonly float[] _peakSevByCause = new float[6];
            private readonly string[] _detailByCause = new string[6];
            private readonly Dictionary<string, int> _appSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, float> _appPeak = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            public EpisodeBuilder(long startUnix) { _start = startUnix; _end = startUnix; }

            public void Add(Sample s, List<ProcUsage> top)
            {
                _end = Epoch.FromLocal(s.Time);
                int cause = (int)s.PrimaryCause;
                if (s.Severity > _peakSev) _peakSev = s.Severity;
                // Keep the detail sentence from the worst moment of each cause, so the episode can say
                // exactly why it triggered (commit and free memory, GHz below base, disk latency).
                if (s.Severity > _peakSevByCause[cause]) { _peakSevByCause[cause] = s.Severity; _detailByCause[cause] = s.CauseDetail; }
                if (s.ProbeValid && s.DeliveredPct < _minDelivered) _minDelivered = s.DeliveredPct;
                if (s.ClockPct < _minClock) _minClock = s.ClockPct;
                if (s.TempC.HasValue && s.TempC.Value > _peakTemp) _peakTemp = s.TempC.Value;
                _secByCause[cause]++;
                if (!string.IsNullOrEmpty(s.TopCpuProc))
                {
                    _appSeconds.TryGetValue(s.TopCpuProc, out int sec); _appSeconds[s.TopCpuProc] = sec + 1;
                    _appPeak.TryGetValue(s.TopCpuProc, out float pk); if (s.TopCpuProcPct > pk) _appPeak[s.TopCpuProc] = s.TopCpuProcPct;
                }
            }

            private ThrottleCause Dominant()
            {
                int bestIdx = 0, bestVal = -1;
                for (int i = 1; i < _secByCause.Length; i++) if (_secByCause[i] > bestVal) { bestVal = _secByCause[i]; bestIdx = i; }
                return (ThrottleCause)bestIdx;
            }

            private void TopApp(out string name, out float cpu)
            {
                name = null; cpu = 0; int best = -1;
                foreach (var kv in _appSeconds) if (kv.Value > best) { best = kv.Value; name = kv.Key; }
                if (name != null) _appPeak.TryGetValue(name, out cpu);
            }

            public ThrottleEpisode Build()
            {
                TopApp(out string app, out float appCpu);
                int secCpu = _secByCause[(int)ThrottleCause.CpuThermal] + _secByCause[(int)ThrottleCause.CpuPower] + _secByCause[(int)ThrottleCause.CpuSaturated];
                return new ThrottleEpisode
                {
                    StartUnix = _start,
                    EndUnix = _end,
                    CauseCode = (int)Dominant(),
                    PeakSeverity = _peakSev,
                    MinDeliveredPct = _minDelivered,
                    MinClockPct = _minClock,
                    PeakTempC = _peakTemp,
                    TopProcName = app,
                    TopProcCpuPct = appCpu,
                    Detail = _detailByCause[(int)Dominant()] ?? "",
                    SecondsCpu = secCpu,
                    SecondsRam = _secByCause[(int)ThrottleCause.Ram],
                    SecondsDisk = _secByCause[(int)ThrottleCause.Disk]
                };
            }

            public ThrottleEpisode Snapshot()
            {
                var e = Build();
                return e;
            }
        }
    }
}
