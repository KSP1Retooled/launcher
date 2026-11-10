using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;

namespace KSP.Retooled.Patch.Cli;

public static class Program
{
    /// <summary>The defaults that <see cref="BuildOptions"/> uses for the options that it doesn't require.</summary>
    static readonly BuildOptions Defaults = new() { Sources = [], Target = "", Output = "" };

    /// <summary>The longer descriptions that --help shows instead of the short ones that -h shows.</summary>
    static readonly ConditionalWeakTable<Symbol, string> Details = new();

    public static int Main(string[] args)
    {
        var result = Root().Parse(args);
        // Both help options clear parse errors, so a command can show its help even when its required options are missing.
        if (result.Errors.Count > 0 && result.Action is not (HelpAction or DetailedHelpAction))
        {
            foreach (var error in result.Errors) Console.Error.WriteLine(error.Message);
            var command = result.CommandResult.Command;
            string name = command is RootCommand ? "retooled-patch" : $"retooled-patch {command.Name}";
            Console.Error.WriteLine($"To see the options, run '{name} --help'.");
            return 2;
        }
        return result.Invoke();
    }

    static RootCommand Root()
    {
        var root = Describe(new RootCommand
        {
            BuildCommand(), InfoCommand(), CheckCommand(), ApplyCommand(), RevertCommand(),
        }, "Builds, checks, applies and reverts KSP Retooled patch files.", """
            retooled-patch is a tool for working with and applying .rtpatch files.
            """);
        root.Options.Remove(root.Options.OfType<HelpOption>().Single());
        root.Options.Add(new HelpOption("-h", "-?") { Recursive = true, Description = "Show short help." });
        root.Options.Add(new HelpOption("--help") { Recursive = true, Description = "Show detailed help.", Action = new DetailedHelpAction() });
        return root;
    }

    static Command BuildCommand()
    {
        var source = Describe(new Option<string[]>("--source") { HelpName = "[name=]dirs", Required = true },
            "A stock install that the patch should accept. Repeat --source once for each install.", $"""
            A stock install that the patch should accept. Repeat --source once for each install. If one install is
            split across several directories, such as a game depot and its DLC depots, list them all, separated
            by '{Path.PathSeparator}'. A file in a later directory replaces the file at the same path in an earlier one. If you
            leave out the name, the command reads it from the install's buildID file (for example, steam-03190).
            If there is no buildID file, it uses the directory name.
            """);
        var target = Describe(new Option<string>("--target") { HelpName = "dir", Required = true },
            "The KSP Retooled build that the patch should produce.");
        var output = Describe(new Option<string>("--out") { HelpName = "file", Required = true },
            "The patch file to write.",
            "The patch file to write. If the name doesn't end in .rtpatch, the command adds it.");
        var platform = Describe(new Option<string>("--platform"),
            "The platform that the patch is for.",
            "The platform that the patch is for. If you leave it out, the command looks at the game executable in --target to decide.");
        platform.AcceptOnlyFromAmong("windows", "linux", "macos");
        var exclude = Describe(new Option<string[]>("--exclude") { HelpName = "pattern" },
            "Paths to leave out of every source and the target.", """
            Paths to leave out of every source and the target. A built-in list already leaves out saves, logs,
            settings.cfg and similar files; these patterns add to that list. Patterns are paths relative to the
            top of the install. In a pattern, '*' matches any characters, including '/', so it can match across
            directories. To give more than one pattern, repeat the option or separate the patterns with commas.
            """);
        var excludeTarget = Describe(new Option<string[]>("--exclude-target") { HelpName = "pattern" },
            "Paths to leave out of the target only.",
            "Paths to leave out of the target only. These use the same patterns as --exclude.");
        var copy = Describe(new Option<string[]>("--copy") { HelpName = "path" },
            "Files or directories to copy unchanged from the install, instead of patching them.", """
            Files or directories that the patch doesn't contain. Each one must be an exact path, not a pattern.
            When someone applies the patch with --out, the command copies these paths unchanged from their install.
            To give more than one path, repeat the option or separate the paths with commas.
            """);
        var jobs = Describe(new Option<int>("--jobs") { HelpName = "n", DefaultValueFactory = _ => Defaults.Jobs },
            "How many worker threads to use.",
            "How many worker threads to use. By default there is one per CPU core, or fewer if the machine doesn't have enough memory for that many.");
        jobs.Validators.Add(r => { if (r.GetValueOrDefault<int>() < 1) r.AddError("--jobs must be at least 1."); });
        var level = Describe(new Option<int>("--level") { HelpName = "n", DefaultValueFactory = _ => Defaults.Level },
            "The zstd compression level.");
        var window = Describe(new Option<int>("--window") { HelpName = "n", DefaultValueFactory = _ => Defaults.Window },
            "The zstd window size, as a power of 2.",
            "The zstd window size, as a power of 2. For example, 27 is 128 MB. The largest allowed value is 31, which is 2 GB.");
        window.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int>() is < 10 or > 31) r.AddError("--window must be between 10 and 31.");
        });

        var command = Describe(new Command("build")
        {
            source, target, output, platform, exclude, excludeTarget, copy, jobs, level, window,
        }, "Build a patch file.", """
            Creates one patch file that turns each --source install into the --target build. The command also writes
            a report next to the patch file, named <file>.report.txt.
            """);
        command.SetAction(result => Run(() =>
        {
            var sources = result.GetRequiredValue(source).Select(ParseSource).ToList();
            string path = Path.GetFullPath(result.GetRequiredValue(output));
            if (!path.EndsWith(PatchFile.Extension, StringComparison.OrdinalIgnoreCase)) path += PatchFile.Extension;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var built = PatchBuilder.Build(new BuildOptions
            {
                Sources = sources,
                Target = result.GetRequiredValue(target),
                Output = path,
                Platform = result.GetValue(platform),
                Jobs = result.GetValue(jobs),
                Level = result.GetValue(level),
                Window = result.GetValue(window),
                Excludes = Patterns(result.GetValue(exclude)),
                TargetExcludes = Patterns(result.GetValue(excludeTarget)),
                CopiedPaths = Patterns(result.GetValue(copy)),
                Log = Console.Out,
            });
            string report = Path.ChangeExtension(path, ".report.txt");
            File.WriteAllText(report, built.Report);
            Console.Write(built.Report);
            Console.WriteLine($"report: {report}");
            return 0;
        }));
        return command;
    }

    static Command InfoCommand()
    {
        var patch = PatchOption("The patch file to describe.");
        var command = Describe(new Command("info") { patch }, "Show what a patch file contains.", """
            Shows what a patch file contains: the platform, the source installs that it accepts, the files that it
            copies unchanged, its compressed sections, and its DLC components.
            """);
        command.SetAction(result => Run(() =>
        {
            var m = PatchFile.Open(result.GetRequiredValue(patch)).Manifest;
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
        }));
        return command;
    }

    static Command CheckCommand()
    {
        var install = InstallOption("The KSP install to check.");
        var patch = PatchOption("The patch file to check the install against.");
        var command = Describe(new Command("check") { install, patch }, "Check whether a patch can be applied to an install.", """
            Reports which source the install matches, and whether the patch can be applied to it. This command doesn't
            change any files. It exits with 0 if the patch can be applied, and with 1 if it can't.
            """);
        command.SetAction(result => Run(() =>
        {
            string dir = result.GetRequiredValue(install);
            var check = new PatchApplier(PatchFile.Open(result.GetRequiredValue(patch)), dir).Check(new ApplyOptions { Log = Console.Out });
            foreach (string name in check.SkippedComponents) Console.WriteLine($"{name}: not installed");
            if (check.CanApply)
            {
                Console.WriteLine($"{dir} matches {check.Variant ?? "a mix of the known sources"}; the patch applies");
                return 0;
            }
            Console.WriteLine($"the patch does not apply to {dir}:\n  " + string.Join("\n  ", check.Problems));
            return 1;
        }));
        return command;
    }

    static Command ApplyCommand()
    {
        var install = InstallOption("The stock KSP install to patch.");
        var patch = PatchOption("The patch file to apply.");
        var output = Describe(new Option<string>("--out") { HelpName = "new-dir" },
            "Writes a patched copy of the install to <new-dir>.",
            "Writes a patched copy of the install to <new-dir>, and leaves the original install unchanged.");
        var inPlace = Describe(new Option<bool>("--in-place"),
            "Patches the install directly.", $"""
            Patches the install directly. Before the command replaces a stock file, it saves that file in
            <ksp-dir>/{InstallJournal.Directory}, so that revert can restore it later.
            """);
        var command = Describe(new Command("apply") { install, patch, output, inPlace }, "Apply a patch to a stock install.", """
            Patches a stock install so that it becomes KSP Retooled. You must pass either --out or --in-place, but not
            both. In either case, the command only patches a DLC if that DLC is installed.
            """);
        command.Validators.Add(r =>
        {
            if ((r.GetResult(output) == null) == (r.GetResult(inPlace) == null))
                r.AddError("Pass either --out or --in-place, but not both.");
        });
        command.SetAction(result => Run(() =>
        {
            new PatchApplier(PatchFile.Open(result.GetRequiredValue(patch)), result.GetRequiredValue(install))
                .Apply(new ApplyOptions { OutputDirectory = result.GetValue(output), Log = Console.Out });
            return 0;
        }));
        return command;
    }

    static Command RevertCommand()
    {
        var install = InstallOption("The install that was patched with apply --in-place.");
        var command = Describe(new Command("revert") { install }, "Undo an in-place apply.",
            "Undoes an in-place apply, so that the install is stock again. Your saves and settings aren't changed.");
        command.SetAction(result => Run(() =>
        {
            PatchReverter.Revert(result.GetRequiredValue(install), Console.Out);
            return 0;
        }));
        return command;
    }

    static Option<string> InstallOption(string description) =>
        Describe(new Option<string>("--install") { HelpName = "ksp-dir", Required = true }, description);

    static Option<string> PatchOption(string description) =>
        Describe(new Option<string>("--patch") { HelpName = "file", Required = true }, description);

    /// <summary>
    /// Sets the description that -h shows and, if there is one, the longer description that --help shows. The text is
    /// wrapped here only to keep the source readable, so its lines are joined; the help output wraps it again to fit
    /// the terminal.
    /// </summary>
    static T Describe<T>(T symbol, string summary, string? details = null) where T : Symbol
    {
        symbol.Description = summary.ReplaceLineEndings(" ");
        if (details != null) Details.AddOrUpdate(symbol, details.ReplaceLineEndings(" "));
        return symbol;
    }

    static string[] Patterns(string[]? values) => (values ?? [])
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();

    /// <summary>Splits <c>[name=]dir[;dir...]</c>. A drive letter's colon, or an '=' inside a path, doesn't separate a name.</summary>
    static BuildSource ParseSource(string value)
    {
        int split = value.IndexOf('=');
        string? name = split > 0 && !value[..split].Contains(Path.DirectorySeparatorChar) && !value[..split].Contains('/') ? value[..split] : null;
        string dirs = name == null ? value : value[(split + 1)..];
        return new BuildSource { Name = name, Roots = dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) };
    }

    /// <summary>Runs a command's action, and reports the errors that a bad install or patch file causes without a stack trace.</summary>
    static int Run(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (e is PatchException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>
    /// Shows the same help as -h, but with the longer descriptions of the command and its options. The commands that
    /// it lists keep their short descriptions.
    /// </summary>
    sealed class DetailedHelpAction : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var command = parseResult.CommandResult.Command;
            foreach (var symbol in command.Options.Prepend<Symbol>(command))
                if (Details.TryGetValue(symbol, out var details)) symbol.Description = details;
            return new HelpAction().Invoke(parseResult);
        }
    }
}
