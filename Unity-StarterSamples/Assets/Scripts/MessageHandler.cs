using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SharedModels.Game;
using SharedModels.WebCommunication.DataTransferObjects;
using UnityEngine;

/// <summary>
/// Handles inbound messages for the offline game server, the way the server's GameWebSocketHandler
/// does: it accepts player connections, routes each DTO to its handler, validates it, and applies
/// it to <see cref="OfflineGameManager"/>'s game master. It also keeps the player ID to connection
/// map and does the sending, which the server splits out into WebSocketConnectionManager.
///
/// Players connect over TCP and exchange newline-delimited JSON (one <see cref="DTOSerializer"/>
/// message per line). The first message on a connection must be a <see cref="ConnectDTO"/>.
///
/// Sockets are read on background threads; every message is handled on the main thread in Update.
/// </summary>
public class MessageHandler : MonoBehaviour
{
    [SerializeField] OfflineGameManager _gameManager;
    [SerializeField] int _port = 7777;

    class Connection
    {
        public TcpClient Client;
        public StreamWriter Writer;
        public int PlayerID = -1; // Unknown until its ConnectDTO arrives.
    }

    // A null message means the connection closed.
    readonly ConcurrentQueue<(Connection connection, string message)> _incomingMessages =
        new ConcurrentQueue<(Connection, string)>();

    readonly List<Connection> _connections = new List<Connection>(); // Every open socket; locked.
    readonly Dictionary<int, Connection> _connectionsByPlayerID = new Dictionary<int, Connection>(); // Main thread only.
    TcpListener _listener;
    Thread _acceptThread;
    volatile bool _listening;

    #region Unity Functions

    void Awake()
    {
        if (_gameManager == null)
            _gameManager = GetComponent<OfflineGameManager>();
    }

    void OnEnable()
    {
        StartListening();
    }

    void OnDisable()
    {
        StopListening();
        _connectionsByPlayerID.Clear();
    }

    void Update()
    {
        while (_incomingMessages.TryDequeue(out var item))
        {
            if (item.message == null)
                HandleDisconnect(item.connection);
            else
                HandleMessage(item.connection, item.message);
        }
    }

    #endregion Unity Functions

    #region Handle Connections

    void StartListening()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
        }
        catch (SocketException e)
        {
            Debug.LogError($"{nameof(MessageHandler)}: could not listen on port {_port}: {e.Message}");
            _listener = null;
            return;
        }

        _listening = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MessageHandler accept" };
        _acceptThread.Start();
        Debug.Log($"{nameof(MessageHandler)}: listening for players on TCP port {_port}.");
    }

    void StopListening()
    {
        _listening = false;
        _listener?.Stop(); // Unblocks AcceptTcpClient.
        _listener = null;

        lock (_connections)
        {
            foreach (var connection in _connections)
                connection.Client.Close(); // Unblocks ReadLine in each connection thread.
            _connections.Clear();
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
            catch (ObjectDisposedException)
            {
                break;
            }

            var connection = new Connection
            {
                Client = client,
                // No BOM: the other end parses each line as JSON.
                Writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true },
            };

            lock (_connections)
                _connections.Add(connection);
            new Thread(() => ReceiveLoop(connection)) { IsBackground = true, Name = "MessageHandler connection" }.Start();
        }
    }

    void ReceiveLoop(Connection connection)
    {
        try
        {
            using var reader = new StreamReader(connection.Client.GetStream(), Encoding.UTF8);
            string line;
            while (_listening && (line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _incomingMessages.Enqueue((connection, line));
            }
        }
        catch (IOException) { } // Connection dropped or closed during shutdown.
        catch (ObjectDisposedException) { }
        finally
        {
            lock (_connections)
                _connections.Remove(connection);
            connection.Client.Close();
            _incomingMessages.Enqueue((connection, null));
        }
    }

    void HandleConnect(Connection connection, ConnectDTO connectDTO)
    {
        int playerID = connectDTO.PlayerID;

        if (connection.PlayerID >= 0)
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: player {connection.PlayerID} sent a second ConnectDTO.");
            return;
        }

        if (!_gameManager.IsPlayer(playerID))
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: player {playerID} is not in this game; closing the connection.");
            connection.Client.Close();
            return;
        }

        // The newest connection wins, as when a client reconnects before its old socket times out.
        if (_connectionsByPlayerID.TryGetValue(playerID, out var oldConnection))
            oldConnection.Client.Close();

        connection.PlayerID = playerID;
        _connectionsByPlayerID[playerID] = connection;
        Debug.Log($"{nameof(MessageHandler)}: player {playerID} connected.");

        _gameManager.OnPlayerConnected(playerID);
    }

    void HandleDisconnect(Connection connection)
    {
        int playerID = connection.PlayerID;

        // Ignore sockets that never said who they were, or that a newer connection replaced.
        if (playerID < 0 || !_connectionsByPlayerID.TryGetValue(playerID, out var current) || current != connection)
            return;

        _connectionsByPlayerID.Remove(playerID);
        Debug.Log($"{nameof(MessageHandler)}: player {playerID} disconnected.");

        _gameManager.OnPlayerDisconnected(playerID);
    }

    #endregion Handle Connections

    #region Process DTOs

    /// <summary>
    /// Handle an incoming message.
    /// </summary>
    /// <param name="connection">The connection that received the message.</param>
    /// <param name="message">One line of JSON, as written by <see cref="DTOSerializer"/>.</param>
    void HandleMessage(Connection connection, string message)
    {
        object objDTO;
        try
        {
            objDTO = DTOSerializer.Deserialize(message);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: invalid message format: {ex.Message}");
            return;
        }

        if (objDTO is ConnectDTO connectDTO)
        {
            HandleConnect(connection, connectDTO);
            return;
        }

        if (connection.PlayerID < 0)
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: {objDTO.GetType().Name} arrived before the connection's ConnectDTO.");
            return;
        }

        if (objDTO is SingleActionDTO singleActionDTO)
            ProcessSingleAction(connection.PlayerID, singleActionDTO);
        else
            Debug.LogWarning($"{nameof(MessageHandler)}: data type {objDTO.GetType().Name} is not supported.");
    }

    /// <summary>
    /// Processes an incoming single action DTO. Accepted actions go to the other players; a rejected
    /// one gets the sender a full game state so their client can correct itself.
    /// </summary>
    /// <param name="playerID">The player the connection belongs to.</param>
    /// <param name="actionDTO">The single action DTO.</param>
    void ProcessSingleAction(int playerID, SingleActionDTO actionDTO)
    {
        CubeGameMaster gameMaster = _gameManager.GameMaster;

        if (gameMaster == null)
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: action from player {playerID} arrived before the game started.");
            return;
        }

        if (actionDTO.Action.PlayerID != playerID)
        {
            Debug.LogWarning($"{nameof(MessageHandler)}: DTO's player ID does not match the connection's player ID.");
            _gameManager.AddOutgoingDTO(playerID, new GameStateDTO(gameMaster));
            return;
        }

        if (!gameMaster.HandleSingleAction(actionDTO.Action))
        {
            Debug.Log($"{nameof(MessageHandler)}: rejected {actionDTO.Action.FormatActionText()}");
            _gameManager.AddOutgoingDTO(playerID, new GameStateDTO(gameMaster));
            return;
        }

        // Send info about the action to the other players.
        foreach (int otherPlayerID in gameMaster.PlayerIDs)
        {
            if (otherPlayerID != playerID)
                _gameManager.AddOutgoingDTO(otherPlayerID, actionDTO);
        }
    }

    #endregion Process DTOs

    #region Send

    /// <summary>
    /// Sends a DTO to a player.
    /// </summary>
    /// <returns>True if the player is connected and the message was written.</returns>
    public bool SendObject(int playerID, object outgoingDTO)
    {
        if (!_connectionsByPlayerID.TryGetValue(playerID, out var connection))
            return false;

        try
        {
            connection.Writer.WriteLine(DTOSerializer.Serialize(outgoingDTO));
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            connection.Client.Close(); // Its receive thread reports the disconnect.
            return false;
        }
    }

    #endregion Send
}
