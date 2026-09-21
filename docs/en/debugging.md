# Debugging Tools Guide

> Debugging and diagnostic tools provided by Machine.

[中文](../zh/debugging.md) | [Back to Home](../en/README.md) | [Development Guide](mod-development.md)

## Table of Contents

1. [Log System](#1-log-system)
2. [Diagnostic Command](#2-diagnostic-command)
3. [Performance Profiler](#3-performance-profiler)
4. [Crash Reports](#4-crash-reports)
5. [Mod Manager](#5-mod-manager)
6. [Debugging Tips](#6-debugging-tips)

---

## 1. Log System

### Log Levels

| Level | Description | Unity Console | Default Output |
|-------|-------------|---------------|----------------|
| `ERROR` | Errors | ✅ Error | ✅ |
| `WARN` | Warnings | ✅ Warning | ✅ |
| `INFO` | Info | ✅ Log | ✅ |
| `DEBUG` | Debug | ❌ | ❌ |
| `TRACE` | Trace | ❌ | ❌ |

### Configure Log Level

Edit `Machine/config.json`:

```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

### Log Output Location

- **File**: `Machine/logs/Machine.log`
- **Rotation**: 5MB auto-rotation, keep last 5 files

### Use Logs in Mod

```csharp
using Machine.Core;

// Recommended: with module name
Log.Info("MyMod", "Initializing...");
Log.Debug("MyMod", "Variable value: " + x);
Log.Error("MyMod", "Failed to load: " + e.Message);

// Simple (in MachineModBase)
Log("Info message");
LogWarn("Warning message");
LogError("Error message");
```

---

## 2. Diagnostic Command

### /machine diag

Enter `/machine diag` in-game chat to output full diagnostic info:

- Loader version, API version, log level, update channel
- Loaded mods list with status
- Log statistics (count per level)
- Recent errors
- Performance report (init/avg/peak time per mod)
- Object pool statistics

---

## 3. Performance Profiler

### Overview

ModProfiler automatically records performance data for each mod:

- Initialization time
- Per-frame Update/FixedUpdate time
- Average/peak time
- Frame budget warning count
- Exception count

### Frame Budget

Default: **2ms per mod per frame**. Exceeding triggers warnings (throttled to avoid spam).

### View Performance Data

1. Enter `/machine diag` in-game
2. Check the `[PERF]` section

### Performance Fields

| Field | Description |
|-------|-------------|
| `init` | Initialization time (OnLoad+OnEnable) |
| `avg` | Average per-frame Update time |
| `peak` | Peak Update time |
| `[WARN:N]` | Frame budget warning count |
| `[TIMEOUT]` | Init timeout (>5s) |
| `[ERR:N]` | Exception count |

---

## 4. Crash Reports

### Auto-Generated

On game crash, Machine automatically generates:

```
Machine/logs/crash_report_YYYYMMDD_HHMMSS.txt
```

### Report Contents

1. Basic info: time, loader version, log level, context
2. Exception info: type, message, stack trace, inner exception
3. Mod list: all loaded mods with status
4. Log statistics: count per level
5. Recent errors: last 50 errors
6. Last 200 log lines

### Manual Generation

```csharp
try
{
    // Code that might crash
}
catch (Exception ex)
{
    string reportPath = Log.GenerateCrashReport(ex, "MyMod operation");
    Log.Error("Crash report saved to: " + reportPath);
}
```

---

## 5. Mod Manager

### Open

Click the `Mods` button in the main menu.

### Features

- View all installed mods
- Enable/disable mods
- View mod details (version, author, description, dependencies)
- View disable reasons (missing dependencies, version incompatibility, conflicts)

---

## 6. Debugging Tips

### Check if Mod Loaded

1. Check `Machine/logs/Machine.log`
2. Search for your mod ID
3. Expected output:
   ```
   mod [machine.mymod] manifest ok: My Mod v1.0.0
   mod [machine.mymod] My Mod v1.0.0 enabled=True
   code mod loaded: Machine.MyMod.MyModClass
   ```

### Common Load Failures

| Symptom | Cause | Fix |
|---------|-------|-----|
| Mod not in log | mod.json format error | Check JSON syntax |
| "assembly missing" | DLL path error | Check code.assemblies path in mod.json |
| "no IMachineMod found" | Main class name error | Check code.mainClass matches actual class |
| "OnLoad failed" | Code exception | Check stack trace, fix code |
| Mod disabled | Missing dependency/version/conflict | Check disable reason, install deps |

### Debug OnUpdate Performance

1. Enter `/machine diag` in-game
2. Check `[PERF]` section
3. Find mod with high avg/peak
4. Check OnUpdate for:
   - Per-frame object creation (use object pool)
   - Per-frame reflection calls (cache results)
   - Per-frame file I/O (event-driven or throttle)
   - Complex loops (optimize or reduce frequency)

### Debug Memory Leaks

1. Set `logLevel: "DEBUG"`
2. Check memory detection output in logs
3. Ensure OnDisable:
   - Unsubscribes all events
   - Destroys all created UI/GameObjects
   - Clears static references

### Debug UI Issues

1. Check Canvas `sortingOrder` is high enough
2. Check RectTransform anchors and position
3. Use `Log.Debug` to output UI element position and state
4. Ensure UI is destroyed in OnDisable to avoid residue

---

## More Resources

- [Mod Development Guide](mod-development.md)
- [API Reference](../api/README.md)
- [Example Mods](../../examples/)
- [FAQ](../en/README.md#faq)
