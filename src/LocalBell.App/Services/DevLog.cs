namespace LocalBell.App.Services;

/// <summary>
/// The log of developer mode (Settings › About, as in ScreenClip and TextCase), to understand what went wrong: the
/// network (cards, computers found or gone, rings sent and received, replies, failures), the place of the widget, the
/// popups and the sound. **Never the text of the messages.** In localbell.log next to the settings (of the profile), cut
/// back to its last half beyond 200 KB. Developer mode off: nothing is written.
/// </summary>
internal static class DevLog
{
    private const long MaxBytes = 200 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>Developer mode is on.</summary>
    public static bool Enabled { get; set; }

    public static string FilePath { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalBell", "localbell.log");

    /// <summary>The log of a profile is next to its settings.</summary>
    public static void UseFolder(string folder) => FilePath = Path.Combine(folder, "localbell.log");

    /// <summary>Writes a line (from any thread).</summary>
    public static void Write(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines[(lines.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
