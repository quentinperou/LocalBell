using System.Windows;
using System.Windows.Controls;
using LocalBell.App.Services;

namespace LocalBell.App;

/// <summary>The card of a ring, discreet or loud: who rings, the message, the reply buttons.</summary>
public partial class RingCard : UserControl
{
    public RingCard() => InitializeComponent();

    /// <summary>A reply button was clicked (its text).</summary>
    public event Action<string>? Replied;

    /// <summary>The cross was clicked.</summary>
    public event Action? Closed;

    public void Show(PopupStyle style, string name, string message, bool everyone, string time, IReadOnlyList<string> replies)
    {
        bool loud = style == PopupStyle.Loud;
        QuietCard.Visibility = loud ? Visibility.Collapsed : Visibility.Visible;
        LoudCard.Visibility = loud ? Visibility.Visible : Visibility.Collapsed;

        QuietTitle.Text = Strings.Format(everyone ? "PopupRingsEveryone" : "PopupRings", name);
        QuietTime.Text = time;
        LoudName.Text = name;
        LoudLine.Text = Strings.Format(everyone ? "PopupLoudLineEveryone" : "PopupLoudLine", time);

        bool hasMessage = message.Length > 0;
        QuietMessage.Text = LoudMessage.Text = message;
        QuietMessageCard.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        LoudMessage.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;

        var panel = loud ? LoudReplies : QuietReplies;
        panel.Children.Clear();
        for (int i = 0; i < replies.Count; i++)
        {
            string text = replies[i];
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(loud ? 4 : 3, 0, loud ? 4 : 3, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = loud ? 40 : 32,
            };
            if (i == 0) button.Style = (Style)FindResource("AccentButtonStyle");
            button.Click += (_, _) => Replied?.Invoke(text);
            panel.Children.Add(button);
        }
        panel.Visibility = replies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();
}
