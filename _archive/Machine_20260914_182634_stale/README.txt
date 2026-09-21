Machine Mod Loader

Mods    : <game>\mods\<Mod>\mod.json
Logs    : <game>\Machine\logs\Machine.log
          <game>\Machine\logs\installer.log   (installer / updater)
Config  : <game>\Machine\net.json      (online / LAN server)
          <game>\Machine\update.json   (update repo + channel)
Backup  : <game>\Machine\backup\       (previous Machine.Core.dll)

Update  : after downloading in game, exit the game and run
            Machine\machine_update.bat
          (applies a hash + RSA-signature verified Machine.Core.dll)
Remove  : run uninstall_machine.bat, or
            MachineInstaller.exe --uninstall

<game> is your Aviassembly install directory. Paths above are relative;
this file is generated at install time and never contains machine-specific
absolute paths.
