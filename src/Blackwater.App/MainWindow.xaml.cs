using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace BlackwaterExchange.App;

/// <summary>
/// The app's one window. Closing it hides it to the notification area; the engine keeps working. Everything shown
/// comes from <see cref="Engine.Status"/> and <see cref="Engine.Settings"/>.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Dictionary<int, string> Rulesets = new()
    {
        [1] = "PvP", [2] = "Normal (PvE)", [3] = "Roleplay", [15] = "Roleplay PvP",
    };

    private readonly Engine engine;
    private readonly DispatcherTimer clock;
    private CancellationTokenSource? linking;
    private LinkStart? link;

    /// <summary>Set by the app when quitting, so closing really closes.</summary>
    public bool Quitting { get; set; }

    public MainWindow(Engine engine)
    {
        this.engine = engine;
        InitializeComponent();
        VersionText.Text = $"App {AppInfo.Version}";
        engine.StatusChanged += _ => Dispatcher.BeginInvoke(Render);
        // Keeps "5 minutes ago" true while the window is open.
        clock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        ShowSettings();
        Render();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!Quitting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // What the window shows.

    private void Render()
    {
        var status = engine.Status;
        BusyText.Visibility = status.Busy ? Visibility.Visible : Visibility.Collapsed;
        ProblemText.Text = status.Problem;
        ProblemBox.Visibility = status.Problem is null ? Visibility.Collapsed : Visibility.Visible;

        UnlinkedPanel.Visibility = !status.Linked && link is null ? Visibility.Visible : Visibility.Collapsed;
        LinkingPanel.Visibility = link is not null ? Visibility.Visible : Visibility.Collapsed;
        LinkedPanel.Visibility = status.Linked && link is null ? Visibility.Visible : Visibility.Collapsed;
        UserText.Text = status.LinkedUser ?? "";
        DeviceText.Text = $"as {engine.DeviceName}";
        UnlinkButton.Visibility = status.Linked ? Visibility.Visible : Visibility.Collapsed;

        var settings = engine.Settings;
        UploadButton.IsEnabled = status.Linked && settings.UploadLedger && !status.Busy;
        UploadText.Text = !status.Linked ? "Link this PC to upload your ledger."
            : !settings.UploadLedger ? "Off. Switch it on in Settings."
            : status.Upload ?? "Uploads when WoW saves: when you log out or exit, or press Sync in the addon's Ledger tab while playing.";
        UploadWhen.Text = status.LastUploadAt is { } uploaded ? $"Last upload {Ago(uploaded)}" : "";

        PricesButton.IsEnabled = settings.KeepPrices && !status.Busy;
        PricesText.Text = !settings.KeepPrices ? "Off. Switch it on in Settings."
            : status.Prices is null ? "Not checked yet."
            : status.LastPricesAt is { } prices ? $"{status.Prices}. Last written {Ago(prices)}." : status.Prices;
        HousesText.Text = Houses(status.Houses);

        AddonButton.IsEnabled = !status.Busy;
        var version = status.AddonVersion is null ? "Not installed" : $"Version {status.AddonVersion}";
        AddonText.Text = !settings.KeepAddon ? $"{version}. Updates are off." : status.Addon is null ? version : $"{version} · {status.Addon}";

        GameFolderText.Text = status.GameFolder ?? "Not found. Choose the _forever_ folder inside World of Warcraft.";
    }

    /// <summary>"Alliance, Horde and Blackwater · Normal (PvE)" per ruleset.</summary>
    private static string Houses(IReadOnlyList<string> keys)
    {
        var houses = keys.Select(MarketKey.Parse).OfType<MarketKey>().ToList();
        if (houses.Count == 0)
            return "";
        var lines = houses.GroupBy(house => house.Ruleset).Select(group =>
        {
            var factions = group.Select(house => house.Faction).OrderBy(f => f == "Blackwater" ? 1 : 0).ThenBy(f => f).ToList();
            var names = factions.Count == 1 ? factions[0] : string.Join(", ", factions[..^1]) + " and " + factions[^1];
            return $"{names} · {Rulesets.GetValueOrDefault(group.Key, $"ruleset {group.Key}")} ({group.First().Region.ToUpperInvariant()})";
        });
        return "Houses: " + string.Join("; ", lines);
    }

    internal static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h ago"
            : when.ToLocalTime().ToString("d MMM, HH:mm");
    }

    private void ShowSettings()
    {
        var settings = engine.Settings;
        UploadToggle.IsChecked = settings.UploadLedger;
        PricesToggle.IsChecked = settings.KeepPrices;
        AddonToggle.IsChecked = settings.KeepAddon;
        DeviceNameBox.Text = engine.DeviceName;
        if (StartWithWindows.IsPackaged)
        {
            // A Store app's start-up task is switched on and off by Windows, not by the app.
            StartupToggle.IsChecked = true;
            StartupToggle.IsEnabled = false;
            StartupSettingsLink.Visibility = Visibility.Visible;
        }
        else
        {
            StartupToggle.IsChecked = StartWithWindows.IsEnabled;
        }
    }

    // Linking.

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        linking = new CancellationTokenSource();
        LinkButton.IsEnabled = false;
        try
        {
            var user = await engine.LinkAsync(start => Dispatcher.Invoke(() =>
            {
                link = start;
                CodeText.Text = start.UserCode;
                LinkWaitText.Text = "Waiting for you to approve it…";
                Render();
                OpenLinkPage();
            }), linking.Token);
            link = null;
            Render();
            Activate();
        }
        catch (OperationCanceledException)
        {
            link = null;
            Render();
        }
        catch (Exception exception) when (exception is LinkFailedException or ApiException or HttpRequestException)
        {
            link = null;
            Render();
            MessageBox.Show(this, exception is HttpRequestException ? "Could not reach blackwater.exchange. Check your connection and try again." : exception.Message,
                "Linking stopped", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            LinkButton.IsEnabled = true;
            linking?.Dispose();
            linking = null;
        }
    }

    /// <summary>
    /// For Store screenshots: sample values in place of whoever runs the tool, so no real account, PC name or
    /// folder ends up in the Store.
    /// </summary>
    internal void ShowDemo(bool linking)
    {
        clock.Stop();
        Render();
        link = linking ? new LinkStart("HDPH-N7W2", "", "", "", 600, 5) : null;
        CodeText.Text = "HDPH-N7W2";
        UnlinkedPanel.Visibility = Visibility.Collapsed;
        LinkingPanel.Visibility = linking ? Visibility.Visible : Visibility.Collapsed;
        LinkedPanel.Visibility = linking ? Visibility.Collapsed : Visibility.Visible;
        UnlinkButton.Visibility = linking ? Visibility.Collapsed : Visibility.Visible;
        ProblemBox.Visibility = Visibility.Collapsed;
        BusyText.Visibility = Visibility.Collapsed;
        UserText.Text = "Tess";
        DeviceText.Text = "as GAMING-PC";
        DeviceNameBox.Text = "GAMING-PC";
        UploadButton.IsEnabled = !linking;
        UploadText.Text = linking ? "Link this PC to upload your ledger." : "Uploaded 42 gold changes and 6 auctions.";
        UploadWhen.Text = linking ? "" : "Last upload just now";
        PricesButton.IsEnabled = AddonButton.IsEnabled = true;
        PricesText.Text = "Updated 3 houses. Last written 4 min ago.";
        HousesText.Text = "Houses: Alliance, Horde and Blackwater · Normal (PvE) (EU)";
        AddonText.Text = "Version 1.7.2 · Up to date";
        GameFolderText.Text = @"C:\Program Files (x86)\World of Warcraft\_forever_";
        UploadToggle.IsChecked = PricesToggle.IsChecked = AddonToggle.IsChecked = StartupToggle.IsChecked = true;
        StartupToggle.IsEnabled = true;
        StartupSettingsLink.Visibility = Visibility.Collapsed;
    }

    /// <summary>For the --preview-linking picture: the linking panel with a sample code.</summary>
    internal void PreviewLinking(string code)
    {
        link = new LinkStart(code, "", "", "", 600, 5);
        CodeText.Text = code;
        Render();
    }

    private void OpenLinkPage()
    {
        if (link is not null)
            OpenOnServer(new Uri(link.VerificationUrlComplete));
    }

    private void OpenLinkPage_Click(object sender, RoutedEventArgs e) => OpenLinkPage();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (link is not null)
            Clipboard.SetText(link.UserCode);
    }

    private void CancelLink_Click(object sender, RoutedEventArgs e) => linking?.Cancel();

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop uploading from this PC? Your ledger stays on the website. You can link again at any time.",
                "Unlink this PC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (!await engine.UnlinkAsync(CancellationToken.None))
            MessageBox.Show(this, engine.Status.Problem, "Device key may still be active", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Jobs.

    private async void UploadNow_Click(object sender, RoutedEventArgs e) => await engine.UploadNowAsync(CancellationToken.None);
    private async void PricesNow_Click(object sender, RoutedEventArgs e) => await engine.UpdatePricesNowAsync(CancellationToken.None);
    private async void AddonNow_Click(object sender, RoutedEventArgs e) => await engine.UpdateAddonNowAsync(CancellationToken.None, manual: true);

    // Settings.

    private void Toggle_Click(object sender, RoutedEventArgs e) => engine.UpdateSettings(engine.Settings with
    {
        UploadLedger = UploadToggle.IsChecked == true,
        KeepPrices = PricesToggle.IsChecked == true,
        KeepAddon = AddonToggle.IsChecked == true,
    });

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        StartWithWindows.Set(StartupToggle.IsChecked == true);
        engine.UpdateSettings(engine.Settings with { StartWithWindows = StartupToggle.IsChecked == true });
    }

    private void StartupSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:startupapps");

    private void DeviceName_LostFocus(object sender, RoutedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        engine.UpdateSettings(engine.Settings with { DeviceName = name.Length == 0 || name == Environment.MachineName ? null : name });
        DeviceNameBox.Text = engine.DeviceName;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the _forever_ folder inside World of Warcraft",
            InitialDirectory = engine.Settings.GameFolder ?? "",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var folder = dialog.FolderName;
        // Choosing World of Warcraft itself is fine too.
        if (!GameFolder.LooksRight(folder) && GameFolder.LooksRight(System.IO.Path.Combine(folder, "_forever_")))
            folder = System.IO.Path.Combine(folder, "_forever_");
        if (!GameFolder.LooksRight(folder))
        {
            MessageBox.Show(this, "That is not WoW Forever's folder. Choose the _forever_ folder inside World of Warcraft.",
                "Not WoW Forever", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        engine.UpdateSettings(engine.Settings with { GameFolder = folder });
        _ = engine.UpdateAddonNowAsync(CancellationToken.None);
    }

    // Links.

    private void OpenWebsite_Click(object sender, RoutedEventArgs e) => Open(new Uri(ApiClient.ParseServer(engine.Settings.ServerUrl), "/").AbsoluteUri);
    private void OpenLedger_Click(object sender, RoutedEventArgs e) => OpenLedger(engine.Settings);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => App.OpenLog();

    internal static void OpenLedger(AppSettings settings) => Open(new Uri(ApiClient.ParseServer(settings.ServerUrl), "/ledger").AbsoluteUri);

    /// <summary>Opens a page on the configured server only, never an address from anywhere else.</summary>
    private void OpenOnServer(Uri page)
    {
        var server = ApiClient.ParseServer(engine.Settings.ServerUrl);
        if (ApiClient.IsSameOrigin(server, page))
            Open(page.AbsoluteUri);
    }

    internal static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
        }
    }
}
