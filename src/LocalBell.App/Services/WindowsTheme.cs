using Microsoft.Win32;

namespace LocalBell.App.Services;

/// <summary>The mode of Windows itself (taskbar, Start, its menus), which may differ from the mode of the apps.</summary>
internal static class WindowsTheme
{
    /// <summary>True when the taskbar is dark ("Choose your Windows mode": Dark; dark too when it cannot be read).</summary>
    public static bool TaskbarIsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is not int light || light == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return true;
            }
        }
    }

    /// <summary>Windows 11 (build 22000 and later): the Widgets button, centred icons; Windows 10 has neither.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
}
