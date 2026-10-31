namespace LocalBell.App.ViewModels;

/// <summary>A ready-made message or a reply in Settings: shown as text, edited in place after its pencil.</summary>
public sealed class EditableText(string text) : ObservableObject
{
    private string text = text;
    private bool isEditing;

    public string Text { get => text; set => Set(ref text, value); }

    public bool IsEditing { get => isEditing; set => Set(ref isEditing, value); }

    /// <summary>The text before the edit, put back by Escape.</summary>
    public string Before { get; set; } = text;
}
