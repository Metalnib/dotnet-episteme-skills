using System.Buffers;
using System.Text;

namespace Synopsis.Mcp;

/// <summary>
/// Shared newline-delimited JSON-RPC framing used by every MCP transport
/// (stdio, Unix socket, TCP). Two things it locks down that default
/// <see cref="StreamReader"/> / <see cref="StreamWriter"/> would not:
/// <list type="bullet">
///   <item>
///     <description>
///       UTF-8 <b>no BOM</b>, BOM detection off. JSON-RPC specifies UTF-8
///       without a preamble; BOM autodetection on the read side or BOM
///       emission on the write side silently corrupts strict parsers.
///     </description>
///   </item>
///   <item>
///     <description>
///       A per-line byte cap. A misbehaving or malicious client that sends
///       megabytes without a newline would otherwise buffer the entire
///       payload before returning.
///     </description>
///   </item>
/// </list>
/// </summary>
internal static class LineProtocol
{
    /// <summary>
    /// Hard cap per JSON-RPC request line. 1 MiB is comfortably above any
    /// realistic MCP payload (largest today is a graph.json path + a few
    /// args); an oversized line triggers an <see cref="IOException"/> and
    /// the connection is torn down by the server's outer handler.
    /// </summary>
    public const int MaxLineBytes = 1 * 1024 * 1024;

    private const int InitialCapacity = 256;

    public static readonly Encoding Utf8NoBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Stateful line reader backed by a chunked input buffer. One instance
    /// per connection — keeps residual bytes between <see cref="ReadLineAsync"/>
    /// calls so we don't make an async state-machine trip per byte, and
    /// reuses its line-assembly buffer so a steady stream of requests
    /// allocates no scratch arrays after warmup.
    /// </summary>
    public sealed class LineReader
    {
        private const int ChunkSize = 4096;
        private readonly Stream _stream;
        private readonly byte[] _chunk = new byte[ChunkSize];
        private int _pos;   // next unread byte in _chunk
        private int _len;   // valid bytes in _chunk

        // Reused across calls; grows up to MaxLineBytes + 1 for the largest
        // line ever seen on this connection, never shrinks. Steady state:
        // zero allocations per request body beyond the returned string.
        private byte[] _line = new byte[InitialCapacity];

        public LineReader(Stream stream) => _stream = stream;

        /// <summary>
        /// Read one newline-delimited line. Returns <see langword="null"/>
        /// on clean EOF (peer closed). Throws <see cref="IOException"/> if a
        /// single line exceeds <see cref="MaxLineBytes"/> — the caller
        /// should drop the connection.
        /// </summary>
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var lineLen = 0;

            while (true)
            {
                // Drain whatever is already in the residual chunk.
                while (_pos < _len)
                {
                    var b = _chunk[_pos++];
                    if (b == (byte)'\n')
                        return Utf8NoBom.GetString(_line, 0, lineLen);
                    // Strips every CR, not only CR-before-LF. Raw \r in a JSON
                    // string is already spec-invalid, so the next-layer JSON
                    // parser would reject it regardless; safe in practice.
                    if (b == (byte)'\r')
                        continue;

                    if (lineLen >= MaxLineBytes)
                        throw new IOException(
                            $"MCP request line exceeds {MaxLineBytes} bytes; disconnecting client.");

                    if (lineLen >= _line.Length)
                        Array.Resize(ref _line, Math.Min(_line.Length * 2, MaxLineBytes + 1));

                    _line[lineLen++] = b;
                }

                // Refill.
                try
                {
                    _len = await _stream.ReadAsync(_chunk, ct);
                    _pos = 0;
                }
                catch (Exception ex) when (
                    ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
                {
                    return null;  // peer disconnected — treat as clean EOF
                }

                if (_len == 0)
                    return lineLen == 0 ? null : Utf8NoBom.GetString(_line, 0, lineLen);
            }
        }
    }

    /// <summary>
    /// Write <paramref name="line"/> followed by a single <c>\n</c> as UTF-8
    /// (no BOM). Swallows peer-gone errors — the caller sees no signal,
    /// which is fine at this layer; the next read on the same connection
    /// will return null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Encodes into an <see cref="ArrayPool{T}"/> buffer instead of
    /// <c>Encoding.GetBytes(string)</c> so a busy daemon doesn't allocate a
    /// fresh byte[] per response.
    /// </para>
    /// <para>
    /// Returns with <c>clearArray: true</c>. Response payloads contain SQL
    /// table names, endpoint routes, configuration keys, and file paths —
    /// leaving those in a pooled buffer for the next unrelated renter is a
    /// real information-flow hazard in a process handling multiple MCP
    /// clients. The zero-fill cost is negligible compared to the syscall.
    /// </para>
    /// </remarks>
    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var byteCount = Utf8NoBom.GetByteCount(line);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Utf8NoBom.GetBytes(line, buffer);
            await stream.WriteAsync(buffer.AsMemory(0, written), ct);
            await stream.WriteAsync(Newline, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex) when (
            ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Peer gone; nothing useful we can do here.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static readonly byte[] Newline = [(byte)'\n'];
}
