# KSP Retooled launcher

The desktop launcher for KSP Retooled and the library that builds and applies its patches.

| Project | |
| --- | --- |
| [RetooledLauncher](RetooledLauncher/README.md) | Avalonia app: install, upgrade and uninstall releases, start the game |
| `RetooledLauncher.Core` | The launcher's logic |
| [RetooledPatch](RetooledPatch/README.md) | Builds, applies and reverts `.rtpatch` files |
| `RetooledPatch.Cli` | The `retooled-patch` command |

```sh
dotnet build ksp1retooled-launcher.slnx
dotnet test ksp1retooled-launcher.slnx
```

Needs the .NET 10 SDK.
