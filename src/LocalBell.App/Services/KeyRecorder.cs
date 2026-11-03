using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace LocalBell.App.Services;

/// <summary>
/// The keys pressed while Settings records a shortcut, read before Windows acts on them (a low-level keyboard hook, for
/// that time only). WPF does not count Win as a modifier, and Windows acts on its own shortcuts before any window sees
/// them (Win alone opens Start, Win + E the Explorer): Win + E is recorded instead, and then said taken. Meanwhile no
/// key reaches any app (the author, November 3: "on ne peut pas utiliser la touche Windows").
/// </summary>
public sealed class KeyRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    private readonly Action<ModifierKeys, Key?, bool> pressed;
    private readonly HookProc proc; // kept from the garbage collector while hooked
    private readonly HashSet<int> held = [];
    private IntPtr hook;
    private bool ending;

    /// <param name="pressed">The modifiers held, the key pressed (null for a modifier), and whether it went down.</param>
    public KeyRecorder(Action<ModifierKeys, Key?, bool> pressed)
    {
        this.pressed = pressed;
        proc = OnKey;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) DevLog.Write($"Keyboard hook refused ({Marshal.GetLastWin32Error()}): shortcuts recorded from the window's keys");
        // The keyboard of every app is held meanwhile: 30 s at most, then the window's own keys (a recording left
        // open must not leave the PC without a keyboard).
        var limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        limit.Tick += (_, _) =>
        {
            limit.Stop();
            Dispose();
        };
        limit.Start();
    }

    /// <summary>False when Windows refused the hook: the window's own key events are used instead.</summary>
    public bool IsHooked => hook != IntPtr.Zero;

    /// <summary>
    /// The recording is over: the hook goes once every key held is let go (Windows must not see half of them: a Win let
    /// go alone would open Start), or after 3 s if a key let go was missed.
    /// </summary>
    public void End()
    {
        if (ending) return;
        ending = true;
        if (held.Count == 0)
        {
            Dispose();
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Dispose();
        };
        timer.Start();
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    // On the UI thread (the one that hooked), through its message loop.
    private IntPtr OnKey(int code, IntPtr message, ref KBDLLHOOKSTRUCT info)
    {
        if (code < 0 || hook == IntPtr.Zero) return CallNextHookEx(IntPtr.Zero, code, message, ref info);
        int vk = (int)info.vkCode;
        bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
        try
        {
            if (down)
            {
                bool repeat = !held.Add(vk);
                if (!ending && !repeat) pressed(Modifiers(), IsModifier(vk) ? null : KeyInterop.KeyFromVirtualKey(vk), true);
            }
            else
            {
                held.Remove(vk);
                if (ending)
                {
                    if (held.Count == 0) Dispatcher.CurrentDispatcher.BeginInvoke(Dispose);
                }
                else if (IsModifier(vk)) pressed(Modifiers(), null, false);
            }
        }
        catch (Exception ex)
        {
            // Never out of the hook: Windows would end the app.
            DevLog.Write($"Error while recording a shortcut: {ex}");
        }
        return 1; // kept from Windows and the other apps
    }

    private ModifierKeys Modifiers()
    {
        var modifiers = ModifierKeys.None;
        foreach (int vk in held)
            modifiers |= vk switch
            {
                0x5B or 0x5C => ModifierKeys.Windows,
                0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
                0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
                0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
                _ => ModifierKeys.None,
            };
        return modifiers;
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, ref KBDLLHOOKSTRUCT info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, ref KBDLLHOOKSTRUCT info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
