using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LocalBell.App.Services;
using LocalBell.App.ViewModels;

namespace LocalBell.App;

public partial class MainWindow : Window
{
    private bool quitting;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = ViewModel = viewModel;
        MicaBackdrop.Apply(this);
    }

    public MainViewModel ViewModel { get; }

    /// <summary>The cross hides the window: LocalBell keeps running near the clock, to be rung. Quit closes it.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true;
        Hide();
    }

    public void CloseForQuit()
    {
        quitting = true;
        Close();
    }

    /// <summary>"Mettre à jour" in the quick panel or the menu: installed from the bar of this window, which shows the progress.</summary>
    public void InstallUpdate() => Updates.Install();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenSettings();

    /// <summary>The moon: the lengths of "do not disturb", as in the menu of the bell near the clock.</summary>
    private void MoonButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = DoNotDisturbMenu.Build(ViewModel);
        menu.PlacementTarget = MoonButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}

/// <summary>The lengths of "do not disturb", shared by the moon of the list and the menu of the bell near the clock.</summary>
public static class DoNotDisturbMenu
{
    public static readonly (string Length, string Key)[] Lengths =
        [("30", "Dnd30"), ("60", "Dnd60"), ("tomorrow", "DndTomorrow"), ("forever", "DndForever")];

    public static ContextMenu Build(MainViewModel viewModel)
    {
        var menu = new ContextMenu();
        foreach (var item in Items(viewModel)) menu.Items.Add(item);
        return menu;
    }

    public static IEnumerable<MenuItem> Items(MainViewModel viewModel) => Lengths.Select(l =>
    {
        var item = new MenuItem { Header = Strings.Get(l.Key) };
        item.Click += (_, _) => viewModel.TurnOnDoNotDisturb(l.Length);
        return item;
    });
}
