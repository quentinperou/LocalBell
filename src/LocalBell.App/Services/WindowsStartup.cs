using Microsoft.Win32;

namespace LocalBell.App.Services;

/// <summary>
/// "Start with Windows": a value of the user's Run key, starting LocalBell hidden (the bell near the clock only).
/// A test profile gets a value of its own.
/// </summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// True for the installed app (in Program Files): only it follows the setting at every start, so that a build
    /// tried from the source folder never registers itself.
    /// </summary>
    public static bool IsInstalled =>
        Environment.ProcessPath is { } path
        && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

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
