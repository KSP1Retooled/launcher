namespace KSP.Retooled.Patch.Cli;

public static class Program
{
    static readonly string Usage = $"""
        retooled-patch: builds KSP Retooled from a stock KSP 1.12.5 install, and restores the stock install.

          retooled-patch build --source [<name>=]<dir>[{Path.PathSeparator}<dir>...] [--source ...] --target <retooled-build> --out <file>.rtpatch
                               [--platform windows|linux|macos] [--jobs N] [--level 19] [--window 27]
                               [--exclude <pattern>,...] [--exclude-target <pattern>,...] [--copy <path>,...]
              one patch file that turns any --source into the target, plus <file>.report.txt. A source's directories
              overlay each other (game depot, then DLC depots); its name defaults to its buildID file
              (e.g. steam-03190). Patterns match install-relative paths ('*' spans directories); --exclude applies
              to every tree, --exclude-target only to the build. A --copy, an exact file or directory, is left out
              of every tree too, and an apply to --out copies it from the install whole.
          retooled-patch info --patch <file>
              the platform, sources, sections and outputs of a patch file
          retooled-patch check --install <ksp-dir> --patch <file>
              which source the install matches and whether the patch applies; writes nothing
          retooled-patch apply --install <ksp-dir> --patch <file> --out <new-dir>
              writes the retooled install to <new-dir>; the stock install is not modified
          retooled-patch apply --install <ksp-dir> --patch <file> --in-place
              patches the install, keeping the stock files it replaces in <ksp-dir>/{InstallJournal.Directory}
          retooled-patch revert --install <ksp-dir>
              restores the stock install an in-place apply patched; saves and settings are left alone

        DLC outputs apply only when that DLC is installed.
        """;

    public static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["build", .. var rest] => Build(Options.Parse(rest)),
                ["info", .. var rest] => Info(Options.Parse(rest)),
                ["check", .. var rest] => Check(Options.Parse(rest)),
                ["apply", .. var rest] => Apply(Options.Parse(rest)),
                ["revert", .. var rest] => Revert(Options.Parse(rest)),
                _ => Fail(Usage),
            };
        }
        catch (Exception e) when (e is PatchException or InvalidDataException or IOException or UsageException)
        {
            return Fail(e.Message);
        }
    }

    static int Build(Options o)
    {
        var sources = o.All("source").Select(value =>
        {
            int split = value.IndexOf('=');
            // a drive letter's colon or a path's own '=' is not a name separator
            string? name = split > 0 && !value[..split].Contains(Path.DirectorySeparatorChar) && !value[..split].Contains('/') ? value[..split] : null;
            string dirs = name == null ? value : value[(split + 1)..];
            return new BuildSource { Name = name, Roots = dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) };
        }).ToList();
        if (sources.Count == 0) throw new UsageException("missing --source");
        string output = Path.GetFullPath(o.Required("out"));
        if (!output.EndsWith(PatchFile.Extension, StringComparison.OrdinalIgnoreCase)) output += PatchFile.Extension;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var defaults = new BuildOptions { Sources = sources, Target = "", Output = "" };
        var result = PatchBuilder.Build(new BuildOptions
        {
            Sources = sources,
            Target = o.Required("target"),
            Output = output,
            Platform = o.Optional("platform"),
            Jobs = o.Int("jobs") ?? defaults.Jobs,
            Level = o.Int("level") ?? defaults.Level,
            Window = o.Int("window") ?? defaults.Window,
            Excludes = Patterns(o.Optional("exclude")),
            TargetExcludes = Patterns(o.Optional("exclude-target")),
            CopiedPaths = Patterns(o.Optional("copy")),
            Log = Console.Out,
        });
        string report = Path.ChangeExtension(output, ".report.txt");
        File.WriteAllText(report, result.Report);
        Console.Write(result.Report);
        Console.WriteLine($"report: {report}");
        return 0;
    }

    static int Info(Options o)
    {
        var m = PatchFile.Open(o.Required("patch")).Manifest;
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

    static int Check(Options o)
    {
        string install = o.Required("install");
        var check = new PatchApplier(PatchFile.Open(o.Required("patch")), install).Check(new ApplyOptions { Log = Console.Out });
        foreach (string name in check.SkippedComponents) Console.WriteLine($"{name}: not installed");
        if (check.CanApply)
        {
            Console.WriteLine($"{install} matches {check.Variant ?? "a mix of the known sources"}; the patch applies");
            return 0;
        }
        Console.WriteLine($"the patch does not apply to {install}:\n  " + string.Join("\n  ", check.Problems));
        return 1;
    }

    static int Apply(Options o)
    {
        string? output = o.Optional("out");
        bool inPlace = o.Flag("in-place");
        if ((output == null) == !inPlace) throw new UsageException("pass exactly one of --out <dir> and --in-place");
        new PatchApplier(PatchFile.Open(o.Required("patch")), o.Required("install"))
            .Apply(new ApplyOptions { OutputDirectory = output, Log = Console.Out });
        return 0;
    }

    static int Revert(Options o)
    {
        PatchReverter.Revert(o.Required("install"), Console.Out);
        return 0;
    }

    static string[] Patterns(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

sealed class UsageException(string message) : Exception(message);

/// <summary><c>--key value</c> options (repeatable) and <c>--flag</c> switches.</summary>
sealed class Options
{
    static readonly string[] Flags = ["in-place"];
    readonly List<(string Key, string Value)> values = [];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"bad argument '{args[i]}'");
            string key = args[i][2..];
            if (Flags.Contains(key)) o.values.Add((key, "true"));
            else if (i + 1 < args.Length) o.values.Add((key, args[++i]));
            else throw new UsageException($"--{key} needs a value");
        }
        return o;
    }

    public IEnumerable<string> All(string key) => values.Where(v => v.Key == key).Select(v => v.Value);

    public string? Optional(string key) => All(key).LastOrDefault();

    public string Required(string key) => Optional(key) ?? throw new UsageException($"missing --{key}");

    public int? Int(string key) => Optional(key) is string v ? int.TryParse(v, out int n) ? n : throw new UsageException($"--{key} {v} is not a number") : null;

    public bool Flag(string key) => All(key).Any();
}
