namespace KSP.Retooled.Patch.Models.Manifest;

public sealed class Extent
{
    public Extent() { }

    public Extent(int file, long offset, long length)
    {
        this.File   = file;
        this.Offset = offset;
        this.Length = length;
    }

    public int File { get; set; }

    public long Offset { get; set; }

    public long Length { get; set; }
}
