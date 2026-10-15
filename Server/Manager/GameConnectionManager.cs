using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Server.Manager
{
    /// <summary>
    /// Holds connection information including references to connections for each player based on player ID.
    /// </summary>
    public class GameConnectionManager
    {
        private readonly ConcurrentDictionary<WebSocket, bool> _webSockets = new ConcurrentDictionary<WebSocket, bool>();
        private readonly ConcurrentDictionary<int, WebSocket> _webSocketsByPlayer = new ConcurrentDictionary<int, WebSocket>();

        /// <summary>
        /// Add a web socket to the game connection list, checking that it is already in the web sockets list.
        /// </summary>
        /// <param name="playerID">The player ID.</param>
        /// <param name="webSocket">The web socket to add.</param>
        /// <returns>True if added successfully, false otherwise.</returns>
        public bool AddGameSocket(int playerID, WebSocket webSocket)
        {
            if (!_webSockets.TryGetValue(webSocket, out _))
            {
                return false;
            }

            return _webSocketsByPlayer.TryAdd(playerID, webSocket);
        }

        /// <summary>
        /// Add a web socket to the connection list only.
        /// </summary>
        /// <param name="webSocket"></param>
        /// <returns>True if added successfully, false otherwise.</returns>
        public bool AddSocket(WebSocket webSocket)
        {
            return _webSockets.TryAdd(webSocket, true);
        }

        /// <summary>
        /// Removes a web socket from the connection list only.
        /// </summary>
        /// <param name="webSocket">The web socket to remove.</param>
        /// <returns>True if removal was successful, false otherwise.</returns>
        public bool RemoveSocket(WebSocket webSocket)
        {
            return _webSockets.TryRemove(webSocket, out _);
        }

        /// <summary>
        /// Removes a web socket from the game connection list.
        /// </summary>
        /// <param name="playerID">The player ID of the game socket to remove.</param>
        /// <returns>True if removal was successful, false otherwise.</returns>
        public bool RemoveGameSocket(int playerID)
        {
            return _webSocketsByPlayer.TryRemove(playerID, out _);
        }

        /// <summary>
        /// Get a game socket by player ID.
        /// </summary>
        /// <param name="playerID">The player ID.</param>
        /// <returns>A web socket, or null if not found.</returns>
        public WebSocket? GetGameSocketByPlayerID(int playerID)
        {
            return _webSocketsByPlayer.GetValueOrDefault(playerID);
        }

        /// <summary>
        /// Returns all of the sockets in the connection list.
        /// </summary>
        /// <returns>A list of web sockets.</returns>
        public IEnumerable<WebSocket> GetAllSockets()
        {
            return _webSockets.Keys;
        }
    }
}