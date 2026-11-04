# RetooledLauncher

Desktop launcher (Windows, Linux, macOS) that installs KSP Retooled onto a stock KSP 1.12.5 install, upgrades or
downgrades it between releases, uninstalls it, and starts the game. `RetooledLauncher` is the Avalonia UI;
`RetooledLauncher.Core` holds everything else and is what the tests cover.

```
dotnet run --project RetooledLauncher
dotnet test RetooledLauncher.Tests
dotnet publish RetooledLauncher -c Release -r win-x64 --self-contained   # or linux-x64, osx-arm64, osx-x64
dotnet tool restore && dotnet vpk pack ...   # see .github/workflows/installers.yml
```

## Releases

Releases are the GitHub releases of the repository in the launcher's settings (`KSP1Retooled/retooled` by
default). Each release carries one patch per platform, named `retooled-windows.rtpatch`,
`retooled-linux.rtpatch` and `retooled-macos.rtpatch` (built with `retooled-patch build`, see
[RetooledPatch](../RetooledPatch/README.md)). A release without the current platform's patch is listed but cannot be
installed.

- Whether a release is a pre-release is GitHub's pre-release flag. Pre-releases are hidden unless **Show pre-releases**
  is ticked; the installed release is listed either way.
- Releases tagged with a semantic version (`v1.2.0`, `v1.3.0-beta.1`) are ordered by version, so a maintenance release
  of an older line stays below the newer line; other tags are ordered by publish date.
- Set `GITHUB_TOKEN` to read a private repository or to get past GitHub's anonymous rate limit. The token is never
  saved.
- Downloads are checked against the size and, when GitHub has one, the SHA-256 digest of the asset.

## Install, upgrade, uninstall

Installing applies the patch in place: the stock files it replaces are kept in `<ksp>/.retooled/original`, with a
journal, and `.retooled/launcher.json` records the release. Patches are built against stock, so moving to another
release reverts to stock and applies the other release's patch. If that apply fails, the previously installed release
is put back from its cached patch. Uninstalling restores the stock files and keeps saves, settings and mods. An install
or uninstall that stopped half way shows as interrupted, and **Uninstall** finishes restoring the stock game.

## Files

| What | Where |
|---|---|
| Settings (`settings.json`) | `%LOCALAPPDATA%\KSPRetooled\Launcher`, `~/Library/Application Support/KSPRetooled/Launcher`, `~/.local/share/KSPRetooled/Launcher` |
| Downloaded patches (`cache/<tag>/`) | the same folder; only the installed release's patch is kept |

Installs are found in the Steam libraries (`libraryfolders.vdf`) and the default GOG folders of each platform;
any other folder can be chosen with **Browse**.

## Not done yet

- Code signing, so Windows and macOS stop warning about the installers; self-update of the launcher.
- Choosing the release repository for good (it is the `repository` setting for now).
