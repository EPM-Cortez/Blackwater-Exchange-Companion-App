using System.Security.Cryptography;

namespace BlackwaterExchange;

/// <summary>What happened to one saved file.</summary>
public sealed record FileUpload(string File, string Outcome, UploadResult? Result = null, string? Problem = null);

/// <summary>
/// Uploads Forever Craft Assistant's saved file for each game account whenever WoW has written a new one (on
/// logout, /reload or exit). A file is sent only once its content changes and it has been still for a few
/// seconds, so a half-written file is never sent. The website keeps the addon's IDs, so a repeat adds nothing anyway.
/// </summary>
public sealed class LedgerUploader(ApiClient api, Func<DateTimeOffset>? clock = null)
{
    /// <summary>The website's limit (Ledger/LedgerFile.cs MaxBytes).</summary>
    public const long MaxBytes = 16 * 1024 * 1024;

    /// <summary>How long a file must be unchanged before it is read.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Uploads each changed file. Returns what happened to each, and the state with the new hashes. Throws
    /// <see cref="UnlinkedException"/> when the key was removed, so the caller can ask the player to link again.
    /// </summary>
    public async Task<(IReadOnlyList<FileUpload> Uploads, AppState State)> UploadChangedAsync(IReadOnlyList<string> files,
        string token, AppState state, CancellationToken cancellationToken)
    {
        var uploads = new List<FileUpload>();
        var hashes = new Dictionary<string, string>(state.UploadedFiles, StringComparer.OrdinalIgnoreCase);
        var uploadedAny = false;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                continue;
            if (now() - info.LastWriteTimeUtc < Settle)
            {
                uploads.Add(new FileUpload(file, "settling"));
                continue;
            }
            if (info.Length > MaxBytes)
            {
                uploads.Add(new FileUpload(file, "too_large", Problem: "The saved file is over 16 MB, which the website does not accept."));
                continue;
            }
            byte[] bytes;
            try
            {
                // WoW may still hold the file; read it without locking WoW out.
                await using var stream = GameFolder.OpenSavedFile(file);
                var length = stream.Length;
                if (length > MaxBytes)
                {
                    uploads.Add(new FileUpload(file, "too_large", Problem: "The saved file is over 16 MB, which the website does not accept."));
                    continue;
                }
                bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
            }
            catch (IOException exception)
            {
                uploads.Add(new FileUpload(file, "busy", Problem: exception.Message));
                continue;
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (hashes.TryGetValue(file, out var previous) && previous == hash)
            {
                uploads.Add(new FileUpload(file, "unchanged"));
                continue;
            }
            try
            {
                var result = await api.UploadLedgerAsync(token, bytes, cancellationToken);
                hashes[file] = hash;
                uploadedAny = true;
                uploads.Add(new FileUpload(file, "uploaded", result));
            }
            catch (ApiException exception) when (exception.Error is "bad_file" or "empty" or "too_large")
            {
                // The website will refuse this content again, so do not retry it until the game writes a new one.
                hashes[file] = hash;
                uploads.Add(new FileUpload(file, "refused", Problem: exception.Message));
            }
        }
        return (uploads, state with
        {
            UploadedFiles = hashes,
            LastUploadAt = uploadedAny ? now() : state.LastUploadAt,
        });
    }
}
