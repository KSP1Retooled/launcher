using System.Text.Json.Serialization;

namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// One way to produce an output: identical to stock file <see cref="Same"/>, or <see cref="Segments"/> whose blobs
/// start at decompressed offset <see cref="Offset"/> of section <see cref="Section"/>.
/// </summary>
public sealed class Recipe
{
    public int? Same { get; set; }

    public int? Section { get; set; }

    public long? Offset { get; set; }

    public List<Segment>? Segments { get; set; }

    [JsonIgnore]
    public long ArchiveBytes => this.Segments?.Sum(s => s.ArchiveBytes) ?? 0;

    /// <summary>
    /// The <see cref="PatchManifest.Sources"/> this recipe reads.
    /// </summary>
    public IEnumerable<int> Inputs() => this.Same is { } same
                                            ? [same]
                                            : (this.Segments ?? []).SelectMany(s => s.Refs?.Select(r => r.File) ?? (s.File is { } f ? [f] : []));
}
