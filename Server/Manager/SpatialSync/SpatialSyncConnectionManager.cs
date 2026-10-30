using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Server.Manager.SpatialSync
{
    /// <summary>
    /// Holds connection information including references to connections for each player based on player ID.
    /// </summary>
    public class SpatialSyncConnectionManager
    {
        private readonly ConcurrentDictionary<WebSocket, bool> _webSockets = new ConcurrentDictionary<WebSocket, bool>();
        // Map player ID -> IP address
        private readonly ConcurrentDictionary<int, string> _playerIPByID = new ConcurrentDictionary<int, string>();
        // Reverse map IP address -> player ID
        private readonly ConcurrentDictionary<string, int> _playerIDByIP = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<int, WebSocket> _webSocketsByPlayer = new ConcurrentDictionary<int, WebSocket>();

        /// <summary>
        /// Add a web socket to the spatial sync connection list, checking that it is already in the web sockets list.
        /// </summary>
        /// <param name="playerID">The player ID.</param>
        /// <param name="webSocket">The web socket to add.</param>
        /// <returns>True if added successfully, false otherwise.</returns>
        public bool AddSpatialSyncSocket(int playerID, WebSocket webSocket)
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
        /// Removes a web socket from the spatial synchronization connection list.
        /// </summary>
        /// <param name="playerID">The player ID of the spatial synchronization socket to remove.</param>
        /// <returns>True if removal was successful, false otherwise.</returns>
        public bool RemoveSessionSocket(int playerID)
        {
            return _webSocketsByPlayer.TryRemove(playerID, out _);
        }

        /// <summary>
        /// Get a spatial synchronization socket by player ID.
        /// </summary>
        /// <param name="playerID">The player ID.</param>
        /// <returns>A web socket, or null if not found.</returns>
        public WebSocket? GetSessionSocketByPlayerID(int playerID)
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
            if (_playerIDByIP.TryGetValue(ipAddress, out var existingID))
            {
                return existingID;
            }

            int id = 1;
            while (true)
            {
                // Try to reserve the id -> ip mapping
                if (_playerIPByID.TryAdd(id, ipAddress))
                {
                    // Now try to add the reverse mapping. If another thread added the
                    // same IP concurrently, clean up and return the existing id.
                    if (_playerIDByIP.TryAdd(ipAddress, id))
                    {
                        Console.WriteLine($"Registered new player {id} for IP {ipAddress}.");
                        return id;
                    }

                    // Reverse add failed: remove the forward mapping we just added
                    _playerIPByID.TryRemove(id, out _);

                    // Return the id that was added by the concurrent caller
                    if (_playerIDByIP.TryGetValue(ipAddress, out var concurrentID))
                    {
                        return concurrentID;
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
            if (_playerIPByID.TryRemove(playerID, out var ip))
            {
                _playerIDByIP.TryRemove(ip, out _);
                return true;
            }

            return false;
        }
    }
}