# RetooledPatch

Library that builds, applies and reverts KSP Retooled patch files. `RetooledPatch.Cli` (`retooled-patch`) wraps
it for the command line; the launcher will reference it directly.

```
dotnet run --project RetooledPatch.Cli -- build --source <stock-install> [--source <other-store-install> ...] --target <retooled-build> --out retooled-windows.rtpatch
dotnet run --project RetooledPatch.Cli -- check  --install <ksp-dir> --patch retooled-windows.rtpatch
dotnet run --project RetooledPatch.Cli -- apply  --install <ksp-dir> --patch retooled-windows.rtpatch --in-place
dotnet run --project RetooledPatch.Cli -- revert --install <ksp-dir>
```

One patch file per platform turns any of its sources (each store's KSP 1.12.5) into the same build. Each output file
has one recipe per distinct way the sources hold its stock inputs; an apply uses the first recipe whose stock files
are present with the recorded hashes, so it needs no store detection. The second and later sources only add recipes
for the files the earlier ones cannot provide, in sections of their own that other installs never decompress.
A file every source has as the build ships it is not checked: an apply leaves the install's copy alone if it was edited
or missing, unless another output reads it (a side-by-side apply links the edited copy across, as it does a stock one).

`build --copy <path>,...` takes a file or directory out of the patch altogether, for what a patch must not depend on,
such as a folder that mods overwrite or whose copies differ between stores: it is in no tree, so nothing checks,
replaces or removes it, and an install keeps it as it is. The patch file records it, and a side-by-side apply, which
would otherwise leave it behind, copies it from the install whole. An `--exclude` is a pattern for what the retooled
install should not have, and a `--copy` an exact path for what it needs but the patch should not touch.

`<retooled-build>` is a release build, which has the stock layout:
`Unity.exe -batchmode -quit -projectPath . -buildTarget Win64 -executeMethod KSP.Retooled.Content.Authoring.Builder.Build -retooledOut <empty-dir> -retooledRelease`
(`-buildTarget Linux64` or `OSXUniversal` for the other platforms' builds). Its stock files are what KSP Retooled →
Setup copied into the project (GameData, and the rest of the install's root in `StockRoot`), never read from an
install, so set the project up from a pristine install of that platform, with its DLC, first. For another platform's
release from the same checkout, run `RetooledCli devenv setup --install <that platform's install>` first (it redoes only
the file import) and again with your usual install afterwards. A release has its platform's build profile
in `Assets/Settings/Build Profiles`, so its architecture (x86_64, arm64 or both on macOS) is the one set there in the
Build Profiles window; builds that are not releases are for the machine that builds them.
A source can overlay several directories (`--source "<game-depot>;<dlc-depot>;..."`, `:` on Linux and macOS), e.g.
the Steam depots 220204, 283740 and 982970. `build` refuses a build that lacks a top-level entry of a source, because
an in-place apply removes every stock file the build doesn't ship. The exception is a CoreCLR release, which has no
`MonoBleedingEdge`: an apply removes stock's, since Special K crashes the game if it finds that runtime there.

An in-place apply moves every stock file it replaces or removes into `<ksp-dir>/.retooled/original` and records them
in `.retooled/journal.json`; `revert` puts them back, deletes what the apply added and the texture cache, and leaves
saves and settings alone. A path may be a file in stock and a directory in the build (`MainMenu.nib` on macOS) or the
reverse, so an apply removes stock files before it adds any, and a revert does the opposite. Retooled saves do not load
in stock KSP.

The moves run in a transaction (with [TxFileManager](https://github.com/chinhdo/txFileManager), the library CKAN uses),
so an in-place apply that fails partway puts every file back and removes `.retooled`, leaving the install unpatched with
nothing to revert. The journal covers what a transaction can't: if the process dies, or undoing the apply fails too, it
stays, and `revert` finishes restoring stock.

An in-place apply also sets LANGUAGE in an
existing settings.cfg to the stock buildID's language, which the build's buildID64.txt (buildID.txt on macOS and Linux) replaces.

The design is in forum thread #241.
