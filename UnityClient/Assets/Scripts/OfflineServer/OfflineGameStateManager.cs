using Assets.Scripts.Utilities;
using Client.Scene.Manager;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using SharedLibrary.Utilities;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
// Make UnityEngine.Pose unambiguous in this file
using Pose = UnityEngine.Pose;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;
using SharedPose = SharedLibrary.Game.Actions.Pose;

/// <summary>
/// Stands in for the game server while testing on a single headset, and mirrors the server's
/// ServerGameStateManager: it owns the authoritative <see cref="GameState"/>, starts the game, and
/// decides what each player is sent. Inbound messages are handled by <see cref="MessageHandler"/>,
/// which only enqueues actions; the state sync loop applies them, runs the automatic game functions,
/// and broadcasts the game state every tick.
///
/// Unlike the server, the state sync loop is a coroutine on Unity's main thread rather than a
/// background task, because it uses Unity APIs (Random, the head pose).
///
/// The client (<see cref="GameStateManager"/> + ConnectionManager) runs on the same headset but
/// only reaches this server over a local TCP connection, so pointing the client at a real server
/// needs no changes on the client.
/// </summary>
public class OfflineGameStateManager : MonoBehaviour
{

    #region Properties

    [SerializeField] protected MessageHandler _messageHandler;

    // How far away to spawn from the initial players (meters).
    [SerializeField] float _spawnDistance = 1.0f;

    [Tooltip("Empty space between the two starting cubes (meters).")]
    [SerializeField] float _cubeGap = 0.25f;

    [Tooltip("Seconds to wait for a valid head pose before spawning anyway.")]
    [SerializeField] float _headPoseTimeout = 2f;

    [Header("Game")]
    [Tooltip("Edge length of the cubes that spawn on the board (meters).")]
    [SerializeField] float _cubeSize = CubeState.DefaultCubeSize;

    [Tooltip("Board width (x), thickness (y) and depth (z) (meters).")]
    [SerializeField] Vector3 _boardSize = new Vector3(0.75f, 0.1f, 0.75f);

    [Tooltip("Cubes spawn on one cell of a grid this many cells wide and deep.")]
    [SerializeField] int _gridSize = 8;

    [Tooltip("How far in front of player 0's head the board's center is (meters).")]
    [SerializeField] float _boardForwardOffset = 0.5f;

    [Tooltip("How far below player 0's head the board's center is (meters).")]
    [SerializeField] float _boardDownOffset = 0.5f;

    [Tooltip("Edge length of each player's goal cube (meters).")]
    [SerializeField] float _goalSize = 0.5f;

    [Tooltip("How far above each player's head their goal's center is (meters).")]
    [SerializeField] float _goalHeightAboveHead = 0.5f;

    [Tooltip("Seconds a cube must stay inside a goal to score.")]
    [SerializeField] float _timeInGoalToScore = 0.5f;

    [Tooltip("The game ends once the round passes this number.")]
    [SerializeField] int _maxRounds = 12;

    // Goals exist for both players, even offline where only the local headset plays.
    const int Player0ID = 0;
    const int Player1ID = 1;

    static readonly RGBAColor BoardColor = new RGBAColor(0.5f, 0.5f, 0.5f);
    static readonly RGBAColor Player0GoalColor = new RGBAColor(1f, 0f, 0f, 0.3f);
    static readonly RGBAColor Player1GoalColor = new RGBAColor(0f, 0f, 1f, 0.3f);
    static readonly RGBAColor GameOverGoalColor = new RGBAColor(0f, 1f, 0f, 0.3f);

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

    public double ServerTime { get; set; }

    /// <summary>The authoritative game state; null until the game starts.</summary>
    public GameState GameState { get; private set; }

    /// <summary>The logical timestamps of all clients as recently received.</summary>
    public Dictionary<int, long> PlayerLogicalTimestamps { get; private set; } = new Dictionary<int, long>();

    OVRCameraRig _rig;
    GameStateManager _clientGameStateManager;
    int _nextCubeID;
    bool _gameOver;

    // Cube ID -> the goal it is inside and the server time it entered.
    readonly Dictionary<string, (int PlayerID, double EnterTime)> _cubesInGoals = new Dictionary<string, (int, double)>();

    // The state synchronization loop.
    private Coroutine _stateSyncCoroutine;
    // Per-object action queues. Keyed by object ID (e.g., cube ID). Thread-safe.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<QueuedAction>> _actionQueues
        = new ConcurrentDictionary<string, ConcurrentQueue<QueuedAction>>();

    private sealed class QueuedAction
    {
        public SingleAction Action { get; }
        public long ClientLogicalTimestamp { get; }
        public int PlayerID { get; }

        public QueuedAction(SingleAction action, long clientLogicalTimestamp, int playerID)
        {
            Action = action;
            ClientLogicalTimestamp = clientLogicalTimestamp;
            PlayerID = playerID;
        }
    }

    /// <summary>
    /// Enqueue a single action to the per-object queue. Actions targeting objects should be enqueued
    /// under the object's ID so they are processed in order per-object during the state sync.
    /// </summary>
    /// <param name="action">The action to enqueue.</param>
    public void EnqueueSingleAction(SingleAction action, long clientLogicalTimestamp, int playerID)
    {
        if (action == null) return;

        string key = "global";

        if (action is CubeAction cubeAction)
        {
            key = cubeAction.CubeID ?? "global";
        }

        var queue = _actionQueues.GetOrAdd(key, _ => new ConcurrentQueue<QueuedAction>());
        queue.Enqueue(new QueuedAction(action, clientLogicalTimestamp, playerID));
    }

    #endregion Properties

    #region Unity Functions

    void Awake()
    {
        if (_messageHandler == null)
            _messageHandler = GetOrAddComponent<MessageHandler>();

        // Offline, this headset is also the client. Add its components if the scene doesn't have
        // them; they talk to this server only through the socket.
        _clientGameStateManager = FindFirstObjectByType<GameStateManager>();
        if (_clientGameStateManager == null)
            _clientGameStateManager = gameObject.AddComponent<GameStateManager>();
    }

    IEnumerator Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig == null)
        {
            Debug.LogError($"{nameof(OfflineGameStateManager)}: no OVRCameraRig found in the scene.");
            yield break;
        }

        // The head anchor sits at the origin until the first tracked pose arrives.
        float deadline = Time.time + _headPoseTimeout;
        while (_rig.centerEyeAnchor.localPosition == Vector3.zero && Time.time < deadline)
            yield return null;

        InitializeGame(GetPlayerIDs());
        // Start the periodic state synchronization loop
        StartStateSync();
    }

    void OnDestroy()
    {
        StopStateSync();
    }

    #endregion Unity Functions

    #region Initialize Game

    /// <summary>
    /// Builds the authoritative game state and sends it to every connected player. Players who
    /// connect later get it in <see cref="OnPlayerConnected"/>.
    /// <paramref name="playerIDs"/> The list of player IDs who are initially connected to game instance.</param>
    /// </summary>
    void InitializeGame(IReadOnlyList<int> playerIDs)
    {
        ServerTime = TimeUtilities.GetTime();
        var cubes = new List<CubeState>();
        // Replaced by a single cube on the board.
        // Pose[] startingPoses = GetStartingCubePoses();
        //
        // for (int i = 0; i < startingPoses.Length; i++)
        // {
        //     cubes.Add(new CubeState($"cube-{i}", ServerTime, ServerTime, Conversions.ToSharedPose(startingPoses[i]), playerIDs));
        // }

        Transform head = _rig.centerEyeAnchor;
        Vector3 forward = GetHeadForward();
        Quaternion rotation = Quaternion.LookRotation(forward);

        Vector3 boardCenter = head.position + forward * _boardForwardOffset + Vector3.down * _boardDownOffset;
        var board = new BoardState(BoardColor, Conversions.ToSharedPose(boardCenter, rotation), _gridSize, Conversions.ToSharedPosition(_boardSize));

        // Player 1 is mirrored across the board from player 0: as far beyond it, facing back.
        Vector3 player1Head = head.position + forward * (2f * _boardForwardOffset);
        var goals = new[]
        {
            new GoalCubeState(Player0ID, Player0GoalColor, Conversions.ToSharedPose(head.position + Vector3.up * _goalHeightAboveHead, rotation), _goalSize),
            new GoalCubeState(Player1ID, Player1GoalColor, Conversions.ToSharedPose(player1Head + Vector3.up * _goalHeightAboveHead, Quaternion.LookRotation(-forward)), _goalSize),
        };

        _nextCubeID = 0;
        _gameOver = false;
        _cubesInGoals.Clear();

        GameState = new GameState(isAuthoritative: true);
        foreach (int playerID in playerIDs)
        {
            if (!PlayerLogicalTimestamps.ContainsKey(playerID))
                PlayerLogicalTimestamps.Add(playerID, 0);
        }

        GameState.ServerInitialize(playerIDs, cubes, board, goals);
        GameState.ServerAddCube(CreateCubeOnBoard());

        BroadcastGameState();
    }

    /// <summary>
    /// A new cube with the next ID on a random cell of the board's grid.
    /// </summary>
    CubeState CreateCubeOnBoard()
    {
        BoardState board = GameState.Board;
        var gridLocation = (X: Random.Range(0, board.GridSize), Z: Random.Range(0, board.GridSize));
        var pose = new SharedPose(board.GridToPosition(gridLocation.X, gridLocation.Z, _cubeSize), board.Pose.Rotation);

        return new CubeState($"cube-{_nextCubeID++}", ServerTime, ServerTime, pose, GameState.PlayerIDs, gridLocation, _cubeSize);
    }

    /// <summary>The direction the user faces, flattened onto the horizontal plane.</summary>
    Vector3 GetHeadForward()
    {
        Transform head = _rig.centerEyeAnchor;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // looking straight up/down
        return forward.normalized;
    }

    /// <summary>Two cubes side by side, <see cref="_spawnDistance"/> in front of the user.</summary>
    Pose[] GetStartingCubePoses()
    {
        Transform head = _rig.centerEyeAnchor;
        Vector3 forward = GetHeadForward();

        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 center = head.position + forward * _spawnDistance;
        Vector3 halfSpacing = right * (0.25f + _cubeGap) * 0.5f;
        Quaternion rotation = Quaternion.LookRotation(forward);

        return new[]
        {
            new Pose(center - halfSpacing, rotation),
            new Pose(center + halfSpacing, rotation),
        };
    }

    #endregion Initialize Game

    #region Players

    /// <summary>Return the players in the offline game: just the local headset.
    /// The local player id is taken from the GameStateManager if available.</summary>
    IReadOnlyList<int> GetPlayerIDs()
    {
        if (_clientGameStateManager != null && _clientGameStateManager.LocalPlayerID >= 0)
            return new[] { _clientGameStateManager.LocalPlayerID };

        return new[] { 0 };
    }

    public bool IsPlayer(int playerID)
    {
        // Before the game starts, the players are the ones it will start with.
        IEnumerable<int> playerIDs = GameState != null ? GameState.PlayerIDs : (IEnumerable<int>)GetPlayerIDs();
        foreach (int id in playerIDs)
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
        if (!PlayerLogicalTimestamps.ContainsKey(playerID))
            PlayerLogicalTimestamps.Add(playerID, 0);

        if (GameState != null)
            AddOutgoingDTO(playerID, new GameStateDTO(ServerTime, PlayerLogicalTimestamps, GameState));
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
    /// Start the loop that updates and broadcasts the game state every TickInterval seconds.
    /// </summary>
    public void StartStateSync()
    {
        if (_stateSyncCoroutine != null)
            return; // already running

        _stateSyncCoroutine = StartCoroutine(StateSyncLoop());
    }

    IEnumerator StateSyncLoop()
    {
        while (true)
        {
            // First: drain per-object queues into temporary lists so new incoming messages are not processed this tick.
            var queuesSnapshot = new List<KeyValuePair<string, List<QueuedAction>>>();

            foreach (var kvp in _actionQueues)
            {
                var key = kvp.Key;
                var queue = kvp.Value;
                var drained = new List<QueuedAction>();

                while (queue.TryDequeue(out var action))
                {
                    drained.Add(action);
                }

                if (drained.Count > 0)
                {
                    drained.Sort((a, b) => a.Action.ServerTime.CompareTo(b.Action.ServerTime));
                    queuesSnapshot.Add(new KeyValuePair<string, List<QueuedAction>>(key, drained));
                }
            }

            // Process drained actions. Apply them to the authoritative GameState.
            foreach (var pair in queuesSnapshot)
            {
                foreach (QueuedAction queuedAction in pair.Value)
                {
                    // Apply the action to the authoritative GameState.
                    bool successfulAction = GameState.HandleSingleAction(queuedAction.Action);

                    // Acknowledge the action whether or not it is accepted, so the client stops replaying it.
                    if (PlayerLogicalTimestamps.TryGetValue(queuedAction.PlayerID, out long latest) && latest < queuedAction.ClientLogicalTimestamp)
                    {
                        PlayerLogicalTimestamps[queuedAction.PlayerID] = queuedAction.ClientLogicalTimestamp;
                    }

                    if (!successfulAction)
                    {
                        Debug.Log($"{nameof(OfflineGameStateManager)}: rejected {queuedAction.Action.FormatActionText()} ({queuedAction.ClientLogicalTimestamp})");
                    }
                }
            }

            // Update the game state server time.
            ServerTime = TimeUtilities.GetTime();

            // Per-tick authoritative updates.
            if (!_gameOver)
            {
                CubeTimeInGoal();
                RoundTracker();
            }

            // Broadcast the updated state to all players
            BroadcastGameState();

            yield return new WaitForSeconds(TickInterval);
        }
    }

    /// <summary>
    /// Stops the state synchronization loop.
    /// </summary>
    public void StopStateSync()
    {
        if (_stateSyncCoroutine == null)
            return;

        StopCoroutine(_stateSyncCoroutine);
        _stateSyncCoroutine = null;
    }

    #endregion Process DTOs

    #region Automatic Functions

    /// <summary>
    /// Scores for a goal's player once a cube has stayed inside that goal for
    /// <see cref="_timeInGoalToScore"/> seconds. Leaving the goal resets the timer.
    /// </summary>
    void CubeTimeInGoal()
    {
        // HandleScore swaps cubes, so iterate over a copy.
        foreach (CubeState cube in new List<CubeState>(GameState.Cubes.Values))
        {
            int? goalPlayerID = FindGoalContaining(cube);
            if (goalPlayerID == null)
            {
                _cubesInGoals.Remove(cube.ID);
                continue;
            }

            if (!_cubesInGoals.TryGetValue(cube.ID, out var entry) || entry.PlayerID != goalPlayerID)
            {
                _cubesInGoals[cube.ID] = (goalPlayerID.Value, ServerTime);
                continue;
            }

            if (ServerTime - entry.EnterTime >= _timeInGoalToScore)
            {
                _cubesInGoals.Remove(cube.ID);
                HandleScore(new ScoreAction(goalPlayerID.Value, ServerTime, cube.ID));
            }
        }
    }

    /// <summary>The player ID of the goal the cube's center is inside, or null.</summary>
    int? FindGoalContaining(CubeState cube)
    {
        foreach (GoalCubeState goal in GameState.Goals.Values)
        {
            if (goal.Contains(cube.Pose.Position))
                return goal.PlayerID;
        }

        return null;
    }

    /// <summary>
    /// Gives the action's player a point, advances the round, and replaces the scored cube with a new
    /// one on a random cell of the board. The state sync loop broadcasts the result.
    /// </summary>
    /// <returns>True if the cube was in the game.</returns>
    public bool HandleScore(ScoreAction scoreAction)
    {
        if (!GameState.ServerRemoveCube(scoreAction.CubeID))
            return false;

        GameState.Scores.TryGetValue(scoreAction.PlayerID, out int score);
        GameState.Scores[scoreAction.PlayerID] = score + 1;
        GameState.Round++;

        CubeState newCube = CreateCubeOnBoard();
        GameState.ServerAddCube(newCube);

        Debug.Log($"{nameof(OfflineGameStateManager)}: player {scoreAction.PlayerID} scored with {scoreAction.CubeID} " +
            $"(score {GameState.Scores[scoreAction.PlayerID]}); round {GameState.Round}, spawned {newCube.ID} at grid {newCube.GridLocation}.");

        return true;
    }

    /// <summary>
    /// Ends the game once the round passes <see cref="_maxRounds"/>: both goals turn green and
    /// scoring stops. The state sync loop broadcasts the result.
    /// </summary>
    void RoundTracker()
    {
        if (GameState.Round <= _maxRounds)
            return;

        _gameOver = true;
        foreach (GoalCubeState goal in GameState.Goals.Values)
            goal.Color = GameOverGoalColor;

        Debug.Log($"{nameof(OfflineGameStateManager)}: game over after round {_maxRounds}.");
    }

    #endregion Automatic Functions

    #region Send Messages

    void BroadcastGameState()
    {
        foreach (int playerID in GameState.PlayerIDs)
            AddOutgoingDTO(playerID, new GameStateDTO(ServerTime, PlayerLogicalTimestamps, GameState));
    }

    /// <summary>
    /// Sends a DTO to a player.
    /// </summary>
    /// <returns>True if the player is connected and the message was sent.</returns>
    public bool AddOutgoingDTO(int playerID, object outgoingDTO)
    {
        return _messageHandler.SendObject(playerID, outgoingDTO);
    }

    #endregion Send Messages

    #region Testing

    /// <summary>
    /// Takes every cube away from whoever holds it, as the server could at any time; the next tick
    /// pushes the new state to the players. Right-click the component header in Play mode to use it.
    /// </summary>
    [ContextMenu("Revoke All Grab Permissions")]
    void RevokeAllGrabPermissions()
    {
        if (GameState == null)
            return;

        foreach (string cubeID in GameState.Cubes.Keys)
            GameState.RevokeGrabPermissions(cubeID);
    }

    #endregion Testing

    T GetOrAddComponent<T>() where T : Component
    {
        T component = GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
    }
}
