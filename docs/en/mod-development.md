# Mod Development Guide

> From zero to publish, get your first mod running in 30 minutes.

[中文](../zh/mod-development.md) | [Back to Home](../../README.md) | [API Reference](../api/README.md)

## Table of Contents

1. [Environment Setup](#1-environment-setup)
2. [Create Your First Mod](#2-create-your-first-mod)
3. [Directory Structure](#3-directory-structure)
4. [mod.json Fields](#4-modjson-fields)
5. [Lifecycle](#5-lifecycle)
6. [Event System](#6-event-system)
7. [UI Development](#7-ui-development)
8. [Config System](#8-config-system)
9. [Content Registration](#9-content-registration)
10. [Debugging](#10-debugging)
11. [Packaging & Publishing](#11-packaging--publishing)
12. [Best Practices](#12-best-practices)

---

## 1. Environment Setup

### Requirements

- **Windows 10/11**
- **Aviassembly** (Steam)
- **.NET Framework 4.x**
- **C# Compiler** (`csc.exe`)
- **Text Editor** (VS Code or Visual Studio recommended)

### Verify Environment

```powershell
# Check csc compiler
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /?

# Check game directory
Test-Path "D:\steam\steamapps\common\Aviassembly"
```

---

## 2. Create Your First Mod

### Method 1: Template Generator (Recommended)

```powershell
cd D:\豆包的下载\Machine_Dev
.\newmod.ps1 HelloWorld
```

### Method 2: Manual

#### Step 1: Create Directory

```
Machine/mods/machine.helloworld/
├── mod.json
├── config.json
└── code/
    └── HelloWorld.dll
```

#### Step 2: Create mod.json

```json
{
  "id": "machine.helloworld",
  "name": "Hello World",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "My first Machine Mod",
  "type": "code",
  "apiVersion": "1.0",
  "loaderVersion": ">=2.0.0",
  "dependencies": [],
  "entry": "Machine.HelloWorld.HelloWorldMod",
  "code": {
    "assemblies": ["code/HelloWorld.dll"],
    "mainClass": "Machine.HelloWorld.HelloWorldMod"
  },
  "content": {
    "cargo": [],
    "parts": [],
    "decals": [],
    "textures": []
  }
}
```

#### Step 3: Create Main Class

```csharp
using System;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Machine.HelloWorld
{
    public class HelloWorldMod : MachineModBase
    {
        public override string Id { get { return "machine.helloworld"; } }
        public override string Name { get { return "Hello World"; } }
        public override string Version { get { return "1.0.0"; } }

        private int _frameCount;

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);
            Log("Hello World! Mod loaded.");
        }

        public override void OnEnable()
        {
            base.OnEnable();
            SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
        }

        public override void OnUpdate()
        {
            _frameCount++;
            if (_frameCount % 300 == 0)
                Log("Hello World! Frame count: " + _frameCount);
        }

        public override void OnDisable()
        {
            UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
            base.OnDisable();
        }

        private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
        {
            Log("Scene loaded: " + e.SceneName);
        }
    }
}
```

#### Step 4: Compile

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$game = "D:\steam\steamapps\common\Aviassembly\Aviassembly_Data\Managed"

& $csc /target:library /out:Machine/mods/machine.helloworld/code/HelloWorld.dll `
    /reference:"$game\Assembly-CSharp.dll" `
    /reference:"$game\Machine.Core.dll" `
    /reference:"$game\UnityEngine.dll" `
    /reference:"$game\UnityEngine.CoreModule.dll" `
    HelloWorld.cs
```

#### Step 5: Test

1. Launch the game
2. Click `Mods` button in main menu
3. Confirm `Hello World` is enabled
4. Check logs at `Machine/logs/Machine.log`

---

## 3. Directory Structure

```
mods/machine.mymod/
├── mod.json              # Mod manifest (required)
├── config.json           # Mod config (optional)
├── README.md             # Mod documentation (recommended)
├── changelog.md          # Changelog (recommended)
├── code/                 # Compiled DLLs
│   └── MyMod.dll
├── content/              # Content files
│   ├── cargo/            # Cargo definitions
│   ├── parts/            # Part definitions
│   ├── decals/           # Decal definitions
│   └── textures/         # Texture files
├── assets/               # Asset files
│   ├── audio/            # Audio files
│   ├── models/           # Model files
│   └── ui/               # UI resources
└── localization/         # Multi-language files
    ├── en.json
    └── zh.json
```

---

## 4. mod.json Fields

### Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `id` | string | Unique mod ID, e.g. `machine.radar` |
| `name` | string | Display name |
| `version` | string | Semantic version, e.g. `1.2.0` |
| `type` | string | Mod type, currently `code` |
| `code.assemblies` | array | DLL file paths (relative to mod dir) |
| `code.mainClass` | string | Main class full name |

### Recommended Fields

| Field | Type | Description |
|-------|------|-------------|
| `author` | string | Author name |
| `description` | string | Mod description |
| `apiVersion` | string | API version, currently `1.0` |
| `loaderVersion` | string | Loader version requirement, e.g. `>=2.0.0` |
| `entry` | string | Entry class (same as code.mainClass) |

### Dependencies & Load Order

| Field | Type | Description |
|-------|------|-------------|
| `dependencies` | array | Required dependencies |
| `optionalDependencies` | array | Optional dependencies |
| `conflicts` | array | Conflicting mods |
| `loadBefore` | array | Mods to load before this one |
| `loadAfter` | array | Mods to load after this one |

### Version Range Syntax

| Format | Description | Example |
|--------|-------------|---------|
| `>=1.0.0` | Greater than or equal | `>=2.0.0` |
| `>1.0.0` | Greater than | `>1.5.0` |
| `<=1.0.0` | Less than or equal | `<=3.0.0` |
| `<1.0.0` | Less than | `<2.0.0` |
| `=1.0.0` | Equal | `=1.2.0` |
| `!=1.0.0` | Not equal | `!=1.0.0` |
| `>=1.0.0 <2.0.0` | Combined | `>=1.0.0 <2.0.0` |

---

## 5. Lifecycle

```
OnLoad(api)       ← Mod loading: register content, read config
    ↓
OnEnable()        ← Mod enabled: subscribe events, create UI
    ↓
OnUpdate()        ← Every frame (60fps)
OnFixedUpdate()   ← Fixed timestep (physics, 50fps)
    ↓
OnDisable()       ← Mod disabled: unsubscribe, destroy UI
    ↓
OnUnload()        ← Mod unloaded: cleanup resources
```

See the [Chinese version](../zh/mod-development.md#5-生命周期) for detailed examples.

---

## 6. Event System

### Event Types

| Event | Trigger | Parameter |
|-------|---------|-----------|
| `GameStart` | Game launch | None |
| `SceneLoaded` | Scene loaded | `SceneName` |
| `SceneUnloading` | Before scene unload | `SceneName` |
| `PlayerSpawn` | Player aircraft spawn | None |
| `Tick` | Every frame | `DeltaTime` |
| `FixedTick` | Fixed timestep | `DeltaTime` |
| `FlightStart` | Flight starts | None |
| `FlightEnd` | Flight ends | None |
| `GameEnd` | Game exit | None |
| `ModsLoaded` | All mods loaded | None |
| `SaveLoaded` | Save loaded | None |

### Subscribe/Unsubscribe

```csharp
public override void OnEnable()
{
    base.OnEnable();
    SubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
}

private void OnSceneLoaded(MachineEventBus.MachineEventArgs e)
{
    Log("Scene loaded: " + e.SceneName);
}

public override void OnDisable()
{
    UnsubscribeEvent(MachineEventBus.EventType.SceneLoaded, OnSceneLoaded);
    base.OnDisable();
}
```

---

## 7. UI Development

### Add Main Menu Button

```csharp
public override void OnEnable()
{
    base.OnEnable();
    if (Api != null)
        Api.AddMainMenuButton("My Mod", OnMyButtonClicked);
}

private void OnMyButtonClicked()
{
    Log("Button clicked!");
}
```

### Create Custom Window

See the [AddButton example](../../examples/AddButton/) for a complete example.

---

## 8. Config System

### Config File

Each mod can have its own `config.json` in the mod directory:

```json
{
  "enabled": true,
  "mySetting": "value",
  "myNumber": 42,
  "myFloat": 3.14
}
```

### Read Config

```csharp
[Serializable]
private class MyModConfig
{
    public bool enabled = true;
    public string mySetting = "default";
    public int myNumber = 42;
}

private MyModConfig _config;

private void LoadConfig()
{
    try
    {
        string modDir = Api.GetModsDirectory();
        string configPath = Path.Combine(modDir, "machine.mymod", "config.json");

        if (!File.Exists(configPath))
        {
            _config = new MyModConfig();
            File.WriteAllText(configPath, JsonUtility.ToJson(_config, true));
            return;
        }

        string json = File.ReadAllText(configPath);
        _config = JsonUtility.FromJson<MyModConfig>(json);
    }
    catch (Exception ex)
    {
        LogWarn("Config load failed: " + ex.Message);
        _config = new MyModConfig();
    }
}
```

---

## 9. Content Registration

### Register Cargo

```csharp
var cargo = new CargoDefinition();
cargo.Id = "mymod.specialcargo";
cargo.Name = "Special Cargo";
cargo.Price = 500f;
cargo.Weight = 10f;
cargo.CargoSpace = 3;
api.RegisterCargo(cargo);
```

### Register Part

```csharp
var part = new PartDefinition();
part.Id = "mymod.specialpart";
part.Name = "Special Part";
part.Weight = 5f;
api.RegisterPart(part);
```

---

## 10. Debugging

### Log Output

```csharp
// Recommended: with module name
Log.Info("MyMod", "Initializing...");
Log.Debug("MyMod", "Variable: " + x);
Log.Error("MyMod", "Failed: " + e.Message);
```

### Diagnostic Command

Enter `/machine diag` in-game chat to view:
- Loader version, API version, log level
- Loaded mods list with status
- Performance report (init/avg/peak time per mod)
- Object pool statistics

### Crash Reports

Auto-generated at `Machine/logs/crash_report_YYYYMMDD_HHmmss.txt`, containing:
- Loader version, mod list
- Exception info and stack trace
- Last 200 log lines

See the [Debugging Guide](debugging.md) for more details.

---

## 11. Packaging & Publishing

### Pre-publish Checklist

- [ ] `mod.json` fields complete and correct
- [ ] Version number updated (semantic versioning)
- [ ] Config files have reasonable defaults
- [ ] README.md with features and usage
- [ ] CHANGELOG.md with updates
- [ ] Dependencies declared correctly
- [ ] Conflicts declared correctly
- [ ] Tested in clean environment
- [ ] No obvious performance issues (`/machine diag`)

### Package Structure

```
mymod-v1.0.0.zip
└── machine.mymod/
    ├── mod.json
    ├── config.json
    ├── README.md
    ├── CHANGELOG.md
    ├── code/
    │   └── MyMod.dll
    └── content/
        └── ...
```

### Installation

Players extract the ZIP to `Aviassembly/Machine/mods/`.

---

## 12. Best Practices

### Performance

1. **Lightweight OnUpdate**: Avoid heavy operations, frame budget 2ms
2. **Use Object Pools**: For frequently created/destroyed objects
3. **Cache Reflection**: Don't call GetField/GetMethod every frame
4. **Throttle Logs**: Use Log.Debug() or counters for high-frequency logs
5. **Lazy Init**: Non-critical content init after scene load

### Stability

1. **Exception Isolation**: Wrap external calls in try/catch
2. **Symmetric Cleanup**: Create in OnEnable, destroy in OnDisable
3. **Null Checks**: Check null before accessing game objects
4. **Config Fault Tolerance**: Use defaults on config load failure
5. **Version Compatibility**: Use loaderVersion to declare minimum version

### Maintainability

1. **Modular Design**: Single responsibility, avoid god classes
2. **Config Driven**: Tunable parameters in config.json
3. **Sufficient Logging**: Log key nodes for debugging
4. **Good Documentation**: README, CHANGELOG, code comments
5. **Rich Examples**: Provide minimal runnable examples

---

## Next Steps

- Read the [API Reference](../api/README.md) for complete API
- Check [Example Mods](../../examples/) for practical usage
- Join the community and share your mods!

---

**Questions?** See the [FAQ](../../README.md#faq) or open a GitHub Issue.
