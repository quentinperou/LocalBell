using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using static LocalBell.App.Services.NativeMethods;

namespace LocalBell.App.Services;

/// <summary>
/// The widget process (LocalBell.exe --taskbar, started by <see cref="TaskbarHost"/>), as NowPlaying's: only the widget of
/// the taskbar, nothing else of the app (no network, no settings). It shows what the app sends, sends it its clicks back
/// (allowing the app to come to the front for its flyout and its menu), answers its pings from the widget's thread, and
/// quits when the app goes away or when its taskbar does (Explorer restarted: the app starts a new one).
/// </summary>
public sealed class TaskbarClient
{
    private readonly Application app;
    private readonly LineChannel channel;
    private readonly TaskbarWidget widget;
    private readonly int appProcessId;
    private bool quitting;

    private TaskbarClient(Application app, NamedPipeClientStream pipe, int appProcessId)
    {
        this.app = app;
        this.appProcessId = appProcessId;
        widget = new TaskbarWidget(appProcessId);
        // The lines are handled on the widget's thread, once this is built.
        channel = new LineChannel(pipe,
            line => app.Dispatcher.BeginInvoke(() => Received(line)),
            () => app.Dispatcher.BeginInvoke(Quit));
        widget.FlyoutRequested += (left, barTop) => ToApp("flyout", $"{left},{barTop}");
        widget.RingRequested += (_, _) => channel.Send("ring");
        widget.MenuRequested += barTop => ToApp("menu", barTop.ToString(CultureInfo.InvariantCulture));
        widget.Log += text => channel.Send("log", text);
        // Its taskbar went away with Explorer: this process ends, the app starts a new one for the new taskbar.
        widget.Closed += (_, _) => Quit();
        channel.Send("ready");
    }

    /// <summary>Runs the widget process: connects to the app, waits for a taskbar to belong to, shows the widget.</summary>
    public static void Run(Application app, string pipeName)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // No message box from this process: it ends, and the app starts it again.
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            app.Shutdown(1);
        };

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(5000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            app.Shutdown(1);
            return;
        }
        int appId = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) ? (int)server : 0;

        // Explorer restarting: its taskbar comes back after a moment.
        var waitForTaskbar = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        int tries = 0;
        void TryStart()
        {
            if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
            {
                waitForTaskbar.Stop();
                _ = new TaskbarClient(app, pipe, appId);
            }
            else if (++tries > 120)
            {
                waitForTaskbar.Stop();
                pipe.Dispose();
                app.Shutdown(1);
            }
        }
        waitForTaskbar.Tick += (_, _) => TryStart();
        waitForTaskbar.Start();
        TryStart();
    }

    // This process got the click: the app may come to the front for its flyout or its menu.
    private void ToApp(string verb, string payload)
    {
        if (appProcessId != 0) AllowSetForegroundWindow(appProcessId);
        channel.Send(verb, payload);
    }

    private void Received(string line)
    {
        var (verb, payload) = LineChannel.Parse(line);
        switch (verb)
        {
            case "ping":
                channel.Send("pong");
                break;
            case "state":
                try
                {
                    if (JsonSerializer.Deserialize<TaskbarState>(payload) is { } state) widget.Apply(state);
                }
                catch (JsonException) { }
                break;
        }
    }

    private void Quit()
    {
        if (quitting) return;
        quitting = true;
        channel.Dispose();
        app.Shutdown();
    }
}
