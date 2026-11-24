namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// Core or a DLC. A DLC's outputs are produced only when its <see cref="Requires"/> directory is installed.
/// </summary>
public sealed class Component
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Install-relative directory that must exist for this component to apply (a DLC); null for core.
    /// </summary>
    public string? Requires { get; set; }

    /// <summary>
    /// Stock files of any source that the retooled install has no counterpart for; an in-place apply removes them.
    /// </summary>
    public List<string> Remove { get; set; } = [];

    /// <summary>
    /// Empty directories of the retooled install (stock's Missions, sounds, ...), created by every apply.
    /// </summary>
    public List<string> Directories { get; set; } = [];
}
