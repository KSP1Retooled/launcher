namespace KSP.Retooled.Patch.Patcher;

public sealed class ApplyOptions
{
    /// <summary>
    /// Destination for a separate patched install. The patcher hard-links unchanged files where possible
    /// and copies them otherwise. A null destination patches the stock install in place.
    /// </summary>
    public string? OutputDirectory { get; init; }

    public TextWriter Log { get; init; } = TextWriter.Null;

    public IProgress<PatchProgress>? Progress { get; init; }

    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Called with the destination of each move into or out of the install; lets the tests stop a commit half way.
    /// </summary>
    internal Action<string>? BeforeMove { get; init; }
}
