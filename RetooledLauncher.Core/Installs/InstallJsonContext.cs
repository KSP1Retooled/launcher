using System.Text.Json.Serialization;
using KSP.Retooled.Launcher.Installs.Saves;

namespace KSP.Retooled.Launcher.Installs;

[JsonSerializable(typeof(InstalledRelease))]
[JsonSerializable(typeof(SavesBackupRecord)), JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class InstallJsonContext : JsonSerializerContext;
