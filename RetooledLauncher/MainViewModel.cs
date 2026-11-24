using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSP.Retooled.Launcher.Exceptions;
using KSP.Retooled.Launcher.Installs;
using KSP.Retooled.Launcher.Patches;
using KSP.Retooled.Launcher.Releases;
using KSP.Retooled.Launcher.Releases.GitHub;
using KSP.Retooled.Launcher.Settings;
using KSP.Retooled.Launcher.Utils;
using KSP.Retooled.Patch.Exceptions;

namespace KSP.Retooled.Launcher.App;

/// <summary>
/// What the view model needs from the window.
/// </summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync(string title);

    /// <summary>
    /// The path of the file that the user chose, which has one of the names in <paramref name="players"/>. Null if they
    /// cancelled.
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

/// <summary>
/// One row of the release list.
/// </summary>
public sealed class ReleaseItem(Release release, bool installed, bool latest, bool hasBuild)
{
    public Release Release { get; } = release;

    public string Title => this.Release.DisplayName;

    public string Date => this.Release.Published != DateTimeOffset.MinValue
                              ? this.Release.Published.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                              : string.Empty;

    public bool IsPrerelease => this.Release.Prerelease;

    public bool IsInstalled { get; } = installed;

    public bool IsLatest { get; } = latest;

    public bool HasBuild { get; } = hasBuild;

    public bool NoBuild => !this.HasBuild;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogs dialogs;
    // a short connect timeout, so an unreachable GitHub turns to the mirror quickly
    private readonly HttpClient http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    private readonly StringBuilder log = new();
    private readonly GitHubMetadataCache metadata = new(Path.Combine(LauncherInfo.DataDirectory, "metadata"));
    private readonly LauncherSettings settings;
    private readonly string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    private readonly LauncherUpdater updater;
    private CancellationTokenSource? cancel;
    private ReleaseCatalog catalog = ReleaseCatalog.Empty;
    private CancellationTokenSource? releaseRefresh;

    public MainViewModel(LauncherSettings settings, IDialogs dialogs)
    {
        this.settings = settings;
        this.dialogs  = dialogs;
        this.updater  = new LauncherUpdater(this.token);
        Append(Title);
        this.InstallPath        = settings.InstallPath is { } saved ? KspInstall.Inspect(saved).Path : string.Empty;
        this.IncludePrereleases = settings.IncludePrereleases;
        foreach (string channel in settings.AllChannels)
        {
            this.Channels.Add(channel);
        }

        this.Channel = settings.Repository ?? LauncherInfo.DEFAULT_REPOSITORY;
        Inspect();
    }

    public static string Title => $"KSP Retooled Launcher {LauncherInfo.Version}";

    public ObservableCollection<ReleaseItem> Releases { get; } = [];

    public ObservableCollection<string> DetectedInstalls { get; } = [];

    /// <summary>
    /// The built-in channels first, then the ones the user added.
    /// </summary>
    public ObservableCollection<string> Channels { get; } = [];

    [ObservableProperty]
    public partial string InstallPath { get; set; }

    [ObservableProperty]
    private partial bool IncludePrereleases { get; set; }

    /// <summary>
    /// What the pre-release box shows: ticked, without changing the saved choice, while the channel has nothing but
    /// pre-releases and so offers them anyway.
    /// </summary>
    public bool ShowsPrereleases
    {
        get => this.IncludePrereleases || this.catalog.OnlyPrereleases;
        set => this.IncludePrereleases = value;
    }

    public bool CanChoosePrereleases => !this.catalog.OnlyPrereleases;

    [ObservableProperty]
    public partial string Channel { get; set; }

    [ObservableProperty]
    public partial InstallInfo? Install { get; private set; }

    [ObservableProperty]
    public partial ReleaseItem? SelectedRelease { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Activity { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool ProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string ReleaseError { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Log { get; private set; } = string.Empty;

    /// <summary>
    /// A newer launcher than this one, for the user to fetch because this one cannot update itself.
    /// </summary>
    [ObservableProperty]
    private partial Release? LauncherUpdate { get; set; }

    /// <summary>
    /// The version of a newer launcher that has been downloaded and is waiting for a restart.
    /// </summary>
    [ObservableProperty]
    private partial string? DownloadedLauncher { get; set; }

    public bool HasLauncherUpdate => this.LauncherUpdate is not null;
    public bool HasDownloadedLauncher => this.DownloadedLauncher is not null;

    public string? LauncherUpdateNotice => this.DownloadedLauncher is { } version
                                               ? $"Launcher {version} is ready to install."
                                               : this.LauncherUpdate is { } r
                                                   ? $"Launcher {r.Tag} is available."
                                                   : null;

    public string InstallState => this.Install?.Status switch
    {
        null or InstallStatus.MISSING
            when string.IsNullOrWhiteSpace(this.InstallPath) => "Choose your KSP 1.12.5 folder.",
        null or InstallStatus.MISSING => "That folder does not exist.",
        InstallStatus.NOT_KSP         => "That folder is not a KSP install (it needs GameData/Squad and the game).",
        InstallStatus.STOCK           => $"Stock KSP{this.Build}. KSP Retooled is not installed.",
        InstallStatus.RETOOLED        => $"KSP Retooled {this.InstalledName} is installed{this.Build}.",
        _                             => "An earlier install or uninstall did not finish. Uninstall to restore the stock game."
    };

    /// <summary>
    /// The platform of the patch that the launcher applies to the install, which is the install's own platform.
    /// </summary>
    private string PatchPlatform => KspInstall.PatchPlatform(this.Install, LauncherInfo.Platform);

    /// <summary>
    /// True if the launcher can start the game itself. False when the install is for another platform, such as the
    /// Windows version of KSP on Linux, which the user starts with Proton.
    /// </summary>
    private bool RunsHere => this.PatchPlatform == LauncherInfo.Platform;

    private string Build => this.Install?.BuildId is { } id ? $" (build {id})" : string.Empty;

    /// <summary>
    /// The tooltip on the Play button. It explains why Play is turned off for an install from another platform.
    /// </summary>
    public string? PlayTip => !this.RunsHere
                                  ? $"This is the {PlatformName(this.PatchPlatform)} version of KSP, so the launcher can't start it."
                                  : null;

    private string InstalledName => this.Install?.Release switch
    {
        null                                            => "(unknown version)",
        var r when r.IsFrom(LauncherInfo.LOCAL_CHANNEL) => $"{r.Tag} (local patch file)",
        var r when this.InstalledTag == string.Empty    => $"{r.Tag} (from {r.Channel})",
        var r                                           => r.Tag
    };

    /// <summary>
    /// What is installed, as the selected channel's <see cref="catalog"/> wants it.
    /// </summary>
    private string? InstalledTag => this.Install?.InstalledTagIn(this.Channel);

    private string? InstalledSha256 => this.Install?.Release?.Sha256;

    private Release? Upgrade => this.catalog.UpgradeFor(this.InstalledTag, this.PatchPlatform, this.IncludePrereleases, this.InstalledSha256);

    public string? UpdateNotice => this.Install?.Status is InstallStatus.RETOOLED && this.Upgrade is { } r
                                       ? r.Tag == this.InstalledTag
                                             ? $"{r.Tag} has a new build."
                                             : $"{r.Tag} is available."
                                       : null;

    public string ActionLabel => this.SelectedRelease is null
                                     ? "Install"
                                     : this.catalog.ActionFor(this.SelectedRelease.Release, this.InstalledTag, this.InstalledSha256) switch
                                     {
                                         InstallAction.UPGRADE   => $"Upgrade to {this.SelectedRelease.Release.Tag}",
                                         InstallAction.DOWNGRADE => $"Downgrade to {this.SelectedRelease.Release.Tag}",
                                         InstallAction.REINSTALL => $"Reinstall {this.SelectedRelease.Release.Tag}",
                                         InstallAction.REPLACE   => $"Switch to {this.SelectedRelease.Release.Tag}",
                                         InstallAction.UPDATE    => $"Update {this.SelectedRelease.Release.Tag}",
                                         _                       => $"Install {this.SelectedRelease.Release.Tag}"
                                     };

    public bool HasDetectedInstalls => this.DetectedInstalls.Count > 1;

    public bool HasReleaseError => this.ReleaseError.Length > 0;

    public bool HasReleases => this.Releases.Count > 0;

    public string Notes => this.SelectedRelease?.Release.Notes is { Length: > 0 } notes ? ReleaseNotes.LinkUrls(notes) : string.Empty;

    private bool CanInstall => !this.IsBusy
                            && this.SelectedRelease is { HasBuild: true }
                            && this.Install?.Status is InstallStatus.STOCK or InstallStatus.RETOOLED;

    private bool CanInstallFile => !this.IsBusy && this.Install?.Status is InstallStatus.STOCK
                                                                        or InstallStatus.RETOOLED;

    private bool CanRemoveChannel => !this.IsBusy && !LauncherInfo.IsBuiltInChannel(this.Channel);

    private bool CanUninstall => !this.IsBusy && this.Install?.Status is InstallStatus.RETOOLED
                                                                      or InstallStatus.INTERRUPTED;

    private bool CanPlay => !this.IsBusy && this.Install?.Status == InstallStatus.RETOOLED && this.RunsHere;

    private bool Idle => !this.IsBusy;

    private static string PlatformName(string platform) => platform switch
    {
        "windows" => "Windows",
        "linux"   => "Linux",
        "macos"   => "macOS",
        _         => platform
    };

    public async Task StartAsync()
    {
        Task update = CheckForLauncherUpdateAsync();
        if (string.IsNullOrWhiteSpace(this.InstallPath))
        {
            await DetectAsync();
        }

        await RefreshAsync();
        await update;
    }

    /// <summary>
    /// Failing is not worth interrupting anyone for: the log gets it, and the next start checks again.
    /// </summary>
    private async Task CheckForLauncherUpdateAsync()
    {
        if (this.updater.CanUpdate)
        {
            await DownloadLauncherUpdateAsync();
            return;
        }

        if (LauncherUpdater.OnReadOnlyVolume)
        {
            Append("the launcher can update itself once it is moved to Applications");
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            IReadOnlyList<Release> releases = await new GitHubReleaseSource(this.http,
                                                                            LauncherInfo.REPOSITORY, this.token, this.metadata
                                                                           ).GetReleasesAsync(timeout.Token);

            this.LauncherUpdate = new ReleaseCatalog(releases).NewerThan(LauncherInfo.Version);
        }
        catch (Exception e)
            when (e
                      is LauncherException
                      or HttpRequestException
                      or OperationCanceledException
                      or JsonException
                 )
        {
            Append($"could not check for a new launcher: {e.Message}");
        }
    }

    private async Task DownloadLauncherUpdateAsync()
    {
        try
        {
            this.DownloadedLauncher = await this.updater.DownloadAsync();
        }
        // anything at all: Velopack does not say what it throws, and the launcher works fine without the update
        catch (Exception e)
        {
            Append($"could not update the launcher: {e.Message}");
        }
    }

    partial void OnInstallPathChanged(string value)
    {
        this.settings.InstallPath = string.IsNullOrWhiteSpace(value) ? null : value;
        Save();
        Inspect();
    }

    partial void OnIncludePrereleasesChanged(bool value)
    {
        this.settings.IncludePrereleases = value;
        Save();
        ShowReleases();
    }

    partial void OnChannelChanged(string value)
    {
        string? saved = value == LauncherInfo.DEFAULT_REPOSITORY ? null : value;
        if (saved == this.settings.Repository)
        {
            return;
        }

        this.settings.Repository = saved;
        SaveChannels();
        this.catalog = ReleaseCatalog.Empty;
        ShowReleases();
        // not the command: it would refuse to start while the previous channel's listing is running
        _ = RefreshAsync();
    }

    partial void OnReleaseErrorChanged(string value) => OnPropertyChanged(nameof(this.HasReleaseError));

    partial void OnLauncherUpdateChanged(Release? value)
    {
        OnPropertyChanged(nameof(this.HasLauncherUpdate));
        OnPropertyChanged(nameof(this.LauncherUpdateNotice));
    }

    partial void OnDownloadedLauncherChanged(string? value)
    {
        OnPropertyChanged(nameof(this.HasDownloadedLauncher));
        OnPropertyChanged(nameof(this.LauncherUpdateNotice));
    }

    partial void OnSelectedReleaseChanged(ReleaseItem? value) => Changed();

    partial void OnIsBusyChanged(bool value) => Changed();

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task BrowseAsync()
    {
        string? folder;
        // KSP.app is a folder, which Avalonia's file picker leaves out of what it returns
        if (LauncherInfo.Platform is "macos")
        {
            folder = await this.dialogs.PickFolderAsync("Choose your KSP 1.12.5 folder");
        }
        else
        {
            // we leave out KSP.app because the file picker can't select a folder
            List<string> names =
            [
                ..KspInstall
                 .PlatformsOn(LauncherInfo.Platform)
                 .Where(p => p is not "macos")
                 .Select(KspInstall.Player)
            ];

            string? player = await this.dialogs.PickPlayerAsync($"Select {string.Join(" or ", names)} in your KSP 1.12.5 folder", names);
            folder = Path.GetDirectoryName(player);
        }

        if (folder is not null)
        {
            this.InstallPath = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task DetectAsync()
    {
        IReadOnlyList<InstallInfo> found = await Task.Run(InstallLocator.Find);
        this.DetectedInstalls.Clear();
        foreach (InstallInfo info in found)
        {
            this.DetectedInstalls.Add(info.Path);
        }

        OnPropertyChanged(nameof(this.HasDetectedInstalls));
        if (found.Count is 0)
        {
            Append("no KSP install found in the usual Steam and GOG folders; choose it with Browse");
        }
        else if (string.IsNullOrWhiteSpace(this.InstallPath)
              || KspInstall.Inspect(this.InstallPath).Status is InstallStatus.MISSING or InstallStatus.NOT_KSP)
        {
            this.InstallPath = found[0].Path;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task RefreshAsync()
    {
        CancellationTokenSource? previous = this.releaseRefresh;
        using CancellationTokenSource refresh = new();
        this.releaseRefresh = refresh;
        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        string channel = this.Channel;
        this.ReleaseError = string.Empty;
        ReleaseCatalog found = ReleaseCatalog.Empty;
        string error = string.Empty;
        try
        {
            found = new ReleaseCatalog(await Source().GetReleasesAsync(refresh.Token),
                                       LauncherInfo.IsVersionedChannel(channel));

            if (found.All.Count is 0)
            {
                error = $"{channel} has no releases yet.";
            }
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is LauncherException
                                    or HttpRequestException
                                    or TaskCanceledException
                                    or JsonException)
        {
            error = $"Could not list releases: {e.Message}";
        }
        finally
        {
            if (ReferenceEquals(this.releaseRefresh, refresh))
            {
                this.releaseRefresh = null;
            }
        }

        if (channel != this.Channel || refresh.IsCancellationRequested)
        {
            return; // the user has moved on, and the listing of the channel they chose is on its way
        }

        this.catalog      = found;
        this.ReleaseError = error;
        ShowReleases();
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task OpenSettingsAsync()
    {
        SettingsViewModel edited = new(this.settings);
        ReleaseMirror mirror = this.settings.PreferredMirror;
        if (!await this.dialogs.ShowSettingsAsync(edited) || !edited.ApplyTo(this.settings))
        {
            return;
        }

        Save();
        if (this.settings.PreferredMirror != mirror && LauncherInfo.MirrorOf(LauncherInfo.RepositoryOf(this.Channel)) is not null)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task AddChannelAsync()
    {
        string? channel = await this.dialogs.PromptAsync("Add a release channel",
                                                         "The GitHub repository whose releases to offer.",
                                                         "Add",
                                                         "owner/name");

        if (string.IsNullOrWhiteSpace(channel))
        {
            return;
        }

        channel = channel.Trim();
        if (!GitHubReleaseSource.IsValidRepository(channel))
        {
            await this.dialogs.AlertAsync("Add a release channel", $"'{channel}' is not a GitHub repository (owner/name).");
            return;
        }

        string? known = this.Channels.FirstOrDefault(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            this.Channels.Add(channel);
            SaveChannels();
        }

        this.Channel = known ?? channel;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    private void RemoveChannel()
    {
        string removed = this.Channel;
        this.Channel = LauncherInfo.DEFAULT_REPOSITORY;
        this.Channels.Remove(removed);
        SaveChannels();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedAsync()
    {
        if (this.SelectedRelease is null)
        {
            return;
        }

        Release release = this.SelectedRelease.Release;
        InstallAction action = this.catalog.ActionFor(release, this.InstalledTag, this.InstalledSha256);
        if (
            release.Prerelease
         && !await this.dialogs.ConfirmAsync("Pre-release",
                                             $"{release.Tag} is a pre-release and may be unstable. Your saves folder is copied to saves-backup before it is installed.",
                                             "Install anyway"))
        {
            return;
        }

        if (
            action == InstallAction.DOWNGRADE
         && !await this.dialogs.ConfirmAsync("Downgrade",
                                             $"Saves made with {this.Install?.Release?.Tag} may not load in {release.Tag}. Your saves folder is copied to saves-backup first.",
                                             "Downgrade")
        )
        {
            return;
        }

        await RunAsync(this.ActionLabel, (service, progress, cancellationToken) =>
                           service.InstallAsync(this.InstallPath, release, this.catalog, progress, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanInstallFile))]
    private async Task InstallFileAsync()
    {
        string? file = await this.dialogs.PickPatchFileAsync("Choose a KSP Retooled patch file");
        if (file is null)
        {
            return;
        }

        await RunAsync($"Installing {Path.GetFileName(file)}",
                       (service, progress, cancellationToken) =>
                           service.InstallFileAsync(this.InstallPath, file, this.catalog, progress, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!await this.dialogs.ConfirmAsync("Uninstall KSP Retooled",
                                             "This restores the stock KSP files. Your saves are kept, but saves made with Retooled do not load in stock KSP.",
                                             "Uninstall"))
        {
            return;
        }

        await RunAsync("Uninstalling",
                       (service, progress, _) => service.UninstallAsync(this.InstallPath, progress));
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        try
        {
            GameStarter.Start(this.Install!.Path, this.PatchPlatform, this.settings.LaunchArguments);
        }
        catch (Exception e)
            when (e is LauncherException or Win32Exception or IOException)
        {
            Append(e.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => this.cancel?.Cancel();

    [RelayCommand]
    private async Task OpenLauncherUpdateAsync()
    {
        if (this.LauncherUpdate is not { } release)
        {
            return;
        }

        if (!await this.dialogs.OpenUrlAsync(release.PageUrl))
        {
            await this.dialogs.AlertAsync("New launcher",
                                          $"No browser could be opened. Launcher {release.Tag} is at {release.PageUrl}");
        }
    }

    /// <summary>
    /// Not while busy: restarting would stop an install half way.
    /// </summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task RestartLauncherAsync()
    {
        try
        {
            this.updater.Restart();
        }
        catch (Exception e)
        {
            Append($"could not update the launcher: {e.Message}");
            await this.dialogs.AlertAsync("New launcher",
                                          "The new launcher could not be installed. It will be tried again the next time the launcher starts.");
        }
    }

    /// <summary>
    /// A downloaded update is kept: the next start installs it on Windows and offers it again elsewhere.
    /// </summary>
    [RelayCommand]
    private void DismissLauncherUpdate()
    {
        this.LauncherUpdate     = null;
        this.DownloadedLauncher = null;
    }

    private async Task RunAsync(string what,
                                Func<LauncherService,
                                    IProgress<LauncherProgress>,
                                    CancellationToken,
                                    Task<InstallInfo>> work)
    {
        this.IsBusy                = true;
        this.cancel                = new CancellationTokenSource();
        this.Activity              = what;
        this.ProgressIndeterminate = true;
        Progress<LauncherProgress> progress = new(p =>
        {
            this.Activity              = p.Stage;
            this.ProgressIndeterminate = p.Fraction is null;
            this.ProgressValue         = (p.Fraction ?? 0) * 100;
        });

        LauncherService service = new(new PatchCache(this.http, Source(), Path.Combine(LauncherInfo.DataDirectory, "cache")), this.PatchPlatform,
                                      new LogWriter(this));

        try
        {
            await work(service, progress, this.cancel.Token);
            Append($"{what}: done");
        }
        catch (OperationCanceledException)
        {
            Append($"{what}: cancelled");
        }
        catch (Exception e) when (e is LauncherException
                                    or PatchException
                                    or HttpRequestException
                                    or IOException
                                    or UnauthorizedAccessException)
        {
            Append($"{what} failed: {e.Message}");
            if (e is UnauthorizedAccessException || e.InnerException is UnauthorizedAccessException)
            {
                Append("the KSP folder is not writable by this user; move the game somewhere writable or run the launcher with more rights");
            }

            await this.dialogs.AlertAsync($"{what} failed", e.Message);
        }
        finally
        {
            this.cancel.Dispose();
            this.cancel                = null;
            this.IsBusy                = false;
            this.Activity              = string.Empty;
            this.ProgressIndeterminate = false;
            this.ProgressValue         = 0;
            Inspect();
            ShowReleases();
        }
    }

    private IReleaseSource Source() => MirroredReleaseSource.For(this.http, this.Channel, this.token, this.settings.PreferredMirror,
                                                                 line => Dispatcher.UIThread.Post(() => Append(line)), this.metadata);

    private void Inspect()
    {
        this.Install = string.IsNullOrWhiteSpace(this.InstallPath) ? null : KspInstall.Inspect(this.InstallPath);
        ShowReleases();
    }

    private void ShowReleases()
    {
        string? installed = this.InstalledTag;
        string? previous = this.SelectedRelease?.Release.Tag;
        Release? latest = this.catalog.Latest(this.PatchPlatform, this.IncludePrereleases);
        this.Releases.Clear();
        foreach (Release r in this.catalog.Visible(this.IncludePrereleases, installed))
        {
            this.Releases.Add(new ReleaseItem(r, r.Tag == installed, r == latest, r.PatchFor(this.PatchPlatform) is not null));
        }

        // keep the user's pick; otherwise offer the upgrade, else the installed release, else the newest
        this.SelectedRelease = this.Releases.FirstOrDefault(r => r.Release.Tag == previous)
                            ?? this.Releases.FirstOrDefault(r => r.Release == this.Upgrade)
                            ?? this.Releases.FirstOrDefault(r => r.IsInstalled)
                            ?? this.Releases.FirstOrDefault(r => r.HasBuild);

        OnPropertyChanged(nameof(this.HasReleases));
        OnPropertyChanged(nameof(this.ShowsPrereleases));
        OnPropertyChanged(nameof(this.CanChoosePrereleases));
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(this.InstallState));
        OnPropertyChanged(nameof(this.PlayTip));
        OnPropertyChanged(nameof(this.UpdateNotice));
        OnPropertyChanged(nameof(this.ActionLabel));
        OnPropertyChanged(nameof(this.Notes));
        this.BrowseCommand.NotifyCanExecuteChanged();
        this.DetectCommand.NotifyCanExecuteChanged();
        this.RefreshCommand.NotifyCanExecuteChanged();
        this.AddChannelCommand.NotifyCanExecuteChanged();
        this.OpenSettingsCommand.NotifyCanExecuteChanged();
        this.RemoveChannelCommand.NotifyCanExecuteChanged();
        this.InstallSelectedCommand.NotifyCanExecuteChanged();
        this.InstallFileCommand.NotifyCanExecuteChanged();
        this.UninstallCommand.NotifyCanExecuteChanged();
        this.PlayCommand.NotifyCanExecuteChanged();
        this.RestartLauncherCommand.NotifyCanExecuteChanged();
    }

    private void SaveChannels()
    {
        this.settings.Channels = [..this.Channels.Where(c => !LauncherInfo.IsBuiltInChannel(c))];
        Save();
    }

    private void Save()
    {
        try
        {
            this.settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Append($"could not save settings: {e.Message}");
        }
    }

    private void Append(string line)
    {
        this.log.AppendLine(line);
        this.Log = this.log.ToString();
    }

    /// <summary>
    /// Feeds the patch library's log, written from worker threads, into <see cref="Log"/> a line at a time.
    /// </summary>
    private sealed class LogWriter(MainViewModel model) : TextWriter
    {
        private readonly StringBuilder line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\r')
            {
                return;
            }

            string? done;
            lock (this.line)
            {
                if (value != '\n')
                {
                    this.line.Append(value);
                    return;
                }

                done = this.line.ToString();
                this.line.Clear();
            }

            Dispatcher.UIThread.Post(() => model.Append(done));
        }
    }
}
