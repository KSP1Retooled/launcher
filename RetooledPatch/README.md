# RetooledPatch

Builds KSP Retooled from a stock KSP 1.12.5 install by applying the patch archives that `RetooledCli patch build` writes.

```
dotnet run --project Tools/RetooledCli -- patch build --base <stock-install> --target <retooled-build> --out <patch-dir>
dotnet run --project Tools/RetooledPatch -- apply --install <stock-install> --patch <patch-dir> --out <new-install>
dotnet run --project Tools/RetooledPatch -- apply --install <stock-install> --patch <patch-dir> --in-place
```

`<retooled-build>` is a release build, which has the stock layout:
`Unity.exe -batchmode -quit -projectPath . -executeMethod KSP.Retooled.Content.Authoring.Builder.Build -retooledOut <empty-dir> -retooledStock <stock-root>...`.
Pass every pristine stock root, e.g. the Steam depots 220204, 283740 and 982970. `patch build` refuses a build that
lacks a top-level entry of the stock install, because an in-place apply removes every stock file the build doesn't ship.

The design is in forum thread #241. `<patch-dir>` holds `core.rtpatch`, `makinghistory.rtpatch` and `serenity.rtpatch`, plus
`report.txt` listing the files that cost the most. Each archive is a single zstd stream containing the manifest
followed by raw bsdiff and literal blobs.
