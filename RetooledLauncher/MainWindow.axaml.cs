using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
            HyperlinkCommand = new AsyncRelayCommand<string>(url => Release.IsWebLink(url) ? OpenUrlAsync(url!) : Task.CompletedTask),
        };
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPlayerAsync(string title, IReadOnlyList<string> players)
    {
        IReadOnlyList<IStorageFile> files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                                                                                           {
                                                                                               Title = title,
                                                                                               AllowMultiple = false,
                                                                                               FileTypeFilter = [new FilePickerFileType("KSP game") { Patterns = players }],
                                                                                           });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPatchFileAsync(string title)
    {
        IReadOnlyList<IStorageFile> files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                                                                                           {
                                                                                               Title = title,
                                                                                               AllowMultiple = false,
                                                                                               FileTypeFilter =
                                                                                               [
                                                                                                   new FilePickerFileType("KSP Retooled patch")
                                                                                                   {
                                                                                                       Patterns = [$"*{PatchFile.EXTENSION}"],
                                                                                                   },
                                                                                               ],
                                                                                           });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PromptAsync(string title,
                                           string message,
                                           string accept,
                                           string placeholder)
    {
        TextBox input = new() { PlaceholderText = placeholder };
        return await ShowAsync(title, message, accept, "Cancel", input) ? input.Text : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept) => ShowAsync(title, message, accept, "Cancel");

    public Task AlertAsync(string title, string message) => ShowAsync(title, message, "OK", null);

    public async Task<bool> OpenUrlAsync(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && await this.Launcher.LaunchUriAsync(uri);

    public async Task<bool> ShowSettingsAsync(SettingsViewModel settings) => await new SettingsWindow { DataContext = settings }.ShowDialog<bool>(this);

    /// <summary>
    /// A small modal message box, with <paramref name="input"/> under the message: true when <paramref name="accept"/> is pressed.
    /// </summary>
    private async Task<bool> ShowAsync(string title,
                                       string message,
                                       string accept,
                                       string? decline,
                                       Control? input = null)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Button ok = new()
        {
            Content = accept,
            IsDefault = true,
            Classes = { "accent" },
        };
        ok.Click += (_, _) => dialog.Close(true);
        if (decline is not null)
        {
            Button cancel = new() { Content = decline, IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);
        StackPanel content = new()
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                },
                buttons,
            },
        };
        if (input is not null)
        {
            content.Children.Insert(1, input);
        }

        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }
}
