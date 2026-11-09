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

The [launcher](RetooledLauncher/README.md) and [patch](RetooledPatch/README.md) READMEs describe how each one behaves.
Update them when you change that behaviour.

## Making a change

1. Branch from `main`.
2. Make the change, with tests for any change in behaviour. Logic belongs in `RetooledLauncher.Core` or
   `RetooledPatch`, where the tests can reach it.
3. Run `dotnet test ksp1retooled-launcher.slnx`.
4. If you changed the UI, start the app and try it, because the tests don't cover `RetooledLauncher`.
5. Open a pull request against `main`.

In the pull request, explain what changed and why, call out any behaviour changes, and say how you tested it,
including anything you couldn't test. Write the title in the imperative, such as "Leave files the patch doesn't change
alone".

CI runs the tests on Linux and Windows. Changes to the launcher, or to the installer workflow, also build the
installers and portable zips for every platform, then install and start them.

## Licence

By contributing, you agree that your work is released under the [MIT licence](LICENSE). If you add third-party code,
keep its licence and add it to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Releasing

Maintainers release by pushing a version tag from `main`:

```sh
git tag v1.2.0
git push origin v1.2.0
```

The version comes from the tag, so there's nothing to bump. A tag with a hyphen, such as `v1.2.0-beta.1`, becomes a
pre-release. The tag publishes the NuGet packages, creates the GitHub release, and attaches the installers and
portable zips. The release notes are [release-notes-header.md](.github/release-notes-header.md) followed by GitHub's
generated list of changes. `{{DOWNLOAD_URL}}` in the header becomes the base URL of the release's files, so
`{{DOWNLOAD_URL}}/name` links to the file `name`.

NuGet versions can't be replaced, so a fix that needs a code change gets a new tag. If only the installers failed,
re-run the failed jobs. See [publish.yml](.github/workflows/publish.yml) and
[installers.yml](.github/workflows/installers.yml).
