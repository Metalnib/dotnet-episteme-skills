using System.Runtime.CompilerServices;

namespace Synopsis.Mcp;

/// <summary>
/// One-shot transport bound to the process's stdin/stdout. Yields a single
/// <see cref="IMcpConnection"/> then completes. Used by
/// <c>synopsis mcp --graph foo.json</c> without a socket flag — the default
/// for direct CLI / Claude Code invocations.
/// </summary>
internal sealed class StdioTransport : IMcpTransport
{
    public string Name => "stdio";

    public async IAsyncEnumerable<IMcpConnection> AcceptAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new StdioConnection();
        // Async no-op to satisfy CS1998 — the iterator completes after the
        // single yield and the server awaits its in-flight task.
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class StdioConnection : IMcpConnection
    {
        private readonly Stream _in = Console.OpenStandardInput();
        private readonly Stream _out = Console.OpenStandardOutput();
        private readonly LineProtocol.LineReader _reader;

        public StdioConnection() => _reader = new LineProtocol.LineReader(_in);

        public Task<string?> ReadLineAsync(CancellationToken ct) => _reader.ReadLineAsync(ct);

        public Task WriteLineAsync(string line, CancellationToken ct) =>
            LineProtocol.WriteLineAsync(_out, line, ct);

        public ValueTask DisposeAsync()
        {
            try { _in.Dispose(); } catch { /* already disposed */ }
            try { _out.Dispose(); } catch { /* already disposed */ }
            return ValueTask.CompletedTask;
        }
    }
}
