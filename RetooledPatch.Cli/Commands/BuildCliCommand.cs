using DotMake.CommandLine;
using KSP.Retooled.Patch.Patcher;
using RetooledLauncher.Common;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "build", Parent = typeof(RootCliCommand),
            Description = "Builds one patch file that turns each --source install into the --target build. "
                        + "The command also writes a report next to the patch file, named <file>.report.txt.")]
public class BuildCliCommand
{
    /// <summary>
    /// The defaults that <see cref="BuildOptions"/> uses for the options that it doesn't require.
    /// </summary>
    internal static readonly BuildOptions Defaults = new() { Sources = [], Target = string.Empty, Output = string.Empty };

    [CliOption(Name = "--source", HelpName = "[name=]dirs", Required = true,
               Description = "A stock install that the patch should accept. Repeat --source once for each install. If one install is split " +
                             "across several directories, such as a game depot and its DLC depots, list them all, separated by ';' on Windows " +
                             "or ':' elsewhere. A file in a later directory replaces the file at the same path in an earlier one. If you leave " +
                             "out the name, the command reads it from the install's buildID file (for example, steam-03190). If there is no " +
                             "buildID file, it uses the directory name.")]
    public string[] Sources { get; set; } = [];

    [CliOption(Name = "--target", HelpName = "dir", Description = "The KSP Retooled build that the patch should produce.")]
    public required string Target { get; set; }

    [CliOption(Name = "--out", HelpName = "file",
               Description = "The patch file to write. If the name doesn't end in .rtpatch, the command adds it.")]
    public required string Output { get; set; }

    [CliOption(Name = "--platform", Required = false, AllowedValues = ["windows", "linux", "macos"],
               Description = "The platform that the patch is for. If you leave it out, the command looks at the game executable in --target to decide.")]
    public Platform? Platform { get; set; }

    [CliOption(Name = "--exclude", HelpName = "pattern", Required = false,
               Description = "Paths to leave out of every source and the target. A built-in list already leaves out saves, logs, settings.cfg " +
                             "and similar files; these patterns add to that list. Patterns are paths relative to the top of the install. In a " +
                             "pattern, '*' matches any characters, including '/', so it can match across directories. To give more than one " +
                             "pattern, repeat the option or separate the patterns with commas.")]
    public string[] Excludes { get; set; } = [];

    [CliOption(Name = "--exclude-target", HelpName = "pattern", Required = false,
               Description = "Paths to leave out of the target only. These use the same patterns as --exclude.")]
    public string[] TargetExcludes { get; set; } = [];

    [CliOption(Name = "--copy", HelpName = "path", Required = false,
               Description = "Files or directories that the patch doesn't contain. Each one must be an exact path, not a pattern. When someone " +
                             "applies the patch with --out, the command copies these paths unchanged from their install. To give more than one " +
                             "path, repeat the option or separate the paths with commas.")]
    public string[] CopiedPaths { get; set; } = [];

    [CliOption(Name = "--jobs", HelpName = "n",
               Description = "How many worker threads to use. By default there is one per CPU core, or fewer if the machine doesn't have " +
                             "enough memory for that many.")]
    // ReSharper disable once ArrangeStaticMemberQualifier
    public int Jobs { get; set; } = BuildCliCommand.Defaults.Jobs;

    [CliOption(Name = "--level", HelpName = "n", Description = "The zstd compression level.")]
    // ReSharper disable once ArrangeStaticMemberQualifier
    public int Level { get; set; } = BuildCliCommand.Defaults.Level;

    [CliOption(Name = "--window", HelpName = "n",
               Description = "The zstd window size, as a power of 2. For example, 27 is 128 MB. It must be between 10 and 31, which is 2 GB.")]
    // ReSharper disable once ArrangeStaticMemberQualifier
    public int Window { get; set; } = BuildCliCommand.Defaults.Window;

    public int Run()
    {
        if (this.Jobs < 1) return Program.Fail("--jobs must be at least 1.");
        if (this.Window is < 10 or > 31) return Program.Fail("--window must be between 10 and 31.");

        string path = Path.GetFullPath(this.Output);
        if (!path.EndsWith(PatchFile.EXTENSION, StringComparison.OrdinalIgnoreCase))
        {
            path += PatchFile.EXTENSION;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        BuildResult built = PatchBuilder.Build(new BuildOptions
        {
            Sources        = [..this.Sources.Select(ParseSource)],
            Target         = this.Target,
            Output         = path,
            Platform       = this.Platform,
            Jobs           = this.Jobs,
            Level          = this.Level,
            Window         = this.Window,
            Excludes       = SplitCommas(this.Excludes),
            TargetExcludes = SplitCommas(this.TargetExcludes),
            CopiedPaths    = SplitCommas(this.CopiedPaths),
            Log            = Console.Out
        });

        string report = Path.ChangeExtension(path, ".report.txt");
        File.WriteAllText(report, built.Report);
        Console.Write(built.Report);
        Console.WriteLine($"report: {report}");
        return 0;
    }

    private static string[] SplitCommas(string[] values) =>
    [
        ..values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries
                                            | StringSplitOptions.TrimEntries))
    ];

    /// <summary>
    /// Splits <c>[name=]dir[;dir...]</c>. A drive letter's colon, or an '=' inside a path, doesn't separate a name.
    /// </summary>
    private static BuildSource ParseSource(string value)
    {
        int split = value.IndexOf('=');
        string? name = split > 0 && !value[..split].Contains(Path.DirectorySeparatorChar) && !value[..split].Contains('/') ? value[..split] : null;
        string dirs = name is null ? value : value[(split + 1)..];
        return new BuildSource { Name = name, Roots = dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) };
    }
}
