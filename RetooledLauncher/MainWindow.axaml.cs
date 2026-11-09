using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Patch;
using Markdown.Avalonia;

namespace KSP.Retooled.Launcher.App;

public sealed partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<MarkdownScrollViewer>("NotesView")!.Plugins = new MdAvPlugins
        {
            PathResolver = new NoImages(),
            HyperlinkCommand = new AsyncRelayCommand<string>(url =>
                Release.IsWebLink(url) ? OpenUrlAsync(url!) : Task.CompletedTask
            ),
        };
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false }
        );
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPlayerAsync(string title, string player)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("KSP game") { Patterns = [player] }],
            }
        );
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPatchFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("KSP Retooled patch")
                    {
                        Patterns = [$"*{PatchFile.Extension}"],
                    },
                ],
            }
        );
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PromptAsync(
        string title,
        string message,
        string accept,
        string placeholder
    )
    {
        var input = new TextBox { PlaceholderText = placeholder };
        return await ShowAsync(title, message, accept, "Cancel", input) ? input.Text : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept) =>
        ShowAsync(title, message, accept, "Cancel");

    public Task AlertAsync(string title, string message) => ShowAsync(title, message, "OK", null);

    public async Task<bool> OpenUrlAsync(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && await Launcher.LaunchUriAsync(uri);

    /// <summary>A small modal message box, with <paramref name="input"/> under the message: true when <paramref name="accept"/> is pressed.</summary>
    async Task<bool> ShowAsync(
        string title,
        string message,
        string accept,
        string? decline,
        Control? input = null
    )
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
        var content = new StackPanel
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
        if (input != null)
            content.Children.Insert(1, input);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }
}
