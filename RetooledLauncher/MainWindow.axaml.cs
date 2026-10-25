using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace KSP.Retooled.Launcher.App;

public sealed partial class MainWindow : Window, IDialogs
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false }
        );
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept) =>
        ShowAsync(title, message, accept, "Cancel");

    public Task AlertAsync(string title, string message) => ShowAsync(title, message, "OK", null);

    /// <summary>A small modal message box: true when <paramref name="accept"/> is pressed.</summary>
    async Task<bool> ShowAsync(string title, string message, string accept, string? decline)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button
        {
            Content = accept,
            IsDefault = true,
            Classes = { "accent" },
        };
        ok.Click += (_, _) => dialog.Close(true);
        if (decline != null)
        {
            var cancel = new Button { Content = decline, IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                buttons,
            },
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
