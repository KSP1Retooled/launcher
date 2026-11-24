namespace KSP.Retooled.Patch;

/// <summary>
/// A stock install the patch must apply to; later <see cref="Roots"/> overlay earlier ones (e.g. the game and DLC
/// depots).
/// </summary>
public sealed class BuildSource
{
    /// <summary>
    /// Defaults to <c>&lt;distribution&gt;-&lt;build id&gt;</c> from the install's buildID file, else the directory name.
    /// </summary>
    public string? Name { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }
}
