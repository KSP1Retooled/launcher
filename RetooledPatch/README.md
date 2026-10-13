# RetooledPatch

Builds KSP Retooled from a stock KSP 1.12.5 install by applying the patch archives that `RetooledCli patch build` writes.

```
dotnet run --project Tools/RetooledCli -- patch build --base <stock-install> --target <retooled-build> --out <patch-dir>
dotnet run --project Tools/RetooledPatch -- apply --install <stock-install> --patch <patch-dir> --out <new-install>
dotnet run --project Tools/RetooledPatch -- apply --install <stock-install> --patch <patch-dir> --in-place
```

The design is in forum thread #241. `<patch-dir>` holds `core.rtpatch`, `makinghistory.rtpatch` and `serenity.rtpatch`, plus
`report.txt` listing the files that cost the most. Each archive is a single zstd stream containing the manifest
followed by raw bsdiff and literal blobs.
