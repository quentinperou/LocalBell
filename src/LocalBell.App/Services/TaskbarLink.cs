using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace LocalBell.App.Services;

/// <summary>
/// What the widget process shows, sent by the app at each change (one line of JSON): the bell, or the bell and the
/// favourite (its name, or what its row says for a while), the moon in "do not disturb", the badge of the missed rings,
/// its place on the taskbar, and its tooltips in the language of the app.
/// </summary>
public sealed record TaskbarState(
    WidgetPosition Position,
    bool DoNotDisturb,
    string? Favorite,
    bool FavoriteOnline,
    int Missed,
    string BellTooltip,
    string? RingTooltip);

/// <summary>
/// Lines of text both ways over a pipe between the app and the widget process ("verb payload"), as NowPlaying's.
/// Reading and writing each run on a thread of their own: a side that hangs never holds the other one, its lines just
/// wait (and are dropped past a few hundred, by then the watchdog has acted).
/// </summary>
public sealed class LineChannel : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly PipeStream pipe;
    private readonly BlockingCollection<string> outgoing = new(boundedCapacity: 256);
    private readonly Action<string> received;
    private readonly Action closed;
    private int isClosed;

    /// <param name="received">Each line read, on the reading thread.</param>
    /// <param name="closed">Once, when the pipe breaks or the channel is disposed, on a thread of the channel.</param>
    public LineChannel(PipeStream pipe, Action<string> received, Action closed)
    {
        this.pipe = pipe;
        this.received = received;
        this.closed = closed;
        new Thread(Read) { IsBackground = true, Name = "Pipe in" }.Start();
        new Thread(Write) { IsBackground = true, Name = "Pipe out" }.Start();
    }

    public void Send(string verb, string? payload = null)
    {
        try
        {
            outgoing.TryAdd(payload is null ? verb : $"{verb} {payload}");
        }
        catch (InvalidOperationException) { } // closed meanwhile
    }

    /// <summary>Splits a line into its verb and its payload ("" when none).</summary>
    public static (string Verb, string Payload) Parse(string line)
    {
        int space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }

    private void Read()
    {
        try
        {
            using var reader = new StreamReader(pipe, Utf8, false, 1 << 16, leaveOpen: true);
            while (reader.ReadLine() is { } line) received(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        Close();
    }

    private void Write()
    {
        try
        {
            foreach (string line in outgoing.GetConsumingEnumerable())
            {
                pipe.Write(Utf8.GetBytes(line + "\n"));
                pipe.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        Close();
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref isClosed, 1) != 0) return;
        outgoing.CompleteAdding();
        try { pipe.Dispose(); }
        catch (IOException) { }
        closed();
    }

    public void Dispose() => Close();
}
