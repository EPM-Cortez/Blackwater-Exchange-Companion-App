using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackwaterExchange;

/// <summary>What the player chose. Saved as settings.json in the app's data folder.</summary>
public sealed record AppSettings
{
    public const string DefaultServer = "https://blackwater.exchange";

    public string ServerUrl { get; init; } = DefaultServer;
    /// <summary>The _forever_ folder. Null until found or chosen.</summary>
    public string? GameFolder { get; init; }
    /// <summary>The name the website shows for this PC.</summary>
    public string? DeviceName { get; init; }
    public bool UploadLedger { get; init; }
    public bool KeepPrices { get; init; } = true;
    public bool KeepAddon { get; init; }
    public bool StartWithWindows { get; init; } = true;
}

/// <summary>What the app has done, so it does not repeat it. Saved as state.json.</summary>
public sealed record AppState
{
    /// <summary>The account the app is linked to, as the website last reported it.</summary>
    public string? LinkedUser { get; init; }
    /// <summary>SHA-256 of each saved file as last uploaded, by path.</summary>
    public Dictionary<string, string> UploadedFiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastUploadAt { get; init; }
    /// <summary>When the scan in the installed price file was taken, by house.</summary>
    public Dictionary<string, DateTimeOffset> PriceCaptures { get; init; } = [];
    public DateTimeOffset? LastPricesAt { get; init; }
    public DateTimeOffset? LastAddonCheckAt { get; init; }
}

/// <summary>
/// The app's own folder (%LOCALAPPDATA%\Blackwater Exchange; inside the MSIX package's private copy when installed
/// from the Store) and its JSON files. Files are written beside the target and moved over it, so a crash never
/// leaves half a file.
/// </summary>
public sealed class AppStorage(string folder)
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blackwater Exchange");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock gate = new();

    public string Folder { get; } = folder;

    public AppSettings LoadSettings() => Load<AppSettings>("settings.json") ?? new AppSettings();
    public void SaveSettings(AppSettings settings) => Save("settings.json", settings);
    public AppState LoadState() => Load<AppState>("state.json") is { } state
        // JSON does not keep the dictionary's comparer; Windows paths are not case-sensitive.
        ? state with { UploadedFiles = new(state.UploadedFiles ?? [], StringComparer.OrdinalIgnoreCase) }
        : new AppState();
    public void SaveState(AppState state) => Save("state.json", state);

    private T? Load<T>(string name) where T : class
    {
        var path = Path.Combine(Folder, name);
        lock (gate)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
            }
            catch (JsonException)
            {
                // A damaged file starts over rather than stopping the app.
                return null;
            }
        }
    }

    private void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        lock (gate)
            WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    }

    /// <summary>Writes beside <paramref name="path"/>, then moves the new file over it.</summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        var pending = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
