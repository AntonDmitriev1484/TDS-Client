using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Collections.Concurrent;

namespace Server.Manager.SpatialSync
{
    public class ServerSpatialSyncManager
    {

        #region Properties

        public int ID { get; set; }
        public List<int> PlayerIDs { get; set; }
        private ConcurrentDictionary<int, KeyFrameDTO> _latestKeyFrame { get; set; }

        public GlobalMappingManager GlobalMappingManager { get; set; }


        // Tick rate (updates per second) and corresponding interval (seconds per update).
        private float _tickRate = 30;
        private float _tickInterval = 1.0f / 30;

        public float TickRate
        {
            get => _tickRate;
            set
            {
                _tickRate = value;
                // Avoid division by zero
                _tickInterval = _tickRate > 0f ? 1.0f / _tickRate : float.PositiveInfinity;
            }
        }

        public float TickInterval => _tickInterval;

        private readonly IServiceScopeFactory _scopeFactory;
        // Cancellation and task for the spatial synchronization loop.
        private CancellationTokenSource? _spatialSyncCts;
        private Task? _spatialSyncTask;

        #endregion Properties

        #region Initialization

        /// <summary>
        /// Initializes a new ServerSpatialSyncManager, creates a server GameState, and starts spatial synchronization initialization.
        /// </summary>
        /// <remarks>Creates GameState with the server flag set (new GameState(true)) and invokes InitializeGame
        /// to prepare the session.</remarks>
        /// <param name="scopeFactory">Factory for creating IServiceScope instances to resolve scoped services.</param>
        /// <param name="playerIDs">Initial list of player IDs used to populate the session's initial state.</param>
        public ServerSpatialSyncManager(IServiceScopeFactory scopeFactory, ServerSettings serverSettings, List<int> playerIDs)
        {
            _scopeFactory = scopeFactory;
            PlayerIDs = playerIDs;
            _latestKeyFrame = new ConcurrentDictionary<int, KeyFrameDTO>();
            GlobalMappingManager = new GlobalMappingManager(serverSettings.DebugMode);

            StartSpatialSync();
        }

        #endregion Initialization

        #region Players

        /// <summary>
        /// Check if this session instance has a particular player in it.
        /// </summary>
        /// <param name="playerID">The ID of the player to check for.</param>
        /// <returns>True if the player is part of this session instance, false otherwise.</returns>
        public bool HasPlayer(int playerID)
        {
            foreach (int id in this.PlayerIDs)
            {
                if (id == playerID)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a player ID to the current session .
        /// </summary>
        /// <remarks>No action is taken if GameState is null or the player already exists. Updates
        /// GameState.PlayerIDs and calls BroadcastGameState.</remarks>
        /// <param name="playerID">The unique identifier of the player to add.</param>
        public void AddPlayer(int playerID)
        {
            if (HasPlayer(playerID))
                return;
            this.PlayerIDs.Add(playerID);
        }

        /// <summary>
        /// A player joined (or rejoined): send them the whole game state if the game has started.
        /// </summary>
        public void OnPlayerConnected(int playerID)
        {

        }

        /// <summary>
        /// A player dropped: free every cube they were holding so others can grab it.
        /// </summary>
        public void OnPlayerDisconnected(int playerID)
        {
            this.PlayerIDs.Remove(playerID);
        }

        #endregion Players

        #region Process DTOs

        public void EnqueueKeyFrame(int playerID, KeyFrameDTO keyFrameDTO)
        {
            if (_latestKeyFrame.ContainsKey(playerID))
            {
                _latestKeyFrame[playerID] = keyFrameDTO;
            }
            else
            {
                _latestKeyFrame.TryAdd(playerID, keyFrameDTO);
            }
        }

        public void StartSpatialSync()
        {
            if (_spatialSyncTask != null && !_spatialSyncTask.IsCompleted)
                return; // already running

            _spatialSyncCts = new CancellationTokenSource();
            var token = _spatialSyncCts.Token;

            _spatialSyncTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_latestKeyFrame.IsEmpty)
                    {
                        // First: drain per-object queues into temporary lists so new incoming messages are not processed this tick.
                        var latestKeyframeSnapshot = new List<KeyValuePair<int, KeyFrameDTO>>();

                        foreach (var kvp in _latestKeyFrame.ToDictionary())
                        {
                            _latestKeyFrame.TryRemove(kvp);
                            latestKeyframeSnapshot.Add(kvp);
                        }

                        // Process drained keyframes.
                        foreach (var pair in latestKeyframeSnapshot)
                        {
                            int playerID = pair.Key;
                            KeyFrameDTO keyFrameDTO = pair.Value;

                            // Add to global map and obtain spatial synchronization data to return to client.
                            GlobalMappingManager.HandleNewKeyFrame(playerID, keyFrameDTO);
                        }
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(TickInterval), token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Stops the spatial synchronization loop and waits for it to finish.
        /// </summary>
        public async Task StopSpatialSyncAsync()
        {
            if (_spatialSyncCts == null)
                return;

            _spatialSyncCts.Cancel();

            try
            {
                if (_spatialSyncTask != null)
                    await _spatialSyncTask;
            }
            catch (TaskCanceledException) { }
            finally
            {
                _spatialSyncCts.Dispose();
                _spatialSyncCts = null;
                _spatialSyncTask = null;
            }
        }

        #endregion Process DTOs

    }
}
