using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BlackwaterExchange.App;

/// <summary>
/// Starts the engine, the tray icon and the window. Started at sign-in it stays in the notification area unless
/// something needs the player (linking, finding the game); started by hand it opens the window.
///
/// Development switches: <c>--data FOLDER</c> uses another app folder, <c>--preview FILE.png</c> runs each job once,
/// draws the window to a PNG and exits (<c>--preview-linking</c> shows the linking panel instead).
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;
    private Engine? engine;
    private Tray? tray;
    private MainWindow? window;
    private HttpClient? http;
    private readonly CancellationTokenSource stop = new();
    private static FileLog? log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Option(string name) => args.SkipWhile(arg => !arg.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        var preview = Option("--preview");
        var shots = Option("--store-shots");
        if (Option("--assets") is { } assets)
        {
            StoreArt.WriteAssets(assets);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, error) =>
        {
            log?.Write($"Unexpected error: {error.Exception}");
            error.Handled = true;
            MessageBox.Show(error.Exception.Message, "Blackwater Exchange", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            log?.Write($"Background error: {error.Exception.GetBaseException().Message}");
            error.SetObserved();
        };

        if (preview is null && shots is null)
        {
            AppInfo.SetShellIdentity();
            instance = SingleInstance.Claim(() => Dispatcher.BeginInvoke(ShowWindow));
            if (instance is null)
            {
                Shutdown();
                return;
            }
        }

        var storage = new AppStorage(Option("--data") ?? AppStorage.DefaultFolder);
        log = new FileLog(storage.Folder);
        http = ApiClient.CreateHttpClient();
        engine = new Engine(storage, http, AppInfo.Version, log.Write);
        window = new MainWindow(engine, new ReleaseChecker(http, AppInfo.Version), stop.Token);

        if (shots is not null)
        {
            await RenderStoreShots(shots);
            return;
        }
        if (preview is not null)
        {
            await RenderPreview(preview, args.Contains("--preview-linking"));
            return;
        }

        log.Write($"App {AppInfo.Version} started{(StartWithWindows.IsPackaged ? " (Store package)" : "")}");
        tray = new Tray(engine, ShowWindow, Quit);
        engine.Notify += (title, text) => Dispatcher.BeginInvoke(() => tray.Notify(title, text));
        _ = Task.Run(async () =>
        {
            try
            {
                await engine.RunAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });
        window.StartVersionChecks();
        if (!StartWithWindows.StartedAtSignIn(args) || !engine.Status.Linked)
            ShowWindow();
    }

    private void ShowWindow()
    {
        if (window is null)
            return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit()
    {
        log?.Write("App quit");
        stop.Cancel();
        tray?.Dispose();
        if (window is not null)
        {
            window.Quitting = true;
            window.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        http?.Dispose();
        base.OnExit(e);
    }

    public static void OpenLog()
    {
        if (log is not null && File.Exists(log.Path))
            BlackwaterExchange.App.MainWindow.Open(log.Path);
    }

    /// <summary>Saves the Store screenshots with sample data: the linked window and the linking panel.</summary>
    private async Task RenderStoreShots(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            window!.Left = -10000;
            window.ShowActivated = false;
            window.Show();
            var content = (FrameworkElement)window.Content;
            async Task Shot(bool linking, string file)
            {
                window.ShowDemo(linking);
                window.Height = linking ? 1300 : 1120;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                StoreArt.WriteScreenshot(content, Path.Combine(folder, file));
            }
            await Shot(false, "1-overview.png");
            await Shot(true, "2-linking.png");
        }
        finally
        {
            window!.Quitting = true;
            window.Close();
            Shutdown();
        }
    }

    /// <summary>Runs each job once, then saves a picture of the window, off screen.</summary>
    private async Task RenderPreview(string file, bool linking)
    {
        try
        {
            await engine!.UpdateAddonNowAsync(CancellationToken.None);
            await engine.UpdatePricesNowAsync(CancellationToken.None);
            await engine.UploadNowAsync(CancellationToken.None);
            await window!.CheckVersionsAsync();
            window!.Left = -10000;
            // Tall enough to show every section.
            window.Height = 1080;
            window.ShowActivated = false;
            window.Show();
            if (linking)
                window.PreviewLinking("HDPH-N7W2");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var context = background.RenderOpen())
                context.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var stream = File.Create(file);
            encoder.Save(stream);
        }
        finally
        {
            window!.Quitting = true;
            window.Close();
            Shutdown();
        }
    }
}
