using System.Windows;
using System.Windows.Controls;
using LocalBell.App.Services;
using LocalBell.Core.Updates;

namespace LocalBell.App;

/// <summary>
/// "LocalBell x.y.z is available" with Update / What's new / close, over the list as soon as the startup check finds a
/// newer version. Update installs from here, as in PageCompose and NowPlaying.
/// </summary>
public partial class UpdateBar : UserControl
{
    // Closing the bar hides that version for the rest of the run, also after a language change (a new list window).
    private static SemanticVersion? dismissed;

    private bool installingHere;

    public UpdateBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Updater.Checked += Refresh;
            Show();
        };
        Unloaded += (_, _) => Updater.Checked -= Refresh;
    }

    private void Refresh(object? sender, EventArgs e) => Show();

    private void Show(string? message = null)
    {
        if (installingHere) return;
        if (Updater.Available is not { } release || release.Version == dismissed)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Message.Text = message ?? Strings.Format("UpdateAvailable", release.Version);
        NotesLink.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    /// <summary>The version offered, unless its bar was closed or it is installing: the quick panel shows it too.</summary>
    public static ReleaseInfo? Offered =>
        Updater.Available is { } release && release.Version != dismissed && !Updater.IsInstalling ? release : null;

    private void Install_Click(object sender, RoutedEventArgs e) => Install();

    /// <summary>Downloads and installs the new version, its progress on this bar (also from the quick panel and the menu).</summary>
    public async void Install()
    {
        if (Updater.Available is not { } release || Updater.IsInstalling) return;
        dismissed = null; // closed earlier, offered again by the menu
        Visibility = Visibility.Visible;

        InstallButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
        NotesLink.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        Progress.IsIndeterminate = false;
        Progress.Visibility = Visibility.Visible;
        Message.Text = Strings.Get("UpdateDownloading");
        var progress = new Progress<double>(p =>
        {
            Progress.Value = p;
            if (p < 1) return;
            // Downloaded: the installer now waits for the UAC prompt, then installs.
            Progress.IsIndeterminate = true;
            Message.Text = Strings.Get("UpdateStarting");
        });

        installingHere = true;
        string message;
        try
        {
            if (await Updater.InstallAsync(release, progress)) return; // the app is closing, the installer takes over
            message = Strings.Get("UpdateCancelled");
        }
        catch (Exception ex)
        {
            message = Strings.Format("ErrorUpdate", Updater.Describe(ex));
        }
        installingHere = false;
        Show(message);
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is { } release) Updater.OpenReleasePage(release);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        dismissed = Updater.Available?.Version;
        Visibility = Visibility.Collapsed;
    }
}
