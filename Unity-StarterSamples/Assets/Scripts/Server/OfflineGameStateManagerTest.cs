using System.Collections;
using System.Collections.Generic;
using SharedModels.Game;
using SharedModels.WebCommunication.DataTransferObjects;
using UnityEngine;

/// <summary>
/// <see cref="OfflineGameStateManager"/> plus a scripted test of the client/server sync. The game
/// starts the same way: two cubes, one player. Then, whenever the player has held a cube for
/// <see cref="_holdDuration"/> seconds, the server revokes their grab permission and moves the cube
/// along its own +x axis for <see cref="_moveDuration"/> seconds at <see cref="_moveSpeed"/> m/s.
///
/// If the sync works, the client drops the cube when permission is revoked, then sees it slide away.
///
/// Use it in place of <see cref="OfflineGameStateManager"/> on the same GameObject;
/// <see cref="MessageHandler"/> finds it as one.
/// </summary>
public class OfflineGameStateManagerTest : OfflineGameStateManager
{
    [Header("Test")]
    [Tooltip("Seconds the player may hold a cube before the server takes it away.")]
    [SerializeField] float _holdDuration = 1.5f;

    [Tooltip("Seconds the server then moves the cube for.")]
    [SerializeField] float _moveDuration = 3f;

    [Tooltip("Speed of the server's move along the cube's +x axis (m/s).")]
    [SerializeField] float _moveSpeed = 0.2f;

    [Tooltip("Game state updates per second sent to the players while the server moves a cube.")]
    [SerializeField] float _sendRate = 30f;

    readonly Dictionary<string, float> _grabStartTimes = new Dictionary<string, float>(); // Cube ID -> when it was grabbed.
    readonly HashSet<string> _serverMovedCubes = new HashSet<string>();

    void Update()
    {
        if (GameState == null)
            return;

        foreach (CubeState cube in GameState.Cubes.Values)
        {
            if (!cube.IsHeld || _serverMovedCubes.Contains(cube.Id))
            {
                _grabStartTimes.Remove(cube.Id);
                continue;
            }

            if (!_grabStartTimes.TryGetValue(cube.Id, out float grabStartTime))
            {
                _grabStartTimes[cube.Id] = Time.time;
                continue;
            }

            if (Time.time - grabStartTime >= _holdDuration)
            {
                _grabStartTimes.Remove(cube.Id);
                StartCoroutine(RevokeAndMoveCube(cube));
            }
        }
    }

    /// <summary>
    /// Takes the cube from its holder, then moves it along its +x axis, sending the players the
    /// game state as it goes.
    /// </summary>
    IEnumerator RevokeAndMoveCube(CubeState cube)
    {
        _serverMovedCubes.Add(cube.Id);
        Debug.Log($"{nameof(OfflineGameStateManagerTest)}: player {cube.HolderPlayerID} held {cube.Id} for {_holdDuration}s; revoking and moving it.");

        GameState.RevokeGrabPermissions(cube.Id);
        SendGameState();

        Vector3 startPosition = cube.Position;
        Vector3 direction = cube.Rotation * Vector3.right;
        float startTime = Time.time;
        float nextSendTime = startTime;

        while (true)
        {
            float elapsed = Mathf.Min(Time.time - startTime, _moveDuration);
            bool finished = elapsed >= _moveDuration;

            // The server keeps the cube for the whole move, even if the player grabs it again.
            GameState.RevokeGrabPermissions(cube.Id);

            cube.Position = startPosition + direction * (_moveSpeed * elapsed);

            if (finished || Time.time >= nextSendTime)
            {
                SendGameState();
                nextSendTime = Time.time + 1f / _sendRate;
            }

            if (finished)
                break;

            yield return null;
        }

        _serverMovedCubes.Remove(cube.Id);
        Debug.Log($"{nameof(OfflineGameStateManagerTest)}: finished moving {cube.Id}; it can be grabbed again.");
    }

    void SendGameState()
    {
        foreach (int playerID in GameState.PlayerIDs)
            AddOutgoingDTO(playerID, new GameStateDTO(GameState));
    }
}
