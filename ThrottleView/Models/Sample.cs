using System;

namespace ThrottleView.Models
{
    /// <summary>
    /// What is holding the machine back at a moment in time. The two CPU clock causes are split so
    /// the dashboard can say not just "CPU throttled" but whether heat or a power/current limit is
    /// the reason, which is the practical difference between "improve cooling" and "raise power limits".
    /// </summary>
    public enum ThrottleCause
    {
        None = 0,
        CpuThermal = 1,   // delivered clock below base while the package is hot
        CpuPower = 2,     // delivered clock below base, not hot: power / current (PL1/PL2/EDP) limit
        CpuSaturated = 3, // cores pinned near 100%: not enough CPU, work is queueing
        Ram = 4,          // commit pressure and hard paging to disk
        Disk = 5          // storage latency / queue is the bottleneck
    }

    /// <summary>One process's contribution to load during a sample.</summary>
    public sealed class ProcUsage
    {
        public string Name;
        public int Pid;
        public float CpuPct;        // share of total machine CPU, 0..100
        public long WorkingSetBytes;
    }

    /// <summary>
    /// One second of measured state. Built by <c>Monitor</c> from the performance counters, the
    /// active probe, temperature, and the top-process sampler, then classified by the analyzer.
    /// </summary>
    public sealed class Sample
    {
        public DateTime Time;

        // CPU clock and load.
        public float CpuPct;          // % Processor Time, 0..100 (demand)
        public float ClockPct;        // % Processor Performance: 100 = base, >100 turbo, <100 below base
        public float BaseGhz;         // rated/base frequency
        public float EffectiveGhz;    // BaseGhz * ClockPct / 100

        // Active probe: directly measured delivered throughput vs this machine's calibrated peak.
        public bool ProbeValid;
        public float DeliveredPct;    // 100 = at calibrated peak; lower = a real, observed slowdown

        // Memory.
        public float RamUsedPct;      // physical RAM in use, 0..100
        public float CommitPct;       // % Committed Bytes In Use
        public float AvailMB;
        public float PagesInput;      // Pages Input/sec: hard-fault reads from disk (paging pressure)

        // Disk (busiest physical disk).
        public float DiskActivePct;   // % Disk Time
        public float DiskLatencyMs;   // Avg. Disk sec/Transfer, in ms
        public float DiskQueue;       // Avg. Disk Queue Length

        // Temperature (best-effort; null when no sensor is readable).
        public float? TempC;
        public string TempSource;     // "Sensor library", "ACPI thermal zone", or "Unavailable"

        // Classification (filled by ThrottleAnalyzer).
        public ThrottleCause PrimaryCause;
        public float Severity;        // 0..100 strength of the primary cause
        public string CauseDetail;    // human sentence, e.g. "1.2 GHz, 38% below base, 94 C"

        // Top offender this second (max single-process CPU share).
        public string TopCpuProc;
        public float TopCpuProcPct;

        public bool IsThrottled { get { return PrimaryCause != ThrottleCause.None; } }
    }
}
