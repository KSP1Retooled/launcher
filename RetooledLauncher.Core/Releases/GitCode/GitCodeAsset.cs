using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases.GitCode;

internal sealed class GitCodeAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
