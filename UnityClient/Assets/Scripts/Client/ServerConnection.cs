using SharedLibrary.Communication.DataTransferObjects;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// The client's connection to the game server. Sends the DTOs that
/// <see cref="GameStateManager"/> produces and hands it the DTOs the server sends back.
///
/// Talks newline-delimited JSON over TCP (see <see cref="DTOSerializer"/>), opening every connection
/// with a <see cref="ConnectDTO"/>. Reconnects on its own if the connection drops.
/// </summary>
public class ServerConnection : MonoBehaviour
{
    [SerializeField] GameStateManager _client;

    [SerializeField] string _host = "";
    [SerializeField] int _port = 5000;
    [SerializeField] int _playerID = 0;

    [Tooltip("Seconds to wait before trying to connect again.")]
    [SerializeField] float _reconnectDelay = 1f;

    readonly object _sendLock = new object();
    TcpClient _tcp;       // Guarded by _sendLock.
    StreamWriter _writer; // Guarded by _sendLock; null while disconnected.
    Thread _connectionThread;
    volatile bool _running;

    [Serializable]
    class ServerConfig
    {
        public string host;
        public int port;
        public float reconnectDelay;
    }

    void Awake()
    {
        if (_client == null)
            _client = GetComponent<GameStateManager>();
    }

    /// <summary>
    /// Attempts to read a JSON config file from StreamingAssets/serverconfig.json and override
    /// the serialized host/port/playerID/reconnectDelay values if present.
    /// </summary>
    void LoadConfigFromStreamingAssets()
    {
        try
        {
            string configPath = Path.Combine(Application.streamingAssetsPath, "serverconfig.json");
            if (!File.Exists(configPath))
            {
                Debug.Log($"{nameof(ServerConnection)}: no config file at {configPath}; using serialized values.");
                return;
            }

            string json = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                Debug.LogWarning($"{nameof(ServerConnection)}: config file {configPath} is empty; using serialized values.");
                return;
            }

            var cfg = JsonUtility.FromJson<ServerConfig>(json);
            if (cfg == null)
            {
                Debug.LogWarning($"{nameof(ServerConnection)}: failed to parse config file {configPath}; using serialized values.");
                return;
            }

            if (!string.IsNullOrEmpty(cfg.host))
                _host = cfg.host;
            if (cfg.port != 0)
                _port = cfg.port;
            if (cfg.reconnectDelay > 0f)
                _reconnectDelay = cfg.reconnectDelay;

            Debug.Log($"{nameof(ServerConnection)}: loaded config from {configPath} -> host={_host}, port={_port}, playerID={_playerID}, reconnectDelay={_reconnectDelay}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{nameof(ServerConnection)}: error reading config: {ex.Message}; using serialized values.");
        }
    }

    void OnEnable()
    {
        // Try to load host/port from a config file in StreamingAssets before using serialized values.
        LoadConfigFromStreamingAssets();

        _client.Initialize(_playerID);
        _client.OnOutgoingDTO += SendDTO;

        _running = true;
        string host = _host;
        int port = _port;
        int reconnectDelayMs = Mathf.RoundToInt(_reconnectDelay * 1000);
        _connectionThread = new Thread(() => ConnectionLoop(host, port, reconnectDelayMs))
        { IsBackground = true, Name = "ServerConnection" };
        _connectionThread.Start();
    }

    void OnDisable()
    {
        _client.OnOutgoingDTO -= SendDTO;

        _running = false;
        lock (_sendLock)
            _tcp?.Close(); // Unblocks Connect/ReadLine on the connection thread.
    }

    /// <summary>
    /// Connects, reads until the connection drops, and repeats until disabled.
    /// </summary>
    void ConnectionLoop(string host, int port, int reconnectDelayMs)
    {
        bool loggedWaiting = false;

        while (_running)
        {
            var tcp = new TcpClient();
            lock (_sendLock)
                _tcp = tcp;

            try
            {
                tcp.Connect(host, port);
                NetworkStream stream = tcp.GetStream();

                lock (_sendLock)
                {
                    // No BOM: the server parses each line as JSON.
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writer.WriteLine(DTOSerializer.Serialize(new ConnectDTO(_playerID)));
                }

                loggedWaiting = false;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    object objDTO;
                    try
                    {
                        objDTO = DTOSerializer.Deserialize(line);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{nameof(ServerConnection)}: invalid message from server: {ex.Message}");
                        continue;
                    }

                    _client.EnqueueIncomingMessage(objDTO);
                }
            }
            catch (SocketException) { } // Server not up yet, or connection refused/reset.
            catch (IOException) { }
            catch (ObjectDisposedException) { } // Closed in OnDisable.
            finally
            {
                lock (_sendLock)
                {
                    _writer = null;
                    _tcp = null;
                }
                tcp.Close();
            }

            if (!_running)
                break;

            if (!loggedWaiting)
            {
                Debug.Log($"{nameof(ServerConnection)}: not connected to {host}:{port}; retrying every {reconnectDelayMs} ms.");
                loggedWaiting = true;
            }
            Thread.Sleep(reconnectDelayMs);
        }
    }

    void SendDTO(object outgoingDTO)
    {
        string line = DTOSerializer.Serialize(outgoingDTO);

        lock (_sendLock)
        {
            if (_writer == null)
            {
                Debug.LogWarning($"{nameof(ServerConnection)}: not connected; dropped {outgoingDTO.GetType().Name}.");
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                _tcp?.Close(); // The connection thread notices and reconnects.
            }

            Debug.Log($"{nameof(ServerConnection)}: sent {outgoingDTO.GetType().Name} to server.");
        }
    }
}
