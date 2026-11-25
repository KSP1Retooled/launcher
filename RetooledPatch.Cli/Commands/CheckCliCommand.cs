using DotMake.CommandLine;
using KSP.Retooled.Patch.Install;
using KSP.Retooled.Patch.Patcher;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "check", Parent = typeof(RootCliCommand),
            Description = "Reports which source the install matches, and whether the patch can be applied to it. "
                        + "This command doesn't change any files. It exits with 0 if the patch can be applied, "
                        + "and with 1 if it can't.")]
public sealed class CheckCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The KSP install to check.", ValidationRules = CliValidationRules.ExistingDirectory)]
    public required DirectoryInfo Install { get; set; }

    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to check the install against.", ValidationRules = CliValidationRules.ExistingFile)]
    public required FileInfo Patch { get; set; }

    public int Run()
    {
        InstallCheck check = new PatchApplier(PatchFile.Open(this.Patch.FullName), this.Install.FullName).Check(new ApplyOptions { Log = Console.Out });
        foreach (string name in check.SkippedComponents)
        {
            Console.WriteLine($"{name}: not installed");
        }

        if (check.CanApply)
        {
            Console.WriteLine($"{this.Install} matches {check.Variant ?? "a mix of the known sources"}; the patch applies");
            return 0;
        }

        Console.WriteLine($"the patch does not apply to {this.Install}:\n  " + string.Join("\n  ", check.Problems));
        return 1;
    }
}
