using Server.Handler;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using SharedLibrary.Utilities;
using System.Collections.Concurrent;

/// <summary>
/// Server-side game manager for the online game.
/// It owns the authoritative <see cref="GameState"/>, starts the game, and decides what each player is sent.
/// Inbound messages are handled by <see cref="GameConnectionHandler"/>.
/// The client must connect to this server over a reliable UDP connection.
/// </summary>
namespace Server.Manager
{
    public class ServerGameStateManager
    {

        #region Properties

        public int ID { get; set; }

        // How far away to spawn from the initial players (meters).
        float _spawnDistance = 1.0f;
        // Empty space between the two starting cubes (meters).
        float _cubeGap = 0.25f;

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

        public float ServerTime => TimeUtilities.GetTime();

        /// <summary>The authoritative game state; null until the game starts.</summary>
        public GameState GameState { get; private set; }

        private readonly IServiceScopeFactory _scopeFactory;
        // Cancellation and task for the state synchronization loop.
        private CancellationTokenSource? _stateSyncCts;
        private Task? _stateSyncTask;
        // Per-object action queues. Keyed by object ID (e.g., cube ID). Thread-safe.
        private readonly ConcurrentDictionary<string, ConcurrentQueue<SingleAction>> _actionQueues
            = new ConcurrentDictionary<string, ConcurrentQueue<SingleAction>>();

        /// <summary>
        /// Enqueue a single action to the per-object queue. Actions targeting objects should be enqueued
        /// under the object's ID so they are processed in order per-object during the state sync.
        /// </summary>
        /// <param name="action">The action to enqueue.</param>
        public void EnqueueSingleAction(SingleAction action)
        {
            if (action == null) return;

            string key = "global";

            if (action is CubeAction cubeAction)
            {
                key = cubeAction.CubeID ?? "global";
            }

            var queue = _actionQueues.GetOrAdd(key, _ => new ConcurrentQueue<SingleAction>());
            queue.Enqueue(action);
        }

        #endregion Properties

        #region Constructor/Destructor

        /// <summary>
        /// Initializes a new ServerGameStateManager, creates a server GameState, and starts game initialization.
        /// </summary>
        /// <remarks>Creates GameState with the server flag set (new GameState(true)) and invokes InitializeGame
        /// to prepare the game.</remarks>
        /// <param name="scopeFactory">Factory for creating IServiceScope instances to resolve scoped services.</param>
        /// <param name="playerIDs">Initial list of player IDs used to populate the game's initial state.</param>
        public ServerGameStateManager(IServiceScopeFactory scopeFactory, List<int> playerIDs)
        {
            _scopeFactory = scopeFactory;
            GameState = new GameState(true);
            InitializeGame(playerIDs);
            // Start the periodic state synchronization loop
            StartStateSync();
        }

        #endregion Constructor/Destructor

        #region Initialize Game

        /// <summary>
        /// Builds the authoritative game state and sends it to every connected player. Players who
        /// connect later get it in <see cref="OnPlayerConnected"/>.
        /// <paramref name="playerIDs"/> The list of player IDs who are initially connected to game instance.</param>
        /// </summary>
        void InitializeGame(List<int> playerIDs)
        {
            var cubes = new List<CubeState>();
            Pose[] startingPoses = GetStartingCubePoses();
            for (int i = 0; i < startingPoses.Length; i++)
                cubes.Add(new CubeState($"cube-{i}", startingPoses[i], playerIDs));

            GameState = new GameState(isAuthoritative: true);
            GameState.ServerInitialize(playerIDs, cubes);

            BroadcastGameState();
        }

        /// <summary>Two cubes side by side, <see cref="_spawnDistance"/> in the middle of all users.</summary>
        Pose[] GetStartingCubePoses()
        {
            return new[]
            {
                new Pose(new Vector3(0.25f, 0, 0), new Quaternion(0, 0, 0, 1)),
                new Pose(new Vector3(0, 0, 0), new Quaternion(0, 0, 0, 1)),
            };
        }

        #endregion Initialize Game

        #region Players

        public bool IsPlayer(int playerID)
        {
            foreach (int id in GameState.PlayerIDs)
            {
                if (id == playerID)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a player ID to the current game state and broadcasts the updated state.
        /// </summary>
        /// <remarks>No action is taken if GameState is null or the player already exists. Updates
        /// GameState.PlayerIDs and calls BroadcastGameState.</remarks>
        /// <param name="playerID">The unique identifier of the player to add.</param>
        public void AddPlayer(int playerID)
        {
            if (GameState == null)
                return;
            if (IsPlayer(playerID))
                return;
            GameState.PlayerIDs.Add(playerID);
            BroadcastGameState();
        }

        /// <summary>
        /// A player joined (or rejoined): send them the whole game state if the game has started.
        /// </summary>
        public void OnPlayerConnected(int playerID)
        {
            if (GameState != null)
                this.AddOutgoingDTO(playerID, new GameStateDTO(ServerTime, GameState));
        }

        /// <summary>
        /// A player dropped: free every cube they were holding so others can grab it.
        /// </summary>
        public void OnPlayerDisconnected(int playerID)
        {
            if (GameState == null)
                return;

            bool changed = false;
            foreach (CubeState cube in GameState.Cubes.Values)
            {
                if (cube.HolderPlayerID == playerID)
                    changed |= GameState.RevokeGrabPermissions(cube.ID);
            }

            if (changed)
                BroadcastGameState();
        }

        #endregion Players

        #region Process DTOs

        /// <summary>
        /// Start the background loop that updates and broadcasts the game state every TickInterval seconds.
        /// The per-tick authoritative state update should be implemented where indicated below.
        /// </summary>
        public void StartStateSync()
        {
            if (_stateSyncTask != null && !_stateSyncTask.IsCompleted)
                return; // already running

            _stateSyncCts = new CancellationTokenSource();
            var token = _stateSyncCts.Token;

            _stateSyncTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    // First: drain per-object queues into temporary lists so new incoming messages are not processed this tick.
                    var queuesSnapshot = new List<KeyValuePair<string, List<SingleAction>>>();

                    foreach (var kvp in _actionQueues)
                    {
                        var key = kvp.Key;
                        var queue = kvp.Value;
                        var drained = new List<SingleAction>();

                        while (queue.TryDequeue(out var action))
                        {
                            drained.Add(action);
                        }

                        if (drained.Count > 0)
                            queuesSnapshot.Add(new KeyValuePair<string, List<SingleAction>>(key, drained));
                    }

                    // Process drained actions. Apply them to the authoritative GameState.
                    foreach (var pair in queuesSnapshot)
                    {
                        foreach (var action in pair.Value)
                        {
                            // TODO: handle pre-action checks on each input.


                            // Apply the action to the authoritative GameState.
                            bool successfulAction = GameState.HandleSingleAction(action);

                            // TODO: handle action failures, logging, and generating per-player responses or broadcasts.
                        }
                    }

                    // TODO: Run per-tick authoritative updates here (physics, AI, etc.)

                    // Broadcast the updated state to all players
                    BroadcastGameState();

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
        /// Stops the state synchronization loop and waits for it to finish.
        /// </summary>
        public async Task StopStateSyncAsync()
        {
            if (_stateSyncCts == null)
                return;

            _stateSyncCts.Cancel();

            try
            {
                if (_stateSyncTask != null)
                    await _stateSyncTask;
            }
            catch (TaskCanceledException) { }
            finally
            {
                _stateSyncCts.Dispose();
                _stateSyncCts = null;
                _stateSyncTask = null;
            }
        }

        #endregion Process DTOs

        #region Send Messages

        void BroadcastGameState()
        {
            foreach (int playerID in GameState.PlayerIDs)
                this.AddOutgoingDTO(playerID, new GameStateDTO(ServerTime, GameState));
        }

        public async Task<bool> AddOutgoingDTO(int playerID, object obj)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var sessionConnectionHandler = scope.ServiceProvider.GetRequiredService<GameConnectionHandler>();
                return await sessionConnectionHandler.SendObjectAsync(playerID, obj);
            }
        }

        #endregion Send Messages

    }
}