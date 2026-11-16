using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace BlackwaterExchange;

/// <summary>
/// WoW Forever's folder (…\World of Warcraft\_forever_): where the addons live and where the game saves
/// Forever Craft Assistant's data, one file per Battle.net game account.
/// </summary>
public sealed class GameFolder(string path)
{
    public const string AddonName = "ForeverCraftAssistant";
    public const string MarketDataAddonName = "ForeverCraftAssistantMarketData";
    public const string SavedFileName = "ForeverCraftAssistant.lua";

    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string AddOns => System.IO.Path.Combine(Path, "Interface", "AddOns");

    /// <summary>A WoW Forever folder: named _forever_ and holding the game or its Interface/WTF folders.</summary>
    public static bool LooksRight(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
        && string.Equals(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path))),
            "_forever_", StringComparison.OrdinalIgnoreCase)
        && (File.Exists(System.IO.Path.Combine(path, "Wow.exe")) || Directory.Exists(System.IO.Path.Combine(path, "Interface"))
            || Directory.Exists(System.IO.Path.Combine(path, "WTF")));

    /// <summary>
    /// Finds WoW Forever: from where Blizzard's launcher last installed or ran the game (its registry entry points at
    /// the version folder, such as …\_forever_ or …\_retail_), then the usual install folders on each fixed drive.
    /// </summary>
    public static IReadOnlyList<string> Find()
    {
        var found = new List<string>();
        void Consider(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;
            try
            {
                var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
                // The registry may name another version's folder; WoW Forever sits beside it.
                var candidate = string.Equals(System.IO.Path.GetFileName(full), "_forever_", StringComparison.OrdinalIgnoreCase)
                    ? full : System.IO.Path.Combine(full, "_forever_");
                if (LooksRight(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    found.Add(candidate);
                var parent = System.IO.Path.GetDirectoryName(full);
                if (parent is not null && System.IO.Path.GetFileName(full).StartsWith('_'))
                    Consider(parent);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException
                                                  or UnauthorizedAccessException)
            {
            }
        }

        foreach (var key in (string[])[@"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                     @"SOFTWARE\Blizzard Entertainment\World of Warcraft"])
        {
            try
            {
                using var entry = Registry.LocalMachine.OpenSubKey(key);
                Consider(entry?.GetValue("InstallPath") as string);
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
            foreach (var folder in (string[])["World of Warcraft", @"Program Files (x86)\World of Warcraft",
                         @"Program Files\World of Warcraft", @"Games\World of Warcraft", @"Blizzard\World of Warcraft"])
                Consider(System.IO.Path.Combine(drive.RootDirectory.FullName, folder));
        return found;
    }

    /// <summary>Forever Craft Assistant's saved file for each game account that has one.</summary>
    public IReadOnlyList<string> SavedFiles()
    {
        var accounts = System.IO.Path.Combine(Path, "WTF", "Account");
        RequireOrdinaryPath(accounts);
        if (!Directory.Exists(accounts))
            return [];
        return Directory.EnumerateDirectories(accounts)
            .Select(account => System.IO.Path.Combine(account, "SavedVariables", SavedFileName))
            .Where(file => File.Exists(file) && !HasReparsePoint(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The folder WoW writes saved files into, to watch for changes.</summary>
    public string AccountsFolder => System.IO.Path.Combine(Path, "WTF", "Account");

    internal void RequireOrdinaryPath(string path)
    {
        if (HasReparsePoint(path))
            throw new IOException("Blackwater Exchange cannot use a linked folder or file inside the WoW Forever installation.");
    }

    private bool HasReparsePoint(string path)
    {
        var relative = System.IO.Path.GetRelativePath(Path, path);
        if (System.IO.Path.IsPathRooted(relative) || relative.Split(System.IO.Path.DirectorySeparatorChar).Contains(".."))
            throw new ArgumentException("The path must be inside this WoW Forever installation.", nameof(path));
        var current = Path;
        if (IsReparsePoint(current))
            return true;
        foreach (var part in relative.Split(System.IO.Path.DirectorySeparatorChar))
        {
            current = System.IO.Path.Combine(current, part);
            if (IsReparsePoint(current))
                return true;
        }
        return false;
    }

    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static FileStream OpenSavedFile(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var buffer = new char[512];
            var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Length, 0);
            if (length >= buffer.Length && length <= 32768)
            {
                buffer = new char[length + 1];
                length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Length, 0);
            }
            if (length == 0 || length >= buffer.Length)
                throw new IOException("Could not verify the location of the opened WoW saved file.");
            var actual = new string(buffer, 0, (int)length);
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal))
                actual = actual[4..];
            if (!string.Equals(actual, System.IO.Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The opened WoW saved file resolves to another location.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    /// <summary>The installed addon's version from its .toc file, or null when it is not installed.</summary>
    public string? InstalledAddonVersion()
    {
        var toc = System.IO.Path.Combine(AddOns, AddonName, AddonName + ".toc");
        RequireOrdinaryPath(toc);
        if (!File.Exists(toc))
            return null;
        foreach (var line in File.ReadLines(toc).Take(40))
            if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase))
                return line["## Version:".Length..].Trim();
        return null;
    }

    /// <summary>Whether this copy of WoW is running. When Windows will not say where a Wow.exe runs from, it counts.</summary>
    public bool IsGameRunning()
    {
        foreach (var process in Process.GetProcessesByName("Wow"))
        {
            using (process)
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file is null || file.StartsWith(Path, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
