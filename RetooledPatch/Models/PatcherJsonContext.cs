using System.Text.Json.Serialization;
using KSP.Retooled.Patch.Models.Journal;
using KSP.Retooled.Patch.Models.Manifest;

namespace KSP.Retooled.Patch.Models;

[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(InstallJournal))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
internal sealed partial class PatcherJsonContext : JsonSerializerContext;
