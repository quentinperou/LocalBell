using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalBell.Core.Network;

/// <summary>An HTTP request as <see cref="MiniHttpServer"/> hands it over.</summary>
public sealed record HttpRequestData(string Method, string Path, byte[] Body, IPAddress Remote);

/// <summary>An answer: its status code and its JSON (none for an empty answer).</summary>
public sealed record HttpResponseData(int Status, byte[]? Json = null)
{
    public static HttpResponseData Ok(byte[] json) => new(200, json);

    public static readonly HttpResponseData NotFound = new(404);
    public static readonly HttpResponseData BadRequest = new(400);
}

/// <summary>
/// The little HTTP server of each PC, on a plain TCP socket: the requests of LocalBell are a few hundred bytes of JSON,
/// one per connection. HttpListener would need a URL reservation made as administrator, and ASP.NET Core a runtime
/// that the Desktop one does not include.
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private readonly IPAddress bindAddress;
    private readonly int preferredPort;
    private readonly Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler;
    private readonly CancellationTokenSource stop = new();
    private TcpListener? listener;
    private Task? acceptLoop;

    public MiniHttpServer(IPAddress bindAddress, int preferredPort,
        Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
    {
        this.bindAddress = bindAddress;
        this.preferredPort = preferredPort;
        this.handler = handler;
    }

    /// <summary>The port it listens on, once started.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Listens on the preferred port, else one of the next nine, else any free port: a second LocalBell on the same PC
    /// (a test) or another program on the port must not stop the app. The others learn the port from its messages.
    /// </summary>
    public void Start()
    {
        IEnumerable<int> ports = preferredPort == 0 ? [0] : Enumerable.Range(preferredPort, 10).Append(0);
        foreach (int port in ports)
        {
            var candidate = new TcpListener(bindAddress, port) { ExclusiveAddressUse = true };
            try
            {
                candidate.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                continue;
            }
            listener = candidate;
            Port = ((IPEndPoint)candidate.LocalEndpoint).Port;
            acceptLoop = AcceptAsync(candidate, stop.Token);
            return;
        }
        throw new InvalidOperationException("No TCP port is free.");
    }

    private async Task AcceptAsync(TcpListener tcp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var stream = client.GetStream();
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            var request = await ReadRequestAsync(stream, remote, timeout.Token);
            HttpResponseData response;
            if (request is null) response = HttpResponseData.BadRequest;
            else
            {
                try { response = await handler(request, timeout.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { response = new HttpResponseData(500); }
            }
            await WriteResponseAsync(stream, response, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The caller went away or was too slow: nothing to answer.
        }
    }

    /// <summary>The request line, the headers (only Content-Length matters) and the body; null when it is not one.</summary>
    internal static async Task<HttpRequestData?> ReadRequestAsync(Stream stream, IPAddress remote, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0, headerEnd;
        while ((headerEnd = buffer.AsSpan(0, length).IndexOf(HeaderEnd)) < 0)
        {
            if (length == buffer.Length) return null;
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !int.TryParse(line[(colon + 1)..].Trim(), out contentLength)) return null;
        }
        if (contentLength is < 0 or > MaxBodyBytes) return null;

        var body = new byte[contentLength];
        int already = Math.Min(length - headerEnd - HeaderEnd.Length, contentLength);
        Array.Copy(buffer, headerEnd + HeaderEnd.Length, body, 0, already);
        await stream.ReadExactlyAsync(body.AsMemory(already), token);

        string path = requestLine[1];
        int query = path.IndexOf('?');
        return new HttpRequestData(requestLine[0], query < 0 ? path : path[..query], body, remote);
    }

    private static async Task WriteResponseAsync(Stream stream, HttpResponseData response, CancellationToken token)
    {
        byte[] body = response.Json ?? [];
        string reason = response.Status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Internal Server Error",
        };
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener?.Stop();
        if (acceptLoop is not null)
        {
            try { await acceptLoop; }
            catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }
}
