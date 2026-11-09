using Server.Handler;
using Server.Manager.Game;
using Server.Manager.SpatialSync;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Server.Manager
{
    /// <summary>
    /// Handles adding users to a queue for the main game session.
    /// </summary>
    public class QueueManager
    {

        private readonly IServiceScopeFactory _scopeFactory;

        private readonly ConcurrentDictionary<int, bool> _queue;

        /// <summary>
        /// How long a matched group gets to form a global map (all of its players' key frames merged into one map) before the match
        /// is abandoned and the players are put back in the queue.
        /// </summary>
        public int GlobalMapTimeoutSeconds { get; set; } = 180;

        /// <summary>The minimum number of players required to start a game session.</summary>
        private int MinPlayersPerSession;

        // 1 while a matched group is waiting for its global map. There is no game yet during that time, so without this flag the queue
        // would treat players that queue meanwhile as the start of another match.
        private int _matchInProgress;


        public QueueManager(IServiceScopeFactory scopeFactory, ServerSettings serverSettings)
        {
            _scopeFactory = scopeFactory;
            MinPlayersPerSession = serverSettings.MinPlayersPerSession;

            _queue = new ConcurrentDictionary<int, bool>();

            ProcessQueue();
        }

        /// <summary>
        /// Enqueue a player for the given application type.
        /// </summary>
        /// <param name="playerID">The ID of the player to queue.</param>
        /// <returns>True if the player could be queued, false otherwise (if the player is already in the queue or in a game).</returns>
        public bool EnqueuePlayer(int playerID)
        {

            // Check to see if the player is already in the game.


            // Try to add it to this current queue.
            bool successfulAdd = _queue.TryAdd(playerID, true);

            if (!successfulAdd)
            {
                Console.WriteLine($"Player with ID: {playerID} is already in the queue!");
                return false;
            }

            Console.WriteLine($"Player with ID: {playerID} has been added to the queue.");

            return true;
        }

        /// <summary>
        /// Remove a player from any queues it may be in.
        /// </summary>
        /// <param name="playerID">The ID of the player.</param>
        public void DequeuePlayer(int playerID)
        {
            _queue.TryRemove(playerID, out _);
        }

        /// <summary>
        /// Process each queue and attempt to match players.
        /// This repeats infinitely, either restarting every 5 seconds or after all matchings have been made in the past interation.
        /// Current implementation only allows for a single game instance, so new players in queue are automatically connected to the same game instance.
        /// </summary>
        public async Task ProcessQueue()
        {
            List<Task> tasks = new List<Task>();

            tasks.Add(Task.Delay(5000));

            try
            {
                List<int> playersToMatch = _queue.Keys.ToList();

                Console.WriteLine("Players to match: " + string.Join(", ", playersToMatch));

                // While a matched group is still waiting for its global map there is no game yet, so players queued now would look like
                // the start of another match. They stay in the queue until that match is done, and then join its game.
                if (playersToMatch.Count > 0 && Volatile.Read(ref _matchInProgress) == 0)
                {
                    // If a game instance already exists, add all players to that game instance (even a single player can join a running game).
                    int? firstGameInstanceID;

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();
                        firstGameInstanceID = gameCoordinator.GetGameIDOfFirstInstance();
                        if (firstGameInstanceID != null)
                        {
                            // Dequeue each of the players being added.
                            foreach (var playerID in playersToMatch)
                            {
                                DequeuePlayer(playerID);
                            }

                            foreach (int playerID in playersToMatch)
                            {
                                Task addPlayerTask = gameCoordinator.AddPlayerToGame(playerID, firstGameInstanceID.Value);
                                tasks.Add(addPlayerTask);
                            }

                            // Also add them to the spatial synchronization session and tell them to begin, so they get aligned with the others.
                            var spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();
                            int? firstSessionInstanceID = spatialSyncCoordinator.GetSessionIDOfFirstInstance();
                            if (firstSessionInstanceID != null)
                            {
                                foreach (int playerID in playersToMatch)
                                {
                                    tasks.Add(JoinSpatialSyncSessionAsync(playerID, firstSessionInstanceID.Value));
                                }
                            }
                        }
                    }

                    // Otherwise, a new game needs at least two players: start their spatial synchronization and create the game once the
                    // global map has formed. This is not added to `tasks`, because waiting for the global map can take minutes and must not
                    // hold up the queue.
                    if (firstGameInstanceID == null && playersToMatch.Count >= MinPlayersPerSession && Interlocked.CompareExchange(ref _matchInProgress, 1, 0) == 0)
                    {
                        _ = RunMatchAsync(playersToMatch);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to process the queue: {ex}");
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                // One failed task must not stop the queue from ever being processed again.
                Console.Error.WriteLine($"A queue task failed: {ex}");
            }

            _ = ProcessQueue();
        }

        /// <summary>
        /// Runs <see cref="HandleMatchedPlayers"/> without anyone awaiting it. Never throws, and always clears the in-progress flag.
        /// </summary>
        private async Task RunMatchAsync(List<int> playerIDs)
        {
            try
            {
                await HandleMatchedPlayers(playerIDs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Matching players failed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _matchInProgress, 0);
            }
        }

        /// <summary>
        /// Handles when a set of players have been matched for a given game type: starts their spatial synchronization, waits until it
        /// has produced a global map, and then creates the game for the players that are still in it. If that does not work out
        /// (timeout, too many players lost, error), the session is abandoned and the players that are still connected are queued again.
        /// </summary>
        /// <param name="playerIDs">The ID of the players.</param>
        private async Task HandleMatchedPlayers(List<int> playerIDs)
        {
            // Dequeue each of the players being matched.
            foreach (var playerID in playerIDs)
            {
                DequeuePlayer(playerID);
            }

            SpatialSyncCoordinator spatialSyncCoordinator;
            using (var scope = _scopeFactory.CreateScope())
            {
                // A singleton, so it stays valid after the scope is gone.
                spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();
            }

            int syncSessionID = -1;
            bool gameCreated = false;

            try
            {
                // The session keeps the list it is given and removes players from it as they drop, so give it a copy.
                syncSessionID = await spatialSyncCoordinator.GenerateSpatialSyncSession(new List<int>(playerIDs));
                if (syncSessionID == -1)
                {
                    Console.Error.WriteLine("Error: Could not create a spatial synchronization session for the matched players.");
                    return;
                }

                ServerSpatialSyncManager? spatialSyncManager = spatialSyncCoordinator.GetSession(syncSessionID);
                if (spatialSyncManager == null)
                {
                    Console.Error.WriteLine($"Error: Could not find spatial synchronization session with ID {syncSessionID}.");
                    return;
                }

                // Tell each player to begin spatial synchronization. A player that could not be reached never sends a key frame,
                // and the session drops them once its first key frame timeout runs out.
                await Task.WhenAll(playerIDs.Select(SendBeginSpatialSyncAsync));

                // Wait until a global spatial synchronization session has been established before creating the game instance.
                List<int>? readyPlayerIDs = await WaitForGlobalMapAsync(spatialSyncCoordinator, syncSessionID, spatialSyncManager);
                if (readyPlayerIDs == null)
                {
                    return;
                }

                // Create a game with the players that are in the global map (some may have dropped while waiting).
                using (var scope = _scopeFactory.CreateScope())
                {
                    var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();
                    await gameCoordinator.GenerateGame(readyPlayerIDs);
                }
                gameCreated = true;

                // Players that were lost on the way go back in the queue and join the game like any later arrival.
                RequeueConnectedPlayers(playerIDs.Except(readyPlayerIDs));
            }
            finally
            {
                if (!gameCreated)
                {
                    await AbandonSpatialSyncSessionAsync(spatialSyncCoordinator, syncSessionID, playerIDs);
                    RequeueConnectedPlayers(playerIDs);
                }
            }
        }

        /// <summary>
        /// Wait until the global map of the session has formed. Returns the players that are in it, or null (after logging why) if the
        /// wait should be given up: the session is gone, fewer than <see cref="MinPlayersToStartGame"/> players are left in it, or
        /// <see cref="GlobalMapTimeoutSeconds"/> ran out.
        /// </summary>
        private async Task<List<int>?> WaitForGlobalMapAsync(SpatialSyncCoordinator spatialSyncCoordinator, int sessionID, ServerSpatialSyncManager spatialSyncManager)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(GlobalMapTimeoutSeconds);

            while (!spatialSyncManager.GlobalMappingManager.IsGlobalMapFormed)
            {
                if (!spatialSyncCoordinator.SessionExists(sessionID))
                {
                    Console.Error.WriteLine($"Spatial synchronization session {sessionID} was removed while waiting for its global map.");
                    return null;
                }

                int playersLeft;
                lock (spatialSyncManager.PlayerIDs)
                    playersLeft = spatialSyncManager.PlayerIDs.Count;

                if (playersLeft < MinPlayersPerSession)
                {
                    Console.Error.WriteLine($"Only {playersLeft} player(s) are left in spatial synchronization session {sessionID}; giving up on its global map.");
                    return null;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    Console.Error.WriteLine($"Spatial synchronization session {sessionID} did not form a global map within {GlobalMapTimeoutSeconds} seconds; giving up.");
                    return null;
                }

                await Task.Delay(100); // Wait for 100 milliseconds before checking again.
            }

            lock (spatialSyncManager.PlayerIDs)
                return spatialSyncManager.PlayerIDs.ToList();
        }

        /// <summary>
        /// Stop and remove a spatial synchronization session that will not lead to a game, and close the spatial sync sockets of its
        /// players so their clients stop sending key frames to a session that no longer exists.
        /// </summary>
        private async Task AbandonSpatialSyncSessionAsync(SpatialSyncCoordinator spatialSyncCoordinator, int sessionID, List<int> playerIDs)
        {
            Console.WriteLine($"Abandoning spatial synchronization session {sessionID}.");

            if (sessionID != -1)
            {
                ServerSpatialSyncManager? spatialSyncManager = spatialSyncCoordinator.GetSession(sessionID);
                if (spatialSyncManager != null)
                {
                    spatialSyncCoordinator.RemoveSession(sessionID);

                    try
                    {
                        await spatialSyncManager.StopSpatialSyncAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to stop spatial synchronization session {sessionID}: {ex}");
                    }
                }
            }

            await Task.WhenAll(playerIDs.Select(async playerID =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var spatialSyncConnectionHandler = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionHandler>();
                    await spatialSyncConnectionHandler.DisconnectPlayerAsync(playerID, "Spatial synchronization was abandoned");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to close the spatial sync socket of player {playerID}: {ex}");
                }
            }));
        }

        /// <summary>
        /// Put the players that are still connected to the game server back in the queue.
        /// </summary>
        private void RequeueConnectedPlayers(IEnumerable<int> playerIDs)
        {
            using var scope = _scopeFactory.CreateScope();
            var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

            foreach (int playerID in playerIDs.ToList())
            {
                WebSocket? gameSocket = gameConnectionManager.GetGameSocketByPlayerID(playerID);
                if (gameSocket != null && gameSocket.State == WebSocketState.Open)
                {
                    EnqueuePlayer(playerID);
                }
            }
        }

        /// <summary>
        /// Add a player to the existing spatial synchronization session and tell them to begin.
        /// </summary>
        private async Task JoinSpatialSyncSessionAsync(int playerID, int sessionID)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();
                await spatialSyncCoordinator.AddPlayerToSession(playerID, sessionID);
            }

            await SendBeginSpatialSyncAsync(playerID);
        }

        /// <summary>
        /// Tell a player to begin spatial synchronization. Returns false (after logging) if the message could not be sent.
        /// </summary>
        private async Task<bool> SendBeginSpatialSyncAsync(int playerID)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var gameConnectionHandler = scope.ServiceProvider.GetRequiredService<GameConnectionHandler>();
                bool sent = await gameConnectionHandler.SendObjectAsync(playerID, new BeginSpatialSyncDTO());
                if (!sent)
                    Console.Error.WriteLine($"Could not tell player {playerID} to begin spatial synchronization.");
                return sent;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to tell player {playerID} to begin spatial synchronization: {ex}");
                return false;
            }
        }
    }
}
