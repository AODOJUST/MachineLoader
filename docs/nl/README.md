# Machine Loader - Nederlands

Mod-loader voor het spel Aviassembly (vergelijkbaar met Forge/Fabric voor Minecraft)

## Functies

- **Mod-beheer** - Beheer mods via de Mod-knop in het hoofdmenu
- **Mods-map** - Automatische aanmaak van de mods-map in de spelmap
- **16 ingebouwde mods** - Gevechtssysteem, radar, raketten, gesproken waarschuwingen en meer
- **Automatische updates** - Auto-update-systeem met SHA-256 + RSA-3072 handtekeningverificatie
- **Multiplayer** - Ondersteuning voor online/LAN-kamers
- **Meertalig** - Documentatie in 7 talen

## Installatie

1. Download `MachineLoader-2.5.0.zip` van de [Releases-pagina](https://github.com/AODOJUST/MachineLoader/releases)
2. Pak het uit en voer `MachineInstaller.exe` uit
3. Het installatieprogramma detecteert Aviassembly automatisch
4. Bevestig het installatiepad en installeer
5. Start het spel - als `Machine v2.5.0` linksonder in het hoofdmenu wordt weergegeven, is de installatie geslaagd

## Snelstart

### Mods installeren
1. Plaats het DLL-bestand van de mod in de map `spelmap/mods/`
2. Start het spel opnieuw op
3. Schakel de mod in/uit via de Mod-knop in het hoofdmenu

### Eigen mods ontwikkelen
Zie [Mod-ontwikkelingsgids (Engels)](../en/mod-development.md).

## Lijst met ingebouwde mods

| Mod | Beschrijving |
|-----|--------------|
| BattleCore | Gevechtsinformatie-hub (afhankelijkheid) |
| BattleHold | Beheer van gevechtsmagazijn |
| CombatBay | Gevechtsvrachtruimte |
| FactionSystem | AI-systeem voor 3 facties |
| FlightTrails | Weergave van vluchtsporen |
| GMeter | G-kracht berekening |
| GVision | Gevechts-HUD (Head-Up Display) |
| KillFeed | Gebeurtenis-killfeed |
| MachineAAM | Lucht-lucht raketten + kanon |
| MachineShop | Onafhankelijke vrachtwinkel-interface |
| OptiMod | CPU-prestatie-optimalisatie |
| Radar | Meer-niveau radar + doelvergrendeling |
| VoiceAlerts | Chinees-Engelse tweetalige gesproken waarschuwingen |
| ZoomMod | Schermzoom |

## Documentatie

- [README (Engels)](../en/README.md)
- [Mod-ontwikkelingsgids (Engels)](../en/mod-development.md)
- [Debugging-gids (Engels)](../en/debugging.md)
- [API-referentie](../api/README.md)
- [Voorbeeld-mods](../../examples/)

## Bijdragen

Zie [Bijdragegids (Engels)](../../CONTRIBUTING.md).

## Licentie

MIT License - zie [LICENSE](../../LICENSE)
