using System.Collections;
using System.Collections.Generic;
using SharedModels.Game;
using SharedModels.WebCommunication.DataTransferObjects;
using UnityEngine;

/// <summary>
/// Stands in for the game server while testing on a single headset. Like the server's
/// GameManager, it owns the authoritative <see cref="CubeGameMaster"/>, starts the game, and
/// decides what each player is sent. Inbound messages are handled by <see cref="MessageHandler"/>.
///
/// The client (<see cref="CubeGameStateManager"/> + <see cref="ServerConnection"/>) runs on the
/// same headset but only reaches this server over a local TCP connection, so pointing
/// <see cref="ServerConnection"/> at a real server needs no changes on the client.
/// </summary>
public class OfflineGameManager : MonoBehaviour
{
    /// <summary>The players in the offline game: just the local headset.</summary>
    static readonly IReadOnlyList<int> PlayerIDs = new[] { 0 };

    [SerializeField] GrabbableCubeManager _cubeManager;
    [SerializeField] MessageHandler _messageHandler;

    [SerializeField] float _spawnDistance = 1.0f;

    [Tooltip("Empty space between the two starting cubes (meters).")]
    [SerializeField] float _cubeGap = 0.25f;

    [Tooltip("Seconds to wait for a valid head pose before spawning anyway.")]
    [SerializeField] float _headPoseTimeout = 2f;

    OVRCameraRig _rig;

    /// <summary>The authoritative game state; null until the game starts.</summary>
    public CubeGameMaster GameMaster { get; private set; }

    #region Unity Functions

    void Awake()
    {
        if (_cubeManager == null)
            _cubeManager = GetComponent<GrabbableCubeManager>();
        if (_messageHandler == null)
            _messageHandler = GetOrAddComponent<MessageHandler>();

        // Offline, this headset is also the client. Add its components if the scene doesn't have
        // them; they talk to this server only through the socket. The state manager goes first,
        // because the connection looks it up when added.
        GetOrAddComponent<CubeGameStateManager>();
        GetOrAddComponent<ServerConnection>();
    }

    IEnumerator Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig == null)
        {
            Debug.LogError($"{nameof(OfflineGameManager)}: no OVRCameraRig found in the scene.");
            yield break;
        }

        // The head anchor sits at the origin until the first tracked pose arrives.
        float deadline = Time.time + _headPoseTimeout;
        while (_rig.centerEyeAnchor.localPosition == Vector3.zero && Time.time < deadline)
            yield return null;

        InitializeGame();
    }

    #endregion Unity Functions

    #region Initialize Game

    /// <summary>
    /// Builds the authoritative game state and sends it to every connected player. Players who
    /// connect later get it in <see cref="OnPlayerConnected"/>.
    /// </summary>
    void InitializeGame()
    {
        var cubes = new List<CubeState>();
        Pose[] startingPoses = GetStartingCubePoses();
        for (int i = 0; i < startingPoses.Length; i++)
            cubes.Add(new CubeState($"cube-{i}", startingPoses[i], PlayerIDs));

        GameMaster = new CubeGameMaster(isAuthoritative: true);
        GameMaster.ServerInitialize(PlayerIDs, cubes);

        BroadcastGameState();
    }

    /// <summary>Two cubes side by side, <see cref="_spawnDistance"/> in front of the user.</summary>
    Pose[] GetStartingCubePoses()
    {
        Transform head = _rig.centerEyeAnchor;
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f)
            forward = Vector3.ProjectOnPlane(head.up, Vector3.up); // looking straight up/down
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward);
        Vector3 center = head.position + forward * _spawnDistance;
        Vector3 halfSpacing = right * (_cubeManager.CubeSize + _cubeGap) * 0.5f;
        Quaternion rotation = Quaternion.LookRotation(forward);

        return new[]
        {
            new Pose(center - halfSpacing, rotation),
            new Pose(center + halfSpacing, rotation),
        };
    }

    #endregion Initialize Game

    #region Players

    public bool IsPlayer(int playerID)
    {
        foreach (int id in PlayerIDs)
        {
            if (id == playerID)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A player joined (or rejoined): send them the whole game state if the game has started.
    /// </summary>
    public void OnPlayerConnected(int playerID)
    {
        if (GameMaster != null)
            AddOutgoingDTO(playerID, new GameStateDTO(GameMaster));
    }

    /// <summary>
    /// A player dropped: free every cube they were holding so others can grab it.
    /// </summary>
    public void OnPlayerDisconnected(int playerID)
    {
        if (GameMaster == null)
            return;

        bool changed = false;
        foreach (CubeState cube in GameMaster.Cubes.Values)
        {
            if (cube.HolderPlayerID == playerID)
                changed |= GameMaster.RevokeGrabPermissions(cube.Id);
        }

        if (changed)
            BroadcastGameState();
    }

    #endregion Players

    #region Process DTOs

    /// <summary>
    /// Sends a DTO to a player.
    /// </summary>
    /// <returns>True if the player is connected and the message was sent.</returns>
    public bool AddOutgoingDTO(int playerID, object outgoingDTO) => _messageHandler.SendObject(playerID, outgoingDTO);

    void BroadcastGameState()
    {
        foreach (int playerID in PlayerIDs)
            AddOutgoingDTO(playerID, new GameStateDTO(GameMaster));
    }

    #endregion Process DTOs

    #region Testing

    /// <summary>
    /// Takes every cube away from whoever holds it, as the server could at any time, and pushes the
    /// new state to the players. Right-click the component header in Play mode to use it.
    /// </summary>
    [ContextMenu("Revoke All Grab Permissions")]
    void RevokeAllGrabPermissions()
    {
        if (GameMaster == null)
            return;

        foreach (string cubeID in GameMaster.Cubes.Keys)
            GameMaster.RevokeGrabPermissions(cubeID);

        BroadcastGameState();
    }

    #endregion Testing

    T GetOrAddComponent<T>() where T : Component
    {
        T component = GetComponent<T>();
        return component != null ? component : gameObject.AddComponent<T>();
    }
}
