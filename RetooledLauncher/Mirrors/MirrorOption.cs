using KSP.Retooled.Launcher.Settings;

namespace KSP.Retooled.Launcher.App.Mirrors;

/// <summary>
/// A host to offer in the mirror list.
/// </summary>
public sealed record MirrorOption(ReleaseMirror Value, string Label)
{
    public override string ToString() => this.Label;
}
