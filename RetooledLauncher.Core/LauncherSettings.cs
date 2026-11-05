using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher;

/// <summary>The launcher's choices, in <c>settings.json</c> under <see cref="LauncherInfo.DataDirectory"/>.</summary>
public sealed class LauncherSettings
{
    public string? InstallPath { get; set; }
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// The selected channel: the GitHub repository (<c>owner/name</c>) whose releases are offered, or null for
    /// <see cref="LauncherInfo.DefaultRepository"/>.
    /// </summary>
    public string? Repository { get; set; }

    /// <summary>
    /// The channels the user added; <see cref="LauncherInfo.DefaultRepository"/> and
    /// <see cref="LauncherInfo.DebugChannel"/> are always offered too.
    /// </summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>Every channel to offer, the built-in ones first, including a <see cref="Repository"/> that is not in <see cref="Channels"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllChannels =>
        [
            .. new[] { LauncherInfo.DefaultRepository, LauncherInfo.DebugChannel }
                .Concat(Channels)
                .Concat(Repository is string selected ? [selected] : [])
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

    public static string DefaultFile => Path.Combine(LauncherInfo.DataDirectory, "settings.json");

    public static LauncherSettings Load(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize(
                        File.ReadAllBytes(file),
                        SettingsJson.Default.LauncherSettings
                    ) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { }
        return new();
    }

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(
            file + ".tmp",
            JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.LauncherSettings)
        );
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
[JsonSerializable(typeof(LauncherSettings))]
sealed partial class SettingsJson : JsonSerializerContext;
