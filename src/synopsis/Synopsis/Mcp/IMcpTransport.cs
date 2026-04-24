namespace Synopsis.Mcp;

/// <summary>
/// Source of MCP client connections. Three implementations ship in P0:
/// <see cref="StdioTransport"/> for one-shot CLI use (yields a single
/// connection backed by stdin/stdout), <see cref="UnixSocketTransport"/>
/// for the single-image daemon case, and <see cref="TcpTransport"/> for
/// multi-container / remote-daemon setups.
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Short name used in startup diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Produce a stream of inbound connections. For stdio, yields exactly
    /// one connection and then completes. For socket transports, yields
    /// each accepted connection until <paramref name="ct"/> cancels or the
    /// listener is disposed.
    /// </summary>
    IAsyncEnumerable<IMcpConnection> AcceptAsync(CancellationToken ct);
}

/// <summary>
/// Newline-delimited JSON-RPC framing over a byte stream. Independent of
/// the specific transport (stdio, Unix socket, TCP).
/// </summary>
public interface IMcpConnection : IAsyncDisposable
{
    /// <summary>
    /// Read the next newline-delimited message. Returns <see langword="null"/>
    /// on clean disconnect (client closed the stream).
    /// </summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Write a newline-delimited JSON-RPC response.</summary>
    Task WriteLineAsync(string line, CancellationToken ct);
}
