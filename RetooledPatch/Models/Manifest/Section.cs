namespace KSP.Retooled.Patch.Models.Manifest;

/// <summary>
/// One independent zstd frame of the patch file holding raw blobs: <see cref="Offset"/> and <see cref="Length"/>
/// locate
/// it in the file, <see cref="Size"/> is its decompressed length.
/// </summary>
public sealed class Section
{
    public string Name { get; set; } = string.Empty;

    public long Offset { get; set; }

    public long Length { get; set; }

    public long Size { get; set; }
}
