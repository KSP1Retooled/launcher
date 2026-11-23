using System.Text.Json;
using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher;

/// <summary>
/// The launcher's choices, in <c>settings.json</c> under <see cref="LauncherInfo.DataDirectory"/>.
/// </summary>
public sealed class LauncherSettings
{
    public string? InstallPath { get; set; }

    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// The selected channel: the GitHub repository (<c>owner/name</c>) whose releases are offered, or null for
    /// <see cref="LauncherInfo.DEFAULT_REPOSITORY"/>.
    /// </summary>
    public string? Repository { get; set; }

    /// <summary>
    /// The channels the user added; <see cref="LauncherInfo.DEFAULT_REPOSITORY"/>, <see cref="LauncherInfo.DEBUG_CHANNEL"/>
    /// and <see cref="LauncherInfo.EXPERIMENTAL_CHANNEL"/> are always offered too.
    /// </summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>
    /// Where the default channel's releases are fetched from first; the other is the fallback.
    /// </summary>
    public ReleaseMirror PreferredMirror { get; set; }

    /// <summary>
    /// The command line arguments the game is started with, as the user typed them, or null for none. See
    /// <see cref="Installs.GameStarter.SplitArguments"/>.
    /// </summary>
    public string? LaunchArguments { get; set; }

    /// <summary>
    /// Every channel to offer, the built-in ones first, including a <see cref="Repository"/> that is not in <see cref="Channels"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> AllChannels =>
    [
        ..new[] { LauncherInfo.DEFAULT_REPOSITORY, LauncherInfo.DEBUG_CHANNEL, LauncherInfo.EXPERIMENTAL_CHANNEL }
         .Concat(this.Channels)
         .Concat(this.Repository is { } selected ? [selected] : [])
         .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    public static string DefaultFile => Path.Combine(LauncherInfo.DataDirectory, "settings.json");

    public static LauncherSettings Load(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize(
                                                  File.ReadAllBytes(file),
                                                  SettingsJson.Default.LauncherSettings
                                                 ) ?? new LauncherSettings();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new LauncherSettings();
    }

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes($"{file}.tmp", JsonSerializer.SerializeToUtf8Bytes(this, SettingsJson.Default.LauncherSettings));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

/// <summary>
/// The hosts that carry the default channel's releases.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReleaseMirror>))]
public enum ReleaseMirror
{
    // TODO csavard: proper json converter for these enums
    // ReSharper disable InconsistentNaming
    GitHub,
    GitCode,
    // ReSharper restore InconsistentNaming
}

[JsonSerializable(typeof(LauncherSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class SettingsJson : JsonSerializerContext;
