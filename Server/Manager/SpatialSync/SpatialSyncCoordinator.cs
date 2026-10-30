using System.Collections.Concurrent;

namespace Server.Manager.SpatialSync
{
    /// <summary>
    /// A coordinator for all spatial synchronization sessions going on. Creates spatial synchronization sessions
    /// and holds session instances in a dictionary for easy access. In our special case, we only have one session instance
    /// and Session Synchronization Coordinator will always add new players to that session instance. 
    /// However, this class is designed to support multiple session instances if needed in the future.
    /// </summary>
    public class SpatialSyncCoordinator
    {

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ServerSettings _serverSettings;

        /// <summary>
        /// A dictionary of the ongoing spatial synchronization manager instances by session IDs.
        /// </summary>
        private ConcurrentDictionary<int, ServerSpatialSyncManager> _sessionInstances = new ConcurrentDictionary<int, ServerSpatialSyncManager>();
        /// <summary>
        /// A dictionary of the ongoing spatial synchronization session for a player by player ID.
        /// </summary>
        private ConcurrentDictionary<int, int> _sessionIDByPlayerID = new ConcurrentDictionary<int, int>();


        public SpatialSyncCoordinator(IServiceScopeFactory scopeFactory, ServerSettings serverSettings)
        {
            _scopeFactory = scopeFactory;
            _serverSettings = serverSettings;
        }

        /// <summary>
        /// Generates a new, unique session ID.
        /// </summary>
        /// <returns>The unique session ID.</returns>
        private int GenerateUniqueSessionID()
        {
            int newID;
            do
            {
                newID = new Random().Next(1, int.MaxValue);
            } while (_sessionInstances.ContainsKey(newID));

            return newID;
        }

        /// <summary>
        /// Adds a spatial synchronization session manager to this coordinator. Generates a new, unique number session ID for the spatial synchronization session.
        /// </summary>
        /// <param name="spatialSyncManager">The manager of the spatial synchronization session to add.</param>
        /// <returns></returns>
        public int AddSession(ServerSpatialSyncManager spatialSyncManager)
        {
            int newSessionID = GenerateUniqueSessionID();

            // Add to the session instances by session ID.
            while (!_sessionInstances.TryAdd(newSessionID, spatialSyncManager))
            {
                newSessionID = GenerateUniqueSessionID();
            }

            // Add to the session instances by player IDs.
            foreach (int playerID in spatialSyncManager.PlayerIDs.ToList())
            {
                if (!_sessionIDByPlayerID.TryAdd(playerID, newSessionID))
                {
                    Console.WriteLine("The player already has a spatial synchronization session instance associated with it!");
                }
            }

            spatialSyncManager.ID = newSessionID;

            return newSessionID;
        }

        /// <summary>
        /// Removes a spatial synchronization session manager from this coordinator. Should be called when a session finishes.
        /// </summary>
        /// <param name="sessionID">The ID of the session to remove.</param>
        /// <returns>True if the session is successfully found and removed. False if otherwise.</returns>
        public bool RemoveSession(int sessionID)
        {
            ServerSpatialSyncManager spatialSyncManager;

            bool successfulRemoval = _sessionInstances.TryRemove(sessionID, out spatialSyncManager);

            if (successfulRemoval && spatialSyncManager != null)
            {
                foreach (int playerID in spatialSyncManager.PlayerIDs.ToList())
                {
                    _sessionIDByPlayerID.TryRemove(playerID, out _);
                }
            }
            else
            {
                Console.Error.WriteLine("Failed to find the specified spatial synchronization session to remove!");
            }

            return successfulRemoval;
        }


        /// <summary>
        /// Determines whether a spatial synchronization session with the specified ID exists.
        /// </summary>
        /// <remarks>Performs a lookup in the internal session instances dictionary.</remarks>
        /// <param name="sessionID">The session identifier to check for existence.</param>
        /// <returns>true if a session with the specified ID exists; otherwise, false.</returns>
        public bool SessionExists(int sessionID)
        {
            return _sessionInstances.ContainsKey(sessionID);
        }

        /// <summary>
        /// Grab a spatial synchronization session from the list of ongoing sessions.
        /// </summary>
        /// <param name="sessionID">The ID of the session to get.</param>
        /// <returns>The session requested, or null if it could not be found.</returns>
        public ServerSpatialSyncManager? GetSession(int sessionID)
        {
            ServerSpatialSyncManager? spatialSyncManager = null;

            _sessionInstances.TryGetValue(sessionID, out spatialSyncManager);

            return spatialSyncManager;
        }

        /// <summary>
        /// Grab a spatial synchronization session from the dictionary of sessions by player ID.
        /// </summary>
        /// <param name="playerID">The ID of the player to get the session for.</param>
        /// <returns>The session requested, or null if it could not be found.</returns>
        public ServerSpatialSyncManager? GetSessionByPlayerID(int playerID)
        {
            int sessionID;

            _sessionIDByPlayerID.TryGetValue(playerID, out sessionID);

            ServerSpatialSyncManager? sessionStateManager = null;

            _sessionInstances.TryGetValue(sessionID, out sessionStateManager);

            return sessionStateManager;
        }

        /// <summary>
        /// Generate a spatial synchronization session for a set of players.
        /// </summary>
        /// <param name="playerIDs">The list of player IDs for the players to generate the session for.</param>
        /// <returns>The manager for the generated session instance.</returns>
        public async Task GenerateSpatialSyncSession(List<int> playerIDs)
        {
            try
            {
                ServerSpatialSyncManager spatialSyncManager = new ServerSpatialSyncManager(_scopeFactory, _serverSettings, playerIDs);
                this.AddSession(spatialSyncManager);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in generating session: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds the player with the specified playerID to the spatial synchronization session identified by sessionID asynchronously.
        /// </summary>
        /// <remarks>If the session is not found, a message is written to the console and no player is added.
        /// The actual add operation is performed by the session's ServerSpatialSyncManager.</remarks>
        /// <param name="playerID">Identifier of the player to add.</param>
        /// <param name="sessionID">Identifier of the target session.</param>
        /// <returns>A task that completes when the player has been added to the session.</returns>
        public async Task AddPlayerToSession(int playerID, int sessionID)
        {
            ServerSpatialSyncManager? spatialSyncManager = GetSession(sessionID);
            if (spatialSyncManager == null)
            {
                Console.WriteLine($"Spatial synchronization session with ID: {sessionID} not found.");
                return;
            }

            _sessionIDByPlayerID.TryAdd(playerID, sessionID);

            spatialSyncManager.AddPlayer(playerID);

            Console.WriteLine($"Player with ID: {playerID} added to spatial synchronization session with ID: {sessionID}.");
        }

        /// <summary>
        /// Gets the ID of the first spatial synchronization session instance, or null if no instances are present.
        /// </summary>
        /// <remarks>Writes a message to the console when no instances are found. If multiple instances
        /// exist, the returned ID corresponds to the first element in the collection's enumeration order.</remarks>
        /// <returns>The ID of the first session instance, or null when no instances exist.</returns>
        public int? GetSessionIDOfFirstInstance()
        {
            if (_sessionInstances.IsEmpty)
            {
                Console.WriteLine("No spatial synchronization session instances found.");
                return null;
            }
            return _sessionInstances.Keys.First();
        }
    }
}