using KSP.Retooled.Launcher.Common;

namespace KSP.Retooled.Patch;

public sealed class BuildOptions
{
    public const int DEFAULT_LEVEL = 19;

    public const int DEFAULT_WINDOW = 27;

    public required IReadOnlyList<BuildSource> Sources { get; init; }

    /// <summary>
    /// The retooled release build every source is patched into.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// The patch file to write.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>
    /// <c>windows</c>, <c>linux</c> or <c>macos</c>; detected from the target's player when null.
    /// </summary>
    public Platform? Platform { get; init; }

    public int Jobs { get; init; } = PatchBuilder.DefaultJobs;

    public int Level { get; init; } = DEFAULT_LEVEL;

    public int Window { get; init; } = DEFAULT_WINDOW;

    /// <summary>
    /// Install-relative patterns ('*' spans directories) left out of every tree, on top of the defaults.
    /// </summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    /// <summary>
    /// Patterns left out of the target only.
    /// </summary>
    public IReadOnlyList<string> TargetExcludes { get; init; } = [];

    /// <summary>
    /// Install-relative files and directories left out of every tree, which a side-by-side apply copies from the install.
    /// </summary>
    public IReadOnlyList<string> CopiedPaths { get; init; } = [];

    public TextWriter Log { get; init; } = TextWriter.Null;
}
