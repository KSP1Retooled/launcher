using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Releases;

namespace KSP.Retooled.Launcher.App;

/// <summary>What the view model needs from the window.</summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickPatchFileAsync(string title);

    /// <summary>The text the user entered, or null if they cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string accept, string placeholder);
    Task<bool> ConfirmAsync(string title, string message, string accept);
    Task AlertAsync(string title, string message);

    /// <summary>Opens <paramref name="url"/> in the browser: false if that did not work.</summary>
    Task<bool> OpenUrlAsync(string url);
}

/// <summary>One row of the release list.</summary>
public sealed class ReleaseItem(Release release, bool installed, bool latest, bool hasBuild)
{
    public Release Release { get; } = release;
    public string Title => Release.DisplayName;
    public string Date =>
        Release.Published == DateTimeOffset.MinValue
            ? ""
            : Release.Published.LocalDateTime.ToString("yyyy-MM-dd");
    public bool IsPrerelease => Release.Prerelease;
    public bool IsInstalled { get; } = installed;
    public bool IsLatest { get; } = latest;
    public bool HasBuild { get; } = hasBuild;
    public bool NoBuild => !HasBuild;
}

public sealed partial class MainViewModel : ObservableObject
{
    readonly LauncherSettings settings;
    readonly IDialogs dialogs;
    readonly LauncherUpdater updater;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    readonly string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    readonly string platform = LauncherInfo.Platform;
    readonly StringBuilder log = new();
    ReleaseCatalog catalog = ReleaseCatalog.Empty;
    CancellationTokenSource? cancel;

    public MainViewModel(LauncherSettings settings, IDialogs dialogs)
    {
        this.settings = settings;
        this.dialogs = dialogs;
        updater = new LauncherUpdater(token);
        Append(Title);
        InstallPath = settings.InstallPath is string saved ? KspInstall.Inspect(saved).Path : "";
        IncludePrereleases = settings.IncludePrereleases;
        foreach (string channel in settings.AllChannels)
            Channels.Add(channel);
        Channel = settings.Repository ?? LauncherInfo.DefaultRepository;
        Inspect();
    }

    public string Title => $"KSP Retooled Launcher {LauncherInfo.Version}";
    public ObservableCollection<ReleaseItem> Releases { get; } = [];
    public ObservableCollection<string> DetectedInstalls { get; } = [];

    /// <summary>The built-in channels first, then the ones the user added.</summary>
    public ObservableCollection<string> Channels { get; } = [];

    [ObservableProperty]
    public partial string InstallPath { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    [ObservableProperty]
    public partial string Channel { get; set; }

    [ObservableProperty]
    public partial InstallInfo? Install { get; private set; }

    [ObservableProperty]
    public partial ReleaseItem? SelectedRelease { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Activity { get; private set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string ReleaseError { get; private set; } = "";

    [ObservableProperty]
    public partial string Log { get; private set; } = "";

    /// <summary>A newer launcher than this one, for the user to fetch because this one cannot update itself.</summary>
    [ObservableProperty]
    public partial Release? LauncherUpdate { get; private set; }

    /// <summary>The version of a newer launcher that has been downloaded and is waiting for a restart.</summary>
    [ObservableProperty]
    public partial string? DownloadedLauncher { get; private set; }

    public bool HasLauncherUpdate => LauncherUpdate != null;
    public bool HasDownloadedLauncher => DownloadedLauncher != null;

    public string? LauncherUpdateNotice =>
        DownloadedLauncher is string version ? $"Launcher {version} is ready to install."
        : LauncherUpdate is Release r ? $"Launcher {r.Tag} is available."
        : null;

    public string InstallState =>
        Install?.Status switch
        {
            null or InstallStatus.Missing when string.IsNullOrWhiteSpace(InstallPath) =>
                "Choose your KSP 1.12.5 folder.",
            null or InstallStatus.Missing => "That folder does not exist.",
            InstallStatus.NotKsp =>
                "That folder is not a KSP install (it needs GameData/Squad and the game).",
            InstallStatus.Stock => $"Stock KSP{Build}. KSP Retooled is not installed.",
            InstallStatus.Retooled =>
                $"KSP Retooled {InstalledName} is installed{Build}.",
            _ =>
                "An earlier install or uninstall did not finish. Uninstall to restore the stock game.",
        };

    string Build => Install?.BuildId is string id ? $" (build {id})" : "";

    string InstalledName =>
        Install?.Release switch
        {
            null => "(unknown version)",
            var r when r.IsFrom(LauncherInfo.LocalChannel) => $"{r.Tag} (local patch file)",
            var r when InstalledTag == "" => $"{r.Tag} (from {r.Channel})",
            var r => r.Tag,
        };

    /// <summary>What is installed, as the selected channel's <see cref="catalog"/> wants it.</summary>
    string? InstalledTag => Install?.InstalledTagIn(Channel);

    public string? UpdateNotice =>
        Install?.Status == InstallStatus.Retooled
        && catalog.UpgradeFor(InstalledTag, platform, IncludePrereleases) is Release r
            ? $"{r.Tag} is available."
            : null;

    public string ActionLabel =>
        SelectedRelease == null
            ? "Install"
            : catalog.ActionFor(SelectedRelease.Release, InstalledTag) switch
            {
                InstallAction.Upgrade => $"Upgrade to {SelectedRelease.Release.Tag}",
                InstallAction.Downgrade => $"Downgrade to {SelectedRelease.Release.Tag}",
                InstallAction.Reinstall => $"Reinstall {SelectedRelease.Release.Tag}",
                InstallAction.Replace => $"Switch to {SelectedRelease.Release.Tag}",
                _ => $"Install {SelectedRelease.Release.Tag}",
            };

    public bool HasDetectedInstalls => DetectedInstalls.Count > 1;
    public bool HasReleaseError => ReleaseError.Length > 0;
    public bool HasReleases => Releases.Count > 0;

    public string Notes =>
        SelectedRelease?.Release.Notes is { Length: > 0 } notes ? ReleaseNotes.LinkUrls(notes) : "";

    bool CanInstall =>
        !IsBusy
        && SelectedRelease is { HasBuild: true }
        && Install?.Status is InstallStatus.Stock or InstallStatus.Retooled;
    bool CanInstallFile =>
        !IsBusy && Install?.Status is InstallStatus.Stock or InstallStatus.Retooled;
    bool CanRemoveChannel => !IsBusy && !LauncherInfo.IsBuiltInChannel(Channel);
    bool CanUninstall =>
        !IsBusy && Install?.Status is InstallStatus.Retooled or InstallStatus.Interrupted;
    bool CanPlay => !IsBusy && Install?.Status == InstallStatus.Retooled;
    bool Idle => !IsBusy;

    public async Task StartAsync()
    {
        var update = CheckForLauncherUpdateAsync();
        if (string.IsNullOrWhiteSpace(InstallPath))
            await DetectAsync();
        await RefreshAsync();
        await update;
    }

    /// <summary>Failing is not worth interrupting anyone for: the log gets it, and the next start checks again.</summary>
    async Task CheckForLauncherUpdateAsync()
    {
        if (updater.CanUpdate)
        {
            await DownloadLauncherUpdateAsync();
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var releases = await new GitHubReleaseSource(
                http,
                LauncherInfo.Repository,
                token
            ).GetReleasesAsync(timeout.Token);
            LauncherUpdate = new ReleaseCatalog(releases).NewerThan(LauncherInfo.Version);
        }
        catch (Exception e)
            when (e
                    is LauncherException
                        or HttpRequestException
                        or OperationCanceledException
                        or System.Text.Json.JsonException
            )
        {
            Append($"could not check for a new launcher: {e.Message}");
        }
    }

    async Task DownloadLauncherUpdateAsync()
    {
        try
        {
            DownloadedLauncher = await updater.DownloadAsync();
        }
        // anything at all: Velopack does not say what it throws, and the launcher works fine without the update
        catch (Exception e)
        {
            Append($"could not update the launcher: {e.Message}");
        }
    }

    partial void OnInstallPathChanged(string value)
    {
        settings.InstallPath = string.IsNullOrWhiteSpace(value) ? null : value;
        Save();
        Inspect();
    }

    partial void OnIncludePrereleasesChanged(bool value)
    {
        settings.IncludePrereleases = value;
        Save();
        ShowReleases();
    }

    partial void OnChannelChanged(string value)
    {
        string? saved = value == LauncherInfo.DefaultRepository ? null : value;
        if (saved == settings.Repository)
            return;
        settings.Repository = saved;
        SaveChannels();
        catalog = ReleaseCatalog.Empty;
        ShowReleases();
        // not the command: it would refuse to start while the previous channel's listing is running
        _ = RefreshAsync();
    }

    partial void OnReleaseErrorChanged(string value) => OnPropertyChanged(nameof(HasReleaseError));

    partial void OnLauncherUpdateChanged(Release? value)
    {
        OnPropertyChanged(nameof(HasLauncherUpdate));
        OnPropertyChanged(nameof(LauncherUpdateNotice));
    }

    partial void OnDownloadedLauncherChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDownloadedLauncher));
        OnPropertyChanged(nameof(LauncherUpdateNotice));
    }

    partial void OnSelectedReleaseChanged(ReleaseItem? value) => Changed();

    partial void OnIsBusyChanged(bool value) => Changed();

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task BrowseAsync()
    {
        string? folder = await dialogs.PickFolderAsync("Choose your KSP 1.12.5 folder");
        if (folder != null)
            InstallPath = folder;
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task DetectAsync()
    {
        var found = await Task.Run(InstallLocator.Find);
        DetectedInstalls.Clear();
        foreach (var info in found)
            DetectedInstalls.Add(info.Path);
        OnPropertyChanged(nameof(HasDetectedInstalls));
        if (found.Count == 0)
            Append(
                "no KSP install found in the usual Steam and GOG folders; choose it with Browse"
            );
        else if (
            string.IsNullOrWhiteSpace(InstallPath)
            || KspInstall.Inspect(InstallPath).Status
                is InstallStatus.Missing
                    or InstallStatus.NotKsp
        )
            InstallPath = found[0].Path;
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task RefreshAsync()
    {
        string channel = Channel;
        ReleaseError = "";
        var found = ReleaseCatalog.Empty;
        string error = "";
        try
        {
            found = new ReleaseCatalog(await Source().GetReleasesAsync());
            if (found.All.Count == 0)
                error = $"{channel} has no releases yet.";
        }
        catch (Exception e)
            when (e
                    is LauncherException
                        or HttpRequestException
                        or TaskCanceledException
                        or System.Text.Json.JsonException
            )
        {
            error = $"Could not list releases: {e.Message}";
        }
        if (channel != Channel)
            return; // the user has moved on, and the listing of the channel they chose is on its way
        catalog = found;
        ReleaseError = error;
        ShowReleases();
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task AddChannelAsync()
    {
        string? channel = await dialogs.PromptAsync(
            "Add a release channel",
            "The GitHub repository whose releases to offer.",
            "Add",
            "owner/name"
        );
        if (string.IsNullOrWhiteSpace(channel))
            return;
        channel = channel.Trim();
        if (!GitHubReleaseSource.IsValidRepository(channel))
        {
            await dialogs.AlertAsync(
                "Add a release channel",
                $"'{channel}' is not a GitHub repository (owner/name)."
            );
            return;
        }
        string? known = Channels.FirstOrDefault(c =>
            c.Equals(channel, StringComparison.OrdinalIgnoreCase)
        );
        if (known == null)
        {
            Channels.Add(channel);
            SaveChannels();
        }
        Channel = known ?? channel;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    void RemoveChannel()
    {
        string removed = Channel;
        Channel = LauncherInfo.DefaultRepository;
        Channels.Remove(removed);
        SaveChannels();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    async Task InstallSelectedAsync()
    {
        if (SelectedRelease == null)
            return;
        var release = SelectedRelease.Release;
        var action = catalog.ActionFor(release, InstalledTag);
        if (
            release.Prerelease
            && !await dialogs.ConfirmAsync(
                "Pre-release",
                $"{release.Tag} is a pre-release and may be unstable. Back up your saves before playing it.",
                "Install anyway"
            )
        )
            return;
        if (
            action == InstallAction.Downgrade
            && !await dialogs.ConfirmAsync(
                "Downgrade",
                $"Saves made with {Install?.Release?.Tag} may not load in {release.Tag}.",
                "Downgrade"
            )
        )
            return;

        await RunAsync(
            ActionLabel,
            (service, progress, token) =>
                service.InstallAsync(InstallPath, release, catalog, progress, token)
        );
    }

    [RelayCommand(CanExecute = nameof(CanInstallFile))]
    async Task InstallFileAsync()
    {
        string? file = await dialogs.PickPatchFileAsync("Choose a KSP Retooled patch file");
        if (file == null)
            return;
        await RunAsync(
            $"Installing {Path.GetFileName(file)}",
            (service, progress, token) =>
                service.InstallFileAsync(InstallPath, file, catalog, progress, token)
        );
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    async Task UninstallAsync()
    {
        if (
            !await dialogs.ConfirmAsync(
                "Uninstall KSP Retooled",
                "This restores the stock KSP files. Your saves are kept, but saves made with Retooled do not load in stock KSP.",
                "Uninstall"
            )
        )
            return;
        await RunAsync(
            "Uninstalling",
            (service, progress, _) => service.UninstallAsync(InstallPath, progress)
        );
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    void Play()
    {
        try
        {
            GameStarter.Start(Install!.Path, platform);
        }
        catch (Exception e)
            when (e is LauncherException or System.ComponentModel.Win32Exception or IOException)
        {
            Append(e.Message);
        }
    }

    [RelayCommand]
    void Cancel() => cancel?.Cancel();

    [RelayCommand]
    async Task OpenLauncherUpdateAsync()
    {
        if (LauncherUpdate is not Release release)
            return;
        if (!await dialogs.OpenUrlAsync(release.PageUrl))
            await dialogs.AlertAsync(
                "New launcher",
                $"No browser could be opened. Launcher {release.Tag} is at {release.PageUrl}"
            );
    }

    /// <summary>Not while busy: restarting would stop an install half way.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    async Task RestartLauncherAsync()
    {
        try
        {
            updater.Restart();
        }
        catch (Exception e)
        {
            Append($"could not update the launcher: {e.Message}");
            await dialogs.AlertAsync(
                "New launcher",
                "The new launcher could not be installed. It will be tried again the next time the launcher starts."
            );
        }
    }

    /// <summary>A downloaded update is kept: the next start installs it on Windows and offers it again elsewhere.</summary>
    [RelayCommand]
    void DismissLauncherUpdate()
    {
        LauncherUpdate = null;
        DownloadedLauncher = null;
    }

    async Task RunAsync(
        string what,
        Func<
            LauncherService,
            IProgress<LauncherProgress>,
            CancellationToken,
            Task<InstallInfo>
        > work
    )
    {
        IsBusy = true;
        cancel = new CancellationTokenSource();
        Activity = what;
        ProgressIndeterminate = true;
        var progress = new Progress<LauncherProgress>(p =>
        {
            Activity = p.Stage;
            ProgressIndeterminate = p.Fraction == null;
            ProgressValue = (p.Fraction ?? 0) * 100;
        });
        var service = new LauncherService(
            new PatchCache(http, Source(), Path.Combine(LauncherInfo.DataDirectory, "cache")),
            platform,
            new LogWriter(this)
        );
        try
        {
            await work(service, progress, cancel.Token);
            Append($"{what}: done");
        }
        catch (OperationCanceledException)
        {
            Append($"{what}: cancelled");
        }
        catch (Exception e)
            when (e
                    is LauncherException
                        or Patch.PatchException
                        or HttpRequestException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            Append($"{what} failed: {e.Message}");
            if (e is UnauthorizedAccessException || e.InnerException is UnauthorizedAccessException)
                Append(
                    "the KSP folder is not writable by this user; move the game somewhere writable or run the launcher with more rights"
                );
            await dialogs.AlertAsync($"{what} failed", e.Message);
        }
        finally
        {
            cancel.Dispose();
            cancel = null;
            IsBusy = false;
            Activity = "";
            ProgressIndeterminate = false;
            ProgressValue = 0;
            Inspect();
            ShowReleases();
        }
    }

    GitHubReleaseSource Source() => new(http, Channel, token);

    void Inspect()
    {
        Install = string.IsNullOrWhiteSpace(InstallPath) ? null : KspInstall.Inspect(InstallPath);
        ShowReleases();
    }

    void ShowReleases()
    {
        string? installed = InstalledTag;
        string? previous = SelectedRelease?.Release.Tag;
        var latest = catalog.Latest(platform, IncludePrereleases);
        Releases.Clear();
        foreach (var r in catalog.Visible(IncludePrereleases, installed))
            Releases.Add(
                new ReleaseItem(r, r.Tag == installed, r == latest, r.PatchFor(platform) != null)
            );
        // keep the user's pick; otherwise offer the upgrade, else the installed release, else the newest
        SelectedRelease =
            Releases.FirstOrDefault(r => r.Release.Tag == previous)
            ?? Releases.FirstOrDefault(r =>
                r.Release == catalog.UpgradeFor(installed, platform, IncludePrereleases)
            )
            ?? Releases.FirstOrDefault(r => r.IsInstalled)
            ?? Releases.FirstOrDefault(r => r.HasBuild);
        OnPropertyChanged(nameof(HasReleases));
        Changed();
    }

    void Changed()
    {
        OnPropertyChanged(nameof(InstallState));
        OnPropertyChanged(nameof(UpdateNotice));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(Notes));
        BrowseCommand.NotifyCanExecuteChanged();
        DetectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        AddChannelCommand.NotifyCanExecuteChanged();
        RemoveChannelCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        InstallFileCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        RestartLauncherCommand.NotifyCanExecuteChanged();
    }

    void SaveChannels()
    {
        settings.Channels = [.. Channels.Where(c => !LauncherInfo.IsBuiltInChannel(c))];
        Save();
    }

    void Save()
    {
        try
        {
            settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Append($"could not save settings: {e.Message}");
        }
    }

    void Append(string line)
    {
        log.AppendLine(line);
        Log = log.ToString();
    }

    /// <summary>Feeds the patch library's log, written from worker threads, into <see cref="Log"/> a line at a time.</summary>
    sealed class LogWriter(MainViewModel model) : TextWriter
    {
        readonly StringBuilder line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r')
                return;
            string? done = null;
            lock (line)
            {
                if (value != '\n')
                {
                    line.Append(value);
                    return;
                }
                done = line.ToString();
                line.Clear();
            }
            Dispatcher.UIThread.Post(() => model.Append(done));
        }
    }
}
