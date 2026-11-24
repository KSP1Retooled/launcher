using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace KSP.Retooled.Launcher.App.Views;

/// <summary>
/// Edits a <see cref="ViewModels.SettingsViewModel"/>; closes with true when the user saves.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => AvaloniaXamlLoader.Load(this);

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
