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
    Task<bool> ConfirmAsync(string title, string message, string accept);
    Task AlertAsync(string title, string message);
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
        InstallPath = settings.InstallPath is string saved ? KspInstall.Inspect(saved).Path : "";
        IncludePrereleases = settings.IncludePrereleases;
        Repository = settings.Repository ?? LauncherInfo.DefaultRepository;
        Inspect();
    }

    public string Title => $"KSP Retooled Launcher {LauncherInfo.Version}";
    public ObservableCollection<ReleaseItem> Releases { get; } = [];
    public ObservableCollection<string> DetectedInstalls { get; } = [];

    [ObservableProperty]
    public partial string InstallPath { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    [ObservableProperty]
    public partial string Repository { get; set; }

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
                $"KSP Retooled {Install?.Release?.Tag ?? "(unknown version)"} is installed{Build}.",
            _ =>
                "An earlier install or uninstall did not finish. Uninstall to restore the stock game.",
        };

    string Build => Install?.BuildId is string id ? $" (build {id})" : "";

    public string? UpdateNotice =>
        Install?.Status == InstallStatus.Retooled
        && catalog.UpgradeFor(Install.InstalledTag, platform, IncludePrereleases) is Release r
            ? $"{r.Tag} is available."
            : null;

    public string ActionLabel =>
        SelectedRelease == null
            ? "Install"
            : catalog.ActionFor(SelectedRelease.Release, Install?.InstalledTag) switch
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

    public string Notes => SelectedRelease?.Release.Notes is { Length: > 0 } notes ? notes : "";

    bool CanInstall =>
        !IsBusy
        && SelectedRelease is { HasBuild: true }
        && Install?.Status is InstallStatus.Stock or InstallStatus.Retooled;
    bool CanUninstall =>
        !IsBusy && Install?.Status is InstallStatus.Retooled or InstallStatus.Interrupted;
    bool CanPlay => !IsBusy && Install?.Status == InstallStatus.Retooled;
    bool Idle => !IsBusy;

    public async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(InstallPath))
            await DetectAsync();
        await RefreshAsync();
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

    partial void OnReleaseErrorChanged(string value) => OnPropertyChanged(nameof(HasReleaseError));

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
        string repository = Repository.Trim();
        string? saved = repository == LauncherInfo.DefaultRepository ? null : repository;
        if (saved != settings.Repository)
        {
            settings.Repository = saved;
            Save();
        }
        ReleaseError = "";
        try
        {
            var releases = await Source().GetReleasesAsync();
            catalog = new ReleaseCatalog(releases);
            if (catalog.All.Count == 0)
                ReleaseError = $"{repository} has no releases yet.";
        }
        catch (Exception e)
            when (e
                    is LauncherException
                        or HttpRequestException
                        or TaskCanceledException
                        or System.Text.Json.JsonException
            )
        {
            catalog = ReleaseCatalog.Empty;
            ReleaseError = $"Could not list releases: {e.Message}";
        }
        ShowReleases();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    async Task InstallSelectedAsync()
    {
        if (SelectedRelease == null)
            return;
        var release = SelectedRelease.Release;
        var action = catalog.ActionFor(release, Install?.InstalledTag);
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

    GitHubReleaseSource Source() => new(http, Repository.Trim(), token);

    void Inspect()
    {
        Install = string.IsNullOrWhiteSpace(InstallPath) ? null : KspInstall.Inspect(InstallPath);
        ShowReleases();
    }

    void ShowReleases()
    {
        string? installed = Install?.InstalledTag;
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
        InstallSelectedCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
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
