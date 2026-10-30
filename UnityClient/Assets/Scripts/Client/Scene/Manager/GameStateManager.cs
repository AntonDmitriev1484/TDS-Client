using Assets.Scripts.Utilities;
using Client.Communication.Manager;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using SharedLibrary.Utilities;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;


namespace Client.Scene.Manager
{
    /// <summary>
    /// Manages the game state from the client's side. Keeps a non-authoritative
    /// <see cref="GameState"/> in sync with the server and makes the scene reflect it.
    ///
    /// Our hand input (pinches from <see cref="GrabbableCubeManager"/>) becomes a
    /// <see cref="SingleAction"/> that is applied locally straight away and then sent to the server
    /// through <see cref="OnOutgoingDTO"/>. If the server disagrees, it answers with a
    /// <see cref="GameStateDTO"/>, and any cube we no longer have grab permission for is dropped.
    /// </summary>
    public class GameStateManager : MonoBehaviour
    {
        [SerializeField] GrabbableCubeManager _cubeManager;
        [SerializeField] GameBoardView _boardView;
        [SerializeField] OVRHand _rightHand;
        [SerializeField] OVRHand _leftHand;

        [Tooltip("Minimum seconds between pose updates sent for a cube we are holding.")]
        [SerializeField] float _poseSendInterval = 0.05f;

        [Tooltip("A held cube must move or turn at least this much before a new pose is sent (meters / degrees).")]
        [SerializeField] float _poseSendThreshold = 0.001f;

        // The next time to send a pose. 
        float _nextPoseSendTime;


        // DTOs arrive on the WebSocket thread; enqueued here and applied on the main thread in Update.
        readonly ConcurrentQueue<SingleActionDTO> _incomingSingleActions = new ConcurrentQueue<SingleActionDTO>();
        GameStateDTO _latestGameStateDTO = null;
        readonly object _gameStateLock = new object();

        // Pending actions that have not yet been confirmed by the server.
        readonly Dictionary<string, List<PendingAction>> _pendingActionsByCube = new Dictionary<string, List<PendingAction>>();

        // The latest authoritative game state from the server.
        GameState _authoritativeGameState;

        // Logical clock for client's actions.
        long _nextClientLogicalTimestamp = 1;



        // Estimated latency between the client and server.
        float _estimatedLatency => Mathf.Max(0f, ConnectionManager.Instance.GetConnectionLatency(ConnectionType.Game));
        float _estimatedRoundTripTime => _estimatedLatency * 2f;
        double _estimatedServerTime => GameState.CurrentAppliedOffset + TimeUtilities.GetTime();

        // The actual gamestate, as rendered for the user.
        public GameState GameState { get; private set; }

        // Dictionary storing the most recent cube state for updated cubes (to be updated per frame).
        public ConcurrentDictionary<string, CubeState> UpdatedCubes = new();

        // The PlayerID of this client.
        public int LocalPlayerID { get; private set; } = -1;

        /// <summary>
        /// Raised with every DTO this client sends to the server. Whatever carries messages to the
        /// server (<see cref="ServerConnection"/>) subscribes to it.
        /// </summary>
        public event Action<object> OnOutgoingDTO;

        #region Unity Functions

        void Awake()
        {
            if (_cubeManager == null)
                _cubeManager = GetComponent<GrabbableCubeManager>();
            if (_cubeManager == null)
                _cubeManager = FindFirstObjectByType<GrabbableCubeManager>();
            if (_boardView == null)
                _boardView = GetComponent<GameBoardView>();
            if (_boardView == null)
                _boardView = gameObject.AddComponent<GameBoardView>();

            foreach (OVRHand hand in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            {
                if (hand.GetHand() == OVRPlugin.Hand.HandRight)
                {
                    _rightHand = hand;
                }
                else if (hand.GetHand() == OVRPlugin.Hand.HandLeft)
                {
                    _leftHand = hand;
                }
            }

            if (_rightHand == null)
                Debug.LogError($"{nameof(GameStateManager)}: no right hand OVRHand found in the scene.");
            if (_leftHand == null)
                Debug.LogError($"{nameof(GameStateManager)}: no left hand OVRHand found in the scene.");

            GameState = new GameState(isAuthoritative: false);
            _authoritativeGameState = new GameState(isAuthoritative: false);
            GameState.OnCubeAdded += HandleCubeAdded;
            GameState.OnCubeRemoved += HandleCubeRemoved;
            GameState.OnGrabPermissionsChanged += FlagCubeStateChanged;
            GameState.OnCubePoseChanged += FlagCubeStateChanged;
        }

        void OnEnable()
        {
            _cubeManager.PinchStarted += OnPinchStarted;
            _cubeManager.PinchEnded += OnPinchEnded;
        }

        void OnDisable()
        {
            _cubeManager.PinchStarted -= OnPinchStarted;
            _cubeManager.PinchEnded -= OnPinchEnded;
        }

        IEnumerator Start()
        {
            // Wait for ConnectionManager to finish initializing before subscribing to its events.
            while (ConnectionManager.Instance == null || !ConnectionManager.Instance.IsInitialized)
                yield return null;

            var cm = ConnectionManager.Instance;
            Initialize(cm.PlayerID);

            if (cm.InboundMessagesDict.TryGetValue(typeof(GameStateDTO), out var gsEvent))
                ((UnityEvent<GameStateDTO>)gsEvent).AddListener(OnGameStateDTOReceived);
            if (cm.InboundMessagesDict.TryGetValue(typeof(SingleActionDTO), out var saEvent))
                ((UnityEvent<SingleActionDTO>)saEvent).AddListener(OnSingleActionDTOReceived);
        }

        void OnDestroy()
        {
            var cm = ConnectionManager.Instance;
            if (cm == null || cm.InboundMessagesDict == null) return;

            if (cm.InboundMessagesDict.TryGetValue(typeof(GameStateDTO), out var gsEvent))
                ((UnityEvent<GameStateDTO>)gsEvent).RemoveListener(OnGameStateDTOReceived);
            if (cm.InboundMessagesDict.TryGetValue(typeof(SingleActionDTO), out var saEvent))
                ((UnityEvent<SingleActionDTO>)saEvent).RemoveListener(OnSingleActionDTOReceived);
        }

        void Update()
        {
            GameStateDTO gameStateToProcess = null;
            lock (_gameStateLock)
            {
                gameStateToProcess = _latestGameStateDTO;
                _latestGameStateDTO = null;
            }
            if (gameStateToProcess != null)
                HandleIncomingMessage(gameStateToProcess);

            while (_incomingSingleActions.TryDequeue(out var action))
                HandleIncomingMessage(action);

            var snapshot = Interlocked.Exchange(
                ref UpdatedCubes,
                new ConcurrentDictionary<string, CubeState>());


            foreach (CubeState cubeState in snapshot.Values)
            {
                HandleCubeUpdated(cubeState);
            }

        }

        void LateUpdate()
        {
            SendHeldCubePoses();
        }

        #endregion Unity Functions

        /// <summary>Sets which player this client acts for. Call before the first game state arrives.</summary>
        public void Initialize(int localPlayerID)
        {
            LocalPlayerID = localPlayerID;
        }

        #region Handle Incoming Events

        // Called on the WebSocket thread — only enqueue; all Unity API calls happen in Update.
        void OnGameStateDTOReceived(GameStateDTO dto)
        {
            lock (_gameStateLock)
                _latestGameStateDTO = dto; // always replace — only latest matters
        }

        void OnSingleActionDTOReceived(SingleActionDTO dto) => _incomingSingleActions.Enqueue(dto);

        void HandleIncomingMessage(object objDTO)
        {

            if (objDTO is GameStateDTO gameStateDTO)
            {
                string cubeStateString = "";
                foreach (CubeState cubeState in gameStateDTO.Cubes)
                {
                    cubeStateString += $"ID: {cubeState.ID}; permissions: ";
                    foreach (int ID in cubeState.GrabPermissions.Keys)
                    {
                        cubeStateString += $"PlayerID {ID} - {cubeState.GrabPermissions[ID]}, ";
                    }
                    cubeStateString += "\n";
                }

                Debug.Log($"Incoming DTO:\n {cubeStateString}");

                _authoritativeGameState.ApplyGameState(_estimatedRoundTripTime, gameStateDTO);
                AcknowledgeAppliedActions(gameStateDTO.PlayerLogicalTimestamps);
                ApplyingPredictedState();
                _boardView.Apply(GameState.Board, GameState.Goals.Values, GameState.Scores);
            }
            else if (objDTO is SingleActionDTO singleActionDTO)
            {
                // The server already accepted this action, so a rejection here means we are out of sync.
                if (!GameState.HandleSingleAction(singleActionDTO.Action))
                    Debug.LogWarning($"{nameof(GameStateManager)}: server action rejected locally: {singleActionDTO.Action.FormatActionText()}");
            }
            else
            {
                Debug.LogError($"{nameof(GameStateManager)}: incoming message of type {objDTO.GetType().Name} was never handled!");
            }
        }

        void ApplyingPredictedState()
        {
            GameState.ReplaceFrom(_authoritativeGameState);

            string cubeStateString = "";
            foreach (CubeState cubeState in _authoritativeGameState.Cubes.Values.ToList())
            {
                cubeStateString += $"ID: {cubeState.ID}; permissions: ";
                foreach (int ID in cubeState.GrabPermissions.Keys)
                {
                    cubeStateString += $"PlayerID {ID} - {cubeState.GrabPermissions[ID]}, ";
                }
                cubeStateString += "\n";
            }

            Debug.Log($"Authoritative Game State:\n {cubeStateString}");

            var allPending = _pendingActionsByCube.Values
                .SelectMany(list => list)
                .OrderBy(p => p.ClientLogicalTimestamp);


            foreach (PendingAction pendingAction in allPending)
            {
                if (!GameState.HandleSingleAction(pendingAction.Action))
                    Debug.LogWarning($"{nameof(GameStateManager)}: pending action could not be replayed: {pendingAction.Action.FormatActionText()}");
            }
        }

        void AcknowledgeAppliedActions(Dictionary<int, long> playerLogicalTimestamps)
        {
            if (!playerLogicalTimestamps.ContainsKey(LocalPlayerID))
                return;

            long lastAcknowledgedTimestamp = playerLogicalTimestamps[LocalPlayerID];

            var completedActions = new List<PendingAction>();

            int remainingActionsLength = 0;

            foreach (var pendingActions in _pendingActionsByCube.Values.ToList())
            {
                foreach (PendingAction pendingAction in pendingActions)
                {
                    if (pendingAction.ClientLogicalTimestamp <= lastAcknowledgedTimestamp)
                    {
                        completedActions.Add(pendingAction);
                    }
                    else
                    {
                        remainingActionsLength += 1;
                    }
                }
            }

            foreach (PendingAction completedAction in completedActions)
            {
                RemovePendingAction(completedAction);
                Debug.Log($"Removing completed action with client logical timestamp: {completedAction.ClientLogicalTimestamp} and type: {completedAction.Action.SingleActionType}.");
            }

            Debug.Log($"Pending actions has length: {remainingActionsLength}");
        }

        PendingAction FindPendingAction(long clientLogicalTimestamp)
        {
            foreach (var pendingActions in _pendingActionsByCube.Values)
            {
                foreach (PendingAction pendingAction in pendingActions)
                {
                    if (pendingAction.ClientLogicalTimestamp == clientLogicalTimestamp)
                        return pendingAction;
                }
            }

            return null;
        }

        void RemovePendingAction(PendingAction pendingAction)
        {
            if (!_pendingActionsByCube.TryGetValue(pendingAction.CubeID, out var pendingActions))
                return;

            pendingActions.Remove(pendingAction);
            if (pendingActions.Count == 0)
                _pendingActionsByCube.Remove(pendingAction.CubeID);
        }

        sealed class PendingAction
        {
            public long ClientLogicalTimestamp { get; }
            public SingleAction Action { get; }
            public string CubeID { get; }

            public PendingAction(long clientLogicalTimestamp, SingleAction action, string cubeID)
            {
                ClientLogicalTimestamp = clientLogicalTimestamp;
                Action = action;
                CubeID = cubeID;
            }
        }

        #endregion Handle Incoming Events

        #region Handle Game Master Events

        void HandleCubeAdded(CubeState state)
        {
            _cubeManager.SpawnCube(Conversions.ToUnityPose(state.Pose), state.ID, state.CubeSize);
            FlagCubeStateChanged(state);
        }

        /// <summary>
        /// The server removed a cube (e.g. it scored): destroy it and forget any actions on it.
        /// </summary>
        void HandleCubeRemoved(CubeState state)
        {
            UpdatedCubes.TryRemove(state.ID, out _);
            _pendingActionsByCube.Remove(state.ID);
            _cubeManager.DestroyCube(state.ID);
        }

        /// <summary>
        /// Updates or adds the specified cube state in the collection of updated cubes.
        /// </summary>
        /// <param name="cubeState">The cube state to update or add.</param>
        void FlagCubeStateChanged(CubeState cubeState)
        {
            if (this.UpdatedCubes.ContainsKey(cubeState.ID))
            {
                this.UpdatedCubes[cubeState.ID] = cubeState;
            }
            else
            {
                this.UpdatedCubes.TryAdd(cubeState.ID, cubeState);
            }
        }

        /// <summary>
        /// Handles when a cube's state has been updated.
        /// </summary>
        /// <param name="cubeState">The fresh cube state.</param>
        void HandleCubeUpdated(CubeState cubeState)
        {
            if (!_cubeManager.TryGetCube(cubeState.ID, out var cube))
                return;

            Debug.Log($"Updating cube: {JsonUtilities.ToJson(cubeState)}");

            // Appropriately set grab permissions.
            if (cubeState.HasGrabPermission(LocalPlayerID))
            {
                // If our player is grabbing the block, then show it grabbing properly.
                if (cubeState.GrabPermissions[LocalPlayerID] == CubeFollowTarget.LeftHand && _leftHand != null)
                {
                    cube.Grab(_leftHand.transform);
                }
                else if (cubeState.GrabPermissions[LocalPlayerID] == CubeFollowTarget.RightHand && _rightHand != null)
                {
                    cube.Grab(_rightHand.transform);
                }
            }

            // Still shown as grabbed (without following anything) while another player holds it.
            if (cubeState.HolderPlayerID == null)
            {
                cube.Grabbed = GrabbedState.None;
            }
            else if (cubeState.HolderPlayerID != LocalPlayerID)
            {
                cube.Grabbed = GrabbedState.AnotherPlayer;
            }


            // Set the position if our player isn't holding the cube.
            if (cubeState.HolderPlayerID != LocalPlayerID)
            {
                cube.transform.SetPositionAndRotation(Conversions.ToUnityPosition(cubeState.Pose), Conversions.ToUnityRotation(cubeState.Pose));
            }
        }

        #endregion Handle Game Master Events

        #region Handle Our Actions

        void OnPinchStarted(GrabbableCube cube, OVRHand hand)
        {
            CubeFollowTarget followTarget = CubeFollowTarget.RightHand;
            if (hand.GetHand() == OVRPlugin.Hand.HandLeft)
            {
                followTarget = CubeFollowTarget.LeftHand;
            }

            if (!HandleOurAction(new GrabCubeAction(LocalPlayerID, _estimatedServerTime, cube.Id, followTarget)))
            {
                Debug.LogWarning($"Failed to grab cube {cube.Id} (possibly because we don't have permission).");
                CubeState cubeState = this.GameState.Cubes[cube.Id];
            }
        }

        void OnPinchEnded(GrabbableCube cube, OVRHand hand)
        {
            // Ignore a hand letting go of a cube that the other hand has since taken over, or that
            // was already dropped because we lost permission.
            if (cube.FollowTarget != hand.transform)
                return;

            Debug.Log("Player released cube!");

            HandleOurAction(new ReleaseCubeAction(LocalPlayerID, _estimatedServerTime, cube.Id));
        }

        /// <summary>Sends the pose of every cube one of our hands is carrying, throttled.</summary>
        void SendHeldCubePoses()
        {
            if (Time.time < _nextPoseSendTime)
                return;
            _nextPoseSendTime = Time.time + _poseSendInterval;

            foreach (var cube in _cubeManager.Cubes.Values)
            {
                if (cube.FollowTarget == null || !GameState.Cubes.TryGetValue(cube.Id, out var state))
                    continue;



                Transform t = cube.transform;
                if (Vector3.Distance(t.position, Conversions.ToUnityPosition(state.Pose)) < _poseSendThreshold &&
                    Quaternion.Angle(t.rotation, Conversions.ToUnityRotation(state.Pose)) < _poseSendThreshold)
                    continue;

                HandleOurAction(new MoveCubeAction(LocalPlayerID, _estimatedServerTime, cube.Id, Conversions.ToSharedPose(t.position, t.rotation)));
            }
        }

        /// <summary>
        /// Applies one of our actions to the local game state and, if it is accepted, sends it to the server.
        /// </summary>
        /// <returns>True if the action was accepted locally.</returns>
        bool HandleOurAction(SingleAction singleAction)
        {
            if (!GameState.HandleSingleAction(singleAction))
                return false;

            if (!(singleAction is CubeAction cubeAction))
                return false;

            var pendingAction = new PendingAction(++_nextClientLogicalTimestamp, singleAction, cubeAction.CubeID);
            if (!_pendingActionsByCube.TryGetValue(cubeAction.CubeID, out var pendingActions))
            {
                pendingActions = new List<PendingAction>();
                _pendingActionsByCube.Add(cubeAction.CubeID, pendingActions);
            }

            if (singleAction is MoveCubeAction)
                pendingActions.RemoveAll(pending => pending.Action is MoveCubeAction);

            pendingActions.Add(pendingAction);

            var dto = new SingleActionDTO(singleAction, pendingAction.ClientLogicalTimestamp);

            // Raise legacy event for any subscribers (e.g., ServerConnection)
            OnOutgoingDTO?.Invoke(dto);

            // If the ConnectionManager is available, send via it as well.
            try
            {
                var cm = ConnectionManager.Instance;
                if (cm != null)
                {
                    // Fire-and-forget send; do not await on the network thread here.
                    _ = cm.SendDTO(ConnectionType.Game, dto);

                    Debug.Log($"Sent SingleAction message with action type: {singleAction.SingleActionType} to server!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{nameof(GameStateManager)}: failed to send DTO via ConnectionManager: {ex.Message}");
            }
            return true;
        }

        #endregion Handle Our Actions
    }
}
