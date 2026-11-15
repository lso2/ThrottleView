using System;
using System.Globalization;
using ThrottleView.Models;

namespace ThrottleView.Services
{
    /// <summary>
    /// Turns one measured sample into a verdict: is the machine throttled, by what, and how hard.
    ///
    /// CPU clock throttle is the headline case. It only counts when there is real demand
    /// (CpuPct above the busy floor) yet the delivered clock sits below base (% Processor Performance
    /// under the threshold), because an idle chip clocking itself down is normal power saving rather
    /// than throttling. When the active probe is calibrated it raises severity if the measured
    /// slowdown agrees. Heat versus power is decided by temperature: hot reads as thermal, cool reads
    /// as a power or current limit, and no reading reports power limit with that uncertainty stated.
    /// </summary>
    public sealed class ThrottleAnalyzer
    {
        private readonly SettingsStore _settings;
        private static readonly string Deg = ((char)0x00B0).ToString();

        public ThrottleAnalyzer(SettingsStore settings) { _settings = settings; }

        public void Classify(Sample s)
        {
            var cfg = _settings.Current;

            ThrottleCause best = ThrottleCause.None;
            float bestSev = 0;
            string detail = "Running with headroom";

            // CPU clock throttle: real demand present, yet delivered clock is below base.
            bool busy = s.CpuPct >= cfg.CpuBusyPct;
            float belowBase = 100f - s.ClockPct;
            if (busy && s.ClockPct < cfg.ClockThrottleBelowPct && belowBase > 0)
            {
                float sev = Clamp(belowBase * 1.6f, 0, 100);
                if (s.ProbeValid)
                {
                    float probeDrop = 100f - s.DeliveredPct;
                    if (probeDrop > 0) sev = Math.Max(sev, Clamp(probeDrop * 1.25f, 0, 100));
                }
                bool hot = s.TempC.HasValue && s.TempC.Value >= cfg.TempHotC;
                ThrottleCause cause = hot ? ThrottleCause.CpuThermal : ThrottleCause.CpuPower;
                string d = s.EffectiveGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz, "
                           + Math.Round(belowBase) + "% below base";
                if (s.TempC.HasValue) d += ", " + Math.Round(s.TempC.Value) + Deg + "C";
                d += hot ? ", thermal limit"
                         : (s.TempC.HasValue ? ", power or current limit" : ", power limit (temp n/a)");
                Consider(cause, sev, d, ref best, ref bestSev, ref detail);
            }

            // CPU saturation: cores pinned near full, work is queueing.
            if (s.CpuPct >= cfg.CpuSaturatedPct)
            {
                float sev = Clamp((s.CpuPct - (float)cfg.CpuSaturatedPct) / Math.Max(1f, 100f - (float)cfg.CpuSaturatedPct) * 50f + 48f, 0, 100);
                string who = string.IsNullOrEmpty(s.TopCpuProc) ? "" : ", " + s.TopCpuProc + " " + Math.Round(s.TopCpuProcPct) + "%";
                string d = "All cores busy (" + Math.Round(s.CpuPct) + "%)" + who;
                Consider(ThrottleCause.CpuSaturated, sev, d, ref best, ref bestSev, ref detail);
            }

            // RAM pressure: only when memory is actually scarce (commit high or little physical RAM
            // free). Hard paging raises severity but never triggers on its own, because normal code
            // and memory-mapped page-ins also show up as Pages Input/sec and must not be mistaken for
            // pressure on a machine with gigabytes free.
            bool commitHigh = s.CommitPct >= cfg.CommitHighPct;
            bool availLow = s.AvailMB > 0 && s.AvailMB <= cfg.AvailLowMB;
            bool paging = s.PagesInput >= cfg.PagesInputHigh;
            if (commitHigh || availLow)
            {
                float over = Math.Max(s.CommitPct - (float)cfg.CommitHighPct, 0);
                float sev = Clamp(over / Math.Max(1f, 100f - (float)cfg.CommitHighPct) * 55f + 30f, 0, 100);
                if (availLow) sev = Math.Max(sev, 45f);
                if (paging) sev = Math.Max(sev, Clamp((float)(s.PagesInput / (cfg.PagesInputHigh * 4) * 35.0) + 45f, 0, 100));
                string d = "Commit " + Math.Round(s.CommitPct) + "%, " + (s.AvailMB / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " GB free";
                if (paging) d += ", " + Math.Round(s.PagesInput) + " hard faults/s";
                Consider(ThrottleCause.Ram, sev, d, ref best, ref bestSev, ref detail);
            }

            // Disk bottleneck: high transfer latency while the disk is busy or its queue is deep.
            bool diskBusy = s.DiskActivePct >= cfg.DiskBusyPct || s.DiskQueue >= cfg.DiskQueueHigh;
            if (s.DiskLatencyMs >= cfg.DiskLatencyHighMs && diskBusy)
            {
                float sev = Clamp((s.DiskLatencyMs - (float)cfg.DiskLatencyHighMs) / (float)cfg.DiskLatencyHighMs * 50f + 42f, 0, 100);
                string d = "Disk " + Math.Round(s.DiskActivePct) + "% busy, " + Math.Round(s.DiskLatencyMs) + " ms latency, queue " + s.DiskQueue.ToString("0.0", CultureInfo.InvariantCulture);
                Consider(ThrottleCause.Disk, sev, d, ref best, ref bestSev, ref detail);
            }

            s.PrimaryCause = best;
            s.Severity = best == ThrottleCause.None ? 0 : bestSev;
            s.CauseDetail = detail;
        }

        // Keep the strongest cause. On a near-tie, prefer a genuine clock throttle over plain
        // saturation, since "held below base" is more actionable than "fully used".
        private static void Consider(ThrottleCause cause, float sev, string detail,
            ref ThrottleCause best, ref float bestSev, ref string bestDetail)
        {
            if (sev <= 12f) return;
            bool win = sev > bestSev + 0.001f;
            if (!win && Math.Abs(sev - bestSev) <= 6f && IsClock(cause) && !IsClock(best)) win = true;
            if (win) { best = cause; bestSev = sev; bestDetail = detail; }
        }

        private static bool IsClock(ThrottleCause c)
        {
            return c == ThrottleCause.CpuThermal || c == ThrottleCause.CpuPower;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (float.IsNaN(v) || v < lo) return lo;
            return v > hi ? hi : v;
        }
    }
}
