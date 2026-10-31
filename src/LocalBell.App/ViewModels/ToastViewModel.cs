using System.Windows.Threading;

namespace LocalBell.App.ViewModels;

/// <summary>The short notice over the bottom of the window (a ring sent, a reply), gone after a few seconds.</summary>
public sealed class ToastViewModel : ObservableObject
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer timer;
    private string title = "";
    private string detail = "";
    private string glyph = "";
    private bool isVisible;

    public ToastViewModel()
    {
        timer = new DispatcherTimer { Interval = Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsVisible = false;
        };
    }

    public string Title { get => title; private set => Set(ref title, value); }

    public string Detail { get => detail; private set => Set(ref detail, value); }

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string Glyph { get => glyph; private set => Set(ref glyph, value); }

    public bool HasDetail => Detail.Length > 0;

    public bool IsVisible { get => isVisible; private set => Set(ref isVisible, value); }

    public void Show(string newTitle, string newDetail, string newGlyph)
    {
        Title = newTitle;
        Detail = newDetail;
        Glyph = newGlyph;
        OnPropertyChanged(nameof(HasDetail));
        IsVisible = true;
        timer.Stop();
        timer.Start();
    }
}
