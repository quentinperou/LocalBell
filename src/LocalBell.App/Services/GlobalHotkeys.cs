using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace LocalBell.App.Services;

/// <summary>
/// Keys pressed together for a shortcut, as Settings records them: the modifiers and one key. Saved as "Ctrl+Alt+D1"
/// (the names of <see cref="ModifierKeys"/> and <see cref="Key"/>), shown as "Ctrl", "Alt", "1".
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Modifier, string Name, string TextKey)[] Order =
    [
        (ModifierKeys.Windows, "Win", "KeyWin"),
        (ModifierKeys.Control, "Ctrl", "KeyCtrl"),
        (ModifierKeys.Alt, "Alt", "KeyAlt"),
        (ModifierKeys.Shift, "Shift", "KeyShift"),
    ];

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], out var key) || key == Key.None || IsModifier(key)) return null;
        var modifiers = ModifierKeys.None;
        foreach (string part in parts[..^1])
        {
            int at = Array.FindIndex(Order, o => o.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return null;
            modifiers |= Order[at].Modifier;
        }
        return new Hotkey(modifiers, key);
    }

    public override string ToString()
    {
        var modifiers = Modifiers;
        return string.Join('+', Order.Where(o => modifiers.HasFlag(o.Modifier)).Select(o => o.Name).Append(Key.ToString()));
    }

    /// <summary>What the key caps of Settings show: the modifiers in the UI language ("Maj"), then the key.</summary>
    public IReadOnlyList<string> Parts => [.. ModifierParts(Modifiers), KeyName(Key)];

    public static IEnumerable<string> ModifierParts(ModifierKeys modifiers) =>
        Order.Where(o => modifiers.HasFlag(o.Modifier)).Select(o => Strings.Get(o.TextKey));

    /// <summary>
    /// Fit to be a shortcut anywhere in Windows: two keys among Ctrl, Alt and Shift, or Win (Ctrl + C alone would take
    /// copying from every app, Shift + A typing), or F13 to F24 (the extra keys of some keyboards).
    /// </summary>
    public bool IsEnoughKeys
    {
        get
        {
            if (Key is >= Key.F13 and <= Key.F24 || Modifiers.HasFlag(ModifierKeys.Windows)) return true;
            int count = (Modifiers.HasFlag(ModifierKeys.Control) ? 1 : 0) + (Modifiers.HasFlag(ModifierKeys.Alt) ? 1 : 0)
                + (Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0);
            return count >= 2;
        }
    }

    /// <summary>
    /// What it types on the keyboard in use, when Ctrl + Alt is AltGr there: on a French keyboard AltGr + 0 writes "@"
    /// (Ctrl + Alt + 0 as a shortcut would take it from every app). Null when it types nothing.
    /// </summary>
    public string? TypedCharacter()
    {
        if (!Modifiers.HasFlag(ModifierKeys.Control) || !Modifiers.HasFlag(ModifierKeys.Alt) || Modifiers.HasFlag(ModifierKeys.Windows))
            return null;
        var state = new byte[256];
        state[VK_CONTROL] = state[VK_MENU] = 0x80;
        if (Modifiers.HasFlag(ModifierKeys.Shift)) state[VK_SHIFT] = 0x80;
        int vk = KeyInterop.VirtualKeyFromKey(Key);
        var buffer = new StringBuilder(8);
        // Flag 4: the state of the keyboard (a dead key waiting) is left as it is (Windows 10 1607 and later).
        int count = ToUnicodeEx((uint)vk, MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC), state, buffer, buffer.Capacity, 4, GetKeyboardLayout(0));
        if (count == 0) return null;
        string typed = buffer.ToString(0, Math.Clamp(Math.Abs(count), 0, buffer.Length));
        return typed.Length > 0 && !char.IsControl(typed[0]) ? typed : null;
    }

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>The name of a key as its cap reads: "1", "A", "F5", "Num 1", "ù", or Windows' own name ("Espace", "Suppr").</summary>
    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"Num {(int)(key - Key.NumPad0)}";
        if (key is >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F24) return key.ToString();
        int vk = KeyInterop.VirtualKeyFromKey(key);
        uint character = MapVirtualKey((uint)vk, MAPVK_VK_TO_CHAR) & 0x7FFFFFFF;
        if (character > ' ') return char.ToUpperInvariant((char)character).ToString();
        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        bool extended = key is Key.Insert or Key.Delete or Key.Home or Key.End or Key.PageUp or Key.PageDown
            or Key.Left or Key.Up or Key.Right or Key.Down or Key.Divide or Key.NumLock;
        var name = new StringBuilder(64);
        if (scan != 0 && GetKeyNameText((int)((scan << 16) | (extended ? 1u << 24 : 0)), name, name.Capacity) > 0)
            return name.Length > 1 ? char.ToUpper(name[0]) + name.ToString(1, name.Length - 1).ToLower() : name.ToString();
        return key.ToString();
    }

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const uint MAPVK_VK_TO_VSC = 0, MAPVK_VK_TO_CHAR = 2;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder name, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);
}

/// <summary>
/// The keyboard shortcuts of Settings → Keyboard shortcuts, anywhere in Windows (RegisterHotKey, on a message-only
/// window of the UI thread): ring a PC, open the window or the quick panel, "do not disturb". None by default (the
/// author, November 3, 2026): the user sets his own. A shortcut another app already holds cannot be had: it is in
/// <see cref="Taken"/>, for Settings to say it.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public const string OpenAction = "open";
    public const string PanelAction = "panel";
    public const string DoNotDisturbAction = "dnd";
    private const string RingPrefix = "ring:";

    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly Action<string> run;
    private readonly HwndSource window;
    private readonly Dictionary<int, string> registered = [];
    private readonly HashSet<string> taken = [];

    public GlobalHotkeys(Action<string> run)
    {
        this.run = run;
        // HWND_MESSAGE: a window that is never shown, only there for WM_HOTKEY.
        window = new HwndSource(new HwndSourceParameters("LocalBell shortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        window.AddHook(WndProc);
    }

    public static string RingAction(string pcId) => RingPrefix + pcId;

    /// <summary>The id of the PC a "ring" action rings, or null for the other actions.</summary>
    public static string? PcOf(string action) => action.StartsWith(RingPrefix, StringComparison.Ordinal) ? action[RingPrefix.Length..] : null;

    /// <summary>The actions whose shortcut another app holds (or Windows: Win + L…).</summary>
    public IReadOnlySet<string> Taken => taken;

    /// <summary>Registers these shortcuts in place of the ones before.</summary>
    public void Apply(IEnumerable<KeyValuePair<string, string>> shortcuts)
    {
        Suspend();
        taken.Clear();
        int id = 1;
        foreach (var (action, text) in shortcuts)
        {
            if (Hotkey.Parse(text) is not { } hotkey) continue;
            int modifiers = MOD_NOREPEAT
                | (hotkey.Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0) | (hotkey.Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
                | (hotkey.Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0) | (hotkey.Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);
            if (RegisterHotKey(window.Handle, id, modifiers, KeyInterop.VirtualKeyFromKey(hotkey.Key)))
                registered[id++] = action;
            else
            {
                taken.Add(action);
                DevLog.Write($"Shortcut {hotkey} for {Describe(action)}: taken by another app");
            }
        }
        if (registered.Count > 0) DevLog.Write($"{registered.Count} shortcut(s) registered");
    }

    /// <summary>None of them works until <see cref="Apply"/> (while Settings records one: the keys must reach it).</summary>
    public void Suspend()
    {
        foreach (int id in registered.Keys) UnregisterHotKey(window.Handle, id);
        registered.Clear();
    }

    public void Dispose()
    {
        Suspend();
        window.Dispose();
    }

    // The id of a PC is cut short in the log, as the node's log does.
    private static string Describe(string action) => PcOf(action) is { } pc ? $"ring {pc[..Math.Min(8, pc.Length)]}" : action;

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && registered.TryGetValue(wParam.ToInt32(), out string? action))
        {
            DevLog.Write($"Shortcut: {Describe(action)}");
            run(action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
