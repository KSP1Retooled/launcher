# Contributing

Thanks for helping out. Bugs and ideas go in the [issue tracker](https://github.com/KSP1Retooled/launcher/issues);
changes go in a pull request.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build ksp1retooled-launcher.slnx
dotnet test ksp1retooled-launcher.slnx
dotnet run --project RetooledLauncher
```

The launcher needs a stock KSP 1.12.5 install to do anything useful.

## Where things live

| Project | |
| --- | --- |
| `RetooledLauncher` | The Avalonia UI |
| `RetooledLauncher.Core` | Everything else in the launcher |
| `RetooledPatch` | Builds, applies and reverts `.rtpatch` files |
| `RetooledPatch.Cli` | The `retooled-patch` command |

The [launcher](RetooledLauncher/README.md) and [patch](RetooledPatch/README.md)
READMEs describe how each one behaves. Update them when you change that behaviour.

## Making a change

1. Branch from `main`.
2. Make the change, with tests for any change in behaviour. Logic belongs in
   `RetooledLauncher.Core` or `RetooledPatch`, where the tests can reach it.
3. Run `dotnet test ksp1retooled-launcher.slnx`.
4. If you changed the UI, start the app and try it, because the tests don't
   cover `RetooledLauncher`.
5. Open a pull request against `main`.

## Licence

By contributing, you agree that your work is released under the
[MIT licence](LICENSE). If you add third-party code, keep its licence and add
it to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Releasing

Make a release by creating a new tag:

```sh
git tag v1.2.0
git push origin v1.2.0
```

CI will take care of creating the release and all the attached packages.
If you want to edit the release blurb it is in `.github\release-notes-header.md`.
