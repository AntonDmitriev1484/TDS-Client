using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace Server.Handler
{
    /// <summary>
    /// A web socket allows only one send in flight at a time (one send and one receive may overlap). Anything that writes to a
    /// socket (messages and the closing handshake) takes this per-socket lock first so writes from different threads take turns.
    /// </summary>
    internal static class WebSocketSendLock
    {
        private static readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> Locks = new();

        public static SemaphoreSlim For(WebSocket webSocket) => Locks.GetValue(webSocket, _ => new SemaphoreSlim(1, 1));
    }
}
