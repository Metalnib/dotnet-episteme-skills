using System.Net.Sockets;

namespace Synopsis.Mcp;

/// <summary>
/// Distinguishes fatal accept errors (listener is gone for good) from
/// transient ones (a client RST'd before accept, a signal interrupted the
/// syscall, we hit an fd ceiling). Blind <c>catch (SocketException)
/// { break; }</c> kills the whole daemon on any of these — a classic "up
/// but unreachable" failure mode — so every transport routes its
/// <c>AcceptAsync</c> catches through here.
/// </summary>
internal static class AcceptErrorClassifier
{
    public static bool IsFatal(SocketError error) => error switch
    {
        // Listener has been shut down / closed / invalidated — stop the loop.
        SocketError.OperationAborted => true,
        SocketError.Shutdown => true,
        SocketError.NotSocket => true,
        SocketError.InvalidArgument => true,

        // Everything else (ConnectionReset, ConnectionAborted, Interrupted,
        // TooManyOpenFiles, NetworkReset, HostUnreachable, etc.) is transient
        // from the listener's point of view. Log and continue.
        _ => false,
    };
}
