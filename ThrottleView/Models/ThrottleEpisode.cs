using System;
using System.Runtime.Serialization;

namespace ThrottleView.Models
{
    /// <summary>
    /// A continuous stretch of throttling, from the first throttled second to the last before
    /// recovery. One row in the history list: when it happened, how long, the dominant cause, how
    /// hard it bit (peak severity and worst delivered performance), and which app was most
    /// responsible. Persisted one-per-line as JSON in episodes.jsonl.
    /// </summary>
    [DataContract]
    public sealed class ThrottleEpisode
    {
        [DataMember(Name = "start")] public long StartUnix;
        [DataMember(Name = "end")] public long EndUnix;
        [DataMember(Name = "cause")] public int CauseCode;
        [DataMember(Name = "peakSev")] public float PeakSeverity;
        [DataMember(Name = "minDelivered")] public float MinDeliveredPct;
        [DataMember(Name = "minClock")] public float MinClockPct;
        [DataMember(Name = "peakTemp")] public float PeakTempC;     // -1 when no temperature was read
        [DataMember(Name = "topProc")] public string TopProcName;
        [DataMember(Name = "topProcCpu")] public float TopProcCpuPct;
        [DataMember(Name = "detail")] public string Detail;     // why it triggered, from the worst moment
        [DataMember(Name = "secCpu")] public int SecondsCpu;        // seconds attributed to each family
        [DataMember(Name = "secRam")] public int SecondsRam;
        [DataMember(Name = "secDisk")] public int SecondsDisk;

        public ThrottleCause DominantCause { get { return (ThrottleCause)CauseCode; } set { CauseCode = (int)value; } }
        public DateTime Start { get { return Epoch.ToLocal(StartUnix); } }
        public DateTime End { get { return Epoch.ToLocal(EndUnix); } }
        public TimeSpan Duration { get { return TimeSpan.FromSeconds(Math.Max(1, EndUnix - StartUnix + 1)); } }
        public bool HasTemp { get { return PeakTempC > -0.5f; } }
    }

    /// <summary>Unix-second helpers shared by the history records.</summary>
    public static class Epoch
    {
        private static readonly DateTime Origin = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long Now() { return (long)(DateTime.UtcNow - Origin).TotalSeconds; }
        public static long FromLocal(DateTime local) { return (long)(local.ToUniversalTime() - Origin).TotalSeconds; }
        public static DateTime ToLocal(long unix) { return Origin.AddSeconds(unix).ToLocalTime(); }
    }
}
