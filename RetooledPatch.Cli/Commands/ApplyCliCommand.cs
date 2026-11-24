using DotMake.CommandLine;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "apply", Parent = typeof(RootCliCommand), RequiredGroups = ["mode"],
            Description = "Patches a stock install so that it becomes KSP Retooled. You must pass either --out or --in-place, but not both. " +
                          "In either case, the command only patches a DLC if that DLC is installed.")]
public class ApplyCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The stock KSP install to patch.")]
    public required string Install { get; set; }

    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to apply.")]
    public required string Patch { get; set; }

    [CliOption(Name = "--out", HelpName = "new-dir", Required = false, Group = "mode",
               Description = "Writes a patched copy of the install to <new-dir>, and leaves the original install unchanged.")]
    public string? Output { get; set; }

    [CliOption(Name = "--in-place", Group = "mode",
               Description = "Patches the install directly. Before the command replaces a stock file, it saves that file "
                           + $"in <ksp-dir>/{InstallJournal.DIRECTORY}, so that revert can restore it later.")]
    public bool InPlace { get; set; }

    public void Run()
    {
        new PatchApplier(PatchFile.Open(this.Patch), this.Install).Apply(new ApplyOptions { OutputDirectory = this.Output, Log = Console.Out });
    }
}
