using System;
using System.Collections.Generic;
using System.Diagnostics;
using ThrottleView.Models;

namespace ThrottleView.Services
{
    /// <summary>
    /// Works out which apps are driving load, so a throttle episode can name the cause
    /// ("Chrome held 71% of the CPU"). CPU share per process is the growth in its total processor
    /// time across the interval, divided by wall time times the logical core count. Processes are
    /// grouped by name, so the many chrome.exe workers read as one "chrome".
    /// </summary>
    public sealed class ProcessSampler
    {
        private sealed class Prev { public TimeSpan Cpu; public DateTime Seen; }

        private readonly Dictionary<int, Prev> _prev = new Dictionary<int, Prev>();
        private readonly int _logical;
        private readonly int _selfPid;
        private DateTime _lastTime = DateTime.MinValue;
        private const int TopN = 12;

        public ProcessSampler(int logicalCount)
        {
            _logical = Math.Max(1, logicalCount);
            try { _selfPid = Process.GetCurrentProcess().Id; } catch { _selfPid = -1; }
        }

        /// <summary>Top processes by CPU share this interval. Empty on the very first call.</summary>
        public List<ProcUsage> Sample()
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastTime).TotalSeconds;
            bool haveBaseline = _lastTime != DateTime.MinValue && elapsed >= 0.3;
            var byName = new Dictionary<string, ProcUsage>(StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<int>();

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch (Exception ex) { Log.Error("GetProcesses", ex); return new List<ProcUsage>(); }

            foreach (var p in procs)
            {
                try
                {
                    int pid = p.Id;
                    if (pid == 0 || pid == _selfPid) continue; // never count or blame the monitor itself
                    live.Add(pid);
                    TimeSpan cpu;
                    try { cpu = p.TotalProcessorTime; }
                    catch { continue; } // system / protected processes deny access
                    long ws = p.WorkingSet64;
                    string name = p.ProcessName;

                    float cpuPct = 0;
                    Prev prev;
                    if (haveBaseline && _prev.TryGetValue(pid, out prev))
                    {
                        double deltaSec = (cpu - prev.Cpu).TotalSeconds;
                        cpuPct = (float)(deltaSec / (elapsed * _logical) * 100.0);
                        if (cpuPct < 0) cpuPct = 0;
                        if (cpuPct > 100) cpuPct = 100;
                    }
                    _prev[pid] = new Prev { Cpu = cpu, Seen = now };

                    ProcUsage u;
                    if (!byName.TryGetValue(name, out u))
                    {
                        u = new ProcUsage { Name = name, Pid = pid };
                        byName[name] = u;
                    }
                    u.CpuPct += cpuPct;
                    u.WorkingSetBytes += ws;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            PrunePrev(live);
            _lastTime = now;

            var list = new List<ProcUsage>(byName.Values);
            if (!haveBaseline) return new List<ProcUsage>();
            list.Sort((a, b) => b.CpuPct.CompareTo(a.CpuPct));
            if (list.Count > TopN) list.RemoveRange(TopN, list.Count - TopN);
            return list;
        }

        private void PrunePrev(HashSet<int> live)
        {
            if (_prev.Count < 2048) // only sweep when the table grows
            {
                var dead = new List<int>();
                foreach (var kv in _prev) if (!live.Contains(kv.Key)) dead.Add(kv.Key);
                foreach (var pid in dead) _prev.Remove(pid);
            }
            else
            {
                _prev.Clear();
            }
        }
    }
}
