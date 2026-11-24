using KSP.Retooled.Patch.Models.Manifest;
using KSP.Retooled.Patch.Patcher;

namespace KSP.Retooled.Patch.Install;

/// <summary>
/// What <see cref="PatchApplier.Check"/> found: whether the install can be patched and which source it matches.
/// </summary>
public sealed class InstallCheck
{
    internal Recipe?[] chosen = [];

    internal Dictionary<string, string?> hashes = [];

    /// <summary>
    /// The source whose files the chosen recipes read, or null for a mix of sources.
    /// </summary>
    public string? Variant { get; init; }

    public bool AlreadyPatched { get; init; }

    /// <summary>
    /// Stock files that are missing or modified, leaving some output with no usable recipe.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public IReadOnlyList<string> SkippedComponents { get; init; } = [];

    public bool CanApply => !this.AlreadyPatched && this.Problems.Count is 0;
}
