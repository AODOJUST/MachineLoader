# Machine Loader - Français

Chargeur de mods pour le jeu Aviassembly (similaire à Forge/Fabric pour Minecraft)

## Fonctionnalités

- **Gestion des mods** - Gérez les mods via le bouton Mod dans le menu principal
- **Dossier mods** - Création automatique du dossier mods dans le répertoire du jeu
- **16 mods intégrés** - Système de combat, radar, missiles, alertes vocales, etc.
- **Mises à jour automatiques** - Système de mise à jour automatique avec vérification de signature SHA-256 + RSA-3072
- **Multijoueur** - Prise en charge des salles en ligne/LAN
- **Multilingue** - Documentation en 7 langues

## Installation

1. Téléchargez `MachineLoader-2.5.0.zip` depuis la page [Releases](https://github.com/AODOJUST/MachineLoader/releases)
2. Extrayez-le et exécutez `MachineInstaller.exe`
3. Le programme d'installation détectera automatiquement Aviassembly
4. Confirmez le chemin d'installation et installez
5. Lancez le jeu - si `Machine v2.5.0` s'affiche en bas à gauche du menu principal, l'installation a réussi

## Démarrage rapide

### Installer des mods
1. Placez le fichier DLL du mod dans le dossier `répertoire du jeu/mods/`
2. Redémarrez le jeu
3. Activez/désactivez le mod via le bouton Mod dans le menu principal

### Développer ses propres mods
Voir [Guide de développement de mods (anglais)](../en/mod-development.md).

## Liste des mods intégrés

| Mod | Description |
|-----|-------------|
| BattleCore | Hub d'informations de combat (dépendance) |
| BattleHold | Gestion de l'entrepôt de combat |
| CombatBay | Soute de combat |
| FactionSystem | Système d'IA pour 3 factions |
| FlightTrails | Affichage des traces de vol |
| GMeter | Calcul de la force G |
| GVision | Affichage tête haute (HUD) de combat |
| KillFeed | Flux d'événements de kills |
| MachineAAM | Missiles air-air + canon |
| MachineShop | Interface de boutique de fret indépendante |
| OptiMod | Optimisation des performances CPU |
| Radar | Radar multi-niveaux + verrouillage de cible |
| VoiceAlerts | Alertes vocales bilingues chinois-anglais |
| ZoomMod | Zoom de l'écran |

## Documentation

- [README (anglais)](../en/README.md)
- [Guide de développement de mods (anglais)](../en/mod-development.md)
- [Guide de débogage (anglais)](../en/debugging.md)
- [Référence API](../api/README.md)
- [Exemples de mods](../../examples/)

## Contribuer

Voir [Guide de contribution (anglais)](../../CONTRIBUTING.md).

## Licence

MIT License - voir [LICENSE](../../LICENSE)
