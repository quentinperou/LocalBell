using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using LocalBell.App.Services;
using LocalBell.App.ViewModels;
using LocalBell.Core;
using LocalBell.Core.Network;
using LocalBell.Core.Protocol;
using LocalBell.Core.Updates;
using Microsoft.Win32;

namespace LocalBell.App;

/// <summary>
/// Settings, on nine pages. Each change applies at once and is saved (no OK button, as in Windows 11); the name of the
/// PC when its box is left or Enter is pressed.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings settings = App.Settings;
    private readonly MainViewModel viewModel;
    private readonly BellNode node;
    private readonly string? profile;
    private readonly ScrollViewer[] pages;
    private readonly ObservableCollection<EditableText> messages;
    private readonly ObservableCollection<EditableText> replies;
    private readonly ObservableCollection<string> addresses;
    private readonly ObservableCollection<ShortcutRow> pcShortcuts = [];
    private readonly ObservableCollection<ShortcutRow> appShortcuts = [];
    private readonly bool initialized;
    private bool updating;
    private ShortcutRow? recording; // the shortcut whose keys are being pressed
    private KeyRecorder? recorder; // its keys, read before Windows acts on them

    public SettingsWindow(MainViewModel viewModel, BellNode node, string? profile, int page = 0)
    {
        InitializeComponent();
        MicaBackdrop.Apply(this);
        this.viewModel = viewModel;
        this.node = node;
        this.profile = profile;
        pages = [GeneralPage, RingPage, PopupPage, MessagesPage, ShortcutsPage, HistoryPage, NetworkPage, UpdatesPage, AboutPage];
        // The pages scroll rather than the window going past the screen.
        MaxHeight = SystemParameters.WorkArea.Height;

        // General
        NameBox.Text = node.Name;
        ThisPcText.Text = Strings.Format("ThisPc", node.Name);
        StartupBox.IsChecked = settings.StartWithWindows;
        if (AppPackage.IsPackaged) ShowStartupAsync();
        WidgetBox.Items.Add(new Option<WidgetPosition>(WidgetPosition.Left, Strings.Get("WidgetLeft")));
        WidgetBox.Items.Add(new Option<WidgetPosition>(WidgetPosition.NearClock, Strings.Get("WidgetNearClock")));
        WidgetBox.Items.Add(new Option<WidgetPosition>(WidgetPosition.Hidden, Strings.Get("WidgetHidden")));
        Select(WidgetBox, settings.Widget);
        TrayClickBox.Items.Add(new Option<TrayClickAction>(TrayClickAction.Panel, Strings.Get("TrayClickPanel")));
        TrayClickBox.Items.Add(new Option<TrayClickAction>(TrayClickAction.Window, Strings.Get("TrayClickWindow")));
        Select(TrayClickBox, settings.TrayClick);
        foreach (var (_, key) in DoNotDisturbMenu.Lengths) DndLengthBox.Items.Add(Strings.Get(key));
        DndLengthBox.SelectedIndex = 1;
        DndBox.IsChecked = settings.IsDoNotDisturb;
        viewModel.DoNotDisturbChanged += OnDoNotDisturbChanged;
        LanguageBox.Items.Add(new Option<string>(AppSettings.SystemLanguage, Strings.Get("LanguageSystem")));
        foreach (string code in AppSettings.SupportedLanguages) LanguageBox.Items.Add(new Option<string>(code, NativeName(code)));
        Select(LanguageBox, settings.Language);
        ThemeBox.Items.Add(new Option<AppTheme>(AppTheme.System, Strings.Get("ThemeSystem")));
        ThemeBox.Items.Add(new Option<AppTheme>(AppTheme.Light, Strings.Get("ThemeLight")));
        ThemeBox.Items.Add(new Option<AppTheme>(AppTheme.Dark, Strings.Get("ThemeDark")));
        Select(ThemeBox, settings.Theme);

        // Ring
        FillSounds();
        VolumeSlider.Value = settings.Volume;
        VolumeText.Text = $"{settings.Volume} %";
        MutedBox.IsChecked = settings.RingWhenMuted;
        foreach (int count in new[] { 1, 2, 3, 5 })
            RepeatBox.Items.Add(new Option<int>(count, count == 1 ? Strings.Get("RepeatOne") : Strings.Format("RepeatMany", count)));
        Select(RepeatBox, settings.Repeat);
        if (RepeatBox.SelectedIndex < 0) RepeatBox.SelectedIndex = 1; // 2 times, the default

        // Popup
        string time = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        var previewReplies = settings.Replies.Take(3).ToList();
        QuietPreview.Show(PopupStyle.Quiet, Strings.Get("PreviewName"), Strings.Get("PreviewMessage"), false, time, previewReplies);
        LoudPreview.Show(PopupStyle.Loud, Strings.Get("PreviewName"), Strings.Get("PreviewMessage"), false, time, previewReplies);
        (settings.PopupStyle == PopupStyle.Loud ? LoudRadio : QuietRadio).IsChecked = true;
        CornerButton(settings.Corner).IsChecked = true;
        ScreenBox.Items.Add(new Option<bool>(false, Strings.Get("ScreenPrimary")));
        ScreenBox.Items.Add(new Option<bool>(true, Strings.Get("ScreenMouse")));
        Select(ScreenBox, settings.PopupOnMouseScreen);
        CloseBox.Items.Add(new Option<int>(0, Strings.Get("CloseOnAnswer")));
        CloseBox.Items.Add(new Option<int>(30, Strings.Format("CloseAfterSeconds", 30)));
        CloseBox.Items.Add(new Option<int>(60, Strings.Format("CloseAfterMinutes", 1)));
        CloseBox.Items.Add(new Option<int>(300, Strings.Format("CloseAfterMinutes", 5)));
        Select(CloseBox, settings.CloseAfterSeconds);
        if (CloseBox.SelectedIndex < 0) CloseBox.SelectedIndex = 0;

        // Messages
        messages = [.. settings.Messages.Select(m => new EditableText(m))];
        replies = [.. settings.Replies.Select(r => new EditableText(r))];
        MessagesList.ItemsSource = messages;
        RepliesList.ItemsSource = replies;
        AddReplyButton.IsEnabled = replies.Count < 3;

        // Keyboard shortcuts
        appShortcuts.Add(new ShortcutRow(GlobalHotkeys.OpenAction, Strings.Get("TrayOpen"), null));
        appShortcuts.Add(new ShortcutRow(GlobalHotkeys.PanelAction, Strings.Get("ShortcutPanel"), null));
        appShortcuts.Add(new ShortcutRow(GlobalHotkeys.DoNotDisturbAction, Strings.Get("ShortcutDnd"), null));
        PcShortcutsList.ItemsSource = pcShortcuts;
        AppShortcutsList.ItemsSource = appShortcuts;
        FillPcShortcuts();
        viewModel.Peers.CollectionChanged += OnPeerListChanged;

        // History
        HistoryKeepBox.Items.Add(new Option<int>(1, Strings.Get("HistoryKeepToday")));
        HistoryKeepBox.Items.Add(new Option<int>(7, Strings.Format("HistoryKeepDays", 7)));
        HistoryKeepBox.Items.Add(new Option<int>(30, Strings.Format("HistoryKeepDays", 30)));
        Select(HistoryKeepBox, settings.HistoryDays);
        ShowHistory();
        viewModel.History.Changed += OnHistoryChanged;

        // Network
        var shared = LocalNetwork.AddressesToShare();
        AddressText.Text = shared.Count > 0 ? shared[0].ToString() : "—";
        OtherAddressesText.Text = shared.Count > 1 ? Strings.Format("AddressOthers", string.Join(" · ", shared.Skip(1))) : "";
        OtherAddressesText.Visibility = shared.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        addresses = [.. settings.Addresses];
        AddressesList.ItemsSource = addresses;
        ShowFound();
        node.PeersChanged += OnPeersChanged;

        // Updates
        GitHubUpdatesCard.Visibility = AppPackage.IsPackaged ? Visibility.Collapsed : Visibility.Visible;
        StoreUpdatesCard.Visibility = AppPackage.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
        StoreVersionText.Text = Strings.Format("UpdatesByStoreHint", App.Version);
        ChannelBox.Items.Add(new Option<UpdateChannel>(UpdateChannel.Stable, Strings.Get("UpdateChannelStable")));
        ChannelBox.Items.Add(new Option<UpdateChannel>(UpdateChannel.Beta, Strings.Get("UpdateChannelBeta")));
        Select(ChannelBox, settings.UpdateChannel);
        CheckAtStartupBox.IsChecked = settings.CheckForUpdatesAtStartup;
        ShowUpdateState();
        Updater.Checked += Updater_Checked;

        // About
        VersionText.Text = Strings.Format("AboutVersion", App.Version);
        DeveloperModeBox.IsChecked = settings.DeveloperMode;
        OpenLogButton.IsEnabled = settings.DeveloperMode;

        PageList.SelectedIndex = page;
        initialized = true;
    }

    /// <summary>The page shown, kept when the window is built again for another language.</summary>
    public int Page => PageList.SelectedIndex;

    private sealed record Option<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static void Select<T>(ComboBox box, T value) =>
        box.SelectedItem = box.Items.OfType<Option<T>>().FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value));

    private static T? Selected<T>(ComboBox box) => box.SelectedItem is Option<T> option ? option.Value : default;

    // Languages are listed in their own language ("English", "Français"), whatever the current UI language.
    private static string NativeName(string code)
    {
        var culture = CultureInfo.GetCultureInfo(code);
        return culture.TextInfo.ToUpper(culture.NativeName[0]) + culture.NativeName[1..];
    }

    // The page chosen in the menu; the others stay in place, hidden, so that the window keeps its height.
    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (pages is null) return; // the first page is chosen while the window is built
        StopRecording();
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == PageList.SelectedIndex ? Visibility.Visible : Visibility.Hidden;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopRecording();
        viewModel.Peers.CollectionChanged -= OnPeerListChanged;
        viewModel.History.Changed -= OnHistoryChanged;
        viewModel.DoNotDisturbChanged -= OnDoNotDisturbChanged;
        node.PeersChanged -= OnPeersChanged;
        Updater.Checked -= Updater_Checked;
        CommitName();
        settings.Save();
    }

    // ---- General ----

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => CommitName();

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitName();
        else if (e.Key == Key.Escape)
        {
            NameBox.Text = node.Name;
            e.Handled = true;
        }
    }

    private void CommitName()
    {
        string name = NameBox.Text.Trim();
        if (name.Length == 0 || name == node.Name)
        {
            NameBox.Text = node.Name;
            return;
        }
        viewModel.Rename(name);
        NameBox.Text = node.Name;
        ThisPcText.Text = Strings.Format("ThisPc", node.Name);
    }

    private async void StartupBox_Click(object sender, RoutedEventArgs e)
    {
        settings.StartWithWindows = StartupBox.IsChecked == true;
        settings.Save();
        ShowStartup(await WindowsStartup.SetAsync(settings.StartWithWindows, profile));
    }

    // The Store version: the state of its startup task, which the user may have changed in Windows.
    private async void ShowStartupAsync() => ShowStartup(await WindowsStartup.GetAsync(settings.StartWithWindows));

    private void ShowStartup(StartupState state)
    {
        StartupBox.IsChecked = state is StartupState.On;
        StartupBox.Visibility = state is StartupState.On or StartupState.Off ? Visibility.Visible : Visibility.Collapsed;
        StartupAppsButton.Visibility = state is StartupState.OffByUser ? Visibility.Visible : Visibility.Collapsed;
        StartupNote.Text = state switch
        {
            StartupState.OffByUser => Strings.Get("StartWithWindowsOffByUser"),
            StartupState.ByPolicy => Strings.Get("StartWithWindowsByPolicy"),
            _ => "",
        };
        StartupNote.Visibility = StartupNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartupApps_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:startupapps");

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void WidgetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.Widget = Selected<WidgetPosition>(WidgetBox);
        settings.Save();
        ((App)Application.Current).UpdateWidget();
    }

    private void TrayClickBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.TrayClick = Selected<TrayClickAction>(TrayClickBox);
        settings.Save();
    }

    private void DndBox_Click(object sender, RoutedEventArgs e)
    {
        if (DndBox.IsChecked == true)
            viewModel.TurnOnDoNotDisturb(DoNotDisturbMenu.Lengths[Math.Max(0, DndLengthBox.SelectedIndex)].Length);
        else
            viewModel.TurnOffDoNotDisturb();
    }

    private void OnDoNotDisturbChanged(object? sender, EventArgs e) => DndBox.IsChecked = settings.IsDoNotDisturb;

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || Selected<string>(LanguageBox) is not { } language || language == settings.Language) return;
        settings.Language = language;
        settings.Save();
        // Texts are read when a window is built: the windows are built again in the new language.
        Dispatcher.BeginInvoke(() => ((App)Application.Current).ReloadLanguage(this));
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.Theme = Selected<AppTheme>(ThemeBox);
        settings.Save();
        App.ApplyTheme(settings.Theme);
    }

    // ---- Ring ----

    private void FillSounds()
    {
        SoundBox.Items.Clear();
        foreach (var (sound, key) in BellSound.BuiltIn) SoundBox.Items.Add(new Option<string>(sound, Strings.Get(key)));
        if (File.Exists(settings.SoundFile))
            SoundBox.Items.Add(new Option<string>(BellSound.OwnFile, Strings.Format("SoundOwn", Path.GetFileName(settings.SoundFile))));
        Select(SoundBox, settings.Sound);
        if (SoundBox.SelectedIndex < 0) SoundBox.SelectedIndex = 0;
        OwnSoundText.Text = File.Exists(settings.SoundFile) ? settings.SoundFile : Strings.Get("OwnSoundHint");
    }

    private void SoundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || Selected<string>(SoundBox) is not { } sound) return;
        settings.Sound = sound;
        settings.Save();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => BellSound.Play(settings.Sound, settings.SoundFile, settings.Volume);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Strings.Get("OwnSoundFilter") };
        if (File.Exists(settings.SoundFile)) dialog.InitialDirectory = Path.GetDirectoryName(settings.SoundFile);
        if (dialog.ShowDialog(this) != true) return;
        settings.SoundFile = dialog.FileName;
        settings.Sound = BellSound.OwnFile;
        settings.Save();
        FillSounds();
        BellSound.Play(settings.Sound, settings.SoundFile, settings.Volume);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!initialized) return;
        settings.Volume = (int)Math.Round(VolumeSlider.Value);
        VolumeText.Text = $"{settings.Volume} %";
    }

    private void MutedBox_Click(object sender, RoutedEventArgs e)
    {
        settings.RingWhenMuted = MutedBox.IsChecked == true;
        settings.Save();
    }

    private void RepeatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.Repeat = Selected<int>(RepeatBox);
        settings.Save();
    }

    // ---- Popup ----

    private void QuietOption_Click(object sender, MouseButtonEventArgs e) => QuietRadio.IsChecked = true;

    private void LoudOption_Click(object sender, MouseButtonEventArgs e) => LoudRadio.IsChecked = true;

    private void StyleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.PopupStyle = LoudRadio.IsChecked == true ? PopupStyle.Loud : PopupStyle.Quiet;
        settings.Save();
    }

    private RadioButton CornerButton(ScreenCorner corner) => corner switch
    {
        ScreenCorner.TopLeft => TopLeftCorner,
        ScreenCorner.TopRight => TopRightCorner,
        ScreenCorner.BottomLeft => BottomLeftCorner,
        _ => BottomRightCorner,
    };

    private void Corner_Checked(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        settings.Corner = Enum.GetValues<ScreenCorner>().First(c => CornerButton(c) == sender);
        settings.Save();
    }

    private void ScreenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.PopupOnMouseScreen = Selected<bool>(ScreenBox);
        settings.Save();
    }

    private void CloseBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.CloseAfterSeconds = Selected<int>(CloseBox);
        settings.Save();
    }

    // ---- Messages ----

    private void AddMessage_Click(object sender, RoutedEventArgs e) => messages.Add(new EditableText("") { IsEditing = true });

    private void AddReply_Click(object sender, RoutedEventArgs e)
    {
        if (replies.Count < 3) replies.Add(new EditableText("") { IsEditing = true });
        AddReplyButton.IsEnabled = replies.Count < 3;
    }

    private static EditableText? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as EditableText;

    private void EditText_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        row.Before = row.Text;
        row.IsEditing = true;
    }

    private void DeleteText_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        messages.Remove(row);
        replies.Remove(row);
        SaveTexts();
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not TextBox box) return;
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void EditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsEditing: true } row) EndEdit(row);
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (e.Key == Key.Enter) EndEdit(row);
        else if (e.Key == Key.Escape)
        {
            row.Text = row.Before;
            EndEdit(row);
            e.Handled = true;
        }
    }

    /// <summary>The edit ends: an empty text removes its row.</summary>
    private void EndEdit(EditableText row)
    {
        row.IsEditing = false;
        row.Text = row.Text.Trim();
        if (row.Text.Length == 0)
        {
            messages.Remove(row);
            replies.Remove(row);
        }
        SaveTexts();
    }

    private void SaveTexts()
    {
        settings.Messages = messages.Where(m => !m.IsEditing && m.Text.Length > 0).Select(m => m.Text).ToList();
        settings.Replies = replies.Where(r => !r.IsEditing && r.Text.Length > 0).Select(r => r.Text).Take(3).ToList();
        settings.Save();
        AddReplyButton.IsEnabled = replies.Count < 3;
        viewModel.ReloadMessages();
        string time = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        QuietPreview.Show(PopupStyle.Quiet, Strings.Get("PreviewName"), Strings.Get("PreviewMessage"), false, time, settings.Replies);
        LoudPreview.Show(PopupStyle.Loud, Strings.Get("PreviewName"), Strings.Get("PreviewMessage"), false, time, settings.Replies);
    }

    // ---- Keyboard shortcuts ----

    private static App TheApp => (App)Application.Current;

    private void OnPeerListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => FillPcShortcuts();

    // One row a PC of the list, in its order; a row already there is kept (it may be recording).
    private void FillPcShortcuts()
    {
        var rows = viewModel.Peers
            .Select(p => pcShortcuts.FirstOrDefault(r => r.Peer == p) ?? new ShortcutRow(GlobalHotkeys.RingAction(p.Id), p.Name, p))
            .ToList();
        if (recording is { IsPc: true } && !rows.Contains(recording)) StopRecording();
        pcShortcuts.Clear();
        foreach (var row in rows) pcShortcuts.Add(row);
        NoPcRow.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowShortcuts();
    }

    // The keys of each row, from the settings, and whether another app holds them.
    private void ShowShortcuts()
    {
        var taken = TheApp.Shortcuts?.Taken;
        foreach (var row in pcShortcuts.Concat(appShortcuts))
        {
            row.Hotkey = settings.Shortcuts.TryGetValue(row.Action, out string? text) ? Hotkey.Parse(text) : null;
            row.IsTaken = row.HasHotkey && taken?.Contains(row.Action) == true;
        }
    }

    // "Définir" or the pencil: the row waits for keys; meanwhile no shortcut works (their keys must reach this window).
    private void SetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutRow row) return;
        StopRecording();
        recording = row;
        ShowHeld(row, ModifierKeys.None);
        row.IsRecording = true;
        TheApp.Shortcuts?.Suspend();
        recorder = new KeyRecorder(OnShortcutKeys);
        // Without the hook, the keys go to the window: the button clicked is hidden now.
        Dispatcher.BeginInvoke(() => Keyboard.Focus(this), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void DeleteShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutRow row) return;
        StopRecording();
        settings.Shortcuts.Remove(row.Action);
        settings.Save();
        TheApp.ApplyShortcuts();
        ShowShortcuts();
    }

    // Recording ends (keys taken, Échap, a click elsewhere, another page): the shortcuts work again.
    private void StopRecording()
    {
        if (recording is null) return;
        recording.IsRecording = false;
        recording = null;
        recorder?.End();
        recorder = null;
        TheApp.ApplyShortcuts();
        ShowShortcuts();
    }

    private static void ShowHeld(ShortcutRow row, ModifierKeys modifiers)
    {
        row.IsRefused = false;
        row.RecordingNote = Strings.Get("ShortcutEscape");
        row.RecordingText = modifiers == ModifierKeys.None
            ? Strings.Get("ShortcutPress")
            : string.Join(" + ", Hotkey.ModifierParts(modifiers)) + " + …";
    }

    /// <summary>
    /// While a row records: the modifiers held show in it; a key with them is the shortcut, unless it is not enough keys,
    /// types a character on this keyboard (AltGr), or is another row's: then the row says why and waits for others.
    /// <paramref name="key"/> is null for a modifier, pressed or let go (a refusal stays until the next keys).
    /// </summary>
    private void OnShortcutKeys(ModifierKeys modifiers, Key? key, bool down)
    {
        if (recording is not { } row) return;
        if (key is not { } pressed)
        {
            if (down || !row.IsRefused) ShowHeld(row, modifiers);
            return;
        }
        if (pressed == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();
            return;
        }
        var hotkey = new Hotkey(modifiers, pressed);
        string? refusal = !hotkey.IsEnoughKeys ? Strings.Get("ShortcutTooSimple")
            : hotkey.TypedCharacter() is { } typed ? Strings.Format("ShortcutTypes", typed)
            : pcShortcuts.Concat(appShortcuts).FirstOrDefault(r => r != row && r.Hotkey == hotkey) is { } other
                ? Strings.Format("ShortcutUsedBy", other.Peer?.Name ?? other.Name)
            : null;
        if (refusal is not null)
        {
            row.RecordingText = string.Join(" + ", hotkey.Parts);
            row.RecordingNote = refusal;
            row.IsRefused = true;
            return;
        }
        settings.Shortcuts[row.Action] = hotkey.ToString();
        settings.Save();
        DevLog.Write($"Shortcut set: {hotkey} for {(GlobalHotkeys.PcOf(row.Action) is { } pc ? "ring " + pc[..Math.Min(8, pc.Length)] : row.Action)}");
        StopRecording();
    }

    // Without the hook (Windows refused it): the keys of the window. WPF does not count Win among the modifiers.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recording is null) return;
        e.Handled = true;
        if (recorder is { IsHooked: true }) return;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        if (e.IsRepeat || key == Key.None) return;
        OnShortcutKeys(HeldModifiers(), Hotkey.IsModifier(key) ? null : key, true);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (recording is null) return;
        e.Handled = true;
        if (recorder is not { IsHooked: true }) OnShortcutKeys(HeldModifiers(), null, false);
    }

    private static ModifierKeys HeldModifiers() =>
        Keyboard.Modifiers | (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) ? ModifierKeys.Windows : ModifierKeys.None);

    // A click anywhere but in the box that records gives up.
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (recording is null) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is FrameworkElement { Name: "RecordBox" }) return;
        StopRecording();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => StopRecording();

    // ---- History ----

    private void OnHistoryChanged(object? sender, EventArgs e) => ShowHistory();

    private void ShowHistory()
    {
        var days = HistoryRow.Days(viewModel.History);
        HistoryList.ItemsSource = days;
        HistoryEmptyText.Visibility = days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryClearButton.IsEnabled = days.Count > 0;
    }

    private void HistoryKeepBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        settings.HistoryDays = Selected<int>(HistoryKeepBox);
        settings.Save();
        viewModel.History.KeepDays = settings.HistoryDays;
        ShowHistory();
    }

    private void HistoryClear_Click(object sender, RoutedEventArgs e) => viewModel.History.Clear();

    private void HistoryRingBack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is HistoryRow row) viewModel.RingBack(row.PeerId);
    }

    // ---- Network ----

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AddressText.Text); }
        catch (System.Runtime.InteropServices.COMException) { } // the clipboard is held by another app
    }

    private void ShowAddAddress_Click(object sender, RoutedEventArgs e)
    {
        AddAddressRow.Visibility = Visibility.Visible;
        AddressBox.Focus();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddAddress_Click(sender, e);
    }

    /// <summary>A PC added by hand: kept even when nobody answers yet (it may be off), the answer said under the box.</summary>
    private async void AddAddress_Click(object sender, RoutedEventArgs e)
    {
        string text = AddressBox.Text.Trim();
        AddAddressResult.Visibility = Visibility.Visible;
        if (BellNode.ParseAddress(text) is null)
        {
            AddAddressResult.Text = Strings.Get("AddressInvalid");
            return;
        }
        AddAddressButton.IsEnabled = false;
        var found = await node.ProbeAsync(text);
        AddAddressButton.IsEnabled = true;
        if (!addresses.Contains(text, StringComparer.OrdinalIgnoreCase)) addresses.Add(text);
        settings.Addresses = [.. addresses];
        settings.Save();
        node.SetAddresses(settings.Addresses);
        AddAddressResult.Text = found is null ? Strings.Get("AddressNotFound") : Strings.Format("AddressFound", found.Name);
        AddressBox.Text = "";
    }

    private void DeleteAddress_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string address) return;
        addresses.Remove(address);
        settings.Addresses = [.. addresses];
        settings.Save();
        node.SetAddresses(settings.Addresses);
    }

    private void OnPeersChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ShowFound);

    private void ShowFound()
    {
        int online = node.Peers.Count(p => p.Online);
        FoundText.Text = online == 0 ? Strings.Get("FoundNone") : Strings.Count("Found", online);
        UdpNote.Text = Strings.Format("UdpUnavailable", BellProtocol.DefaultPort);
        UdpNote.Visibility = node.HearsAnnouncements ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        FoundText.Text = Strings.Get("Searching");
        try { await node.RefreshAsync(); }
        catch (OperationCanceledException) { }
        RefreshButton.IsEnabled = true;
        ShowFound();
    }

    // ---- Updates ----

    private void Updater_Checked(object? sender, EventArgs e) => ShowUpdateState();

    /// <summary>
    /// The installed version, then what the last check found (nothing before a check), or <paramref name="status"/>
    /// (progress, error) instead.
    /// </summary>
    private void ShowUpdateState(string? status = null)
    {
        var available = Updater.Available;
        InstalledVersionText.Text = Strings.Format("UpdateStatusIdle", App.Version);
        UpdateStatusText.Text = status
            ?? (available is not null ? Strings.Format("UpdateAvailable", available.Version)
                : Updater.HasChecked ? Strings.Get("UpdateUpToDate")
                : "");
        UpdateStatusText.Visibility = UpdateStatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesLink.Visibility = available is not null && status is null ? Visibility.Visible : Visibility.Collapsed;

        UpdateButton.Content = available is null ? Strings.Get("UpdateCheck") : Strings.Get("UpdateInstall");
        if (available is null) UpdateButton.ClearValue(StyleProperty);
        else UpdateButton.SetResourceReference(StyleProperty, "AccentButtonStyle");
        UpdateButton.IsEnabled = !updating && !Updater.IsInstalling;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is { } release) await InstallUpdateAsync(release);
        else await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        updating = true;
        ShowUpdateState(Strings.Get("UpdateStatusChecking"));
        string? error = null;
        try
        {
            await Updater.CheckAsync(settings.UpdateChannel);
        }
        catch (Exception ex)
        {
            error = Strings.Format("UpdateCheckFailed", Updater.Describe(ex));
        }
        updating = false;
        ShowUpdateState(error);
    }

    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        if (Updater.IsInstalling) return;
        updating = true;
        ShowUpdateState(Strings.Get("UpdateDownloading"));
        UpdateProgress.Value = 0;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p =>
        {
            UpdateProgress.Value = p;
            if (p < 1) return;
            // Downloaded: the installer now waits for the UAC prompt, then installs.
            UpdateProgress.IsIndeterminate = true;
            ShowUpdateState(Strings.Get("UpdateStarting"));
        });

        string status;
        try
        {
            if (await Updater.InstallAsync(release, progress)) return; // the app is closing, the installer takes over
            status = Strings.Get("UpdateCancelled");
        }
        catch (Exception ex)
        {
            status = Strings.Format("ErrorUpdate", Updater.Describe(ex));
        }
        updating = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        ShowUpdateState(status);
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is { } release) Updater.OpenReleasePage(release);
    }

    private void CheckAtStartup_Click(object sender, RoutedEventArgs e)
    {
        settings.CheckForUpdatesAtStartup = CheckAtStartupBox.IsChecked == true;
        settings.Save();
    }

    private async void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        var channel = Selected<UpdateChannel>(ChannelBox);
        if (channel == settings.UpdateChannel) return;
        settings.UpdateChannel = channel;
        settings.Save();
        // What is offered depends on the channel: look again.
        if (!updating) await CheckForUpdatesAsync();
    }

    private void OpenStore_Click(object sender, RoutedEventArgs e) => OpenUri(AppPackage.StorePage);

    // ---- About ----

    private void DeveloperMode_Click(object sender, RoutedEventArgs e)
    {
        bool on = DeveloperModeBox.IsChecked == true;
        if (!on) DevLog.Write("Developer mode off");
        settings.DeveloperMode = DevLog.Enabled = on;
        settings.Save();
        OpenLogButton.IsEnabled = on;
        if (on) DevLog.Write($"Developer mode on: LocalBell {App.Version}, this computer \"{node.Name}\" ({settings.Id[..8]}), TCP {node.Port}"
            + $", {node.Peers.Count(p => p.Online)} computer(s) online");
    }

    // The log in the app that opens text files (Notepad), created empty if there is none yet.
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DevLog.FilePath)!);
            if (!File.Exists(DevLog.FilePath)) File.WriteAllText(DevLog.FilePath, "");
            Process.Start(new ProcessStartInfo(DevLog.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
