# `retooled-patch`

This is a CLI for creating, installing, and reverting `.rtpatch` files. KSP
retooled uses it (and the underlying library) in order to manage binary patches
for KSP.

## Usage

```
retooled-patch --help
Description:
  retooled-patch is a tool for working with and applying .rtpatch files.

Usage:
  retooled-patch [command] [options]

Options:
  --version  Show version information
  -?, -h     Show short help.
  --help     Show detailed help.

Commands:
  build   Build a patch file.
  info    Show what a patch file contains.
  check   Check whether a patch can be applied to an install.
  apply   Apply a patch to a stock install.
  revert  Undo an in-place apply.

  Description:
  Creates one patch file that turns each --source install into the --target build. The 
  command also writes a report next to the patch file, named <file>.report.txt.
```
