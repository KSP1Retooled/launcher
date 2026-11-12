# KSP Retooled Launcher

This is the launcher for KSP Retooled. It allows you to install, update, or
uninstall the releae patch files into an existing stock KSP 1.12.5 install.

This package is the UI package. The core operations are in `RetooledLauncher.Core`.

## Installers

[installers.yml](../.github/workflows/installers.yml) builds them with [Velopack](https://velopack.io), which also
updates an installed launcher.

- **Windows:** `Setup.exe` is an [Inno Setup](https://jrsoftware.org/isinfo.php) wizard,
  [Installer/Setup.iss](Installer/Setup.iss), that installs Velopack's portable build for the current user. It asks
  where to install and which shortcuts to make. There is no install for all users, because the launcher updates its own
  files, which would need an admin prompt in Program Files. It removes an
  install made by earlier releases' Velopack setup, keeping the launcher's settings. Its uninstaller runs the launcher's
  uninstall hook, which deletes the settings and patch cache.
- **macOS:** Velopack's `.pkg`, with the welcome and closing pages in [Installer](Installer).
- **Linux:** Velopack's AppImage.
