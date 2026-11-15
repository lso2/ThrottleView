# Changelog

## 1.2.2

- Relicensed under GPLv3, with LICENSE.txt installed next to the app.
- Window footer with the running version, GitHub, and Donate links.
- Fixed the effective clock reading on Intel hybrid CPUs.

## 1.2.1

- Lower CPU use while the Live view is not on screen.
- Lower overhead on machines with no readable temperature sensor.

## 1.2.0

- Status banner readouts for effective clock, top process, and temperature with its sensor source.
- Load and throttle gauges redesigned with green, amber, and red zones, tick marks, and a needle.
- Top processes list in the Live view, with CPU share and memory per app.
- Episode count, worst delivered performance, and peak temperature in the History summary.
- Sensor library loading from a full LibreHardwareMonitor release folder placed next to ThrottleView.exe.

## 1.1.0

- Per-user installer with no admin prompt, including a Windows startup entry.
- Uninstalling stops ThrottleView and leaves recorded history in place.
- Launches straight to the tray at Windows sign-in.
- Tray tooltip with the current throttle state, delivered performance, and clock speed.
- Start with Windows and Reset history in the tray menu.
- Only one ThrottleView instance runs at a time.
- Same-name processes grouped as one app.
- Window size saved between sessions.
- Sharp scaling on high-DPI and mixed-DPI monitors.

## 1.0.0

First release.

- System-tray app with a dark dashboard, built in C# / WPF on .NET Framework 4.8, no admin rights and no runtime install.
- Delivered clock read from the hardware `% Processor Performance` counter, so a clock held below base under load is measured, not guessed.
- Cause attribution into CPU thermal throttle, CPU power limit, CPU at capacity, memory pressure, and disk bottleneck, with a per-second severity and each episode recording the exact numbers behind it.
- Best-effort temperatures from the native Thermal Zone Information counter (no admin) and the WMI thermal zone, with an optional `LibreHardwareMonitorLib.dll` drop-in for accurate CPU, GPU, and storage readings when run elevated.
- Continuous top-process sampling so each throttle episode names the app most responsible, never the monitor itself.
- Live view: a CPU load meter and a throttle meter side by side, the current cause, metric cards, a 90-second multi-series graph, and a throttle timeline.
- History view: 1 hour, 24 hours, and 7 days, with percent of time throttled, a cause breakdown, a per-bucket bar chart, temperature over time, recent episodes, and the apps most involved.
- Minute-rollup persistence so days of history cost only kilobytes, plus a JSON episode log.
- Settings for every threshold and start-with-Windows, plus a tray restart-as-administrator for full sensor temperatures.
