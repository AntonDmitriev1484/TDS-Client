using System;
using System.Collections.Concurrent;
using SharedModels.Game;
using SharedModels.Game.Actions;
using SharedModels.WebCommunication.DataTransferObjects;
using UnityEngine;

/// <summary>
/// Manages the game state from the client's side. Keeps a non-authoritative
/// <see cref="CubeGameMaster"/> in sync with the server and makes the scene reflect it.
///
/// Our hand input (pinches from <see cref="GrabbableCubeManager"/>) becomes a
/// <see cref="SingleAction"/> that is applied locally straight away and then sent to the server
/// through <see cref="OnOutgoingDTO"/>. If the server disagrees, it answers with a
/// <see cref="GameStateDTO"/>, and any cube we no longer have grab permission for is dropped.
/// </summary>
public class CubeGameStateManager : MonoBehaviour
{
    [SerializeField] GrabbableCubeManager _cubeManager;

    [Tooltip("Minimum seconds between pose updates sent for a cube we are holding.")]
    [SerializeField] float _poseSendInterval = 0.05f;

    [Tooltip("A held cube must move or turn at least this much before a new pose is sent (meters / degrees).")]
    [SerializeField] float _poseSendThreshold = 0.001f;

    // DTOs may arrive on a network thread; they are applied on the main thread in Update.
    readonly ConcurrentQueue<object> _incomingMessages = new ConcurrentQueue<object>();
    float _nextPoseSendTime;

    public CubeGameMaster GameMaster { get; private set; }

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

        GameMaster = new CubeGameMaster(isAuthoritative: false);
        GameMaster.OnCubeAdded += HandleCubeAdded;
        GameMaster.OnGrabPermissionsChanged += HandleGrabPermissionsChanged;
        GameMaster.OnCubePoseChanged += HandleCubePoseChanged;
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

    void Update()
    {
        while (_incomingMessages.TryDequeue(out var message))
            HandleIncomingMessage(message);
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

    /// <summary>Queues a DTO from the server. Safe to call from any thread.</summary>
    public void EnqueueIncomingMessage(object objDTO) => _incomingMessages.Enqueue(objDTO);

    void HandleIncomingMessage(object objDTO)
    {
        if (objDTO is GameStateDTO gameStateDTO)
        {
            GameMaster.ApplyGameState(gameStateDTO);
        }
        else if (objDTO is SingleActionDTO singleActionDTO)
        {
            // The server already accepted this action, so a rejection here means we are out of sync.
            if (!GameMaster.HandleSingleAction(singleActionDTO.Action))
                Debug.LogWarning($"{nameof(CubeGameStateManager)}: server action rejected locally: {singleActionDTO.Action.FormatActionText()}");
        }
        else
        {
            Debug.LogError($"{nameof(CubeGameStateManager)}: incoming message of type {objDTO.GetType().Name} was never handled!");
        }
    }

    #endregion Handle Incoming Events

    #region Handle Game Master Events

    void HandleCubeAdded(CubeState state)
    {
        _cubeManager.SpawnCube(state.Pose, state.Id);
        HandleGrabPermissionsChanged(state);
    }

    /// <summary>
    /// Makes the cube's grabbed state match its permissions. If our own entry isn't true, the cube
    /// is dropped even if we are still pinching it.
    /// </summary>
    void HandleGrabPermissionsChanged(CubeState state)
    {
        if (!_cubeManager.TryGetCube(state.Id, out var cube))
            return;

        if (!state.HasGrabPermission(LocalPlayerID))
            cube.Grabbed = false; // Forgets the hand, so the cube stops following it.

        // Still shown as grabbed (without following anything) while another player holds it.
        cube.Grabbed = state.IsHeld;
    }

    void HandleCubePoseChanged(CubeState state)
    {
        // A cube following one of our hands is already where it should be.
        if (!_cubeManager.TryGetCube(state.Id, out var cube) || cube.FollowTarget != null)
            return;

        cube.transform.SetPositionAndRotation(state.Position, state.Rotation);
    }

    #endregion Handle Game Master Events

    #region Handle Our Actions

    void OnPinchStarted(GrabbableCube cube, OVRHand hand)
    {
        if (HandleOurAction(new GrabCubeAction(LocalPlayerID, cube.Id)))
            cube.Grab(hand.transform);
    }

    void OnPinchEnded(GrabbableCube cube, OVRHand hand)
    {
        // Ignore a hand letting go of a cube that the other hand has since taken over, or that
        // was already dropped because we lost permission.
        if (cube.FollowTarget != hand.transform)
            return;

        HandleOurAction(new ReleaseCubeAction(LocalPlayerID, cube.Id));
    }

    /// <summary>Sends the pose of every cube one of our hands is carrying, throttled.</summary>
    void SendHeldCubePoses()
    {
        if (Time.time < _nextPoseSendTime)
            return;
        _nextPoseSendTime = Time.time + _poseSendInterval;

        foreach (var cube in _cubeManager.Cubes.Values)
        {
            if (cube.FollowTarget == null || !GameMaster.Cubes.TryGetValue(cube.Id, out var state))
                continue;

            Transform t = cube.transform;
            if (Vector3.Distance(t.position, state.Position) < _poseSendThreshold &&
                Quaternion.Angle(t.rotation, state.Rotation) < _poseSendThreshold)
                continue;

            HandleOurAction(new MoveCubeAction(LocalPlayerID, cube.Id, new Pose(t.position, t.rotation)));
        }
    }

    /// <summary>
    /// Applies one of our actions to the local game state and, if it is accepted, sends it to the server.
    /// </summary>
    /// <returns>True if the action was accepted locally.</returns>
    bool HandleOurAction(SingleAction singleAction)
    {
        if (!GameMaster.HandleSingleAction(singleAction))
            return false;

        OnOutgoingDTO?.Invoke(new SingleActionDTO(singleAction));
        return true;
    }

    #endregion Handle Our Actions
}
