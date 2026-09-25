using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Owns the game state. Spawns the cubes, decides when a cube is grabbed, and hosts the TCP
/// endpoint that a server will later use to drive that state.
///
/// Hand input arrives from <see cref="GrabbableCubeManager"/> as pinch events. For now a pinch
/// grabs the cube directly; once a server is in charge, the pinch handlers should forward the
/// request instead, and <see cref="OnPacket"/> should set <see cref="GrabbableCube.Grabbed"/>.
/// </summary>
public class GameManager : MonoBehaviour
{
    [SerializeField] GrabbableCubeManager _cubeManager;

    [SerializeField] float _spawnDistance = 1.0f;

    [Tooltip("Empty space between the two starting cubes (meters).")]
    [SerializeField] float _cubeGap = 0.25f;

    [Tooltip("Seconds to wait for a valid head pose before spawning anyway.")]
    [SerializeField] float _headPoseTimeout = 2f;

    [Header("Network")]
    [SerializeField] bool _listenForPackets = true;
    [SerializeField] int _port = 7777;

    OVRCameraRig _rig;

    // Packets are read on background threads and handed to the main thread through this queue.
    readonly ConcurrentQueue<string> _incomingPackets = new ConcurrentQueue<string>();
    readonly List<TcpClient> _clients = new List<TcpClient>();
    TcpListener _listener;
    Thread _acceptThread;
    volatile bool _listening;

    void Awake()
    {
        if (_cubeManager == null)
            _cubeManager = GetComponent<GrabbableCubeManager>();
    }

    void OnEnable()
    {
        _cubeManager.PinchStarted += OnPinchStarted;
        _cubeManager.PinchEnded += OnPinchEnded;
        if (_listenForPackets)
            StartListening();
    }

    void OnDisable()
    {
        _cubeManager.PinchStarted -= OnPinchStarted;
        _cubeManager.PinchEnded -= OnPinchEnded;
        StopListening();
    }

    IEnumerator Start()
    {
        _rig = FindFirstObjectByType<OVRCameraRig>();
        if (_rig == null)
        {
            Debug.LogError($"{nameof(GameManager)}: no OVRCameraRig found in the scene.");
            yield break;
        }

        // The head anchor sits at the origin until the first tracked pose arrives.
        float deadline = Time.time + _headPoseTimeout;
        while (_rig.centerEyeAnchor.localPosition == Vector3.zero && Time.time < deadline)
            yield return null;

        SpawnStartingCubes();
    }

    /// <summary>Two cubes side by side, <see cref="_spawnDistance"/> in front of the user.</summary>
    void SpawnStartingCubes()
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

        _cubeManager.SpawnCube(new Pose(center - halfSpacing, rotation));
        _cubeManager.SpawnCube(new Pose(center + halfSpacing, rotation));
    }

    void OnPinchStarted(GrabbableCube cube, OVRHand hand)
    {
        // Local rule for now: a pinch grabs. A server may later veto or grant this instead.
        cube.Grab(hand.transform);
    }

    void OnPinchEnded(GrabbableCube cube, OVRHand hand)
    {
        // Ignore a hand letting go of a cube that the other hand has since taken over.
        if (cube.FollowTarget != hand.transform)
            return;

        cube.Grabbed = false;
    }

    void Update()
    {
        while (_incomingPackets.TryDequeue(out var json))
            OnPacket(json);
    }

    /// <summary>
    /// Called on the main thread once for every complete JSON message received over TCP.
    /// Messages are newline-delimited: one JSON object per line.
    /// </summary>
    void OnPacket(string json)
    {
        // TODO: parse the message and apply it to the game state.
    }

    void StartListening()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
        }
        catch (SocketException e)
        {
            Debug.LogError($"{nameof(GameManager)}: could not listen on port {_port}: {e.Message}");
            _listener = null;
            return;
        }

        _listening = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "GameManager TCP accept" };
        _acceptThread.Start();
        Debug.Log($"{nameof(GameManager)}: listening for JSON packets on TCP port {_port}.");
    }

    void StopListening()
    {
        _listening = false;
        _listener?.Stop(); // Unblocks AcceptTcpClient.
        _listener = null;

        lock (_clients)
        {
            foreach (var client in _clients)
                client.Close(); // Unblocks ReadLine in each client thread.
            _clients.Clear();
        }
    }

    void AcceptLoop()
    {
        while (_listening)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break; // Listener stopped.
            }
            catch (System.ObjectDisposedException)
            {
                break;
            }

            lock (_clients)
                _clients.Add(client);
            new Thread(() => ReceiveLoop(client)) { IsBackground = true, Name = "GameManager TCP client" }.Start();
        }
    }

    void ReceiveLoop(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            string line;
            while (_listening && (line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _incomingPackets.Enqueue(line);
            }
        }
        catch (IOException) { } // Connection dropped or closed during shutdown.
        catch (System.ObjectDisposedException) { }
        finally
        {
            lock (_clients)
                _clients.Remove(client);
            client.Close();
        }
    }
}
