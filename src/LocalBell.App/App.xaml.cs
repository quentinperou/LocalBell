using System.Globalization;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LocalBell.App.Services;
using LocalBell.App.ViewModels;
using LocalBell.Core;
using LocalBell.Core.Protocol;

namespace LocalBell.App;

public partial class App : Application
{
    // Windows display language, kept to come back to it when the user picks "Windows language".
    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;

    private Mutex? instance;
    private EventWaitHandle? showSignal;
    private BellNode? node;
    private TrayIcon? tray;
    private MainViewModel? viewModel;
    private SettingsWindow? settingsWindow;
    private TaskbarWidget? widget;
    private bool withWidget = true;
    private string? profile;

    /// <summary>Settings shared by every window (one instance, saved on change and on exit).</summary>
    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>The version of the app, without the commit that Git adds after a "+".</summary>
    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>
    /// Arguments: --hidden (started with Windows: the bell near the clock only), --profile NAME (a LocalBell of its
    /// own, with its own settings and id: to try two of them on one PC), --no-scan (no scan of the network when the
    /// announcements find nobody: tries on a network that is not one's own), --no-widget (no widget in the taskbar:
    /// the second LocalBell of a try, whose widget would lie over the first one's).
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        profile = ArgumentAfter(e.Args, "--profile");
        bool hidden = e.Args.Contains("--hidden");
        bool scan = !e.Args.Contains("--no-scan");
        withWidget = !e.Args.Contains("--no-widget");

        // One LocalBell per profile and per Windows session: a second launch shows the window of the first.
        string key = "Local\\LocalBell-" + (profile ?? "default");
        instance = new Mutex(true, key, out bool first);
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-show");
        if (!first)
        {
            showSignal.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        Settings = AppSettings.Load(AppSettings.FolderOf(profile));
        DevLog.UseFolder(AppSettings.FolderOf(profile));
        DevLog.Enabled = Settings.DeveloperMode;
        DevLog.Write($"LocalBell {Version} starts{(profile is null ? "" : $", profile {profile}")}, Windows {Environment.OSVersion.Version}"
            + $", arguments: {string.Join(' ', e.Args)}");

        // UI language and theme: the ones chosen in Settings; LOCALBELL_LANG=en|fr and LOCALBELL_THEME=Light|Dark
        // force one, handy to test both.
        ApplyLanguage(Environment.GetEnvironmentVariable("LOCALBELL_LANG") is { Length: > 0 } forced ? forced : Settings.Language);
        ApplyTheme(Enum.TryParse<AppTheme>(Environment.GetEnvironmentVariable("LOCALBELL_THEME"), ignoreCase: true, out var theme)
            ? theme
            : Settings.Theme);
        FillFirstRunTexts(profile);
        // The installed app follows "Start with Windows" (a build tried from the source folder never registers itself).
        if (WindowsStartup.IsInstalled) WindowsStartup.Apply(Settings.StartWithWindows, profile);

        DispatcherUnhandledException += (_, args) =>
        {
            DevLog.Write($"Error: {args.Exception}");
            MessageBox.Show(Strings.Format("UnexpectedError", args.Exception.Message), Strings.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        node = new BellNode(new NodeOptions
        {
            Id = Settings.Id,
            Name = Settings.Name,
            Status = Settings.IsDoNotDisturb ? PeerStatus.DoNotDisturb : PeerStatus.Available,
            Version = Version,
            Scan = scan,
            Log = DevLog.Write,
            Addresses = Settings.Addresses,
            KnownPeers = Settings.KnownPcs
                .Where(k => IPAddress.TryParse(k.Address, out _))
                .Select(k => (new PeerInfo { Id = k.Id, Name = k.Name, Port = k.Port }, new IPEndPoint(IPAddress.Parse(k.Address), k.Port)))
                .ToList(),
        });
        try
        {
            await node.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Strings.Format("UnexpectedError", ex.Message), Strings.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        viewModel = new MainViewModel(Settings, node, new RingPopups(Settings));
        tray = new TrayIcon(ShowMainWindow, TrayMenuItems);
        UpdateTrayTooltip();
        viewModel.DoNotDisturbChanged += (_, _) => UpdateTrayTooltip();
        viewModel.NameChanged += (_, _) => UpdateTrayTooltip();

        MainWindow = new MainWindow(viewModel);
        if (!hidden) MainWindow.Show();
        if (withWidget) ShowWidget();
    }

    /// <summary>Its place changed in Settings (left, near the clock, hidden).</summary>
    public void PlaceWidget() => widget?.Place();

    /// <summary>The widget in the taskbar, built again after a change of language (its texts are read when it is built).</summary>
    private void ShowWidget()
    {
        widget?.Close();
        widget = new TaskbarWidget(viewModel!) { Left = -10000, Top = -10000 };
        widget.Show();
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length && args[at + 1].Length > 0 ? args[at + 1] : null;
    }

    /// <summary>
    /// The first start: the ready-made messages and the replies in the UI language (the user changes them in
    /// Settings), and, for a test profile, a name that tells it from the PC's own LocalBell.
    /// </summary>
    private static void FillFirstRunTexts(string? profile)
    {
        bool changed = false;
        if (Settings.Messages.Count == 0)
        {
            Settings.Messages = [.. Enumerable.Range(1, 4).Select(i => Strings.Get("DefaultMessage" + i))];
            changed = true;
        }
        if (Settings.Replies.Count == 0)
        {
            Settings.Replies = [.. Enumerable.Range(1, 3).Select(i => Strings.Get("DefaultReply" + i))];
            changed = true;
        }
        if (profile is not null && Settings.Name.Length == 0)
        {
            Settings.Name = $"{Environment.MachineName} ({profile})";
            changed = true;
        }
        if (changed) Settings.Save();
    }

    /// <summary>Opens Settings (or brings it to the front) at a page.</summary>
    public void OpenSettings(int page = 0)
    {
        if (viewModel is null || node is null) return;
        if (settingsWindow is { IsLoaded: true })
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(viewModel, node, profile, page);
        if (MainWindow is { IsVisible: true })
        {
            settingsWindow.Owner = MainWindow;
            settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    /// <summary>
    /// Texts are read once, when a window is built: to switch language, the list window is built again at the same
    /// place, on the same view model, and Settings opens again at its page.
    /// </summary>
    public void ReloadLanguage(SettingsWindow from)
    {
        if (viewModel is null || MainWindow is not MainWindow old) return;
        ApplyLanguage(Settings.Language);
        int page = from.Page;
        bool visible = old.IsVisible;
        var window = new MainWindow(viewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = old.Left, Top = old.Top, Width = old.Width, Height = old.Height,
        };
        MainWindow = window;
        if (visible) window.Show();
        old.CloseForQuit();
        if (withWidget) ShowWidget();
        viewModel.RefreshTexts();
        UpdateTrayTooltip();
        from.Close();
        OpenSettings(page);
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void UpdateTrayTooltip()
    {
        if (tray is null || node is null || viewModel is null) return;
        tray.Tooltip = Strings.Format("TrayTooltip", node.Name) + (viewModel.IsDoNotDisturb ? " · " + viewModel.DoNotDisturbTitle : "");
    }

    /// <summary>
    /// The menu of the bell near the clock and of the taskbar widget (the approved mock-up): open, "do not disturb" and
    /// its lengths, Settings, quit.
    /// </summary>
    public IEnumerable<object> TrayMenuItems()
    {
        yield return PopupMenu.BellItem(Strings.Get("TrayOpen"), ShowMainWindow, bold: true);
        yield return new Separator();
        if (viewModel!.IsDoNotDisturb)
            yield return PopupMenu.Item(Strings.Get("TrayDndOff"), "", viewModel.TurnOffDoNotDisturb);
        else
        {
            var dnd = PopupMenu.Item(Strings.Get("StatusDnd"), "", null);
            foreach (var (length, key) in DoNotDisturbMenu.Lengths)
                dnd.Items.Add(PopupMenu.Item(Strings.Get(key), "", () => viewModel.TurnOnDoNotDisturb(length)));
            yield return dnd;
        }
        yield return PopupMenu.Item(Strings.Get("SettingsTooltip"), "", () => OpenSettings());
        yield return new Separator();
        yield return PopupMenu.Item(Strings.Get("TrayQuit"), "", Quit);
    }

    public void Quit()
    {
        (MainWindow as MainWindow)?.CloseForQuit();
        Shutdown();
    }

    /// <summary>The others are told this PC leaves (shown offline at once); off the UI thread, which waits for it.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        widget?.Close();
        if (node is { } running) Task.Run(() => running.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        Settings?.Save();
        if (instance is not null && node is not null) instance.ReleaseMutex();
        base.OnExit(e);
    }

    /// <summary>Switches every window to the light or dark Fluent theme, or back to the Windows one.</summary>
    public static void ApplyTheme(AppTheme theme) =>
        Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

    /// <summary>Sets the UI language: <see cref="AppSettings.SystemLanguage"/> or a culture name ("en", "fr").</summary>
    public static void ApplyLanguage(string language)
    {
        var culture = language == AppSettings.SystemLanguage || !AppSettings.SupportedLanguages.Contains(language)
            ? SystemUICulture
            : CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
