# Machine Mod Loader

> Mod loader for Aviassembly, similar to Forge/Fabric for Minecraft.

[中文](../zh/README.md) | [Development Guide](mod-development.md) | [API Reference](../api/README.md)

## Introduction

Machine is a mod loader for Aviassembly that provides:

- **Mod Management**: Enable/disable mods, dependency and conflict detection
- **Content Extension**: Add new cargo, parts, textures, audio, aircraft models
- **Gameplay Extension**: New mechanics, physics, multiplayer, AI factions
- **Developer API**: Event bus, lifecycle, UI injection, config system, object pooling
- **Performance**: Exception isolation, frame budget, memory leak detection, profiler
- **Multiplayer**: LAN/network rooms, player sync, mod version verification

## Quick Start

### Players: Install Machine

1. Download the latest release `MachineLoader-vX.X.X.zip`
2. Run `MachineInstaller.exe`
3. The installer will automatically scan for Aviassembly
4. Confirm the install path and click install
5. Launch the game - `Machine vX.X.X` in the bottom-left of the main menu confirms installation

> If you encounter issues, run `UninstallMachine.exe` in the game directory to completely remove Machine.

### Players: Install Mods

1. Place the mod folder into `Aviassembly/Machine/mods/`
2. Launch the game and click the `Mods` button in the main menu
3. Enable/disable mods in the manager
4. Missing dependencies will be clearly indicated

### Developers: Create Your First Mod

```bash
# Use the template generator (Windows PowerShell)
.\newmod.ps1 MyFirstMod

# Or manually check the examples/ directory
```

See the [Mod Development Guide](mod-development.md) for details.

## Directory Structure

```
Aviassembly/
├── Aviassembly.exe
├── Aviassembly_Data/Managed/
│   ├── Assembly-CSharp.dll           # Game code (injected)
│   ├── Assembly-CSharp.dll.machinebak # Original backup
│   ├── Machine.Core.dll              # Machine core
│   └── Mono.Cecil.dll               # Dependency
├── Machine/
│   ├── mods/                         # Mod directory
│   │   └── machine.radar/
│   │       ├── mod.json
│   │       ├── config.json
│   │       └── code/
│   ├── config.json                   # Global config
│   ├── update.json                   # Update config
│   ├── profile.json                  # Player profile
│   ├── saves/                        # Mod saves
│   ├── logs/                         # Logs
│   └── update/                       # Update cache
├── MachineInstaller.exe
├── UninstallMachine.exe
└── steam_appid.txt
```

## Core Features

### Loader Core

- **Event Bus**: 12 event types, subscribe/publish/isolation
- **Mod Lifecycle**: OnLoad → OnEnable → OnUpdate/OnFixedUpdate → OnDisable → OnUnload
- **Dependency Management**: Required/optional dependencies, version ranges, cyclic detection
- **Conflict Detection**: Conflicting mods auto-disabled
- **Load Order**: Topological sort, loadBefore/loadAfter support

### Performance & Stability

- **Exception Isolation**: One mod's exception doesn't affect others
- **Auto-Disable**: 10+ consecutive exceptions auto-disables the mod
- **Frame Budget**: 2ms per mod per frame, with warning throttling
- **Profiler**: Init time, per-frame time, peak tracking
- **Memory Leak Detection**: Event subscriptions, object pools, managed memory
- **Log Rotation**: 5MB rotation, 5 files retained, crash reports

### Multiplayer

- **LAN Rooms**: IP:port direct connection
- **Network Rooms**: Room code join, lobby browsing
- **Mod Verification**: Automatic game version and mod list consistency check
- **Player Sync**: Aircraft position, model, state real-time sync
- **Player System**: UID/RID/OID permission levels, encrypted user registry

## FAQ

### Q: Game won't launch after install?
A: Run `UninstallMachine.exe` and reinstall. Ensure the game path has no Chinese or special characters.

### Q: Mod not working?
A: Check `Machine/logs/Machine.log` for errors. Common causes:
- Missing dependencies (log will clearly indicate)
- Mod version incompatible with loader version
- mod.json format error

### Q: How to enable DEBUG logs?
A: Edit `Machine/config.json`:
```json
{
  "disabled": [],
  "logLevel": "DEBUG"
}
```

### Q: Game lagging?
A:
1. Enter `/machine diag` in-game to view performance report
2. Check which mod has high per-frame time
3. Disable unnecessary mods
4. Ensure `machine.opti` performance mod is enabled

## Developer Resources

- [Mod Development Guide](mod-development.md)
- [API Reference](../api/README.md)
- [Example Mods](../../examples/)
- [Debugging Guide](debugging.md)
- [Changelog](../../CHANGELOG.md)

## Build

```bash
# Build core
powershell -ExecutionPolicy Bypass -File build-core.ps1

# Build all mods
powershell -ExecutionPolicy Bypass -File build-mods.ps1

# Build release package
python build_release.py --version 2.3.0

# Run tests
pytest tests/ -v
```

## Contributing

Contributions of code, documentation, translations, or example mods are welcome.

## License

MIT License
