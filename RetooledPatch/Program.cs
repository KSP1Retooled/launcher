namespace KSP.Retooled.Patch;

public static class Program
{
    const string Usage = """
        RetooledPatch: builds KSP Retooled from a stock KSP 1.12.5 install.

          RetooledPatch apply --install <ksp-dir> --patch <dir> --out <new-dir>
              writes the retooled install to <new-dir>; the stock install is not modified
          RetooledPatch apply --install <ksp-dir> --patch <dir> --in-place
              replaces the stock install's files (cannot be undone yet)

        <dir> holds the *.rtpatch components; DLC components apply only when that DLC is installed.
        """;

    public static int Main(string[] args)
    {
        if (args is not ["apply", .. var rest]) return Fail(Usage);
        try
        {
            string? install = null, patch = null, output = null;
            bool inPlace = false;
            for (int i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--install" when i + 1 < rest.Length: install = rest[++i]; break;
                    case "--patch" when i + 1 < rest.Length: patch = rest[++i]; break;
                    case "--out" when i + 1 < rest.Length: output = rest[++i]; break;
                    case "--in-place": inPlace = true; break;
                    default: return Fail($"bad argument '{rest[i]}'\n\n{Usage}");
                }
            }
            if (install == null || patch == null || (output == null) == !inPlace) return Fail(Usage);

            var archives = Directory.GetFiles(patch, "*" + PatchArchive.Extension).Order(StringComparer.Ordinal).ToList();
            if (archives.Count == 0) return Fail($"no {PatchArchive.Extension} files in {patch}");
            new PatchApplier(install, Console.Out).Apply(archives, output);
            return 0;
        }
        catch (Exception e) when (e is PatchException or InvalidDataException or IOException)
        {
            return Fail(e.Message);
        }
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
