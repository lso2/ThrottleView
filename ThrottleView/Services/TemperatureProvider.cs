using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;

namespace ThrottleView.Services
{
    /// <summary>
    /// Best-effort temperature. Two providers, in order of accuracy:
    ///
    ///   1. A drop-in sensor library (LibreHardwareMonitorLib.dll placed next to the exe). Loaded by
    ///      reflection so it is a soft dependency. It reports the real CPU package, GPU, and storage
    ///      temperatures, but its ring-0 driver only loads when the app runs elevated.
    ///   2. WMI MSAcpi_ThermalZoneTemperature (root\WMI). Always present in the API but frequently a
    ///      single motherboard zone rather than the CPU die, and on many laptops it returns nothing.
    ///      Value is tenths of a Kelvin, so Celsius = value/10 - 273.15.
    ///
    /// When neither yields a believable number, temperature is reported as unavailable and the
    /// analyzer attributes clock throttling to a power limit rather than guessing heat.
    /// </summary>
    public sealed class TemperatureProvider : IDisposable
    {
        public string Source { get; private set; }
        public float? LastCpuC { get; private set; }
        public float? LastGpuC { get; private set; }
        public float? LastStorageC { get; private set; }

        private ManagementObjectSearcher _wmi;
        private bool _wmiTried;

        private PerformanceCounter[] _tzCounters;
        private bool _tzTried;
        private bool _tzHighPrecision;

        // Reflection handles into the optional sensor library.
        private object _computer;
        private IEnumerable _hardware;
        private bool _libTried;
        private string _libDir;

        private DateTime _lastRead = DateTime.MinValue;
        private int _consecutiveFail;
        private const int CacheMs = 2500; // temperature moves slowly; avoid frequent WMI/sensor reads

        public TemperatureProvider() { Source = "Unavailable"; }

        public float? Read()
        {
            // Back off hard once temperature has proved unreadable, so a machine with no usable sensor
            // does not pay for a failing WMI query every couple of seconds forever.
            int cacheMs = _consecutiveFail >= 5 ? 30000 : CacheMs;
            if ((DateTime.UtcNow - _lastRead).TotalMilliseconds < cacheMs) return LastCpuC;
            _lastRead = DateTime.UtcNow;

            if (TryLibrary()) { _consecutiveFail = 0; return LastCpuC; }
            if (TryThermalZoneCounter()) { _consecutiveFail = 0; return LastCpuC; }
            if (TryWmi()) { _consecutiveFail = 0; return LastCpuC; }

            _consecutiveFail++;
            LastCpuC = LastGpuC = LastStorageC = null;
            Source = "Unavailable";
            return null;
        }

        // ---- Optional sensor library ----------------------------------------

        private bool TryLibrary()
        {
            try
            {
                if (!_libTried)
                {
                    _libTried = true;
                    InitLibrary();
                }
                if (_computer == null || _hardware == null) return false;

                float? cpu = null, gpu = null, storage = null;
                foreach (var hw in _hardware)
                {
                    if (hw == null) continue;
                    InvokeVoid(hw, "Update");
                    string hwType = GetProp(hw, "HardwareType")?.ToString() ?? "";
                    var sensors = GetProp(hw, "Sensors") as IEnumerable;
                    if (sensors == null) continue;
                    float? pick = PickTemperature(sensors);
                    if (pick == null) continue;
                    if (hwType.IndexOf("Cpu", StringComparison.OrdinalIgnoreCase) >= 0) cpu = Max(cpu, pick);
                    else if (hwType.IndexOf("Gpu", StringComparison.OrdinalIgnoreCase) >= 0) gpu = Max(gpu, pick);
                    else if (hwType.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0) storage = Max(storage, pick);
                }
                if (cpu == null && gpu == null && storage == null) return false;
                LastCpuC = cpu ?? gpu; LastGpuC = gpu; LastStorageC = storage;
                Source = "Sensor library";
                return LastCpuC != null;
            }
            catch (Exception ex) { Log.Error("temp library", ex); return false; }
        }

        private void InitLibrary()
        {
            string dll = FindSensorLib();
            if (dll == null) return;
            _libDir = Path.GetDirectoryName(dll);

            // Resolve the library's own dependencies (HidSharp and the System.* facades) from the folder
            // it lives in, so the whole downloaded LibreHardwareMonitor release folder works as-is.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromLibDir;

            var asm = Assembly.LoadFrom(dll);
            var tComputer = asm.GetType("LibreHardwareMonitor.Hardware.Computer");
            if (tComputer == null) return;
            _computer = Activator.CreateInstance(tComputer);
            SetProp(_computer, "IsCpuEnabled", true);
            SetProp(_computer, "IsGpuEnabled", true);
            SetProp(_computer, "IsStorageEnabled", true);
            InvokeVoid(_computer, "Open");
            _hardware = GetProp(_computer, "Hardware") as IEnumerable;
            Log.Info("Temperature: sensor library loaded from " + _libDir);
        }

        private System.Reflection.Assembly ResolveFromLibDir(object sender, ResolveEventArgs args)
        {
            try
            {
                if (_libDir == null) return null;
                string name = new AssemblyName(args.Name).Name + ".dll";
                string path = Path.Combine(_libDir, name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch { return null; }
        }

        // Look for LibreHardwareMonitorLib.dll next to the exe, in a LibreHardwareMonitor subfolder, or
        // in the project's LibreHardwareMonitor folder when running from a bin\x64 build during dev.
        private static string FindSensorLib()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                string[] candidates =
                {
                    Path.Combine(exeDir, "LibreHardwareMonitorLib.dll"),
                    Path.Combine(exeDir, "LibreHardwareMonitor", "LibreHardwareMonitorLib.dll"),
                    Path.Combine(exeDir, "..", "..", "..", "..", "LibreHardwareMonitor", "LibreHardwareMonitorLib.dll"),
                    Path.Combine(exeDir, "..", "..", "..", "LibreHardwareMonitor", "LibreHardwareMonitorLib.dll"),
                };
                foreach (var c in candidates)
                    if (File.Exists(c)) return Path.GetFullPath(c);
            }
            catch (Exception ex) { Log.Error("find sensor lib", ex); }
            return null;
        }

        private static float? PickTemperature(IEnumerable sensors)
        {
            float? packageTemp = null, maxTemp = null;
            foreach (var s in sensors)
            {
                if (s == null) continue;
                string type = GetProp(s, "SensorType")?.ToString() ?? "";
                if (!type.Equals("Temperature", StringComparison.OrdinalIgnoreCase)) continue;
                var val = GetProp(s, "Value");
                if (!(val is float f) || float.IsNaN(f) || f <= 0 || f > 120) continue;
                string name = GetProp(s, "Name")?.ToString() ?? "";
                if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0)
                    packageTemp = Max(packageTemp, f);
                maxTemp = Max(maxTemp, f);
            }
            return packageTemp ?? maxTemp;
        }

        // ---- WMI thermal zone -----------------------------------------------

        private bool TryWmi()
        {
            try
            {
                if (!_wmiTried)
                {
                    _wmiTried = true;
                    _wmi = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                }
                if (_wmi == null) return false;

                float? best = null;
                foreach (ManagementObject mo in _wmi.Get())
                {
                    var raw = mo["CurrentTemperature"];
                    if (raw == null) continue;
                    double tenthsKelvin = Convert.ToDouble(raw);
                    double c = tenthsKelvin / 10.0 - 273.15;
                    if (c > 5 && c < 110) best = Max(best, (float)c); // ignore the "0 C = unsupported" sentinel
                }
                if (best == null) return false;
                LastCpuC = best; LastGpuC = null; LastStorageC = null;
                Source = "ACPI thermal zone";
                return true;
            }
            catch (Exception ex) { Log.Error("temp wmi", ex); return false; }
        }

        // ---- Thermal Zone Information performance counter -------------------
        // Native, no admin needed, and present on many laptops where MSAcpi returns nothing. The
        // "High Precision Temperature" counter is in tenths of a Kelvin; the plain "Temperature"
        // counter is in whole Kelvin. Prefer a zone whose name mentions CPU, else the hottest zone.

        private bool TryThermalZoneCounter()
        {
            try
            {
                if (!_tzTried) { _tzTried = true; InitThermalZone(); }
                if (_tzCounters == null || _tzCounters.Length == 0) return false;

                float? cpu = null, best = null;
                foreach (var pc in _tzCounters)
                {
                    float raw;
                    try { raw = pc.NextValue(); } catch { continue; }
                    double c = _tzHighPrecision ? raw / 10.0 - 273.15 : raw - 273.15;
                    if (c <= 5 || c >= 110) continue;
                    best = Max(best, (float)c);
                    string inst = pc.InstanceName ?? "";
                    if (inst.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) cpu = Max(cpu, (float)c);
                }
                var pick = cpu ?? best;
                if (pick == null) return false;
                LastCpuC = pick; LastGpuC = null; LastStorageC = null;
                Source = "Thermal zone";
                return true;
            }
            catch (Exception ex) { Log.Error("temp thermal zone", ex); return false; }
        }

        private void InitThermalZone()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("Thermal Zone Information")) return;
                var cat = new PerformanceCounterCategory("Thermal Zone Information");
                string[] instances;
                try { instances = cat.GetInstanceNames(); } catch { return; }
                if (instances == null || instances.Length == 0) return;

                string counter = "High Precision Temperature";
                _tzHighPrecision = true;
                if (!cat.CounterExists(counter)) { counter = "Temperature"; _tzHighPrecision = false; }

                var list = new List<PerformanceCounter>();
                foreach (var inst in instances)
                {
                    try { var pc = new PerformanceCounter("Thermal Zone Information", counter, inst, true); pc.NextValue(); list.Add(pc); }
                    catch { }
                }
                _tzCounters = list.ToArray();
                Log.Info("Thermal zone counter: " + _tzCounters.Length + " zones via '" + counter + "'");
            }
            catch (Exception ex) { Log.Error("thermal zone init", ex); }
        }

        // ---- Reflection helpers ---------------------------------------------

        private static object GetProp(object o, string name)
        {
            try { return o.GetType().GetProperty(name)?.GetValue(o, null); }
            catch { return null; }
        }
        private static void SetProp(object o, string name, object value)
        {
            try { o.GetType().GetProperty(name)?.SetValue(o, value, null); } catch { }
        }
        private static void InvokeVoid(object o, string method)
        {
            try { o.GetType().GetMethod(method, Type.EmptyTypes)?.Invoke(o, null); } catch { }
        }
        private static float? Max(float? a, float? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return a.Value >= b.Value ? a : b;
        }

        public void Dispose()
        {
            try { _wmi?.Dispose(); } catch { }
            if (_tzCounters != null) foreach (var pc in _tzCounters) { try { pc.Dispose(); } catch { } }
            try { if (_computer != null) InvokeVoid(_computer, "Close"); } catch { }
        }
    }
}
