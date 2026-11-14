using DotMake.CommandLine;

namespace KSP.Retooled.Patch.Cli;

// Every option has an explicit name, so that renaming a property can't change the command line.

[CliCommand(Name = "build", Parent = typeof(RootCliCommand), Description =
    "Builds one patch file that turns each --source install into the --target build. The command also writes a report " +
    "next to the patch file, named <file>.report.txt.")]
public class BuildCliCommand
{
    /// <summary>The defaults that <see cref="BuildOptions"/> uses for the options that it doesn't require.</summary>
    internal static readonly BuildOptions Defaults = new() { Sources = [], Target = "", Output = "" };

    [CliOption(Name = "--source", HelpName = "[name=]dirs", Required = true, Description =
        "A stock install that the patch should accept. Repeat --source once for each install. If one install is split " +
        "across several directories, such as a game depot and its DLC depots, list them all, separated by ';' on Windows " +
        "or ':' elsewhere. A file in a later directory replaces the file at the same path in an earlier one. If you leave " +
        "out the name, the command reads it from the install's buildID file (for example, steam-03190). If there is no " +
        "buildID file, it uses the directory name.")]
    public string[] Sources { get; set; } = [];

    [CliOption(Name = "--target", HelpName = "dir", Description = "The KSP Retooled build that the patch should produce.")]
    public required string Target { get; set; }

    [CliOption(Name = "--out", HelpName = "file", Description =
        "The patch file to write. If the name doesn't end in .rtpatch, the command adds it.")]
    public required string Output { get; set; }

    [CliOption(Name = "--platform", Required = false, AllowedValues = ["windows", "linux", "macos"], Description =
        "The platform that the patch is for. If you leave it out, the command looks at the game executable in --target to decide.")]
    public string? Platform { get; set; }

    [CliOption(Name = "--exclude", HelpName = "pattern", Required = false, Description =
        "Paths to leave out of every source and the target. A built-in list already leaves out saves, logs, settings.cfg " +
        "and similar files; these patterns add to that list. Patterns are paths relative to the top of the install. In a " +
        "pattern, '*' matches any characters, including '/', so it can match across directories. To give more than one " +
        "pattern, repeat the option or separate the patterns with commas.")]
    public string[] Excludes { get; set; } = [];

    [CliOption(Name = "--exclude-target", HelpName = "pattern", Required = false, Description =
        "Paths to leave out of the target only. These use the same patterns as --exclude.")]
    public string[] TargetExcludes { get; set; } = [];

    [CliOption(Name = "--copy", HelpName = "path", Required = false, Description =
        "Files or directories that the patch doesn't contain. Each one must be an exact path, not a pattern. When someone " +
        "applies the patch with --out, the command copies these paths unchanged from their install. To give more than one " +
        "path, repeat the option or separate the paths with commas.")]
    public string[] CopiedPaths { get; set; } = [];

    [CliOption(Name = "--jobs", HelpName = "n", Description =
        "How many worker threads to use. By default there is one per CPU core, or fewer if the machine doesn't have " +
        "enough memory for that many.")]
    public int Jobs { get; set; } = BuildCliCommand.Defaults.Jobs;

    [CliOption(Name = "--level", HelpName = "n", Description = "The zstd compression level.")]
    public int Level { get; set; } = BuildCliCommand.Defaults.Level;

    [CliOption(Name = "--window", HelpName = "n", Description =
        "The zstd window size, as a power of 2. For example, 27 is 128 MB. It must be between 10 and 31, which is 2 GB.")]
    public int Window { get; set; } = BuildCliCommand.Defaults.Window;

    public int Run()
    {
        if (Jobs < 1) return Program.Fail("--jobs must be at least 1.");
        if (Window is < 10 or > 31) return Program.Fail("--window must be between 10 and 31.");

        string path = Path.GetFullPath(Output);
        if (!path.EndsWith(PatchFile.Extension, StringComparison.OrdinalIgnoreCase)) path += PatchFile.Extension;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var built = PatchBuilder.Build(new BuildOptions
        {
            Sources = Sources.Select(ParseSource).ToList(),
            Target = Target,
            Output = path,
            Platform = Platform,
            Jobs = Jobs,
            Level = Level,
            Window = Window,
            Excludes = SplitCommas(Excludes),
            TargetExcludes = SplitCommas(TargetExcludes),
            CopiedPaths = SplitCommas(CopiedPaths),
            Log = Console.Out,
        });
        string report = Path.ChangeExtension(path, ".report.txt");
        File.WriteAllText(report, built.Report);
        Console.Write(built.Report);
        Console.WriteLine($"report: {report}");
        return 0;
    }

    static string[] SplitCommas(string[] values) =>
        values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();

    /// <summary>Splits <c>[name=]dir[;dir...]</c>. A drive letter's colon, or an '=' inside a path, doesn't separate a name.</summary>
    static BuildSource ParseSource(string value)
    {
        int split = value.IndexOf('=');
        string? name = split > 0 && !value[..split].Contains(Path.DirectorySeparatorChar) && !value[..split].Contains('/') ? value[..split] : null;
        string dirs = name == null ? value : value[(split + 1)..];
        return new BuildSource { Name = name, Roots = dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) };
    }
}

[CliCommand(Name = "info", Parent = typeof(RootCliCommand), Description =
    "Shows what a patch file contains: the platform, the source installs that it accepts, the files that it copies " +
    "unchanged, its compressed sections, and its DLC components.")]
public class InfoCliCommand
{
    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to describe.")]
    public required string Patch { get; set; }

    public int Run()
    {
        var m = PatchFile.Open(Patch).Manifest;
        Console.WriteLine(PatchFile.Describe(m));
        foreach (var variant in m.Variants)
        {
            var files = variant.Files.ToHashSet();
            int alternatives = m.Outputs.Count(x => x.Recipes.FindIndex(r => r.Inputs().All(files.Contains)) > 0);
            Console.WriteLine($"  source {variant.Id}: {variant.Files.Count} stock files, {alternatives} outputs use a recipe of its own");
        }
        foreach (string path in m.CopiedPaths)
            Console.WriteLine($"  copied whole: {path}");
        foreach (var section in m.Sections)
            Console.WriteLine($"  section {section.Name,-32} {section.Size / 1048576.0,10:F1} MB -> {section.Length / 1048576.0,8:F1} MB");
        foreach (var component in m.Components)
            Console.WriteLine($"  component {component.Name}: {m.Outputs.Count(x => m.Components[x.Component] == component)} outputs, "
                + $"removes {component.Remove.Count} stock files");
        return 0;
    }
}

[CliCommand(Name = "check", Parent = typeof(RootCliCommand), Description =
    "Reports which source the install matches, and whether the patch can be applied to it. This command doesn't change " +
    "any files. It exits with 0 if the patch can be applied, and with 1 if it can't.")]
public class CheckCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The KSP install to check.")]
    public required string Install { get; set; }

    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to check the install against.")]
    public required string Patch { get; set; }

    public int Run()
    {
        var check = new PatchApplier(PatchFile.Open(Patch), Install).Check(new ApplyOptions { Log = Console.Out });
        foreach (string name in check.SkippedComponents) Console.WriteLine($"{name}: not installed");
        if (check.CanApply)
        {
            Console.WriteLine($"{Install} matches {check.Variant ?? "a mix of the known sources"}; the patch applies");
            return 0;
        }
        Console.WriteLine($"the patch does not apply to {Install}:\n  " + string.Join("\n  ", check.Problems));
        return 1;
    }
}

[CliCommand(Name = "apply", Parent = typeof(RootCliCommand), RequiredGroups = ["mode"], Description =
    "Patches a stock install so that it becomes KSP Retooled. You must pass either --out or --in-place, but not both. " +
    "In either case, the command only patches a DLC if that DLC is installed.")]
public class ApplyCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The stock KSP install to patch.")]
    public required string Install { get; set; }

    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to apply.")]
    public required string Patch { get; set; }

    [CliOption(Name = "--out", HelpName = "new-dir", Required = false, Group = "mode", Description =
        "Writes a patched copy of the install to <new-dir>, and leaves the original install unchanged.")]
    public string? Output { get; set; }

    [CliOption(Name = "--in-place", Group = "mode", Description =
        "Patches the install directly. Before the command replaces a stock file, it saves that file in <ksp-dir>/" +
        InstallJournal.Directory + ", so that revert can restore it later.")]
    public bool InPlace { get; set; }

    public int Run()
    {
        new PatchApplier(PatchFile.Open(Patch), Install).Apply(new ApplyOptions { OutputDirectory = Output, Log = Console.Out });
        return 0;
    }
}

[CliCommand(Name = "revert", Parent = typeof(RootCliCommand), Description =
    "Undoes an in-place apply, so that the install is stock again. Your saves and settings aren't changed.")]
public class RevertCliCommand
{
    [CliOption(Name = "--install", HelpName = "ksp-dir", Description = "The install that was patched with apply --in-place.")]
    public required string Install { get; set; }

    public void Run() => PatchReverter.Revert(Install, Console.Out);
}
