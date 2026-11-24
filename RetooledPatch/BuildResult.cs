using JetBrains.Annotations;
using KSP.Retooled.Patch.Models.Manifest;

namespace KSP.Retooled.Patch;

[PublicAPI]
public sealed record BuildResult(PatchManifest Manifest, string Report);
