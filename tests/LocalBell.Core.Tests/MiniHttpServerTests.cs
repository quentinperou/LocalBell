using System.Net;
using System.Text;
using LocalBell.Core.Network;

namespace LocalBell.Core.Tests;

public class MiniHttpServerTests
{
    /// <summary>A stream that gives one byte per read, as a slow network may.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }

    [Fact]
    public async Task ReadRequest_ReadsTheLineAndTheBody_EvenOneByteAtATime()
    {
        byte[] raw = Encoding.UTF8.GetBytes("POST /api/localbell/v1/ring?x=1 HTTP/1.1\r\nHost: a\r\ncontent-length: 7\r\n\r\nSonnez!");
        var request = await MiniHttpServer.ReadRequestAsync(new TrickleStream(raw), IPAddress.Loopback, CancellationToken.None);
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/localbell/v1/ring", request.Path);
        Assert.Equal("Sonnez!", Encoding.UTF8.GetString(request.Body));
    }

    [Theory]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: 999999\r\n\r\n")]
    [InlineData("POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n")]
    public async Task ReadRequest_RefusesWhatIsNotARequestOrIsTooLarge(string raw) =>
        Assert.Null(await MiniHttpServer.ReadRequestAsync(new MemoryStream(Encoding.ASCII.GetBytes(raw)), IPAddress.Loopback, CancellationToken.None));

    [Fact]
    public async Task Server_AnswersHttpClient()
    {
        await using var server = new MiniHttpServer(IPAddress.Loopback, 0, (request, _) => Task.FromResult(request.Path == "/echo"
            ? HttpResponseData.Ok(request.Body)
            : HttpResponseData.NotFound));
        server.Start();
        using var client = new HttpClient();

        var echo = await client.PostAsync($"http://127.0.0.1:{server.Port}/echo", new StringContent("{\"a\":\"é\"}"));
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal("{\"a\":\"é\"}", await echo.Content.ReadAsStringAsync());

        var missing = await client.GetAsync($"http://127.0.0.1:{server.Port}/other");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Server_TakesTheNextPort_WhenThePreferredOneIsUsed()
    {
        await using var first = new MiniHttpServer(IPAddress.Loopback, 0, (_, _) => Task.FromResult(HttpResponseData.NotFound));
        first.Start();
        await using var second = new MiniHttpServer(IPAddress.Loopback, first.Port, (_, _) => Task.FromResult(HttpResponseData.NotFound));
        second.Start();
        Assert.NotEqual(first.Port, second.Port);
    }
}
