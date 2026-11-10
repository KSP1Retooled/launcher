# KSP Retooled launcher

This repo is the desktop launcher and patcher for KSP retooled.

Just looking to install the launcher? Download the installer from the links below:
* [Windows](https://github.com/KSP1Retooled/launcher/releases/latest/download/KSPRetooled.Launcher-windows-x64-Setup.exe)
* [Linux](https://github.com/KSP1Retooled/launcher/releases/latest/download/KSPRetooled.Launcher-linux-x64.AppImage)
* [Macos (Arm64)](https://github.com/KSP1Retooled/launcher/releases/latest/download/KSPRetooled.Launcher-macos-arm64-Setup.pkg)
* [Macos (x86-64)](https://github.com/KSP1Retooled/launcher/releases/latest/download/KSPRetooled.Launcher-macos-x64-Setup.pkg)
* Other? [See the release page][releases].

Looking to uninstall? Also documented on [the release page][releases].

[releases]: https://github.com/KSP1Retooled/launcher/releases/latest

## Repo Contents
| Folder | Contents |
| ------ | -------- |
| [`RetooledLauncher`](RetooledLauncher/README.md)   | The UI for the launcher. |
| `RetooledLauncher.Core`                            | The core logic for the launcher. |
| [RetooledPatch](RetooledPatch/README.md)           | A library for building, applying, and reverting `.rtpatch` files |
| [`RetooledPatch.Cli`](RetooledPatch.Cli/README.md) | The `retooled-patch` CLI |

## Building
You'll need the .NET 10 SDK, but otherwise building is just
```sh
dotnet build
```

See [CONTRIBUTING.md](./CONTRIBUTING.md) for everything else.

## License
This repo is licensed under the MIT license.
