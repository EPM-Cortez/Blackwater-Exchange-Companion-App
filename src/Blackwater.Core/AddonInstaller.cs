using System.IO.Compression;
using System.Security.Cryptography;

namespace BlackwaterExchange;

/// <summary>
/// Installs and updates Forever Craft Assistant from the website's releases (/v1/addon/latest), so players need no
/// other tool. A download must match the size and SHA-256 the website lists and hold nothing but one
/// ForeverCraftAssistant folder. It is unpacked beside the installed copy, then swapped in with two renames; if the
/// swap fails, the old copy is put back. The game reads the new version on its next /reload or start.
/// </summary>
public sealed class AddonInstaller(ApiClient api, string workFolder)
{
    public const long MaxDownload = 32 * 1024 * 1024;
    public const long MaxUnpacked = 128 * 1024 * 1024;
    public const int MaxFiles = 2000;

    public sealed record Outcome(string Result, string? Installed, string? Latest, string? Problem = null);

    /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="installed"/> (1.10.0 is newer than 1.9.3).</summary>
    public static bool IsNewer(string latest, string? installed)
    {
        if (installed is null)
            return true;
        var compared = CompareVersions(latest, installed);
        return compared is null ? !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase) : compared > 0;
    }

    /// <summary>Compares numeric addon versions and prefers the final release over a suffix on the same version.</summary>
    public static int? CompareVersions(string first, string second)
    {
        static (Version? Number, string Suffix) Split(string text)
        {
            var end = 0;
            while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
                end++;
            var number = text[..end].Trim('.');
            if (!number.Contains('.'))
                number += ".0";
            return (Version.TryParse(number, out var version) ? version : null, text[end..]);
        }
        var (a, aRest) = Split(first);
        var (b, bRest) = Split(second);
        if (a is null || b is null)
            return null;
        var compared = a.CompareTo(b);
        if (compared != 0)
            return compared;
        // 1.8.0 is newer than 1.8.0-beta.
        return (aRest.Length == 0).CompareTo(bRest.Length == 0);
    }

    /// <summary>Installs the latest release when it is newer than the installed copy (or none is installed).</summary>
    public async Task<Outcome> UpdateAsync(GameFolder game, CancellationToken cancellationToken)
    {
        var installed = game.InstalledAddonVersion();
        var release = await api.LatestAddonAsync(cancellationToken);
        if (release is null)
            return new Outcome("no_release", installed, null);
        if (!IsNewer(release.Version, installed))
            return new Outcome("up_to_date", installed, release.Version);
        if (!Directory.Exists(game.AddOns))
            return new Outcome("no_addons_folder", installed, release.Version, $"{game.AddOns} does not exist. Start WoW Forever once first.");
        if (release.Size is <= 0 or > MaxDownload || release.Sha256 is not { Length: 64 }
            || !release.Sha256.All(char.IsAsciiHexDigit) || !Uri.TryCreate(release.Url, UriKind.Absolute, out var url))
            return new Outcome("bad_release", installed, release.Version, "The website's release details are not valid.");

        Directory.CreateDirectory(workFolder);
        var zip = Path.Combine(workFolder, $"addon-{Guid.NewGuid():N}.zip");
        try
        {
            await api.DownloadAsync(url, zip, release.Size, cancellationToken);
            await using var stream = File.OpenRead(zip);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (stream.Length != release.Size || !string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                return new Outcome("bad_download", installed, release.Version, "The download did not match the release. It was not installed.");
            stream.Position = 0;
            game.RequireOrdinaryPath(game.AddOns);
            Install(stream, game.AddOns);
            return new Outcome("installed", game.InstalledAddonVersion(), release.Version);
        }
        finally
        {
            if (File.Exists(zip))
                File.Delete(zip);
        }
    }

    /// <summary>Unpacks a checked release zip into <paramref name="addOns"/>, replacing the installed copy.</summary>
    public static void Install(string zipFile, string addOns)
    {
        using var zip = File.OpenRead(zipFile);
        Install(zip, addOns);
    }

    private static void Install(Stream zip, string addOns)
    {
        var name = GameFolder.AddonName;
        var target = Path.Combine(addOns, name);
        var lockFile = Path.Combine(addOns, $".{name}.update.lock");
        if (GameFolder.IsReparsePoint(addOns) || GameFolder.IsReparsePoint(target) || GameFolder.IsReparsePoint(lockFile))
            throw new IOException("An addon update cannot use a linked path.");
        using var updateLock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);
        // Names WoW will not load (the folder must match its .toc), on the same drive so a rename is instant.
        var incoming = Path.Combine(addOns, $".{name}.new-{Guid.NewGuid():N}");
        var previous = Path.Combine(addOns, $".{name}.old-{Guid.NewGuid():N}");
        try
        {
            Unpack(zip, incoming, name);
            if (!File.Exists(Path.Combine(incoming, name + ".toc")))
                throw new InvalidDataException($"The release has no {name}.toc.");
            if (Directory.Exists(target))
                Directory.Move(target, previous);
            try
            {
                Directory.Move(incoming, target);
            }
            catch (Exception activationError) when (activationError is IOException or UnauthorizedAccessException)
            {
                if (Directory.Exists(previous) && !Directory.Exists(target))
                {
                    try
                    {
                        Directory.Move(previous, target);
                    }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException($"Could not restore the addon. The previous version is in {previous}.", restoreError);
                    }
                }
                throw;
            }
            DeleteStagingFolder(previous);
        }
        finally
        {
            DeleteStagingFolder(incoming);
        }
    }

    private static void DeleteStagingFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !GameFolder.IsReparsePoint(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked directory can be recovered or removed by its owner later.
        }
    }

    /// <summary>
    /// Unpacks the entries under "<paramref name="name"/>/" into <paramref name="destination"/>. Refuses absolute
    /// paths, "..", anything outside that folder, and more files or bytes than an addon could need.
    /// </summary>
    private static void Unpack(Stream zip, string destination, string name)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxFiles)
            throw new InvalidDataException("The release has too many files.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith(name + "/", StringComparison.Ordinal))
                throw new InvalidDataException($"The release has a file outside {name}: {entry.FullName}");
            var relative = path[(name.Length + 1)..];
            if (relative.Length == 0)
                continue;
            if (relative.TrimEnd('/').Split('/').Any(IsUnsafeZipPart) || Path.IsPathRooted(relative))
                throw new InvalidDataException($"The release has an unsafe path: {entry.FullName}");
            var output = Path.GetFullPath(Path.Combine(destination, relative));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The release has an unsafe path: {entry.FullName}");
            if (path.EndsWith('/'))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            total += entry.Length;
            if (total > MaxUnpacked)
                throw new InvalidDataException("The release unpacks to more than an addon could need.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            // Entry.Length is what the zip claims; count what actually comes out too.
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                written += read;
                if (written > entry.Length || total - entry.Length + written > MaxUnpacked)
                    throw new InvalidDataException("The release unpacks to more than it says.");
                file.Write(buffer, 0, read);
            }
        }
    }

    private static bool IsUnsafeZipPart(string part)
    {
        if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return true;
        var stem = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'));
    }
}
