using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ThrottleView.Services
{
    /// <summary>One read of the Windows performance counters that matter for throttling.</summary>
    public struct MetricsReading
    {
        public float CpuPct;          // % Processor Time (demand), 0..100
        public float ClockPct;        // % Processor Performance: 100 = base clock, >100 turbo, <100 below base
        public float BaseGhz;
        public float EffectiveGhz;    // BaseGhz * ClockPct / 100

        public float RamUsedPct;
        public float CommitPct;
        public float AvailMB;
        public float TotalMB;
        public float PagesInput;      // hard-fault reads/sec

        public float DiskActivePct;
        public float DiskLatencyMs;
        public float DiskQueue;
    }

    /// <summary>
    /// Wraps the performance counters into one cheap Read(). The throttle signal is
    /// "% Processor Performance": Windows reports the delivered clock as a percentage of the rated
    /// base, so a value under 100 while the CPU is busy means the chip is being held below base by a
    /// thermal or power limit. Every counter read is guarded; a missing counter degrades to zero
    /// rather than throwing.
    /// </summary>
    public sealed class PerfMetrics : IDisposable
    {
        private PerformanceCounter _cpuTime, _cpuPerf;
        private PerformanceCounter _memCommit, _memAvail, _memPagesIn;
        private PerformanceCounter _diskTime, _diskQueue, _diskLatency;

        public float BaseGhz { get; private set; }
        public int LogicalCount { get; private set; }
        public bool CpuPerfAvailable { get; private set; }

        public PerfMetrics()
        {
            LogicalCount = Environment.ProcessorCount;
            BaseGhz = ReadBaseMhz() / 1000f;

            _cpuTime = TryCounter("Processor Information", "% Processor Time", "_Total")
                       ?? TryCounter("Processor", "% Processor Time", "_Total");
            _cpuPerf = TryCounter("Processor Information", "% Processor Performance", "_Total");
            CpuPerfAvailable = _cpuPerf != null;

            _memCommit = TryCounter("Memory", "% Committed Bytes In Use", null);
            _memAvail = TryCounter("Memory", "Available MBytes", null);
            _memPagesIn = TryCounter("Memory", "Pages Input/sec", null);

            _diskTime = TryCounter("PhysicalDisk", "% Disk Time", "_Total");
            _diskQueue = TryCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total");
            _diskLatency = TryCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");

            Prime();
        }

        private static PerformanceCounter TryCounter(string cat, string name, string instance)
        {
            try
            {
                var c = instance == null
                    ? new PerformanceCounter(cat, name, true)
                    : new PerformanceCounter(cat, name, instance, true);
                c.NextValue(); // prime; first call always returns 0
                return c;
            }
            catch (Exception ex) { Log.Error("counter " + cat + "\\" + name, ex); return null; }
        }

        // Discard one reading so rate counters have a baseline interval before the first real Read.
        private void Prime()
        {
            Read();
            System.Threading.Thread.Sleep(120);
        }

        public MetricsReading Read()
        {
            var r = new MetricsReading();
            r.BaseGhz = BaseGhz;
            r.CpuPct = Clamp(Next(_cpuTime), 0, 100);
            r.ClockPct = _cpuPerf != null ? Math.Max(0, Next(_cpuPerf)) : 100f;
            r.EffectiveGhz = BaseGhz > 0 ? BaseGhz * r.ClockPct / 100f : 0f;

            r.CommitPct = Clamp(Next(_memCommit), 0, 100);
            r.AvailMB = Math.Max(0, Next(_memAvail));
            r.PagesInput = Math.Max(0, Next(_memPagesIn));

            ReadPhysicalMemory(ref r);

            r.DiskActivePct = Clamp(Next(_diskTime), 0, 100);
            r.DiskQueue = Math.Max(0, Next(_diskQueue));
            r.DiskLatencyMs = Math.Max(0, Next(_diskLatency)) * 1000f; // seconds -> ms
            return r;
        }

        private static float Next(PerformanceCounter c)
        {
            if (c == null) return 0f;
            try { return c.NextValue(); }
            catch (Exception ex) { Log.Error("counter next", ex); return 0f; }
        }

        private static void ReadPhysicalMemory(ref MetricsReading r)
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref m))
                {
                    r.RamUsedPct = m.dwMemoryLoad;
                    r.TotalMB = m.ullTotalPhys / (1024f * 1024f);
                    if (r.AvailMB <= 0) r.AvailMB = m.ullAvailPhys / (1024f * 1024f);
                }
            }
            catch (Exception ex) { Log.Error("GlobalMemoryStatusEx", ex); }
        }

        // Base is the frequency Windows measures % Processor Performance against. The registry ~MHz
        // value runs above base on Intel hybrid CPUs and is the last fallback.
        private static int ReadBaseMhz()
        {
            try
            {
                using (var c = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true))
                {
                    int mhz = (int)c.NextValue();
                    if (mhz > 0) return mhz;
                }
            }
            catch (Exception ex) { Log.Error("base mhz counter", ex); }

            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(
                    "SELECT MaxClockSpeed FROM Win32_Processor"))
                foreach (var o in s.Get())
                {
                    var v = o["MaxClockSpeed"];
                    if (v != null && Convert.ToInt32(v) > 0) return Convert.ToInt32(v);
                }
            }
            catch (Exception ex) { Log.Error("base mhz wmi", ex); }

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                {
                    var v = key?.GetValue("~MHz");
                    if (v is int mhz && mhz > 0) return mhz;
                }
            }
            catch (Exception ex) { Log.Error("base mhz registry", ex); }
            return 0;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (float.IsNaN(v) || v < lo) return lo;
            return v > hi ? hi : v;
        }

        public void Dispose()
        {
            foreach (var c in new[] { _cpuTime, _cpuPerf, _memCommit, _memAvail, _memPagesIn, _diskTime, _diskQueue, _diskLatency })
                try { c?.Dispose(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
