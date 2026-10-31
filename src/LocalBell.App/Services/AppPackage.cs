using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace LocalBell.App.Services;

/// <summary>
/// The Microsoft Store version (as NowPlaying's): the same exe, run from its MSIX package (store\AppxManifest.xml, built
/// by scripts\build-store.ps1). In the package the start with Windows is the startup task of the manifest, the Store
/// updates the app, the firewall rule comes with the package, and the version installed from GitHub, if it is on the
/// PC too, is offered for removal.
/// </summary>
public static class AppPackage
{
    /// <summary>The startup task declared in store\AppxManifest.xml.</summary>
    private const string StartupTaskId = "LocalBellStartup";

    private const int AppModelErrorNoPackage = 15700;

    // The version installed from GitHub: Inno Setup's uninstall entry (AppId of installer\LocalBell.iss + "_is1").
    private const string ClassicUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{E27D8A23-DEFC-474E-83B5-7C13F2235A4A}_is1";

    /// <summary>True when this process runs with a package identity: the Store version.</summary>
    public static bool IsPackaged { get; } = ReadIsPackaged();

    /// <summary>Started by its startup task when the user signed in: nothing to open, as with --hidden.</summary>
    public static bool LaunchedAtStartup()
    {
        if (!IsPackaged) return false;
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The page of the app in the Microsoft Store (it shows "Update" when there is one).</summary>
    public static string StorePage => $"ms-windows-store://pdp/?PFN={Package.Current.Id.FamilyName}";

    /// <summary>The state of the startup task.</summary>
    public static async Task<StartupState> GetStartupAsync()
    {
        try
        {
            return Map((await StartupTask.GetAsync(StartupTaskId)).State);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return StartupState.Off;
        }
    }

    /// <summary>
    /// Turns the startup task on or off. Windows asks nothing for a desktop app, but once the user turned it off in
    /// Task Manager or Settings the app cannot turn it on again (<see cref="StartupState.OffByUser"/>).
    /// </summary>
    public static async Task<StartupState> SetStartupAsync(bool on)
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (on) return Map(await task.RequestEnableAsync());
            task.Disable();
            return Map(task.State);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return StartupState.Off;
        }
    }

    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => StartupState.On,
        StartupTaskState.DisabledByUser => StartupState.OffByUser,
        StartupTaskState.EnabledByPolicy or StartupTaskState.DisabledByPolicy => StartupState.ByPolicy,
        _ => StartupState.Off,
    };

    /// <summary>The uninstaller of the version installed from GitHub, when it is on this PC.</summary>
    public static string? ClassicUninstaller()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(ClassicUninstallKey);
            return key?.GetValue("UninstallString") is string command && command.Trim('"') is var exe && File.Exists(exe) ? exe : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts the uninstaller of the version installed from GitHub (its own questions, its administrator prompt). It
    /// closes every LocalBell, this one too.
    /// </summary>
    public static void StartClassicUninstall(string uninstaller)
    {
        try
        {
            // Through File Explorer: started by this process, it would run inside the package, its changes to the
            // registry kept apart from the real one.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{uninstaller}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DevLog.Write($"Uninstall of the GitHub version: {ex.Message}");
        }
    }

    private static bool ReadIsPackaged()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}

/// <summary>Whether LocalBell starts with Windows.</summary>
public enum StartupState
{
    Off,
    On,

    /// <summary>Turned off by the user in Task Manager or Settings: only there can it be turned on again.</summary>
    OffByUser,

    /// <summary>Set by the administrator of the PC.</summary>
    ByPolicy,
}
