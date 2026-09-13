# Changelog

All notable changes to the Machine Loader project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- EventBus system with 12 event types (GameStart, SceneLoaded, PlayerSpawn, Tick, etc.)
- MachineModBase abstract base class with unified lifecycle (OnLoad/OnEnable/OnDisable/OnUnload)
- SemVer semantic versioning support (parse, compare, range check)
- Mod dependency resolution and validation
- Conflict detection with auto-disable
- Dependency graph visualization in logs
- Audit logging for all update/install/rollback operations
- Version revocation mechanism
- One-click rollback (`machine_update.py --rollback`)
- Unit test suite (64 tests)
- Integration test suite (28 tests)
- GitHub Actions CI/CD pipeline
- Code quality tools (black, ruff, mypy, bandit)
- `build_release.py` one-click release packaging

### Changed
- Removed absolute paths from `installed.json` (now uses `installerName` + `installChannel`)
- Updated `mod.json` format to v2 with new fields (apiVersion, loaderVersion, dependencies, conflicts, etc.)
- Improved error messages for missing dependencies and version mismatches

### Fixed
- Fixed `time` module import in `machine_common.py`
- Fixed backup directory naming (`backup` instead of `backups`)

## [2.4.0] - 2026-09-13

### Added
- Multiplayer functionality (online button, network/LAN rooms)
- Player name system with UID/RID/OID
- Faction system with 3 factions and AI opponents
- Radar system with multiple tiers (Mk1-Mk5, AESA)
- Air-to-air missile system (PL-15, PL-10, PL-17, AIM-9X)
- Voice alert system (Chinese/English)
- Flight trails visualization
- G-force meter
- Combat HUD (GVision)
- Kill feed event system
- Independent shop interface (MachineShop)
- CPU optimization mod (OptiMod)
- Zoom mod
- Installer and uninstaller programs
- Auto-update system with signature verification

### Security
- RSA signature verification for update packages
- SHA-256 hash verification
- Public key fingerprint self-check

## [2.3.0] - 2026-09-12

### Added
- Machine mod loader core
- Mod management UI in main menu
- Mods folder for user-installed mods
- Basic mod API

[Unreleased]: https://github.com/AODOJUST/MachineLoader/compare/v2.4.0...HEAD
[2.4.0]: https://github.com/AODOJUST/MachineLoader/compare/v2.3.0...v2.4.0
[2.3.0]: https://github.com/AODOJUST/MachineLoader/releases/tag/v2.3.0
