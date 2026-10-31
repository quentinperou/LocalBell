using Microsoft.Win32;

namespace LocalBell.App.Services;

/// <summary>
/// "Start with Windows": a value of the user's Run key, starting LocalBell hidden (the bell near the clock only).
/// A test profile gets a value of its own. The Store version uses the startup task of its package instead
/// (<see cref="AppPackage"/>): a package's writes to the Run key go to its own copy of the registry, never read at
/// sign-in.
/// </summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// True for the app installed from GitHub (in Program Files): only it follows the setting at every start, so that
    /// a build tried from the source folder never registers itself.
    /// </summary>
    public static bool IsInstalled =>
        !AppPackage.IsPackaged
        && Environment.ProcessPath is { } path
        && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    /// <summary>What Settings shows: the startup task of the Store version, else the setting.</summary>
    public static Task<StartupState> GetAsync(bool setting) =>
        AppPackage.IsPackaged ? AppPackage.GetStartupAsync() : Task.FromResult(setting ? StartupState.On : StartupState.Off);

    public static Task<StartupState> SetAsync(bool on, string? profile)
    {
        if (AppPackage.IsPackaged) return AppPackage.SetStartupAsync(on);
        Apply(on, profile);
        return Task.FromResult(on ? StartupState.On : StartupState.Off);
    }

    public static void Apply(bool on, string? profile)
    {
        if (Environment.ProcessPath is not { } exe) return;
        string name = profile is null ? "LocalBell" : $"LocalBell ({profile})";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on) key.SetValue(name, $"\"{exe}\" --hidden" + (profile is null ? "" : $" --profile {profile}"));
            else key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
