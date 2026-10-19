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
        // Map player ID -> IP address
        private readonly ConcurrentDictionary<int, string> _playerIPById = new ConcurrentDictionary<int, string>();
        // Reverse map IP address -> player ID
        private readonly ConcurrentDictionary<string, int> _playerIdByIP = new ConcurrentDictionary<string, int>();
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
        /// <summary>
        /// Adds a new player for the provided IP address and returns a unique player ID.
        /// If the IP address is already registered, the existing player ID is returned.
        /// </summary>
        /// <param name="ipAddress">The remote IP address of the player.</param>
        /// <returns>The allocated (or existing) unique player ID.</returns>
        public int AddPlayer(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                Console.WriteLine("Refused to add player: remote IP was empty or null.");
                return -1;
            }

            // If this IP is already registered, return existing ID
            if (_playerIdByIP.TryGetValue(ipAddress, out var existingId))
            {
                return existingId;
            }

            int id = 1;
            while (true)
            {
                // Try to reserve the id -> ip mapping
                if (_playerIPById.TryAdd(id, ipAddress))
                {
                    // Now try to add the reverse mapping. If another thread added the
                    // same IP concurrently, clean up and return the existing id.
                    if (_playerIdByIP.TryAdd(ipAddress, id))
                    {
                        Console.WriteLine($"Registered new player {id} for IP {ipAddress}.");
                        return id;
                    }

                    // Reverse add failed: remove the forward mapping we just added
                    _playerIPById.TryRemove(id, out _);

                    // Return the id that was added by the concurrent caller
                    if (_playerIdByIP.TryGetValue(ipAddress, out var concurrentId))
                    {
                        return concurrentId;
                    }

                    // If for some reason the reverse map still doesn't contain the IP,
                    // continue and try next id.
                }

                id++;
            }
        }

        /// <summary>
        /// Removes a player and its associated IP mappings.
        /// </summary>
        /// <param name="playerID">The player ID to remove.</param>
        /// <returns>True if removal was successful, false otherwise.</returns>
        public bool RemovePlayer(int playerID)
        {
            if (_playerIPById.TryRemove(playerID, out var ip))
            {
                _playerIdByIP.TryRemove(ip, out _);
                return true;
            }

            return false;
        }
    }
}