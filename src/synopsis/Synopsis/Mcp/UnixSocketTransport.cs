using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Synopsis.Mcp;

/// <summary>
/// MCP transport over a Unix domain socket. Primary transport for the
/// Aegis single-image deployment — both Aegis (Node) and Synopsis (.NET)
/// share the container's filesystem, so a socket file is cheaper and
/// more scoped than a TCP port.
/// </summary>
/// <remarks>
/// <para>
/// Socket file is mode <c>0600</c> (owner-only). Any local uid could
/// otherwise connect and call tools against the graph; the default process
/// umask (0022 → 0755) is unsafe on a shared host.
/// </para>
/// <para>
/// Startup behaviour when something already exists at the path:
/// <list type="bullet">
///   <item><description>If a live daemon is listening: <see cref="IOException"/> —
///     never silently steals ownership from a running peer.</description></item>
///   <item><description>If the probe-connect fails (no listener / stale file):
///     delete and bind. Assumes the path is a canonical Synopsis socket
///     location; pointing it at an arbitrary file is a misconfiguration.</description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class UnixSocketTransport : IMcpTransport
{
    private readonly Socket _listener;
    private readonly string _path;

    public UnixSocketTransport(string path)
    {
        _path = path;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            if (IsLiveUnixSocket(path))
                throw new IOException(
                    $"Socket path '{path}' is already in use by a live peer. Stop the other daemon or choose a different path.");
            File.Delete(path);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(backlog: 32);

        // Tighten permissions to owner-only (0600). The default process umask
        // typically produces 0755, which lets any local uid connect and call
        // tools.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[mcp] Warning: could not chmod socket to 0600: {ex.Message}");
            }
        }
    }

    public string Name => $"unix ({_path})";

    public async IAsyncEnumerable<IMcpConnection> AcceptAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(ct);
            }
            catch (OperationCanceledException) { yield break; }
            catch (ObjectDisposedException) { yield break; }
            catch (SocketException ex) when (AcceptErrorClassifier.IsFatal(ex.SocketErrorCode))
            {
                yield break;
            }
            catch (SocketException ex)
            {
                // Transient: client RST, signal interrupt, fd pressure. Log and keep listening.
                Console.Error.WriteLine($"[mcp] Transient accept error ({ex.SocketErrorCode}); continuing.");
                continue;
            }

            yield return new SocketConnection(client);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _listener.Dispose(); } catch { /* already disposed */ }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Try a bounded connect to the path. Success → a daemon is listening.
    /// Any failure (ECONNREFUSED for a stale file, permission denied,
    /// not-a-socket, or a 500 ms timeout if a peer is alive but stuck
    /// mid-accept with a full backlog) → treated as "safe to replace".
    /// </summary>
    private static bool IsLiveUnixSocket(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            probe.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token)
                .AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
