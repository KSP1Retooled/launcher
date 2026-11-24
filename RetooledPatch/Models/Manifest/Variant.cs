namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// One source install the patch was built from, e.g. <c>steam-03190</c>, and the <see cref="PatchManifest.Sources"/>
/// it has.
/// </summary>
public sealed class Variant
{
    public string Id { get; set; } = string.Empty;

    public List<int> Files { get; set; } = [];
}
