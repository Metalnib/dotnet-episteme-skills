using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Synopsis.Mcp;

/// <summary>
/// MCP transport over a TCP listener. Used when the single-image
/// deployment is split and Aegis reaches Synopsis over the network, or in
/// automated tests that prefer not to manage socket paths.
/// </summary>
/// <remarks>
/// MCP over TCP has <b>no authentication</b>. Binding off-loopback
/// exposes the graph (endpoint routes, SQL table names, configuration
/// keys, package list) to anyone who can reach the port. The constructor
/// logs a loud stderr warning in that case.
/// </remarks>
internal sealed class TcpTransport : IMcpTransport
{
    private readonly Socket _listener;
    private readonly IPEndPoint _boundEndpoint;

    public TcpTransport(IPEndPoint endpoint)
    {
        _listener = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(endpoint);
        _listener.Listen(backlog: 32);
        _boundEndpoint = (IPEndPoint)_listener.LocalEndPoint!;

        // IsLoopback returns true for 127.0.0.0/8 and ::1 — the safe case.
        // Anything else (0.0.0.0, public IP, LAN IP, …) exposes the daemon.
        if (!IPAddress.IsLoopback(_boundEndpoint.Address))
        {
            Console.Error.WriteLine(
                $"[mcp] WARNING: listening on {_boundEndpoint} (non-loopback). MCP has no auth — " +
                "any host that can reach this port can call tools against the graph.");
        }
    }

    /// <summary>
    /// Parse <c>host:port</c>, <c>:port</c>, bare <c>port</c>, or IPv6
    /// bracketed <c>[::1]:port</c>. Delegates to
    /// <see cref="IPEndPoint.TryParse(string, out IPEndPoint?)"/> for the
    /// composite forms so IPv6 edge cases land in the framework parser
    /// rather than hand-rolled string indexing.
    /// </summary>
    public static TcpTransport Create(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
            throw new ArgumentException("TCP address is required (host:port, :port, or port).", nameof(addr));

        // Bare port.
        if (int.TryParse(addr, out var barePort))
            return new TcpTransport(new IPEndPoint(IPAddress.Loopback, barePort));

        // :port → loopback
        if (addr.StartsWith(':') && int.TryParse(addr.AsSpan(1), out var colonPort))
            return new TcpTransport(new IPEndPoint(IPAddress.Loopback, colonPort));

        // Anything else — let IPEndPoint.TryParse handle IPv4 / bracketed IPv6 / etc.
        if (IPEndPoint.TryParse(addr, out var endpoint))
            return new TcpTransport(endpoint);

        throw new ArgumentException(
            $"Invalid TCP address '{addr}'. Expected host:port, :port, or bare port.", nameof(addr));
    }

    /// <summary>Actual bound endpoint (useful when port 0 was requested).</summary>
    public IPEndPoint BoundEndpoint => _boundEndpoint;

    public string Name => $"tcp ({_boundEndpoint})";

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
        return ValueTask.CompletedTask;
    }
}
