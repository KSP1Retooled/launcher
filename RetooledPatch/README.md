# RetooledPatch

Library that builds, applies and reverts KSP Retooled patch files. `Tools/RetooledPatch.Cli` (`retooled-patch`) wraps
it for the command line; the launcher will reference it directly.

```
dotnet run --project Tools/RetooledPatch.Cli -- build --source <stock-install> [--source <other-store-install> ...] --target <retooled-build> --out retooled-windows.rtpatch
dotnet run --project Tools/RetooledPatch.Cli -- check  --install <ksp-dir> --patch retooled-windows.rtpatch
dotnet run --project Tools/RetooledPatch.Cli -- apply  --install <ksp-dir> --patch retooled-windows.rtpatch --in-place
dotnet run --project Tools/RetooledPatch.Cli -- revert --install <ksp-dir>
```

One patch file per platform turns any of its sources (each store's KSP 1.12.5) into the same build. Each output file
has one recipe per distinct way the sources hold its stock inputs; an apply uses the first recipe whose stock files
are present with the recorded hashes, so it needs no store detection. The second and later sources only add recipes
for the files the earlier ones cannot provide, in sections of their own that other installs never decompress.

`<retooled-build>` is a release build, which has the stock layout:
`Unity.exe -batchmode -quit -projectPath . -executeMethod KSP.Retooled.Content.Authoring.Builder.Build -retooledOut <empty-dir> -retooledStock <stock-root>...`.
A source can overlay several directories (`--source "<game-depot>;<dlc-depot>;..."`, `:` on Linux and macOS), e.g.
the Steam depots 220204, 283740 and 982970. `build` refuses a build that lacks a top-level entry of a source, because
an in-place apply removes every stock file the build doesn't ship.

An in-place apply moves every stock file it replaces or removes into `<ksp-dir>/.retooled/original` and records them
in `.retooled/journal.json`; `revert` puts them back, deletes what the apply added and the texture cache, and leaves
saves and settings alone. Retooled saves do not load in stock KSP.

The design is in forum thread #241.
