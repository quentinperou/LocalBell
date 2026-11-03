using LocalBell.App.Services;

namespace LocalBell.App.ViewModels;

/// <summary>
/// A row of Settings → Keyboard shortcuts (the author's choice A, November 3): a PC to ring, or an action of the app;
/// its keys with a pencil and a bin, "Définir" when it has none, or the box that records them.
/// </summary>
public sealed class ShortcutRow(string action, string name, PeerItem? peer) : ObservableObject
{
    private Hotkey? hotkey;
    private bool isRecording;
    private bool isTaken;
    private string recordingText = "";
    private string recordingNote = "";
    private bool isRefused;

    /// <summary>"open", "panel", "dnd" or "ring:" + the id of the PC (<see cref="GlobalHotkeys"/>).</summary>
    public string Action { get; } = action;

    public string Name { get; } = name;

    /// <summary>The PC rung, for its dot (online or not); null for the actions of the app.</summary>
    public PeerItem? Peer { get; } = peer;

    public bool IsPc => Peer is not null;

    public Hotkey? Hotkey
    {
        get => hotkey;
        set
        {
            if (!Set(ref hotkey, value)) return;
            OnPropertyChanged(nameof(HasHotkey));
            OnPropertyChanged(nameof(Keys));
        }
    }

    public bool HasHotkey => hotkey is not null;

    /// <summary>Its key caps: "Ctrl", "Alt", "1".</summary>
    public IReadOnlyList<string> Keys => hotkey?.Parts ?? [];

    /// <summary>Another app holds its keys: the row says it, under the name.</summary>
    public bool IsTaken { get => isTaken; set => Set(ref isTaken, value); }

    public bool IsRecording { get => isRecording; set => Set(ref isRecording, value); }

    /// <summary>"Appuyez sur les touches…", or the modifiers held so far ("Ctrl + Alt + …").</summary>
    public string RecordingText { get => recordingText; set => Set(ref recordingText, value); }

    /// <summary>"Échap pour annuler", or why the keys pressed cannot be had (<see cref="IsRefused"/>).</summary>
    public string RecordingNote { get => recordingNote; set => Set(ref recordingNote, value); }

    public bool IsRefused { get => isRefused; set => Set(ref isRefused, value); }
}
