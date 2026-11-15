# Changelog

## 1.2.2

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

- System-tray app with a dark dashboard that runs without admin rights or a runtime install.
- Delivered CPU clock measured against the rated base clock.
- Every second classified by severity and cause: CPU thermal throttle, CPU power limit, CPU at capacity, memory pressure, or disk bottleneck.
- Throttle episodes that include the readings behind each one and the app most responsible.
- Temperatures from the Windows thermal zones, plus accurate CPU readings from an optional `LibreHardwareMonitorLib.dll` when run as administrator.
- Live view of CPU load and throttle meters, the current cause, metric cards, a 90-second graph, and a throttle timeline.
- History view for 1 hour, 24 hours, and 7 days, showing time throttled, cause breakdown, temperature trend, recent episodes, and the apps most involved.
- Compact on-disk history and a JSON episode log.
- Settings for start-with-Windows and the throttle thresholds.
- Restart as administrator in the tray menu.
