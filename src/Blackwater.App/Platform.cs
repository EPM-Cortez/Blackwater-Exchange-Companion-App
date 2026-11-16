using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BlackwaterExchange.App;

public static class AppInfo
{
    // Matches the Start menu shortcut in packaging/BlackwaterExchange.wxs.
    private const string AppUserModelId = "BlackwaterExchange.Companion";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public static void SetShellIdentity()
    {
        if (!StartWithWindows.IsPackaged)
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

/// <summary>
/// Starting with Windows. Installed from the Store, the package's start-up task (Package.appxmanifest,
/// windows.startupTask) starts the app at sign-in, and the player switches it in Windows Settings, Apps, Startup.
/// Run outside a package (development), the app uses the Run key in the current user's registry.
/// </summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Blackwater Exchange";
    public const string BackgroundArgument = "--background";

    /// <summary>Whether this process runs from an MSIX package.</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    private static bool DetectPackage()
    {
        var length = 0;
        // ERROR_INSUFFICIENT_BUFFER (122) means there is a package name to read; APPMODEL_ERROR_NO_PACKAGE (15700) means none.
        return GetCurrentPackageFullName(ref length, null) == 122;
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        if (IsPackaged)
            return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } path)
            key.SetValue(ValueName, $"\"{path}\" {BackgroundArgument}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Whether Windows started the app at sign-in, so it should stay in the notification area. The Store's start-up
    /// task passes no arguments, so a package counts as started at sign-in when Explorer started within the last
    /// three minutes.
    /// </summary>
    public static bool StartedAtSignIn(string[] args)
    {
        if (args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            return true;
        if (!IsPackaged)
            return false;
        try
        {
            var session = Process.GetCurrentProcess().SessionId;
            var shells = Process.GetProcessesByName("explorer").Where(process => process.SessionId == session).ToList();
            var started = shells.Select(process => process.StartTime).DefaultIfEmpty(DateTime.MinValue).Min();
            shells.ForEach(process => process.Dispose());
            return DateTime.Now - started < TimeSpan.FromMinutes(3);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);
}

/// <summary>
/// One copy of the app per Windows user. Starting it again (from the Start menu, say) asks the running copy to show
/// its window, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\BlackwaterExchangeApp";
    private readonly Mutex mutex;
    private readonly EventWaitHandle showSignal;
    private readonly RegisteredWaitHandle? waiting;

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal, Action show)
    {
        this.mutex = mutex;
        this.showSignal = showSignal;
        waiting = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>This copy's claim, or null after asking the running copy to show itself.</summary>
    public static SingleInstance? Claim(Action show)
    {
        var mutex = new Mutex(initiallyOwned: true, Name, out var first);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + ".Show");
        if (first)
            return new SingleInstance(mutex, signal, show);
        signal.Set();
        signal.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        waiting?.Unregister(null);
        showSignal.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
