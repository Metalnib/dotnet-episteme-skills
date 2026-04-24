using System.Net.Sockets;

namespace Synopsis.Mcp;

/// <summary>
/// Newline-delimited JSON-RPC framing over a <see cref="Socket"/>. Shared
/// by <see cref="UnixSocketTransport"/> and <see cref="TcpTransport"/> —
/// framing is identical once a byte stream is open. All protocol
/// invariants (UTF-8 no BOM, bounded line length) live in
/// <see cref="LineProtocol"/>.
/// </summary>
internal sealed class SocketConnection : IMcpConnection
{
    private readonly NetworkStream _stream;
    private readonly LineProtocol.LineReader _reader;

    public SocketConnection(Socket socket)
    {
        // ownsSocket:true — the NetworkStream's Dispose tears down the
        // socket. No explicit _socket.Dispose() below (single owner).
        _stream = new NetworkStream(socket, ownsSocket: true);
        _reader = new LineProtocol.LineReader(_stream);
    }

    public Task<string?> ReadLineAsync(CancellationToken ct) => _reader.ReadLineAsync(ct);

    public Task WriteLineAsync(string line, CancellationToken ct) =>
        LineProtocol.WriteLineAsync(_stream, line, ct);

    public ValueTask DisposeAsync()
    {
        try { _stream.Dispose(); } catch { /* already disposed */ }
        return ValueTask.CompletedTask;
    }
}
