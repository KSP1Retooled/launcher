using DotMake.CommandLine;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "revert", Parent = typeof(RootCliCommand),
            Description = "Undoes an in-place apply, so that the install is stock again. Your saves and settings aren't changed.")]
public class RevertCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The install that was patched with apply --in-place.")]
    public required string Install { get; set; }

    public void Run() => PatchReverter.Revert(this.Install, Console.Out);
}
