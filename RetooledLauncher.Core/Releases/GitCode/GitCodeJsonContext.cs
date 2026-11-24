using System.Text.Json.Serialization;

namespace KSP.Retooled.Launcher.Releases.GitCode;

[JsonSerializable(typeof(List<GitCodeRelease>))]
internal sealed partial class GitCodeJsonContext : JsonSerializerContext;
