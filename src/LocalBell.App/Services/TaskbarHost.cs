using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Threading;
using static LocalBell.App.Services.NativeMethods;

namespace LocalBell.App.Services;

/// <summary>
/// The widget in the taskbar runs in a process of its own (LocalBell.exe --taskbar, see <see cref="TaskbarClient"/>), as
/// NowPlaying's: its window is owned by the taskbar, which keeps it over the taskbar even while Start or search is open
/// (the author's wish, October 31; a window of the app's own went under the taskbar then), but which ties the two
/// together, so that a widget that stops answering could hold the taskbar. This side, in the app, sends it what to
/// show, gets its clicks back, and watches it: a widget silent for 2 seconds is ended (the taskbar is free again at
/// once) and started again. Whatever happens to it, the app goes on.
/// </summary>
public sealed class TaskbarHost : IDisposable
{
    public const string Argument = "--taskbar";

    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(20);

    private readonly Dispatcher ui;
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer restart = new();
    private Process? process;
    private LineChannel? channel;
    private DateTime startedAt;
    private DateTime? pingSentAt; // a ping not answered yet
    private bool ready;
    private bool enabled;
    private bool disposed;
    private int failures;

    // The last state, sent again to a widget started anew.
    private TaskbarState? state;
    private string? sentState;

    public TaskbarHost(Dispatcher ui)
    {
        this.ui = ui;
        watchdog.Tick += (_, _) => Watch();
        restart.Tick += (_, _) =>
        {
            restart.Stop();
            if (enabled && process is null) Start();
        };
    }

    /// <summary>The bell (or the arrow): the flyout opens or closes above the widget (its left edge, the taskbar's top).</summary>
    public event Action<int, int>? FlyoutRequested;

    /// <summary>The favourite was clicked: it rings.</summary>
    public event EventHandler? RingRequested;

    /// <summary>A right click: the menu of the bell near the clock, above the taskbar (its top).</summary>
    public event Action<int>? MenuRequested;

    /// <summary>
    /// The widget process started anew once, <paramref name="after"/> from now: LocalBell started with Windows, and the
    /// widget made in the first minutes of the session once drew black where it is see-through (the author, November 4:
    /// a black square on the taskbar, gone with a widget started again; what was not ready, the graphics or DWM, is not
    /// known).
    /// </summary>
    public void StartAgainAfter(TimeSpan after)
    {
        var once = new DispatcherTimer { Interval = after };
        once.Tick += (_, _) =>
        {
            once.Stop();
            if (disposed || process is not { } running) return;
            DevLog.Write("Widget process started again once, LocalBell having started with Windows");
            End(running);
        };
        once.Start();
    }

    /// <summary>What the widget shows. <paramref name="enabled"/>: the widget process runs (the widget is on).</summary>
    public void Update(TaskbarState state, bool enabled)
    {
        this.state = state;
        this.enabled = enabled;
        if (!enabled)
        {
            Stop();
            return;
        }
        if (process is null && !restart.IsEnabled) Start();
        Flush();
    }

    // ---- The process ----

    private void Start()
    {
        if (disposed || Environment.ProcessPath is not { } exe) return;
        string name = $"LocalBell.Taskbar.{Environment.ProcessId}.{Guid.NewGuid():N}";
        NamedPipeServerStream pipe;
        Process? started;
        try
        {
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            started = Process.Start(new ProcessStartInfo(exe, $"{Argument} {name}") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            DevLog.Write($"Widget process not started: {ex.Message}");
            Lost(null);
            return;
        }
        if (started is null)
        {
            pipe.Dispose();
            Lost(null);
            return;
        }
        process = started;
        startedAt = DateTime.UtcNow;
        ready = false;
        pingSentAt = null;
        started.EnableRaisingEvents = true;
        started.Exited += (_, _) => ui.BeginInvoke(() => Lost(started));
        watchdog.Start();
        DevLog.Write($"Widget process {started.Id} started");
        _ = ConnectAsync(pipe, started);
    }

    private async Task ConnectAsync(NamedPipeServerStream pipe, Process started)
    {
        try
        {
            using var timeout = new CancellationTokenSource(StartLimit);
            await pipe.WaitForConnectionAsync(timeout.Token);
            // Only the process started here may talk to the app.
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != started.Id)
                throw new IOException("Another process connected.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            pipe.Dispose();
            _ = ui.BeginInvoke(() => End(started));
            return;
        }
        _ = ui.BeginInvoke(() =>
        {
            if (started != process)
            {
                pipe.Dispose();
                return;
            }
            channel = new LineChannel(pipe,
                line => ui.BeginInvoke(() => Received(started, line)),
                () => ui.BeginInvoke(() => Lost(started)));
            sentState = null;
            Flush();
        });
    }

    /// <summary>Sends the state when it changed since the last line.</summary>
    private void Flush()
    {
        if (channel is null || state is null) return;
        string json = JsonSerializer.Serialize(state);
        if (json == sentState) return;
        sentState = json;
        channel.Send("state", json);
    }

    private void Received(Process from, string line)
    {
        if (from != process) return;
        var (verb, payload) = LineChannel.Parse(line);
        switch (verb)
        {
            case "ready":
                ready = true;
                break;
            case "pong":
                pingSentAt = null;
                break;
            case "flyout" when payload.Split(',') is [var left, var top]
                && int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(top, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y):
                FlyoutRequested?.Invoke(x, y);
                break;
            case "ring":
                RingRequested?.Invoke(this, EventArgs.Empty);
                break;
            case "menu" when int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int barTop):
                MenuRequested?.Invoke(barTop);
                break;
            case "log":
                DevLog.Write(payload);
                break;
        }
    }

    // Once a second: a ping, which the widget answers from its window's thread. Measured from the ping itself, so that
    // a busy app never takes itself for a hung widget.
    private void Watch()
    {
        if (process is not { } running)
        {
            watchdog.Stop();
            return;
        }
        var now = DateTime.UtcNow;
        if (channel is null || !ready)
        {
            if (now - startedAt > StartLimit)
            {
                DevLog.Write("Widget process did not start: ended");
                End(running);
            }
            return;
        }
        if (pingSentAt is { } sent)
        {
            if (now - sent > HangLimit)
            {
                DevLog.Write("Widget process not answering: ended (the taskbar is free again)");
                End(running);
            }
            return;
        }
        pingSentAt = now;
        channel.Send("ping");
    }

    private static void Kill(Process target)
    {
        try { target.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // Ended on purpose (hung, or never connected): what follows is the same as a crash.
    private void End(Process target)
    {
        Kill(target);
        Lost(target);
    }

    /// <summary>The widget process went away (ended, crashed, the taskbar it belonged to restarted): started again.</summary>
    private void Lost(Process? lost)
    {
        if (lost is not null && lost != process) return;
        Disconnect();
        if (!enabled || disposed) return;
        // Again at once after a long life (Explorer restarted), slower and slower when it keeps failing.
        failures = DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1) ? 0 : failures + 1;
        restart.Interval = TimeSpan.FromSeconds(failures == 0 ? 1 : Math.Min(60, Math.Pow(2, failures)));
        DevLog.Write($"Widget process gone: started again in {restart.Interval.TotalSeconds:0} s");
        restart.Start();
    }

    private void Disconnect()
    {
        var gone = process;
        process = null;
        channel?.Dispose();
        channel = null;
        ready = false;
        pingSentAt = null;
        if (gone is not null && !gone.HasExited) Kill(gone);
        gone?.Dispose();
    }

    /// <summary>The widget turned off: its process ends (on its own, when its pipe closes).</summary>
    private void Stop()
    {
        restart.Stop();
        watchdog.Stop();
        var stopped = process;
        process = null;
        channel?.Dispose();
        channel = null;
        ready = false;
        stopped?.Dispose();
    }

    public void Dispose()
    {
        disposed = true;
        // A widget that does not answer any more is ended; otherwise it closes itself when the pipe does.
        if (pingSentAt is { } sent && DateTime.UtcNow - sent > TimeSpan.FromSeconds(1) && process is { } hung) Kill(hung);
        Stop();
    }
}
