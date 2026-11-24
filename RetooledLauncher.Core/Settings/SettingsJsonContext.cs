using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Settings;

[JsonSerializable(typeof(LauncherSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
