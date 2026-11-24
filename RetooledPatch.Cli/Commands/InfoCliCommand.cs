using DotMake.CommandLine;

namespace KSP.Retooled.Patch.Cli.Commands;

[CliCommand(Name = "info", Parent = typeof(RootCliCommand),
            Description = "Shows what a patch file contains: the platform, the source installs that it accepts, the files that it copies " +
                          "unchanged, its compressed sections, and its DLC components.")]
public class InfoCliCommand
{
    [CliOption(Name = "--patch", HelpName = "file", Description = "The patch file to describe.")]
    public required string Patch { get; set; }

    public int Run()
    {
        PatchManifest m = PatchFile.Open(this.Patch).Manifest;
        Console.WriteLine(PatchFile.Describe(m));
        foreach (Variant variant in m.Variants)
        {
            HashSet<int> files = [..variant.Files];
            int alternatives = m.Outputs.Count(x => x.Recipes.FindIndex(r => r.Inputs().All(files.Contains)) > 0);
            Console.WriteLine($"  source {variant.Id}: {variant.Files.Count} stock files, {alternatives} outputs use a recipe of its own");
        }

        foreach (string path in m.CopiedPaths)
        {
            Console.WriteLine($"  copied whole: {path}");
        }

        foreach (Section section in m.Sections)
        {
            Console.WriteLine($"  section {section.Name,-32} {section.Size / 1048576.0,10:F1} MB -> {section.Length / 1048576.0,8:F1} MB");
        }

        foreach (Component component in m.Components)
        {
            Console.WriteLine($"  component {component.Name}: {m.Outputs.Count(x => m.Components[x.Component] == component)} outputs, "
                            + $"removes {component.Remove.Count} stock files");
        }

        return 0;
    }
}
