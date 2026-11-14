# `retooled-patch`

This is a CLI for creating, installing, and reverting `.rtpatch` files. KSP
retooled uses it (and the underlying library) in order to manage binary patches
for KSP.

## Usage

```
$ retooled-patch --help
Builds, checks, applies and reverts KSP Retooled patch files (.rtpatch).

Usage:
  retooled-patch [command] [options]

Options:
  -?, -h, --help  Show help and usage information
  --version       Show version information

Commands:
  build   Builds one patch file that turns each --source install into the --target build. The command also writes a report next to the patch file, named <file>.report.txt.
  info    Shows what a patch file contains: the platform, the source installs that it accepts, the files that it copies unchanged, its compressed sections, and its DLC components.
  check   Reports which source the install matches, and whether the patch can be applied to it. This command doesn't change any files. It exits with 0 if the patch can be applied, and with 1 if it can't.
  apply   Patches a stock install so that it becomes KSP Retooled. You must pass either --out or --in-place, but not both. In either case, the command only patches a DLC if that DLC is installed.
  revert  Undoes an in-place apply, so that the install is stock again. Your saves and settings aren't changed.
```

Run `retooled-patch <command> --help` to see a command's options.

The command exits with 0 on success and 2 for a bad command line, install or
patch file. `check` exits with 1 if the patch doesn't apply.
