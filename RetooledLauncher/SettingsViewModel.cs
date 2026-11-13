using CommunityToolkit.Mvvm.ComponentModel;

namespace KSP.Retooled.Launcher.App;

/// <summary>A host to offer in the mirror list.</summary>
public sealed record MirrorOption(ReleaseMirror Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The settings window's working copy of <see cref="LauncherSettings"/>: nothing changes until the user saves, when
/// <see cref="ApplyTo"/> copies it back.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(LauncherSettings settings)
    {
        selectedMirror = Mirrors.First(m => m.Value == settings.PreferredMirror);
    }

    public string MirrorDescription { get; } =
        $"Where releases of the {LauncherInfo.DefaultRepository} channels are downloaded from. If it cannot be "
        + "reached, the launcher tries the other.";

    public IReadOnlyList<MirrorOption> Mirrors { get; } =
    [
        new(ReleaseMirror.GitHub, $"GitHub (github.com/{LauncherInfo.DefaultRepository})"),
        new(ReleaseMirror.GitCode, $"GitCode (gitcode.com/{LauncherInfo.DefaultMirror})"),
    ];

    [ObservableProperty]
    MirrorOption selectedMirror;

    /// <summary>Copies the choices into <paramref name="settings"/>: true if any changed.</summary>
    public bool ApplyTo(LauncherSettings settings)
    {
        if (settings.PreferredMirror == SelectedMirror.Value)
            return false;
        settings.PreferredMirror = SelectedMirror.Value;
        return true;
    }
}
