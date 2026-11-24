namespace KSP.Retooled.Launcher.App.ViewModels;

/// <summary>
/// What the view model needs from the window.
/// </summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync(string title);

    /// <summary>
    /// The path of the file that the user chose, which has one of the names in <paramref name="players"/>.
    /// Null if they cancelled.
    /// </summary>
    Task<string?> PickPlayerAsync(string title, IReadOnlyList<string> players);

    Task<string?> PickPatchFileAsync(string title);

    /// <summary>
    /// The text the user entered, or null if they cancelled.
    /// </summary>
    Task<string?> PromptAsync(string title, string message, string accept, string placeholder);

    Task<bool> ConfirmAsync(string title, string message, string accept);

    Task AlertAsync(string title, string message);

    /// <summary>
    /// Opens <paramref name="url"/> in the browser: false if that did not work.
    /// </summary>
    Task<bool> OpenUrlAsync(string url);

    /// <summary>
    /// Shows the settings window: true if the user saved.
    /// </summary>
    Task<bool> ShowSettingsAsync(SettingsViewModel settings);
}
