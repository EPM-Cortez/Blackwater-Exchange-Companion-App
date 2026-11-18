using System.Net;
using System.Text.Json;

namespace BlackwaterExchange;

/// <summary>What the window and tray show.</summary>
public sealed record EngineStatus
{
    public string? LinkedUser { get; init; }
    public bool Linked => LinkedUser is not null;
    public string? GameFolder { get; init; }
    public DateTimeOffset? LastUploadAt { get; init; }
    public string? Upload { get; init; }
    public DateTimeOffset? LastPricesAt { get; init; }
    public string? Prices { get; init; }
    public IReadOnlyList<string> Houses { get; init; } = [];
    public string? AddonVersion { get; init; }
    public string? Addon { get; init; }
    /// <summary>Something the player needs to act on, such as linking again or choosing the game folder.</summary>
    public string? Problem { get; init; }
    public bool Busy { get; init; }
}

/// <summary>
/// The app's background work. Uploads the ledger after WoW saves it (watching the saved files, with a check every
/// ten minutes in case a change was missed), refreshes prices every 15 minutes when the website has a newer scan,
/// and checks for addon updates every six hours. One job runs at a time. Network trouble is retried on the next
/// round; a removed key stops uploads until the player links again.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    public static readonly TimeSpan UploadEvery = TimeSpan.FromMinutes(10);
    // WoW writes the saved file on logout, exit and every UI reload (the addon's Sync button); upload soon after.
    public static readonly TimeSpan UploadDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PricesEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AddonEvery = TimeSpan.FromHours(6);

    private readonly AppStorage storage;
    private readonly DeviceKey key;
    private readonly HttpClient http;
    private readonly string version;
    private readonly Action<string> log;
    private readonly SemaphoreSlim work = new(1, 1);
    private readonly Lock statusGate = new();
    private FileSystemWatcher? watcher;
    // UTC ticks of a pending upload after WoW saved, or 0. Set from the file watcher's threads.
    private long uploadSoon;
    private DateTimeOffset nextUpload = DateTimeOffset.MinValue, nextPrices = DateTimeOffset.MinValue, nextAddon = DateTimeOffset.MinValue;
    private EngineStatus status = new();

    public Engine(AppStorage storage, HttpClient http, string version, Action<string>? log = null)
    {
        this.storage = storage;
        this.http = http;
        this.version = version;
        this.log = log ?? (_ => { });
        key = new DeviceKey(storage.Folder);
        Settings = storage.LoadSettings();
        try
        {
            _ = ApiClient.ParseServer(Settings.ServerUrl);
        }
        catch (ArgumentException)
        {
            Settings = Settings with { ServerUrl = AppSettings.DefaultServer };
            this.log("Invalid server setting. Using the default Blackwater Exchange server.");
        }
        var state = storage.LoadState();
        status = new EngineStatus
        {
            LinkedUser = state.LinkedUser is not null && key.Read(Api().Server) is not null ? state.LinkedUser : null,
            GameFolder = Settings.GameFolder,
            LastUploadAt = state.LastUploadAt,
            LastPricesAt = state.LastPricesAt,
        };
    }

    public AppSettings Settings { get; private set; }

    public EngineStatus Status
    {
        get { lock (statusGate) return status; }
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised for things worth a Windows notification: (title, text).</summary>
    public event Action<string, string>? Notify;

    private void SetStatus(Func<EngineStatus, EngineStatus> change)
    {
        EngineStatus updated;
        lock (statusGate)
            updated = status = change(status);
        StatusChanged?.Invoke(updated);
    }

    private ApiClient Api() => new(http, Settings.ServerUrl, version);

    public string DeviceName => string.IsNullOrWhiteSpace(Settings.DeviceName) ? Environment.MachineName : Settings.DeviceName!;

    /// <summary>Saves new settings and runs whatever they switched on.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        var folderChanged = !string.Equals(settings.GameFolder, Settings.GameFolder, StringComparison.OrdinalIgnoreCase);
        var server = ApiClient.ParseServer(settings.ServerUrl);
        var originChanged = !ApiClient.IsSameOrigin(Api().Server, server);
        Settings = settings;
        storage.SaveSettings(settings);
        SetStatus(s => s with
        {
            GameFolder = settings.GameFolder,
            LinkedUser = originChanged ? null : s.LinkedUser,
            AddonVersion = folderChanged ? null : s.AddonVersion,
            Addon = folderChanged ? null : s.Addon,
            Problem = null,
        });
        if (folderChanged)
            Watch();
        nextUpload = nextPrices = nextAddon = DateTimeOffset.MinValue;
    }

    /// <summary>The game folder, finding it the first time. Null (with a problem shown) when there is none.</summary>
    private GameFolder? Game()
    {
        if (Settings.GameFolder is { } chosen && GameFolder.LooksRight(chosen))
            return new GameFolder(chosen);
        var found = GameFolder.Find();
        if (found.Count == 0)
        {
            SetStatus(s => s with { Problem = "WoW Forever was not found. Choose its _forever_ folder in Settings." });
            return null;
        }
        log($"Found WoW Forever at {found[0]}");
        UpdateSettings(Settings with { GameFolder = found[0] });
        return new GameFolder(found[0]);
    }

    // Linking.

    /// <summary>Links this PC: shows the code (the window opens the website), waits for approval, keeps the key.</summary>
    public async Task<string> LinkAsync(Action<LinkStart> showCode, CancellationToken cancellationToken)
    {
        var api = Api();
        var (token, userName) = await api.LinkAsync(DeviceName, showCode, cancellationToken);
        key.Save(token, api.Server);
        Save(state => state with { LinkedUser = userName, UploadedFiles = new(StringComparer.OrdinalIgnoreCase) });
        SetStatus(s => s with { LinkedUser = userName, Problem = null });
        log($"Linked to {userName}");
        nextUpload = nextPrices = DateTimeOffset.MinValue;
        return userName;
    }

    /// <summary>Stops local uploads and returns whether the website confirmed revocation.</summary>
    public async Task<bool> UnlinkAsync(CancellationToken cancellationToken)
    {
        var api = Api();
        var revoked = !key.Exists;
        if (key.Read(api.Server) is { } token)
        {
            try
            {
                await api.UnlinkAsync(token, cancellationToken);
                revoked = true;
            }
            catch (Exception exception) when (exception is HttpRequestException or ApiException or TaskCanceledException)
            {
                log($"Could not tell the website about unlinking: {exception.Message}");
            }
        }
        Forget();
        SetStatus(s => s with { Problem = revoked ? null : "Uploads stopped on this PC. Remove this device on the website to revoke its key." });
        return revoked;
    }

    private void Forget()
    {
        key.Delete();
        Save(state => state with { LinkedUser = null });
        SetStatus(s => s with { LinkedUser = null });
    }

    /// <summary>Asks the website whose key this is; forgets the key if it was removed there.</summary>
    public async Task RefreshAccountAsync(CancellationToken cancellationToken)
    {
        var api = Api();
        if (key.Read(api.Server) is not { } token)
            return;
        try
        {
            var account = await api.MeAsync(token, cancellationToken);
            Save(state => state with { LinkedUser = account.UserName });
            SetStatus(s => s with { LinkedUser = account.UserName });
        }
        catch (UnlinkedException)
        {
            Unlinked();
        }
    }

    private void Unlinked()
    {
        Forget();
        SetStatus(s => s with { Problem = "This PC was removed from your account on the website. Link it again to sync prices or upload your ledger." });
        Notify?.Invoke("Blackwater Exchange", "This PC is no longer linked to your account. Open the app to link it again.");
        log("The device key was removed on the website");
    }

    private void Save(Func<AppState, AppState> change) => storage.SaveState(change(storage.LoadState()));

    // The three jobs. Each can also be run from the window's "Check now".

    public Task UploadNowAsync(CancellationToken cancellationToken) => RunJob("upload", async () =>
    {
        nextUpload = DateTimeOffset.UtcNow + UploadEvery;
        Interlocked.Exchange(ref uploadSoon, 0);
        var api = Api();
        if (!Settings.UploadLedger || key.Read(api.Server) is not { } token || Game() is not { } game)
            return;
        var (uploads, state) = await new LedgerUploader(api).UploadChangedAsync(game.SavedFiles(), token, storage.LoadState(),
            cancellationToken);
        storage.SaveState(state);
        var done = uploads.Where(upload => upload.Outcome == "uploaded").ToList();
        var refused = uploads.FirstOrDefault(upload => upload.Outcome is "refused" or "too_large");
        if (uploads.Any(upload => upload.Outcome is "settling" or "busy"))
            UploadSoon();
        var entries = done.Sum(upload => upload.Result!.EntriesAdded);
        var sales = done.Sum(upload => upload.Result!.SalesAdded);
        string? message = done.Count > 0
            ? entries + sales == 0 ? "Uploaded. Nothing new since the last upload."
                : $"Uploaded {entries} gold {(entries == 1 ? "change" : "changes")} and {sales} {(sales == 1 ? "auction" : "auctions")}."
            : refused?.Problem ?? (uploads.Count == 0 ? "No saved file yet. Log in to WoW with the addon, then log out." : "Up to date");
        SetStatus(s => s with { LastUploadAt = state.LastUploadAt, Upload = message });
        if (done.Count > 0)
            log(message);
    });

    public Task UpdatePricesNowAsync(CancellationToken cancellationToken) => RunJob("prices", async () =>
    {
        nextPrices = DateTimeOffset.UtcNow + PricesEvery;
        if (!Settings.KeepPrices)
            return;
        var api = Api();
        if (key.Read(api.Server) is not { } token)
        {
            SetStatus(s => s with { Prices = "Link this PC to update auction prices." });
            return;
        }
        if (Game() is not { } game)
            return;
        var houses = PriceUpdater.HousesFor(game.SavedFiles());
        var outcome = await new PriceUpdater(api).UpdateAsync(game, houses, storage.LoadState(), token, cancellationToken);
        storage.SaveState(outcome.State);
        var message = outcome.Result switch
        {
            "written" => $"Updated {outcome.Written.Count} houses" + (outcome.Skipped.Count == 0 ? "" : $". Skipped {string.Join(", ", outcome.Skipped)} (freshness or account limit)"),
            "up_to_date" => "Up to date",
            "house_limit" => "This account has reached its daily house limit. Existing prices have been kept.",
            "no_characters" => "Log in once with the addon so the app knows your realms.",
            _ => "The website has no recent scan for your houses yet.",
        };
        SetStatus(s => s with { Prices = message, LastPricesAt = outcome.State.LastPricesAt, Houses = houses.Select(h => h.ToString()).ToList() });
        if (outcome.Result == "written")
            log($"Prices written for {string.Join(", ", outcome.Written)}");
    });

    public Task RefreshInstalledAddonVersionAsync() => RunJob("addon", () =>
    {
        var installed = Game()?.InstalledAddonVersion();
        SetStatus(s => s with { AddonVersion = installed });
        return Task.CompletedTask;
    });

    public Task UpdateAddonNowAsync(CancellationToken cancellationToken, bool manual = false) => RunJob("addon", async () =>
    {
        nextAddon = DateTimeOffset.UtcNow + AddonEvery;
        if (Game() is not { } game)
            return;
        if (!manual && !Settings.KeepAddon)
        {
            SetStatus(s => s with { AddonVersion = game.InstalledAddonVersion() });
            return;
        }
        var outcome = await new AddonInstaller(Api(), Path.Combine(storage.Folder, "downloads")).UpdateAsync(game, cancellationToken);
        Save(state => state with { LastAddonCheckAt = DateTimeOffset.UtcNow });
        var message = outcome.Result switch
        {
            "installed" => $"Blackwater installed {outcome.Installed}. Type /reload in WoW to load it.",
            "up_to_date" => $"No newer release on Blackwater ({outcome.Latest}).",
            "no_release" => "Blackwater has no addon download available.",
            _ => outcome.Problem,
        };
        SetStatus(s => s with { AddonVersion = game.InstalledAddonVersion(), Addon = message });
        if (outcome.Result == "installed")
        {
            log($"Forever Craft Assistant {outcome.Installed} installed");
            Notify?.Invoke("Forever Craft Assistant updated",
                game.IsGameRunning() ? $"Version {outcome.Installed} is installed. Type /reload in game to use it." : $"Version {outcome.Installed} is installed.");
        }
    });

    private async Task RunJob(string name, Func<Task> job)
    {
        await work.WaitAsync();
        SetStatus(s => s with { Busy = true });
        try
        {
            await job();
        }
        catch (UnlinkedException)
        {
            Unlinked();
        }
        catch (ApiException exception) when (exception.RateLimited)
        {
            log($"{name}: rate limited");
        }
        catch (Exception exception) when (exception is HttpRequestException or ApiException or IOException
                                              or UnauthorizedAccessException or InvalidDataException or JsonException
                                              or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException
                                              or OverflowException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            log($"{name} failed: {exception.Message}");
            var message = exception is HttpRequestException or TaskCanceledException
                ? "Could not reach blackwater.exchange. Trying again later." : exception.Message;
            SetStatus(s => name switch
            {
                "upload" => s with { Upload = message },
                "prices" => s with { Prices = message },
                _ => s with { Addon = message },
            });
        }
        finally
        {
            SetStatus(s => s with { Busy = false });
            work.Release();
        }
    }

    // The loop.

    private void Watch()
    {
        watcher?.Dispose();
        watcher = null;
        if (Settings.GameFolder is not { } folder || !GameFolder.LooksRight(folder))
            return;
        var accounts = new GameFolder(folder).AccountsFolder;
        if (!Directory.Exists(accounts))
            return;
        watcher = new FileSystemWatcher(accounts, GameFolder.SavedFileName)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        // WoW writes a new file and renames it into place; any of these means "saved".
        watcher.Changed += (_, _) => UploadSoon();
        watcher.Created += (_, _) => UploadSoon();
        watcher.Renamed += (_, _) => UploadSoon();
        watcher.EnableRaisingEvents = true;
    }

    private void UploadSoon() => Interlocked.Exchange(ref uploadSoon, (DateTimeOffset.UtcNow + UploadDelay).UtcTicks);

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Game();
        Watch();
        try
        {
            await RefreshAccountAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or ApiException or JsonException or FormatException
                                              or InvalidOperationException or KeyNotFoundException or TaskCanceledException
                                              { CancellationToken.IsCancellationRequested: false })
        {
            log($"Could not check the account: {exception.Message}");
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= nextAddon)
                await UpdateAddonNowAsync(cancellationToken);
            var soon = Interlocked.Read(ref uploadSoon);
            if (now >= nextUpload || (soon != 0 && now.UtcTicks >= soon))
                await UploadNowAsync(cancellationToken);
            if (now >= nextPrices)
                await UpdatePricesNowAsync(cancellationToken);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        watcher?.Dispose();
        work.Dispose();
        return ValueTask.CompletedTask;
    }
}
