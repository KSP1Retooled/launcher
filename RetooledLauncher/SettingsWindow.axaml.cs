using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace KSP.Retooled.Launcher.App;

/// <summary>Edits a <see cref="SettingsViewModel"/>; closes with true when the user saves.</summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow() => AvaloniaXamlLoader.Load(this);

    void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
