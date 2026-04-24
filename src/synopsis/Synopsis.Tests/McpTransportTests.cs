using System.Net;
using System.Net.Sockets;
using System.Text;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Mcp;

namespace Synopsis.Tests;

public sealed class McpTransportTests
{
    // --- TcpTransport.Create parser ---

    [Fact]
    public async Task TcpTransport_Create_BarePort_BindsToLoopback()
    {
        await using var t = TcpTransport.Create("0");   // OS-assigned port
        Assert.Equal(IPAddress.Loopback, t.BoundEndpoint.Address);
        Assert.True(t.BoundEndpoint.Port > 0);
    }

    [Fact]
    public async Task TcpTransport_Create_ColonPort_BindsToLoopback()
    {
        await using var t = TcpTransport.Create(":0");
        Assert.Equal(IPAddress.Loopback, t.BoundEndpoint.Address);
    }

    [Fact]
    public async Task TcpTransport_Create_HostColonPort_BindsExplicitHost()
    {
        await using var t = TcpTransport.Create("127.0.0.1:0");
        Assert.Equal(IPAddress.Loopback, t.BoundEndpoint.Address);
    }

    [Fact]
    public void TcpTransport_Create_InvalidAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() => TcpTransport.Create("not-a-port"));
        Assert.Throws<ArgumentException>(() => TcpTransport.Create("badhost:not-a-port"));
    }

    [Fact]
    public async Task TcpTransport_Create_BracketedIPv6_Parses()
    {
        // LastIndexOf(':') used to choke on bracketed IPv6. The parser now
        // defers composite forms to IPEndPoint.TryParse.
        await using var t = TcpTransport.Create("[::1]:0");
        Assert.Equal(IPAddress.IPv6Loopback, t.BoundEndpoint.Address);
        Assert.True(t.BoundEndpoint.Port > 0);
    }

    // --- UnixSocketTransport stale cleanup ---

    [Fact]
    public async Task UnixSocketTransport_StaleFileAtPath_IsReplacedByNewSocket()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var path = Path.Combine(Path.GetTempPath(), $"syn-mcp-{Guid.NewGuid():N}.sock");
        File.WriteAllText(path, "stale socket file from crashed daemon");
        try
        {
            await using var t = new UnixSocketTransport(path);
            // Listening socket should now occupy this path; the stale file is gone.
            Assert.True(File.Exists(path), "socket file should exist after bind");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UnixSocketTransport_SocketFileMode_IsOwnerOnly()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"syn-mcp-mode-{Guid.NewGuid():N}.sock");
        try
        {
            await using var t = new UnixSocketTransport(path);
            var mode = File.GetUnixFileMode(path);

            // 0600 — no group, no other access.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UnixSocketTransport_LivePeerAtPath_ThrowsInsteadOfStealingOwnership()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var path = Path.Combine(Path.GetTempPath(), $"syn-mcp-peer-{Guid.NewGuid():N}.sock");
        try
        {
            await using var first = new UnixSocketTransport(path);
            // Second instance at the same path must fail rather than delete
            // the live daemon's socket and silently take over.
            Assert.Throws<IOException>(() => new UnixSocketTransport(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task McpServer_UnixSocket_TwoConcurrentClients_BothGetResponses()
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;

        var path = Path.Combine(Path.GetTempPath(), $"syn-mcp-cc-{Guid.NewGuid():N}.sock");
        try
        {
            await using var transport = new UnixSocketTransport(path);
            var graph = EmptyGraph();
            var server = new McpServer(graph);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverTask = Task.Run(() => server.RunAsync(transport, cts.Token));

            var client1 = UnixPingAsync(path, id: 1, cts.Token);
            var client2 = UnixPingAsync(path, id: 2, cts.Token);

            var responses = await Task.WhenAll(client1, client2);
            Assert.Contains("\"id\":1", responses[0]);
            Assert.Contains("\"id\":2", responses[1]);

            cts.Cancel();
            await serverTask;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- McpServer end-to-end over TCP ---

    [Fact]
    public async Task McpServer_TwoConcurrentTcpClients_BothGetResponses()
    {
        // Regression: pre-M1 the server was single-stream (stdin). After M1
        // the transport accept loop must spawn a task per connection so a
        // second client gets served in parallel with the first.
        await using var transport = TcpTransport.Create(":0");
        var graph = EmptyGraph();
        var server = new McpServer(graph);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(() => server.RunAsync(transport, cts.Token));

        var endpoint = transport.BoundEndpoint;
        // Run both clients on their own tasks and wait for both to complete.
        var client1 = PingAsync(endpoint, id: 1, cts.Token);
        var client2 = PingAsync(endpoint, id: 2, cts.Token);

        var responses = await Task.WhenAll(client1, client2);
        Assert.Contains("\"id\":1", responses[0]);
        Assert.Contains("\"status\":\"ok\"", responses[0]);
        Assert.Contains("\"id\":2", responses[1]);
        Assert.Contains("\"status\":\"ok\"", responses[1]);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task McpServer_ClientDisconnectMidRequest_ServerKeepsServing()
    {
        await using var transport = TcpTransport.Create(":0");
        var graph = EmptyGraph();
        var server = new McpServer(graph);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(() => server.RunAsync(transport, cts.Token));

        var endpoint = transport.BoundEndpoint;

        // First client opens then rudely closes without sending a full request.
        using (var rude = new TcpClient())
        {
            await rude.ConnectAsync(endpoint, cts.Token);
            var rudeStream = rude.GetStream();
            await rudeStream.WriteAsync("{\"jsonrpc\":\"2.0\""u8.ToArray(), cts.Token);  // partial
            await rudeStream.FlushAsync(cts.Token);
            // Close without newline terminator — server should not crash.
        }

        // Second client must still be served.
        var response = await PingAsync(endpoint, id: 42, cts.Token);
        Assert.Contains("\"id\":42", response);
        Assert.Contains("\"status\":\"ok\"", response);

        cts.Cancel();
        await serverTask;
    }

    // --- Review-driven regressions ---

    [Fact]
    public async Task McpServer_ClientRstBeforeAccept_ListenerStaysAlive()
    {
        // Regression for #1: a blind `catch (SocketException) { yield break; }`
        // in the accept loop would kill the listener on transient errors
        // (ECONNABORTED, EINTR, EMFILE). Now the classifier distinguishes
        // fatal from transient. Induce churn by opening and immediately
        // closing many TCP connections, then prove a real client still gets
        // served afterwards.
        await using var transport = TcpTransport.Create(":0");
        var graph = EmptyGraph();
        var server = new McpServer(graph);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = Task.Run(() => server.RunAsync(transport, cts.Token));

        var endpoint = transport.BoundEndpoint;
        for (var i = 0; i < 20; i++)
        {
            using var churn = new TcpClient();
            await churn.ConnectAsync(endpoint, cts.Token);
            churn.Client.LingerState = new LingerOption(enable: true, seconds: 0); // RST on close
            churn.Close();
        }

        var response = await PingAsync(endpoint, id: 99, cts.Token);
        Assert.Contains("\"id\":99", response);
        Assert.Contains("\"status\":\"ok\"", response);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task McpServer_OversizeLine_DisconnectsOffendingClient_LeavesServerUp()
    {
        // Regression for #5: ReadLineAsync had no cap. A 2 MiB line without
        // a newline would have buffered the whole thing. Now LineProtocol
        // throws IOException past 1 MiB; the server logs and drops the
        // connection, other clients are unaffected.
        await using var transport = TcpTransport.Create(":0");
        var graph = EmptyGraph();
        var server = new McpServer(graph);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = Task.Run(() => server.RunAsync(transport, cts.Token));

        var endpoint = transport.BoundEndpoint;
        var payload = new byte[LineProtocol.MaxLineBytes + 16]; // just over the cap
        Array.Fill(payload, (byte)'x');

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(endpoint, cts.Token);
            var stream = client.GetStream();
            // Write in chunks so the server observes growth before failing.
            for (var offset = 0; offset < payload.Length; offset += 64 * 1024)
            {
                var n = Math.Min(64 * 1024, payload.Length - offset);
                await stream.WriteAsync(payload.AsMemory(offset, n), cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        }

        // Server stays up for the next client.
        var response = await PingAsync(endpoint, id: 123, cts.Token);
        Assert.Contains("\"id\":123", response);

        cts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task LineProtocol_WriteReadRoundTripsUtf8WithoutBom()
    {
        // Regression for #4: defaults used to detect/emit BOM, which breaks
        // strict JSON-RPC parsers. Round-trip a payload and assert no BOM
        // was written.
        using var ms = new MemoryStream();
        await LineProtocol.WriteLineAsync(ms, "{\"hello\":\"wörld\"}", CancellationToken.None);

        var bytes = ms.ToArray();
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "WriteLine must not emit a BOM");
        Assert.Equal((byte)'\n', bytes[^1]);

        ms.Position = 0;
        var reader = new LineProtocol.LineReader(ms);
        var line = await reader.ReadLineAsync(CancellationToken.None);
        Assert.Equal("{\"hello\":\"wörld\"}", line);
    }

    [Fact]
    public async Task LineReader_ChunkedStream_ReadsMultipleLinesWithResidual()
    {
        // Chunked reader (4 KiB reads) must correctly split multiple lines
        // that arrive in a single buffer and preserve residual bytes across
        // calls.
        using var ms = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n");
        await ms.WriteAsync(payload);
        ms.Position = 0;

        var reader = new LineProtocol.LineReader(ms);
        Assert.Equal("alpha", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("beta",  await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("gamma", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));  // EOF
    }

    // --- AcceptErrorClassifier unit table ---

    [Theory]
    [InlineData(SocketError.OperationAborted,  true)]    // listener disposed
    [InlineData(SocketError.Shutdown,          true)]    // listener Shutdown() called
    [InlineData(SocketError.NotSocket,         true)]    // fd no longer valid
    [InlineData(SocketError.InvalidArgument,   true)]    // programming error
    [InlineData(SocketError.ConnectionReset,   false)]   // client RST
    [InlineData(SocketError.ConnectionAborted, false)]   // client vanished pre-accept
    [InlineData(SocketError.Interrupted,       false)]   // EINTR
    [InlineData(SocketError.TooManyOpenSockets, false)]  // EMFILE/ENFILE — transient fd pressure
    [InlineData(SocketError.NetworkReset,      false)]
    [InlineData(SocketError.HostUnreachable,   false)]
    public void AcceptErrorClassifier_Classification_IsDeterministic(SocketError error, bool expectedFatal)
    {
        // Locks in the classifier's decisions independently of real sockets.
        // Inducing the transient branch from accept() is OS-timing dependent
        // (see note in McpServer_ClientRstBeforeAccept_ListenerStaysAlive).
        Assert.Equal(expectedFatal, AcceptErrorClassifier.IsFatal(error));
    }

    // --- Helpers ---

    private static ScanResult EmptyGraph()
    {
        var builder = new GraphBuilder();
        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }

    private static async Task<string> PingAsync(IPEndPoint endpoint, int id, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint, ct);
        return await PingOverStream(client.GetStream(), id, ct);
    }

    private static async Task<string> UnixPingAsync(string socketPath, int id, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        using var stream = new NetworkStream(socket, ownsSocket: true);
        return await PingOverStream(stream, id, ct);
    }

    private static async Task<string> PingOverStream(Stream stream, int id, CancellationToken ct)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var writer = new StreamWriter(stream, encoding, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var request = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"ping\"}}";
        await writer.WriteLineAsync(request.AsMemory(), ct);
        return await reader.ReadLineAsync(ct) ?? string.Empty;
    }
}
