using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LocalBell.App.Services;
using LocalBell.App.ViewModels;
using LocalBell.Core;
using LocalBell.Core.History;
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
    private TaskbarHost? taskbar;
    private WidgetFlyout? flyout;
    private RingPopups? popups;
    private GlobalHotkeys? shortcuts;
    private PeerItem? favorite; // the one the widget shows, watched for its name and its notes
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
    /// the second LocalBell of a try, whose widget would lie over the first one's). --taskbar PIPE: the widget process,
    /// started by the app itself (<see cref="TaskbarHost"/>).
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await StartUpAsync(e);
        }
        catch (Exception ex)
        {
            // OnStartup is async void: a failure past its first await left LocalBell running with no window, holding the
            // single-instance mutex (the audit, October 31). Said, logged, and the app ends.
            DevLog.Write($"Error at startup: {ex}");
            MessageBox.Show(Strings.Format("UnexpectedError", ex.Message), Strings.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task StartUpAsync(StartupEventArgs e)
    {
        if (e.Args is [TaskbarHost.Argument, var pipe])
        {
            TaskbarClient.Run(this, pipe);
            return;
        }
        profile = ArgumentAfter(e.Args, "--profile");
        // The Store version is started at sign-in by its startup task, which gives no arguments.
        bool hidden = e.Args.Contains("--hidden") || AppPackage.LaunchedAtStartup();
        bool scan = !e.Args.Contains("--no-scan");
        withWidget = !e.Args.Contains("--no-widget");

        // Before the single instance: the version from GitHub may be the one running.
        if (profile is null && OfferClassicRemoval())
        {
            Shutdown();
            return;
        }

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
        DevLog.Write($"LocalBell {Version}{(AppPackage.IsPackaged ? " (Microsoft Store)" : "")} starts"
            + $"{(profile is null ? "" : $", profile {profile}")}, Windows {Environment.OSVersion.Version}, arguments: {string.Join(' ', e.Args)}");
        if (Settings.LoadIssue is { } issue) DevLog.Write("Settings: " + issue);

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
        // Another PC had this id (settings copied with a disk image): the new one is kept. Before the start, which
        // says hello to the PCs known.
        node.IdChanged += (_, id) => Dispatcher.BeginInvoke(() =>
        {
            Settings.Id = id;
            Settings.Save();
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

        popups = new RingPopups(Settings);
        var history = RingLog.Load(Path.Combine(AppSettings.FolderOf(profile), "history.json"));
        history.KeepDays = Settings.HistoryDays;
        viewModel = new MainViewModel(Settings, node, popups, history);
        tray = new TrayIcon(OnTrayClick, TrayMenuItems);
        UpdateTrayTooltip();
        viewModel.DoNotDisturbChanged += (_, _) => UpdateTrayTooltip();
        viewModel.NameChanged += (_, _) => UpdateTrayTooltip();

        MainWindow = new MainWindow(viewModel);
        if (!hidden) MainWindow.Show();
        if (withWidget) StartWidget(startedWithWindows: hidden);
        shortcuts = new GlobalHotkeys(OnShortcut);
        ApplyShortcuts();
        viewModel.ShortcutsChanged += (_, _) => ApplyShortcuts();

        // A newer version from GitHub shows over the list and in Settings → Updates (not in the Store version).
        _ = Updater.CheckAtStartupAsync();
    }

    /// <summary>
    /// The Store version, the first time it finds the version installed from GitHub on the PC: only one LocalBell runs
    /// at a time, so it offers to uninstall that one (asked once). True when the uninstaller has been started: it closes
    /// every LocalBell, so this one quits.
    /// </summary>
    private static bool OfferClassicRemoval()
    {
        if (!AppPackage.IsPackaged || AppPackage.ClassicUninstaller() is not { } uninstaller) return false;
        var settings = AppSettings.Load(AppSettings.FolderOf(null));
        if (settings.ClassicRemovalOffered) return false;
        settings.ClassicRemovalOffered = true;
        settings.Save();
        ApplyLanguage(settings.Language);
        if (MessageBox.Show(Strings.Get("ClassicRemovalText"), Strings.Get("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return false;
        AppPackage.StartClassicUninstall(uninstaller);
        return true;
    }

    /// <summary>
    /// The widget in the taskbar, in a process of its own owned by the taskbar (<see cref="TaskbarHost"/>): it shows
    /// what this app sends and gives its clicks back; its flyout and its menu open here.
    /// </summary>
    private void StartWidget(bool startedWithWindows)
    {
        taskbar = new TaskbarHost(Dispatcher);
        // Made in the first minutes of the session, it once drew black where it is see-through: made again then.
        if (startedWithWindows) taskbar.StartAgainAfter(TimeSpan.FromSeconds(90));
        taskbar.FlyoutRequested += ToggleFlyout;
        taskbar.RingRequested += (_, _) =>
        {
            if (viewModel?.Favorite is { } item) _ = viewModel.RingAsync(item);
        };
        taskbar.MenuRequested += barTop => PopupMenu.Show(TrayMenuItems(), barTop);
        viewModel!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(MainViewModel.Favorite) or nameof(MainViewModel.IsDoNotDisturb)) UpdateWidget();
        };
        viewModel.MissedRings.CollectionChanged += (_, _) => UpdateWidget();
        UpdateWidget();
    }

    /// <summary>What the widget shows, sent to its process when it changes; also its place, changed in Settings.</summary>
    public void UpdateWidget()
    {
        if (taskbar is null || viewModel is null) return;
        if (!ReferenceEquals(favorite, viewModel.Favorite))
        {
            if (favorite is not null) favorite.PropertyChanged -= OnFavoriteChanged;
            favorite = viewModel.Favorite;
            if (favorite is not null) favorite.PropertyChanged += OnFavoriteChanged;
        }
        var state = new TaskbarState(Settings.Widget, viewModel.IsDoNotDisturb,
            favorite is null ? null : favorite.HasNote ? favorite.Note : favorite.Name, favorite?.Online ?? false,
            viewModel.MissedRings.Count, Strings.Get("AppTitle"), favorite?.RingLabel);
        taskbar.Update(state, enabled: Settings.Widget != WidgetPosition.Hidden);
    }

    private void OnFavoriteChanged(object? sender, PropertyChangedEventArgs e) => UpdateWidget();

    /// <summary>The bell or the arrow of the widget: its flyout opens above it, or closes.</summary>
    private void ToggleFlyout(int left, int barTop)
    {
        flyout ??= new WidgetFlyout(viewModel!);
        if (flyout.IsVisible) flyout.Hide();
        else flyout.ShowAbove(left, barTop);
    }

    /// <summary>The shortcuts of Settings registered again (after a change); those of the PCs no longer listed are not.</summary>
    public void ApplyShortcuts() =>
        shortcuts?.Apply(Settings.Shortcuts.Where(s => GlobalHotkeys.PcOf(s.Key) is not { } pc || viewModel!.Peers.Any(p => p.Id == pc)));

    /// <summary>The shortcuts, for Settings: what another app holds, and none at all while one is recorded.</summary>
    public GlobalHotkeys? Shortcuts => shortcuts;

    /// <summary>
    /// A shortcut pressed, anywhere in Windows: rings its PC (as the favourite of the widget is rung: the row tells what
    /// happened), opens the window or the quick panel, or turns "do not disturb" on or off (on: until turned off).
    /// </summary>
    private void OnShortcut(string action)
    {
        if (viewModel is null) return;
        switch (action)
        {
            case GlobalHotkeys.OpenAction:
                ShowMainWindow();
                break;
            case GlobalHotkeys.PanelAction:
                var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition).WorkingArea;
                flyout ??= new WidgetFlyout(viewModel);
                if (flyout.IsVisible) flyout.Hide();
                else flyout.ShowNear(area.Right, area.Bottom); // near the clock (kept within the screen)
                break;
            case GlobalHotkeys.DoNotDisturbAction:
                if (viewModel.IsDoNotDisturb) viewModel.TurnOffDoNotDisturb();
                else viewModel.TurnOnDoNotDisturb("forever");
                break;
            default:
                if (GlobalHotkeys.PcOf(action) is { } pc) viewModel.RingBack(pc);
                break;
        }
    }

    /// <summary>
    /// A click on the bell near the clock: the same quick panel as the widget's, above the bell (the author, October 31),
    /// or the window, as Settings → General says. A click that closes the panel (it loses the focus to that click) does
    /// not open it again.
    /// </summary>
    private void OnTrayClick()
    {
        if (Settings.TrayClick == TrayClickAction.Window || viewModel is null)
        {
            ShowMainWindow();
            return;
        }
        NativeMethods.GetCursorPos(out var point);
        int barTop = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea.Bottom;
        flyout ??= new WidgetFlyout(viewModel);
        if (flyout.IsVisible) flyout.Hide();
        else flyout.ShowNear(point.X, barTop);
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
        // The flyout is built again in the new language when it opens next; the widget gets its tooltips.
        flyout?.Close();
        flyout = null;
        viewModel.RefreshTexts();
        UpdateWidget();
        UpdateTrayTooltip();
        from.Close();
        OpenSettings(page);
    }

    /// <summary>"Mettre à jour" in the quick panel or the menu: from the window, whose bar shows the progress.</summary>
    public void InstallUpdate()
    {
        ShowMainWindow();
        (MainWindow as MainWindow)?.InstallUpdate();
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
        // A new version first, in the accent colour (the author's wish: seen without opening Settings).
        if (Updater.Available is { } release && !Updater.IsInstalling)
        {
            yield return PopupMenu.Item(Strings.Format("UpdateMenu", release.Version), "\uE896", InstallUpdate, bold: true, accent: true);
            yield return new Separator();
        }
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

    /// <summary>
    /// Windows closes LocalBell (sign-out, shutdown, or the Restart Manager of an update, which closes the widget
    /// process at the same time): the widget is let go first, so that the app does not start a new one that would
    /// still hold LocalBell.exe while the installer replaces it.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        taskbar?.Dispose();
        taskbar = null;
        base.OnSessionEnding(e);
    }

    /// <summary>The others are told this PC leaves (shown offline at once); off the UI thread, which waits for it.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        popups?.StopBell(); // the PC's sound as it was, if it was turned on for a ring
        shortcuts?.Dispose();
        viewModel?.History.Save();
        tray?.Dispose();
        taskbar?.Dispose(); // its process ends with the pipe
        flyout?.Close();
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
