using System.Text.Json;
using System.Text.Json.Serialization;
using LocalBell.Core.Updates;

namespace LocalBell.App.Services;

/// <summary>Light or dark appearance of the app.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows setting ("Choose your mode").</summary>
    System,
    Light,
    Dark,
}

/// <summary>The popup of a ring: the size of a Windows notification, or a loud one with an orange band.</summary>
public enum PopupStyle
{
    Quiet,
    Loud,
}

/// <summary>Where the widget sits on the taskbar (Settings → General).</summary>
public enum WidgetPosition
{
    /// <summary>On the left, after the weather of Windows (and after the widgets of other apps: NowPlaying…).</summary>
    Left,

    /// <summary>Left of the notification area, near the clock.</summary>
    NearClock,

    /// <summary>No widget: the bell near the clock only.</summary>
    Hidden,
}

public enum ScreenCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>A PC heard from in an earlier run: listed offline at start, until it answers.</summary>
public sealed class KnownPc
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// User preferences, saved in %LOCALAPPDATA%\LocalBell\settings.json (a profile of its own with --profile, to run a
/// second LocalBell on the same PC for a test).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Follow the Windows display language.</summary>
    public const string SystemLanguage = "system";

    /// <summary>UI languages with a translation (Properties/Strings*.resx). English is the default.</summary>
    public static readonly string[] SupportedLanguages = ["en", "fr"];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverter() },
    };

    private string filePath = "";

    /// <summary>What went wrong reading the file, for the developer log (written before the log is set up).</summary>
    [JsonIgnore]
    public string? LoadIssue { get; private set; }

    /// <summary>This install, for the other PCs: made once, kept when the PC is renamed.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The name the others see; empty until chosen (the Windows name of the PC is shown meanwhile).</summary>
    public string Name { get; set; } = "";

    public bool StartWithWindows { get; set; } = true;

    /// <summary>"Do not disturb" until then (<see cref="DateTimeOffset.MaxValue"/>: until turned off); null when off.</summary>
    public DateTimeOffset? DoNotDisturbUntil { get; set; }

    /// <summary><see cref="SystemLanguage"/> or one of <see cref="SupportedLanguages"/>.</summary>
    public string Language { get; set; } = SystemLanguage;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>A sound of <see cref="BellSound"/>, or <see cref="BellSound.OwnFile"/> for <see cref="SoundFile"/>.</summary>
    public string Sound { get; set; } = BellSound.CounterBell;

    /// <summary>The user's own sound (WAV or MP3).</summary>
    public string? SoundFile { get; set; }

    /// <summary>The volume of the ring, 0 to 100, apart from the volume of Windows.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>The PC's sound is turned back on while the bell rings (<see cref="WindowsVolume"/>).</summary>
    public bool RingWhenMuted { get; set; } = true;

    /// <summary>How many times the bell rings while the popup waits for an answer.</summary>
    public int Repeat { get; set; } = 3;

    /// <summary>Loud by default (the author's choice, October 31, 2026): a ring must not be missed.</summary>
    public PopupStyle PopupStyle { get; set; } = PopupStyle.Loud;

    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;

    /// <summary>Where the widget sits on the taskbar, or none (asked by the author when it lay under NowPlaying's).</summary>
    public WidgetPosition Widget { get; set; } = WidgetPosition.Left;

    /// <summary>The popup shows on the main screen, or on the screen of the mouse.</summary>
    public bool PopupOnMouseScreen { get; set; }

    /// <summary>The popup closes by itself after this many seconds; 0: when answered or closed.</summary>
    public int CloseAfterSeconds { get; set; }

    /// <summary>The ready-made messages offered under the list; filled in the UI language at the first start.</summary>
    public List<string> Messages { get; set; } = [];

    /// <summary>The reply buttons of the popup (three at most); filled in the UI language at the first start.</summary>
    public List<string> Replies { get; set; } = [];

    /// <summary>PCs added by hand ("192.168.41.12", "PC-BUREAU:53318").</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>The id of the favourite PC, rung in one click from the taskbar.</summary>
    public string? Favorite { get; set; }

    public List<KnownPc> KnownPcs { get; set; } = [];

    /// <summary>Developer mode (About, as in ScreenClip and TextCase; off by default): <see cref="DevLog"/> writes.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Asks GitHub for a newer version at each start (the version installed from GitHub only).</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>The Store version has offered once to uninstall the version installed from GitHub.</summary>
    public bool ClassicRemovalOffered { get; set; }

    [JsonIgnore]
    public bool IsDoNotDisturb => DoNotDisturbUntil is { } until && until > DateTimeOffset.Now;

    public static string FolderOf(string? profile)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalBell");
        return string.IsNullOrEmpty(profile) ? root : Path.Combine(root, "profiles", profile);
    }

    /// <summary>
    /// The settings of the profile, or new ones when there are none. A file that cannot be read (damaged, or written by
    /// a newer LocalBell) is kept aside as settings.json.bad, and its id saved from it when it can be found: a new id
    /// would make this PC a stranger to the others (its favourite, its place in their lists).
    /// </summary>
    public static AppSettings Load(string folder)
    {
        string path = Path.Combine(folder, "settings.json");
        AppSettings? settings = null;
        string? issue = null, text = null;
        try
        {
            if (File.Exists(path))
            {
                text = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(text, Json);
                if (settings is null) issue = "settings.json is empty";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            issue = $"settings.json could not be read ({ex.GetType().Name}: {ex.Message})";
        }
        if (settings is null && text is not null)
        {
            settings = new AppSettings();
            var match = SavedId.Match(text);
            if (match.Success) settings.Id = match.Groups[1].Value;
            try { File.Move(path, path + ".bad", overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            issue += $"; kept as settings.json.bad, {(match.Success ? "its id saved" : "a new id")}";
        }
        settings ??= new AppSettings();
        settings.filePath = path;
        settings.LoadIssue = issue;
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = Guid.NewGuid().ToString("N");
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        settings.Repeat = Math.Clamp(settings.Repeat, 1, 10);
        // A file edited by hand may say null for a list.
        settings.Messages ??= [];
        settings.Replies ??= [];
        settings.Addresses ??= [];
        settings.KnownPcs ??= [];
        return settings;
    }

    // The id in a file that does not parse as a whole (cut short by a power cut): the first "Id": "…" of 32 hex digits.
    private static readonly System.Text.RegularExpressions.Regex SavedId = new("\"Id\"\\s*:\\s*\"([0-9a-fA-F]{32})\"");

    /// <summary>
    /// Writes the file whole or not at all: a temporary file beside it, on the disk before it takes the place of the
    /// old one (a power cut right after the swap would otherwise leave an empty file).
    /// </summary>
    public void Save()
    {
        string temp = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(this, Json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DevLog.Write($"Settings not saved: {ex.Message}");
            try { File.Delete(temp); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Enums as their names; a name this version does not know (written by a newer one) reads as the default value
    /// instead of failing the whole file.
    /// </summary>
    private sealed class TolerantEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsEnum;

        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type))!;

        private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
            {
                JsonTokenType.String when Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value) => value,
                JsonTokenType.Number when reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(T), number) => (T)Enum.ToObject(typeof(T), number),
                _ => default,
            };

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
        }
    }
}
