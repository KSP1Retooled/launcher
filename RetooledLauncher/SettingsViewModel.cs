using CommunityToolkit.Mvvm.ComponentModel;

namespace KSP.Retooled.Launcher.App;

/// <summary>
/// A host to offer in the mirror list.
/// </summary>
public sealed record MirrorOption(ReleaseMirror Value, string Label)
{
    public override string ToString() => this.Label;
}

/// <summary>
/// The settings window's working copy of <see cref="LauncherSettings"/>: nothing changes until the user saves, when
/// <see cref="ApplyTo"/> copies it back.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(LauncherSettings settings)
    {
        this.SelectedMirror  = this.Mirrors.First(m => m.Value == settings.PreferredMirror);
        this.LaunchArguments = settings.LaunchArguments ?? string.Empty;
    }

    public static string LaunchArgumentsDescription => "Command line arguments passed to the game when you press Play, separated by spaces. Put double quotes "
                                                     + "around an argument that contains a space.";

    [ObservableProperty]
    public partial string LaunchArguments { get; set; }

    public static string MirrorDescription => $"Where releases of the {LauncherInfo.DEFAULT_REPOSITORY} channels are downloaded from. If it cannot be "
                                            + "reached, the launcher tries the other.";

    public IReadOnlyList<MirrorOption> Mirrors { get; } =
    [
        new(ReleaseMirror.GitHub, $"GitHub (github.com/{LauncherInfo.DEFAULT_REPOSITORY})"),
        new(ReleaseMirror.GitCode, $"GitCode (gitcode.com/{LauncherInfo.DEFAULT_MIRROR})")
    ];

    [ObservableProperty]
    public partial MirrorOption SelectedMirror { get; set; }

    /// <summary>
    /// Copies the choices into <paramref name="settings"/>: true if any changed.
    /// </summary>
    public bool ApplyTo(LauncherSettings settings)
    {
        string? arguments = !string.IsNullOrWhiteSpace(this.LaunchArguments)
                                ? this.LaunchArguments.Trim()
                                : null;

        if (settings.PreferredMirror == this.SelectedMirror.Value
         && settings.LaunchArguments == arguments)
        {
            return false;
        }

        settings.PreferredMirror = this.SelectedMirror.Value;
        settings.LaunchArguments = arguments;
        return true;
    }
}
