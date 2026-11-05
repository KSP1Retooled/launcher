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

Releases are the GitHub releases of the selected channel. A channel is a GitHub repository: `KSP1Retooled/retooled`
is always offered, and **Add…** adds others (`owner/name`). Each release carries one patch per platform, named `retooled-windows.rtpatch`,
`retooled-linux.rtpatch` and `retooled-macos.rtpatch` (built with `retooled-patch build`, see
[RetooledPatch](../RetooledPatch/README.md)). A release without the current platform's patch is listed but cannot be
installed.

`KSP1Retooled/retooled#debug` is also always offered. It lists the same releases but installs their development
players, from `retooled-<platform>-debug.rtpatch`. It counts as a channel of its own, so moving between it and
`KSP1Retooled/retooled` is a switch.

- Tags are only unique within a channel. A release installed from another channel is shown by name, and moving to this
  channel's releases is a switch, not an upgrade or downgrade, even if the tags look comparable.
- Whether a release is a pre-release is GitHub's pre-release flag. Pre-releases are hidden unless **Show pre-releases**
  is ticked; the installed release is listed either way.
- Releases tagged with a semantic version (`v1.2.0`, `v1.3.0-beta.1`) are ordered by version, so a maintenance release
  of an older line stays below the newer line; other tags are ordered by publish date.
- Set `GITHUB_TOKEN` to read a private repository or to get past GitHub's anonymous rate limit. The token is never
  saved.
- Downloads are checked against the size and, when GitHub has one, the SHA-256 digest of the asset.

## Local patch files

**Install patch file…** installs any `.rtpatch` for this platform, e.g. one built with `retooled-patch build`, without
it being part of a release. It behaves like a release otherwise: it replaces what is installed, a failed install puts
that back, and **Uninstall** removes it. The file is copied into the cache first, so it can be moved or rebuilt
afterwards.

## Install, upgrade, uninstall

Installing applies the patch in place: the stock files it replaces are kept in `<ksp>/.retooled/original`, with a
journal, and `.retooled/launcher.json` records the release. Patches are built against stock, so moving to another
release reverts to stock and applies the other release's patch. If that apply fails, what was installed is put back
from its cached patch, whichever channel or file it came from. Uninstalling restores the stock files and keeps saves, settings and mods. An install
that fails is undone on the spot. One that couldn't be undone, or an install or uninstall that stopped half way because
the launcher died, shows as interrupted, and **Uninstall** finishes restoring the stock game.

## New launcher versions

The launcher checks the releases of `KSP1Retooled/launcher` when it starts. Someone running a pre-release is offered
newer pre-releases too; everyone else only gets stable releases. A failed check or download only goes in the log.

- An installed launcher downloads a new version in the background, and a notice in the lower-left corner offers to
  restart into it. This uses each platform's `releases.<channel>.json` and `-full.nupkg` files on each release, so a
  release without them is skipped.
- Without the restart, Windows installs it the next time the launcher starts. Linux and macOS offer it again instead,
  because replacing an app in `/Applications` or another protected folder asks for an administrator's password. If that
  is declined, the launcher comes back on the version it was.
- A copy that was not installed, such as an AppImage's extracted files or `dotnet run`, cannot update itself, so the
  notice links to the new version's release page instead.

## Files

| What | Where |
|---|---|
| Settings (`settings.json`: `repository` is the selected channel, `channels` the added ones) | `%LOCALAPPDATA%\KSPRetooled\Launcher`, `~/Library/Application Support/KSPRetooled/Launcher`, `~/.local/share/KSPRetooled/Launcher` |
| Patches (`cache/<channel>@<tag>/`, `cache/local@<sha256>/`) | the same folder; only the installed patch is kept |

Installs are found in the Steam libraries (`libraryfolders.vdf`) and the default GOG folders of each platform;
any other folder can be chosen with **Browse**.

## Not done yet

- Code signing, so Windows and macOS stop warning about the installers.
- Choosing the default release repository for good.
