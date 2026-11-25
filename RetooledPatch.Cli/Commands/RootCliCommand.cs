using DotMake.CommandLine;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Description = "Builds, checks, applies and reverts KSP Retooled patch files (.rtpatch).", ShortFormAutoGenerate = CliNameAutoGenerate.None)]
public sealed class RootCliCommand : ICliRunWithContext
{
    /// <inheritdoc />
    public void Run(CliContext cliContext)
    {
        cliContext.ShowHelp();
#if DEBUG
        cliContext.ShowHierarchy();
#endif
    }
}
