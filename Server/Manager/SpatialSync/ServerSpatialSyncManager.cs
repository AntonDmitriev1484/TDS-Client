using Server.Handler;
using Server.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Collections.Concurrent;

namespace Server.Manager.SpatialSync
{
    public class ServerSpatialSyncManager
    {

        #region Properties

        public int ID { get; set; }
        public List<int> PlayerIDs { get; set; }
        private ConcurrentDictionary<int, KeyFrameAnchorDTO> _latestKeyFrame { get; set; }

        /// <summary>
        /// How long a player may go without sending a key frame, after they have sent their first one, before they are disconnected.
        /// Clients send at least once a second, so this only needs to ride out hiccups.
        /// </summary>
        public double KeyFrameTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// How long a player may stay in the session without sending their first key frame before they are disconnected. This is
        /// longer because a client first has to get camera permission and start the camera (the client allows up to 70 seconds).
        /// </summary>
        public double FirstKeyFrameTimeoutSeconds { get; set; } = 90;

        /// <summary>When each player was last heard from, and whether they have sent a key frame yet.</summary>
        private sealed class PlayerActivity
        {
            public long LastSeenMs = Environment.TickCount64; // starts the clock when the entry is created
            public volatile bool SentKeyFrame;
        }

        private readonly ConcurrentDictionary<int, PlayerActivity> _playerActivity = new();

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

        /// <summary>Debug mode: 3D windows of the scene anchor sub-maps and of the merged global map.</summary>
        private SceneMapViewer? _sceneMapViewer;
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
            _latestKeyFrame = new ConcurrentDictionary<int, KeyFrameAnchorDTO>();
            GlobalMappingManager = new GlobalMappingManager(serverSettings.DebugMode)
            {
                // The global map is formed once all of this session's players have been placed in it by matching the scene anchors of their room scans.
                ExpectedPlayerIDs = GetPlayerIDsSnapshot,
            };

            if (serverSettings.DebugMode)
                _sceneMapViewer = new SceneMapViewer(GlobalMappingManager);

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
            lock (PlayerIDs)
            {
                if (HasPlayer(playerID))
                    return;
                this.PlayerIDs.Add(playerID);
            }
        }

        /// <summary>A copy of the session's player IDs that is safe to read while players join and leave.</summary>
        private IReadOnlyCollection<int> GetPlayerIDsSnapshot()
        {
            lock (PlayerIDs)
                return PlayerIDs.ToArray();
        }

        /// <summary>
        /// A player dropped: free every cube they were holding so others can grab it.
        /// </summary>
        public void OnPlayerDisconnected(int playerID)
        {
            lock (PlayerIDs)
                this.PlayerIDs.Remove(playerID);

            // Only this player leaves the global map. The anchors in it stay, so the others (and this player if they rejoin) keep the same global frame.
            GlobalMappingManager.RemovePlayer(playerID);
        }

        #endregion Players

        #region Process DTOs

        public void EnqueueKeyFrame(int playerID, KeyFrameAnchorDTO keyFrameDTO)
        {
            // Any key frame counts as a sign of life for the timeout.
            PlayerActivity activity = _playerActivity.GetOrAdd(playerID, _ => new PlayerActivity());
            Interlocked.Exchange(ref activity.LastSeenMs, Environment.TickCount64);
            activity.SentKeyFrame = true;

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
                        var latestKeyframeSnapshot = new List<KeyValuePair<int, KeyFrameAnchorDTO>>();

                        foreach (var kvp in _latestKeyFrame.ToDictionary())
                        {
                            _latestKeyFrame.TryRemove(kvp);
                            latestKeyframeSnapshot.Add(kvp);
                        }

                        // Process drained keyframes.
                        foreach (var pair in latestKeyframeSnapshot)
                        {
                            int playerID = pair.Key;
                            KeyFrameAnchorDTO keyFrameDTO = pair.Value;

                            // A key frame that was already queued when the player left must not start a new sub-map for them.
                            if (!GetPlayerIDsSnapshot().Contains(playerID))
                                continue;

                            try
                            {
                                // Take the player's spatial anchor poses into the global map; this returns the global pose of the pose they sent
                                // (null until the player is placed in the global map).
                                SharedLibrary.Game.Actions.Pose? globalPose = GlobalMappingManager.HandleNewKeyFrame(playerID, keyFrameDTO);
                                SharedLibrary.Game.Actions.Pose cameraLocalPose = keyFrameDTO.CameraLocalPose;

                                // Tell the player: with the global pose if the global map is ready for them, without it if not.
                                SyncResponseDTO syncResponseDTO = globalPose.HasValue
                                    ? new SyncResponseDTO(keyFrameDTO.ID, cameraLocalPose, globalPose.Value)
                                    : new SyncResponseDTO(keyFrameDTO.ID, cameraLocalPose);
                                _ = AddOutgoingDTO(playerID, syncResponseDTO);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Failed to process key frame {keyFrameDTO.ID} of player {playerID}: {ex}");
                            }
                        }
                    }

                    DisconnectSilentPlayers();

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
                _sceneMapViewer?.Dispose();
                _sceneMapViewer = null;
            }
        }

        #endregion Process DTOs

        #region Player Timeouts

        /// <summary>
        /// Disconnect players that have gone quiet. A player who is in the session but sends no key frames can never be merged into the
        /// global map, and the global map waits for every player in the session, so one silent player would hold everybody back.
        /// Never throws, so the spatial synchronization loop keeps running.
        /// </summary>
        private void DisconnectSilentPlayers()
        {
            try
            {
                long now = Environment.TickCount64;
                IReadOnlyCollection<int> playerIDs = GetPlayerIDsSnapshot();

                foreach (int playerID in playerIDs)
                {
                    // A player is first seen here when they join, which is when their clock starts.
                    PlayerActivity activity = _playerActivity.GetOrAdd(playerID, _ => new PlayerActivity());

                    double silentSeconds = (now - Interlocked.Read(ref activity.LastSeenMs)) / 1000.0;
                    double limitSeconds = activity.SentKeyFrame ? KeyFrameTimeoutSeconds : FirstKeyFrameTimeoutSeconds;

                    if (silentSeconds > limitSeconds)
                    {
                        string reason = activity.SentKeyFrame
                            ? $"no key frame for {silentSeconds:0.#} s"
                            : $"no first key frame after {silentSeconds:0.#} s";
                        DisconnectPlayer(playerID, reason);
                    }
                }

                // Forget players that have left the session.
                foreach (int playerID in _playerActivity.Keys)
                {
                    if (!playerIDs.Contains(playerID))
                        _playerActivity.TryRemove(playerID, out _);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to check for silent players: {ex}");
            }
        }

        /// <summary>
        /// Take a player out of the session right away (so the global map no longer waits for them), then close their socket.
        /// </summary>
        private void DisconnectPlayer(int playerID, string reason)
        {
            Console.WriteLine($"[SpatialSync] Disconnecting player {playerID}: {reason}.");

            _playerActivity.TryRemove(playerID, out _);
            _latestKeyFrame.TryRemove(playerID, out _);
            OnPlayerDisconnected(playerID);

            _ = CloseSocketAsync(playerID, reason);
        }

        private async Task CloseSocketAsync(int playerID, string reason)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionHandler>();
                await handler.DisconnectPlayerAsync(playerID, reason);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to close the socket of player {playerID}: {ex}");
            }
        }

        #endregion Player Timeouts

        #region Send Messages

        /// <summary>
        /// Send a message to a player without waiting for it. Never throws: a failed send is logged here, since nobody awaits the result.
        /// </summary>
        public async Task AddOutgoingDTO(int playerID, object obj)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sessionConnectionHandler = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionHandler>();
                if (!await sessionConnectionHandler.SendObjectAsync(playerID, obj))
                    Console.Error.WriteLine($"Could not send {obj.GetType().Name} to player {playerID}.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to send {obj.GetType().Name} to player {playerID}: {ex}");
            }
        }

        #endregion Send Messages

    }
}
