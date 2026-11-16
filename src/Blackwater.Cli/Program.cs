using BlackwaterExchange;

// A developer tool for the app's engine, without the window:
//   blackwater [--server URL] [--data FOLDER] [--game FOLDER] status|find|link|unlink|upload|prices|addon|run
// --server and --game are saved to the data folder's settings.json, as the window would.

var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var commands = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index].StartsWith("--") && index + 1 < args.Length)
        options[args[index][2..]] = args[++index];
    else
        commands.Add(args[index].ToLowerInvariant());
}
var command = commands.FirstOrDefault() ?? "status";

var data = Path.GetFullPath(options.GetValueOrDefault("data") ?? AppStorage.DefaultFolder);
if (options.ContainsKey("server") && string.Equals(Path.TrimEndingDirectorySeparator(data),
        AppStorage.DefaultFolder, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Use --server with a separate --data folder for testing.");
    return 2;
}

var storage = new AppStorage(data);
var log = new FileLog(storage.Folder);
using var http = ApiClient.CreateHttpClient();
await using var engine = new Engine(storage, http, typeof(Engine).Assembly.GetName().Version?.ToString(3) ?? "0.1.0", message =>
{
    log.Write(message);
    Console.WriteLine($"  {message}");
});
var settings = engine.Settings;
if (options.TryGetValue("server", out var server))
{
    try
    {
        settings = settings with { ServerUrl = ApiClient.ParseServer(server).AbsoluteUri };
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }
}
if (options.TryGetValue("game", out var game))
    settings = settings with { GameFolder = game };
if (settings != engine.Settings)
    engine.UpdateSettings(settings);
engine.Notify += (title, text) => Console.WriteLine($"[{title}] {text}");

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

void Print(EngineStatus s)
{
    Console.WriteLine($"Server:   {engine.Settings.ServerUrl}");
    Console.WriteLine($"Data:     {storage.Folder}");
    Console.WriteLine($"Game:     {s.GameFolder ?? "not found"}");
    Console.WriteLine($"Linked:   {s.LinkedUser ?? "no"} (as {engine.DeviceName})");
    Console.WriteLine($"Upload:   {s.Upload ?? "-"}{(s.LastUploadAt is { } u ? $" (last {u.ToLocalTime():g})" : "")}");
    Console.WriteLine($"Prices:   {s.Prices ?? "-"}{(s.Houses.Count > 0 ? $" [{string.Join(", ", s.Houses)}]" : "")}");
    Console.WriteLine($"Addon:    {s.AddonVersion ?? "-"} {s.Addon}");
    if (s.Problem is not null)
        Console.WriteLine($"Problem:  {s.Problem}");
}

try
{
    switch (command)
    {
        case "status":
            await engine.RefreshAccountAsync(cancel.Token);
            Print(engine.Status);
            break;
        case "find":
            foreach (var folder in GameFolder.Find())
            {
                var found = new GameFolder(folder);
                Console.WriteLine($"{folder}: {found.SavedFiles().Count} saved files, addon {found.InstalledAddonVersion() ?? "not installed"}");
            }
            break;
        case "link":
            var user = await engine.LinkAsync(start =>
            {
                Console.WriteLine($"Open {start.VerificationUrl} and enter {start.UserCode}");
                Console.WriteLine($"  ({start.VerificationUrlComplete})");
                Console.WriteLine("Waiting for approval...");
            }, cancel.Token);
            Console.WriteLine($"Linked to {user}.");
            break;
        case "unlink":
            if (!await engine.UnlinkAsync(cancel.Token))
            {
                Console.Error.WriteLine(engine.Status.Problem);
                return 1;
            }
            Console.WriteLine("Unlinked.");
            break;
        case "upload":
            await engine.UploadNowAsync(cancel.Token);
            Print(engine.Status);
            break;
        case "prices":
            await engine.UpdatePricesNowAsync(cancel.Token);
            Print(engine.Status);
            break;
        case "addon":
            await engine.UpdateAddonNowAsync(cancel.Token, manual: true);
            Print(engine.Status);
            break;
        case "run":
            engine.StatusChanged += s =>
            {
                if (!s.Busy)
                    Console.WriteLine($"{DateTime.Now:T} upload: {s.Upload ?? "-"} | prices: {s.Prices ?? "-"} | addon: {s.AddonVersion} {s.Addon}");
            };
            Console.WriteLine("Running. Ctrl+C to stop.");
            await engine.RunAsync(cancel.Token);
            break;
        default:
            Console.WriteLine("Commands: status, find, link, unlink, upload, prices, addon, run");
            Console.WriteLine("Options: --server URL, --data FOLDER, --game FOLDER");
            return 2;
    }
    return 0;
}
catch (OperationCanceledException)
{
    return 1;
}
catch (Exception exception) when (exception is LinkFailedException or ApiException or HttpRequestException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
