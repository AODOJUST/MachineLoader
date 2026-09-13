# Machine Loader - Deutsch

Mod-Loader für das Spiel Aviassembly (ähnlich wie Forge/Fabric für Minecraft)

## Funktionen

- **Mod-Verwaltung** - Verwalten Sie Mods über die Mod-Schaltfläche im Hauptmenü
- **Mods-Ordner** - Automatische Erstellung des mods-Ordners im Spielverzeichnis
- **16 eingebaute Mods** - Kampfsystem, Radar, Raketen, Sprachwarnungen und mehr
- **Automatische Updates** - Auto-Update-System mit SHA-256 + RSA-3072 Signaturprüfung
- **Mehrspieler** - Online/LAN-Raum-Unterstützung
- **Mehrsprachigkeit** - Dokumentation in 7 Sprachen

## Installation

1. Laden Sie `MachineLoader-2.5.0.zip` von der [Releases-Seite](https://github.com/AODOJUST/MachineLoader/releases) herunter
2. Entpacken Sie es und führen Sie `MachineInstaller.exe` aus
3. Das Installationsprogramm erkennt Aviassembly automatisch
4. Bestätigen Sie den Installationspfad und installieren Sie
5. Starten Sie das Spiel - wenn unten links im Hauptmenü `Machine v2.5.0` angezeigt wird, war die Installation erfolgreich

## Schnellstart

### Mods installieren
1. Legen Sie die DLL-Datei des Mods in den Ordner `Spielverzeichnis/mods/`
2. Starten Sie das Spiel neu
3. Aktivieren/deaktivieren Sie den Mod über die Mod-Schaltfläche im Hauptmenü

### Eigene Mods entwickeln
Siehe [Mod-Entwicklungsanleitung (Englisch)](../en/mod-development.md).

## Liste der eingebauten Mods

| Mod | Beschreibung |
|-----|--------------|
| BattleCore | Kampf-Informations-Hub (Abhängigkeit) |
| BattleHold | Verwaltung des Kampflagers |
| CombatBay | Kampf-Frachtraum |
| FactionSystem | KI-System für 3 Fraktionen |
| FlightTrails | Anzeige von Flugspuren |
| GMeter | G-Kraft-Berechnung |
| GVision | Kampf-HUD (Head-Up Display) |
| KillFeed | Ereignis-Kill-Feed |
| MachineAAM | Luft-Luft-Raketen + Kanone |
| MachineShop | Unabhängige Fracht-Shop-Oberfläche |
| OptiMod | CPU-Leistungsoptimierung |
| Radar | Mehrstufiges Radar + Zielerfassung |
| VoiceAlerts | Chinesisch-Englische zweisprachige Sprachwarnungen |
| ZoomMod | Bildschirm-Zoom |

## Dokumentation

- [README (Englisch)](../en/README.md)
- [Mod-Entwicklungsanleitung (Englisch)](../en/mod-development.md)
- [Debugging-Anleitung (Englisch)](../en/debugging.md)
- [API-Referenz](../api/README.md)
- [Beispiel-Mods](../../examples/)

## Mitwirken

Siehe [Mitwirkungsanleitung (Englisch)](../../CONTRIBUTING.md).

## Lizenz

MIT License - siehe [LICENSE](../../LICENSE)
