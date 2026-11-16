namespace BlackwaterExchange;

/// <summary>
/// app.log in the app's folder, for "what happened?" questions. Kept under 1 MB: when full it becomes app.old.log.
/// It never holds the device key, link codes or file contents.
/// </summary>
public sealed class FileLog(string folder)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock gate = new();

    public string Path => System.IO.Path.Combine(folder, "app.log");

    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes)
                    File.Move(Path, System.IO.Path.Combine(folder, "app.old.log"), overwrite: true);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never stop the app.
            }
        }
    }
}
