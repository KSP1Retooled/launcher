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
        launchArguments = settings.LaunchArguments ?? "";
    }

    public string LaunchArgumentsDescription { get; } =
        "Command line arguments passed to the game when you press Play, separated by spaces. Put double quotes "
        + "around an argument that contains a space.";

    [ObservableProperty]
    string launchArguments;

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
        string? arguments = string.IsNullOrWhiteSpace(LaunchArguments)
            ? null
            : LaunchArguments.Trim();
        if (
            settings.PreferredMirror == SelectedMirror.Value
            && settings.LaunchArguments == arguments
        )
            return false;
        settings.PreferredMirror = SelectedMirror.Value;
        settings.LaunchArguments = arguments;
        return true;
    }
}
