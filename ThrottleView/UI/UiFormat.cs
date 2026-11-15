using System;
using System.Windows.Media;
using ThrottleView.Models;

namespace ThrottleView.UI
{
    /// <summary>Colors and string formatting shared across the views. One source of truth so the
    /// graph, the cards, the timeline, and the history list all speak the same visual language.</summary>
    public static class UiFormat
    {
        // Palette (kept in sync with Themes/Theme.Dark.xaml).
        public static readonly Color Ok = Hex(0x3FB96D);
        public static readonly Color Warn = Hex(0xE8B03F);
        public static readonly Color Danger = Hex(0xE05A5A);
        public static readonly Color Cpu = Hex(0x4DA3FF);
        public static readonly Color Delivered = Hex(0x36C6B0);
        public static readonly Color Ram = Hex(0xB06CF0);
        public static readonly Color Disk = Hex(0xE0A93C);
        public static readonly Color Temp = Hex(0xF06C8A);
        public static readonly Color Grid = Hex(0x2C313B);
        public static readonly Color TextSecondary = Hex(0x9AA3B2);
        public static readonly Color Track = Hex(0x2A2F39);
        public static readonly Color Panel = Hex(0x1C1F26); // matches the card background, for the gauge hub cap
        public static readonly Color TextOnFill = Hex(0x14161A); // dark text on solid cause-colored chips

        public static string CauseShort(ThrottleCause c)
        {
            switch (c)
            {
                case ThrottleCause.CpuThermal: return "CPU heat";
                case ThrottleCause.CpuPower: return "CPU power";
                case ThrottleCause.CpuSaturated: return "CPU maxed";
                case ThrottleCause.Ram: return "RAM";
                case ThrottleCause.Disk: return "Disk";
                default: return "OK";
            }
        }

        public static string CauseLabel(ThrottleCause c)
        {
            switch (c)
            {
                case ThrottleCause.CpuThermal: return "CPU thermal throttle";
                case ThrottleCause.CpuPower: return "CPU power limit";
                case ThrottleCause.CpuSaturated: return "CPU at capacity";
                case ThrottleCause.Ram: return "Memory pressure";
                case ThrottleCause.Disk: return "Disk bottleneck";
                default: return "No throttling";
            }
        }

        public static Color CauseColor(ThrottleCause c)
        {
            switch (c)
            {
                case ThrottleCause.CpuThermal: return Hex(0xE0573C);
                case ThrottleCause.CpuPower: return Cpu;
                case ThrottleCause.CpuSaturated: return Hex(0x7C83FF);
                case ThrottleCause.Ram: return Ram;
                case ThrottleCause.Disk: return Disk;
                default: return Ok;
            }
        }

        public static SolidColorBrush CauseBrush(ThrottleCause c) { return Frozen(CauseColor(c)); }

        // Green when delivering near peak, amber in the middle, red when badly throttled.
        public static Color HealthColor(double deliveredPct)
        {
            if (deliveredPct >= 90) return Ok;
            if (deliveredPct >= 75) return Lerp(Warn, Ok, (deliveredPct - 75) / 15.0);
            if (deliveredPct >= 55) return Lerp(Danger, Warn, (deliveredPct - 55) / 20.0);
            return Danger;
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            if (t.TotalMinutes >= 1) return t.Minutes + "m " + t.Seconds + "s";
            return t.Seconds + "s";
        }

        public static string Gb(double mb) { return (mb / 1024.0).ToString("0.0") + " GB"; }

        public static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color Hex(int rgb)
        {
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        public static Color WithA(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public static Color Lerp(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }
    }
}
