using DotMake.CommandLine;
using JetBrains.Annotations;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Patcher;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "apply", Parent = typeof(RootCliCommand), RequiredGroups = ["mode"],
            Description = "Patches a stock install so that it becomes KSP Retooled. You must pass either --out or --in-place, "
                        + "but not both. In either case, the command only patches a DLC if that DLC is installed.")]
public sealed class ApplyCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The stock KSP install to patch.", ValidationRules = CliValidationRules.ExistingDirectory)]
    public required DirectoryInfo Install { get; set; }

    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to apply.", ValidationRules = CliValidationRules.ExistingFile)]
    public required FileInfo Patch { get; set; }

    [CliOption(Name = "--out", HelpName = "new-dir", Required = false, Group = "mode",
               Description = "Writes a patched copy of the install to <new-dir>, and leaves the original install unchanged.", ValidationRules = CliValidationRules.LegalPath)]
    public DirectoryInfo? Output { get; [UsedImplicitly] set; }

    [CliOption(Name = "--in-place", Group = "mode",
               Description = "Patches the install directly. Before the command replaces a stock file, it saves that file "
                           + $"in <ksp-dir>/{InstallJournal.DIRECTORY}, so that revert can restore it later.")]
    public bool InPlace { [UsedImplicitly] get; set; }

    public void Run()
    {
        new PatchApplier(PatchFile.Open(this.Patch.FullName), this.Install.FullName).Apply(new ApplyOptions { OutputDirectory = this.Output?.FullName, Log = Console.Out });
    }
}
