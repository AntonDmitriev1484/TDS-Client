using Client.Communication.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using WebSocketSharp;
using ErrorEventArgs = WebSocketSharp.ErrorEventArgs;
using Stopwatch = System.Diagnostics.Stopwatch;


namespace Client.Communication.Manager
{
    public class ConnectionManager : MonoBehaviour
    {

        #region Properties

        public static ConnectionManager Instance { get; set; } // Singleton Instance
        public bool IsInitialized { get; set; }
        public Dictionary<ConnectionType, bool> NeedsConnection { get; set; } // Track if web socket needs to be connected
        public Dictionary<ConnectionType, bool> IsConnected { get; set; } // Track if web socket is connected
        private Dictionary<ConnectionType, bool> IsReconnecting { get; set; } // Track if reconnection is in progress

        // Player ID
        public int PlayerID { get; set; }


        // URL for web server connection
        public string _serverUrl { get; private set; }

        // Object for http connections
        private readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// Web sockets.
        /// </summary>
        // Web Socket for game connection
        private WebSocket GameWebSocket;
        public Dictionary<ConnectionType, float> EstimatedLatencyDict { get; set; }

        [SerializeField]
        int RetryConnectionDelaySeconds = 2;

        [SerializeField]
        float LatencyMeasurementIntervalSeconds = 1f;

        private Dictionary<ConnectionType, Coroutine> LatencyMeasurementCoroutines { get; set; }
        private Dictionary<ConnectionType, double> RttTotalsMilliseconds { get; set; }
        private Dictionary<ConnectionType, int> RttSampleCounts { get; set; }

        /// <summary>
        /// In local test mode the game connection is a loopback TCP socket to the in-app
        /// OfflineGameStateManager instead of a WebSocket to the server.
        /// </summary>
        public bool IsLocalTest => ServerConfig.Instance != null && ServerConfig.Instance.IsLocalTest;

        // Local test game connection: newline-delimited DTOSerializer JSON, as MessageHandler expects.
        private TcpClient _localGameClient;
        private StreamWriter _localGameWriter;
        private readonly object _localGameLock = new object();

        #endregion Properties

        #region Events

        // Inbound WebSocket Messages – subscribers receive each message as a typed UnityEvent.
        public Dictionary<Type, UnityEventBase> InboundMessagesDict = new Dictionary<Type, UnityEventBase>();

        // A curated list of types (adjust as per your needs)
        private List<Type> InboundMessageTypes = new List<Type>
        {
            typeof(QueueDTO),
            typeof(DequeueDTO),
            typeof(SingleActionDTO),
            typeof(GameStateDTO),
            typeof(ResponseDTO),
        };

        #endregion Events

        #region Initialization

        /// <summary>
        /// Singleton for Server Manager.
        /// </summary>
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                // Set initial variables on server state and settings
                IsInitialized = false;

                // Wait for necessary objects before initializing Server Manager
                StartCoroutine(WaitToInitialize());
            }
            else
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Wait on necessary objects to initialize before initializing Server Manager.
        /// </summary>
        private IEnumerator WaitToInitialize()
        {
            while (ServerConfig.Instance == null || ServerConfig.Instance.Initialized == false)
            {
                yield return null;
            }

            // Passed wait checks on other needed objects, can now initialize server
            // Use a coroutine to wait for the async operation to complete
            yield return StartCoroutine(InitializeServerCoroutine());
        }

        private IEnumerator InitializeServerCoroutine()
        {
            InitializeServerVariables();
            InitializeWebSocketEvents();

            if (IsLocalTest)
            {
                // No server to log in to: the offline game's only player is player 0.
                PlayerID = 0;
                IsInitialized = true;
                Debug.Log("Server Manager initialized in local test mode as player 0.");
                yield break;
            }

            while (!IsInitialized)
            {
                Task initializeConnectionTask = InitializeServerConnection(); // Call the async initialization method
                while (!initializeConnectionTask.IsCompleted)
                {
                    yield return null; // Wait until the task completes
                }

                if (!IsInitialized)
                {
                    yield return new WaitForSeconds(5);
                }
            }

        }

        /// <summary>
        /// Variable Initialization for Server Manager Singleton
        /// </summary>
        private void InitializeServerVariables()
        {
            // Get the web server url
            _serverUrl = ServerConfig.Instance.ServerUrl;
            Debug.Log($"ServerURL is: {_serverUrl}");

            // Set up all connection-related variables to default state
            this.NeedsConnection = new Dictionary<ConnectionType, bool>();
            this.IsConnected = new Dictionary<ConnectionType, bool>();
            this.IsReconnecting = new Dictionary<ConnectionType, bool>();
            this.EstimatedLatencyDict = new Dictionary<ConnectionType, float>();
            this.LatencyMeasurementCoroutines = new Dictionary<ConnectionType, Coroutine>();
            this.RttTotalsMilliseconds = new Dictionary<ConnectionType, double>();
            this.RttSampleCounts = new Dictionary<ConnectionType, int>();

            foreach (ConnectionType connectionType in Enum.GetValues(typeof(ConnectionType)))
            {
                this.NeedsConnection.Add(connectionType, false);
                this.IsConnected.Add(connectionType, false);
                this.IsReconnecting.Add(connectionType, false);
                this.EstimatedLatencyDict.Add(connectionType, -1.0f);
                this.LatencyMeasurementCoroutines.Add(connectionType, null);
                this.RttTotalsMilliseconds.Add(connectionType, 0d);
                this.RttSampleCounts.Add(connectionType, 0);
            }

            System.Net.ServicePointManager.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) => true;
        }

        /// <summary>
        /// Connection Initialization for Server Manager Singleton.
        /// </summary>
        private async Task InitializeServerConnection()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log("Attempting to connect to server!");
            });
            bool isServerOnline = await SendGET<bool>(ConnectionTypeExtensions.TestConnectionEndpoint, (float)5, "test", "test");

            if (!isServerOnline)
            {
                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log("Failed to connect to server.");
                });
                return;
            }

            int? playerID = await LoginPlayer();

            if (playerID == null)
            {
                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log("Failed to log in player.");
                });
                return;
            }
            else
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log($"Player logged in successfully with ID: {playerID}");
                });

                PlayerID = playerID ?? 0;
            }

            IsInitialized = true;
            Debug.Log("Server Manager initialized!");
        }

        /// <summary>
        /// Initialize InboundMessagesDict with a typed UnityEvent per message type.
        /// </summary>
        void InitializeWebSocketEvents()
        {
            foreach (var type in InboundMessageTypes)
            {
                // Create a UnityEvent<T> for each type and add it to the dictionary
                var eventType = typeof(UnityEvent<>).MakeGenericType(type);
                var unityEvent = (UnityEventBase)Activator.CreateInstance(eventType);

                InboundMessagesDict[type] = unityEvent;
            }
        }

        #endregion Initialization

        #region Log In

        /// <summary>
        /// Send HTTP request to login player and get the Player ID back.
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <returns></returns>
        public async Task<int?> LoginPlayer()
        {
            string url = $"{_serverUrl}{ConnectionTypeExtensions.LoginPlayerEndpoint}";

            // Create and configure the HTTP request
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                try
                {
                    // Send the HTTP request and get the response
                    HttpResponseMessage response = await _httpClient.SendAsync(request);

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Debug.Log("Log in failed, user is unauthorized.");
                    }

                    // Ensure the response indicates success
                    response.EnsureSuccessStatusCode();

                    // Read the response content
                    string responseContent = await response.Content.ReadAsStringAsync();

                    // Deserialize the JSON response
                    int playerID = JsonUtilities.FromJson<int>(responseContent);

                    return playerID;
                }
                catch (HttpRequestException httpEx)
                {
                    // Handle HTTP request errors (e.g., network issues, invalid responses)
                    Console.WriteLine($"Request error: {httpEx.Message}");
                    return null;
                }
                catch (Exception jsonEx)
                {
                    // Handle JSON deserialization errors
                    Console.WriteLine($"Deserialization error: {jsonEx.Message}");
                    return null;
                }
            }
        }

        #endregion Log In

        #region POST/GET Requests

        /// <summary>
        /// Send a POST request to the web server.
        /// </summary>
        /// <typeparam name="T">The type of data to send.</typeparam>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <param name="data">The data object to send</param>
        /// <param name="timeoutSeconds">The amount of seconds to wait before timeout.</param>
        /// <param name="username">The username to use for authentication.</param>
        /// <param name="password">The password to use for authentication.</param>
        /// <returns>A data object based on the type of send.</returns>
        public async Task<B> SendPOST<T, B>(string APIEndpoint, T data, float timeoutSeconds, string username, string password)
        {
            string url = $"{_serverUrl}{APIEndpoint}";

            // Check that credentials are given.
            if (username == null || password == null)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"Username ({username}) or Password ({password}) not specified in POST request.");
                });
                return default;
            }

            // Encode the credentials as Base64
            string encodedCredentials = EncodeCredentials(username, password);

            // Create and configure the HTTP request
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                // Set the Authorization header with the credentials
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);

                // Serialize the PlayerIdentityModel object to JSON and attach it to the request
                string jsonPayload = JsonUtilities.ToJson(data);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // Create a CancellationTokenSource with a 5-second timeout
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        // Send the HTTP request and get the response
                        HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                        // Ensure the response indicates success
                        response.EnsureSuccessStatusCode();

                        // Read the response content
                        string responseContent = await response.Content.ReadAsStringAsync();

                        // Deserialize the JSON response
                        B receivedData = JsonUtilities.FromJson<B>(responseContent);

                        return receivedData;
                    }
                    catch (HttpRequestException httpEx)
                    {
                        // Handle HTTP request errors (e.g., network issues, invalid responses)
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError($"Request error: {httpEx.Message}");
                        });
                        return default;
                    }
                    catch (OperationCanceledException)
                    {
                        // Handle timeout
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError("Request timed out.");
                        });
                        return default;
                    }
                }
            }
        }

        /// <summary>
        /// Send a POST request to the web server using the stored Player username and password.
        /// </summary>
        /// <typeparam name="T">The type of data to send.</typeparam>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <param name="data">The data object to send</param>
        /// <param name="timeoutSeconds">The amount of seconds to wait before timeout.</param>
        /// <returns>A data object based on the type of send.</returns>
        public async Task<B> SendPOST<T, B>(string APIEndpoint, T data, float timeoutSeconds)
        {
            return await SendPOST<T, B>(APIEndpoint, data, timeoutSeconds, ServerConfig.Instance.Username, ServerConfig.Instance.Password);
        }

        /// <summary>
        /// Send a POST request to the web server with a standard timeout of 5 seconds.
        /// </summary>
        /// <typeparam name="T">The type of data to send.</typeparam>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <param name="data">The data object to send</param>
        /// <returns>A data object based on the type of send.</returns>
        public async Task<B> SendPOST<T, B>(string APIEndpoint, T data)
        {
            return await SendPOST<T, B>(APIEndpoint, data, (float)5);
        }

        /// <summary>
        /// Send a GET request to the web server.
        /// </summary>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <param name="timeoutSeconds">The amount of seconds to wait before timeout.</param>
        /// <param name="username">The username to use for authentication.</param>
        /// <param name="password">The password to use for authentication.</param>
        /// <returns>A data object based on the type expected to receive.</returns>
        public async Task<B> SendGET<B>(string APIEndpoint, float timeoutSeconds, string username, string password)
        {
            string url = $"{_serverUrl}{APIEndpoint}";

            if (username == null || password == null)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"Username ({username}) or Password ({password}) not specified in GET request.");
                });
                return default;
            }

            // Encode the credentials as Base64
            string encodedCredentials = EncodeCredentials(username, password);

            // Create and configure the HTTP request
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                // Set the Authorization header with the credentials
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);

                // Create a CancellationTokenSource with a 5-second timeout
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        // Send the HTTP request and get the response
                        HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                        // Ensure the response indicates success
                        response.EnsureSuccessStatusCode();

                        // Read the response content
                        string responseContent = await response.Content.ReadAsStringAsync();

                        // Deserialize the JSON response
                        B receivedData = JsonUtilities.FromJson<B>(responseContent);

                        return receivedData;
                    }
                    catch (HttpRequestException httpEx)
                    {
                        // Handle HTTP request errors (e.g., network issues, invalid responses)
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError($"Request error: {httpEx.ToString()}");
                        });
                        return default;
                    }
                    catch (OperationCanceledException)
                    {
                        // Handle timeout
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError("Request timed out.");
                        });
                        return default;
                    }
                }
            }
        }

        /// <summary>
        /// Send a GET request to the web server using the stored Player username and password.
        /// </summary>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <param name="timeoutSeconds">The amount of second to wait before timeout.</param>
        /// <returns>A data object based on the type expected to receive.</returns>
        public async Task<B> SendGET<B>(string APIEndpoint, float timeoutSeconds)
        {
            return await SendGET<B>(APIEndpoint, timeoutSeconds, ServerConfig.Instance.Username, ServerConfig.Instance.Password);
        }

        /// <summary>
        /// Send a GET request to the web server with a standard timeout of 5 seconds.
        /// </summary>
        /// <typeparam name="B">The type of data expected to receive.</typeparam>
        /// <param name="APIEndpoint">The API Endpoint to send a request for.</param>
        /// <returns>A data object based on the type expected to receive.</returns>
        public async Task<B> SendGET<B>(string APIEndpoint)
        {
            return await SendGET<B>(APIEndpoint, (float)5);
        }

        #endregion POST/GET Requests

        #region Web Socket

        /// <summary>
        /// Connect to the web server using a web socket.
        /// </summary>
        /// <param name="connectionType">The type of connection channel to establish with the web server.</param>
        public async Task<bool> ConnectWebSocket(ConnectionType connectionType)
        {
            if (IsLocalTest && connectionType == ConnectionType.Game)
                return await ConnectLocalGame();

            NeedsConnection[connectionType] = true;
            string baseUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://");

            MainThreadDispatcher.Enqueue(() =>
            {
                Debug.Log($"Attempting to connect websocket for {connectionType} with url: {baseUrl}.");
            });

            string username = ServerConfig.Instance.Username;
            string password = ServerConfig.Instance.Password;
            string encodedCredentials = EncodeCredentials(username, password);

            // Require a valid PlayerID and include credentials and playerID in the URL query string
            if (this.PlayerID < 0)
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"Cannot connect WebSocket for {connectionType}: PlayerID is not set or invalid.");
                });

                return false;
            }

            // Include credentials and playerID in the URL (playerID is used by the server middleware)
            string wsUrl = $"{baseUrl}{connectionType.ToPath()}?auth={encodedCredentials}&playerID={this.PlayerID}";

            WebSocket webSocket;
            switch (connectionType)
            {
                case ConnectionType.Game:
                    this.GameWebSocket = new WebSocket(wsUrl);
                    webSocket = this.GameWebSocket;
                    break;
                default:
                    string methodName = System.Reflection.MethodBase.GetCurrentMethod().Name;
                    Debug.LogError($"Connection type {connectionType.ToString()} is not supported in Server Manager. See {methodName} method for details.");
                    return false;
            }


            // Event for when the WebSocket connection is successfully opened.
            webSocket.OnOpen += (sender, e) =>
            {
                IsConnected[connectionType] = true;
                MainThreadDispatcher.Enqueue(() => StartLatencyMeasurement(connectionType, webSocket));
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log($"{connectionType.ToString()} WebSocket connection opened.");
                });
            };

            webSocket.OnMessage += (sender, e) =>
            {
                HandleIncomingMessage(connectionType, webSocket, e);
            };

            webSocket.OnError += async (sender, e) =>
            {
                await HandleWebSocketError(connectionType, webSocket, e);
            };

            webSocket.OnClose += async (sender, e) =>
            {
                await HandleWebSocketClose(connectionType, webSocket, e);
            };

            webSocket.SslConfiguration.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) => true;
            webSocket.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
            webSocket.SslConfiguration.CheckCertificateRevocation = false;

            await Task.Run(() =>
            {
                webSocket.Connect();
            });


            while (this.NeedsConnection[connectionType] && !this.IsConnected[connectionType])
            {
                await Task.Delay(this.RetryConnectionDelaySeconds * 1000);
            }


            return true;
        }

        private void StartLatencyMeasurement(ConnectionType connectionType, WebSocket webSocket)
        {
            StopLatencyMeasurement(connectionType);

            RttTotalsMilliseconds[connectionType] = 0d;
            RttSampleCounts[connectionType] = 0;
            EstimatedLatencyDict[connectionType] = 0f;
            LatencyMeasurementCoroutines[connectionType] = StartCoroutine(MeasureLatency(connectionType, webSocket));
        }

        private void StopLatencyMeasurement(ConnectionType connectionType)
        {
            if (LatencyMeasurementCoroutines[connectionType] != null)
            {
                StopCoroutine(LatencyMeasurementCoroutines[connectionType]);
                LatencyMeasurementCoroutines[connectionType] = null;
            }
        }

        private IEnumerator MeasureLatency(ConnectionType connectionType, WebSocket webSocket)
        {
            while (IsConnected[connectionType] && webSocket != null && webSocket.IsAlive)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                bool pingSucceeded = webSocket.Ping();
                stopwatch.Stop();

                if (pingSucceeded)
                {
                    RttTotalsMilliseconds[connectionType] += stopwatch.Elapsed.TotalMilliseconds;
                    RttSampleCounts[connectionType]++;

                    double averageRttMilliseconds = RttTotalsMilliseconds[connectionType] /
                        RttSampleCounts[connectionType];

                    // Estimated one-way latency is half the measured round-trip time.
                    EstimatedLatencyDict[connectionType] = (float)(averageRttMilliseconds / 2000d);
                }

                yield return new WaitForSeconds(Mathf.Max(0.01f, LatencyMeasurementIntervalSeconds));
            }

            LatencyMeasurementCoroutines[connectionType] = null;
        }

        /// <summary>
        /// Encode credentials to be sent in an encrypted Base64.
        /// </summary>
        /// <param name="username">Username to encode.</param>
        /// <param name="password">Password to encode.</param>
        /// <returns>A string of {username:password}.</returns>
        public string EncodeCredentials(string username, string password)
        {
            var credentials = $"{username}:{password}";
            return credentials;
        }

        /// <summary>
        /// Handles web socket errors. Then retries connection.
        /// </summary>
        /// <param name="connectionType">The type of connection you are handling.</param>
        /// <param name="webSocket">The web socket to handle errors for.</param>
        /// <param name="e">The error event args passed back from web socket error event.</param>
        private async Task HandleWebSocketError(ConnectionType connectionType, WebSocket webSocket, ErrorEventArgs e)
        {
            // Handle errors

            if (e.Exception is SocketException socketEx)
            {
                // Handle network-related issues

                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"{connectionType.ToString()} Network error: {socketEx.Message}");
                });

                // If it still needs a connection, attempt to reconnect.
                if (NeedsConnection[connectionType])
                {
                    IsConnected[connectionType] = false;
                    await TryReconnection(connectionType, webSocket);
                }
            }
            else if (e.Exception is SecurityException securityEx)
            {
                // Handle security-related issues

                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"{connectionType.ToString()} Security error: {securityEx.Message}");
                });
            }
            else if (e.Exception is TimeoutException timeoutEx)
            {
                // Handle timeout issues

                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"{connectionType.ToString()} Timeout error: {timeoutEx.Message}");
                });
            }
            else if (e.Exception is System.IO.IOException ioEx)
            {
                // Handle I/O errors

                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"{connectionType.ToString()} I/O error: {ioEx.Message}");
                });
            }
            else
            {
                // Handle general or unknown errors

                // Add error to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogError($"{connectionType.ToString()} WebSocket error: " + e.Message);
                });
            }
        }

        /// <summary>
        /// Handles web socket connection closing.
        /// </summary>
        /// <param name="connectionType">The type of connection you are handling.</param>
        /// <param name="webSocket">The web socket to handle connection closing for.</param>
        /// <param name="e">The close event args passed back from web socket closing event.</param>
        private async Task HandleWebSocketClose(ConnectionType connectionType, WebSocket webSocket, CloseEventArgs e)
        {
            // Handle closing connections.

            MainThreadDispatcher.Enqueue(() => StopLatencyMeasurement(connectionType));

            // If it still needs a connection, attempt to reconnect.
            if (NeedsConnection[connectionType])
            {
                IsConnected[connectionType] = false;
                await TryReconnection(connectionType, webSocket);
            }
            // Otherwise, display successful close message.
            else
            {
                Debug.Log($"{connectionType.ToString()} WebSocket connection closed.");
            }

            return;
        }

        /// <summary>
        /// Retries connection with the web server for a given web socket.
        /// </summary>
        /// <param name="webSocket">The web socket to retry a connection with.</param>
        /// <returns></returns>
        private async Task TryReconnection(ConnectionType connectionType, WebSocket webSocket)
        {
            // Check if reconnection is already in progress
            if (this.IsReconnecting[connectionType])
            {
                return; // Exit if already reconnecting
            }

            this.IsReconnecting[connectionType] = true; // Set flag to indicate reconnection is in progress


            // Loop to attempt reconnection every set number of seconds
            while (this.NeedsConnection[connectionType] && !this.IsConnected[connectionType])
            {
                // Wait the specified additional number of seconds before trying to reconnect again.
                await Task.Delay(this.RetryConnectionDelaySeconds * 1000);

                // Add reconnection message to main thread so it can be written to debug log.
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogWarning($"{connectionType.ToString()} WebSocket connection failed. Attempting to reconnect...");
                });

                // Attempt to reconnect.
                try
                {
                    await Task.Run(() =>
                    {
                        webSocket.Connect();
                    });
                }
                catch (Exception ex)
                {
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogError($"{connectionType.ToString()} WebSocket reconnection error: {ex.Message}");
                    });
                }
            }

            this.IsReconnecting[connectionType] = false; // Reset the flag when done
            return;
        }

        /// <summary>
        /// Attempt to close a particular web socket.
        /// </summary>
        /// <returns>Returns true if web socket closed properly, or was already closed. Otherwise, returns false.</returns>
        public async Task<bool> CloseWebSocket(ConnectionType connectionType)
        {
            if (IsLocalTest && connectionType == ConnectionType.Game)
                return CloseLocalGame();

            this.NeedsConnection[connectionType] = false;
            MainThreadDispatcher.Enqueue(() => StopLatencyMeasurement(connectionType));

            if (!IsConnected[connectionType])
            {
                Debug.Log($"{connectionType.ToString()} WebSocket is already closed.");
                return true;
            }

            WebSocket webSocket = this.GetWebSocket(connectionType);

            if (webSocket == null)
            {
                IsConnected[connectionType] = false;
                Debug.LogWarning($"{connectionType.ToString()} WebSocket could not be found.");
                return false;
            }

            try
            {
                if (webSocket.IsAlive)
                {
                    await Task.Run(() =>
                    {
                        webSocket.Close();
                    });
                    return true;
                }
                else { return true; }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception during {connectionType.ToString()} WebSocket connection close: " + ex.Message);
            }
            finally
            {
                webSocket = null;
            }

            return false;
        }

        /// <summary>
        /// Gets the web socket connection of a particular connection type.
        /// </summary>
        /// <param name="connectionType">The type of connection.</param>
        /// <returns>The web socket managing the particular connnection type.</returns>
        private WebSocket GetWebSocket(ConnectionType connectionType)
        {
            WebSocket webSocket;
            switch (connectionType)
            {
                case ConnectionType.Game:
                    webSocket = this.GameWebSocket;
                    break;
                default:
                    string methodName = System.Reflection.MethodBase.GetCurrentMethod().Name;
                    Debug.LogError($"{connectionType.ToString()} WebSocket is not supported in Server Manager. See {methodName} method for more details");
                    return null;
            }

            return webSocket;
        }

        /// <summary>
        /// Gets the running average of round trip time for a particular connection.
        /// </summary>
        /// <param name="connectionType">The type of connection.</param>
        /// <returns>The round trip time, or -1.0f if the connection is not currently alive.</returns>
        public float GetConnectionLatency(ConnectionType connectionType)
        {
            float connectionRTT;
            switch (connectionType)
            {
                case ConnectionType.Game:
                    connectionRTT = this.EstimatedLatencyDict[connectionType];
                    break;
                default:
                    string methodName = System.Reflection.MethodBase.GetCurrentMethod().Name;
                    Debug.LogError($"{connectionType.ToString()} WebSocket is not supported in Server Manager. See {methodName} method for more details");
                    return -1.0f;
            }

            return connectionRTT;
        }

        /// <summary>
        /// Send a JSON message through the specified web socket.
        /// </summary>
        /// <typeparam name="T">Type of Object being sent.</typeparam>
        /// <param name="connectionType">Type of connection to send message through.</param>
        /// <param name="data">Object that is JSON Serializable.</param>
        /// <returns>True if the web socket was able to send the Data Transfer Object. False otherwise.</returns>
        public async Task<bool> SendDTO<T>(ConnectionType connectionType, T data)
        {
            if (IsLocalTest && connectionType == ConnectionType.Game)
                return SendLocalGame(data);

            WebSocket webSocket = this.GetWebSocket(connectionType);

            if (webSocket != null)
            {
                if (webSocket.IsAlive)
                {
                    try
                    {
                        MessageEnvelope<T> message = new MessageEnvelope<T>
                        {
                            Type = typeof(T).Name,
                            Data = data
                        };

                        return await SendObject(webSocket, message);
                    }
                    catch (JsonException ex)
                    {
                        Debug.LogError($"Serialization error for {connectionType} WebSocket Connection with data type: {typeof(T).Name}\n{ex.Message}");
                    }
                }
                else
                {
                    Debug.LogError($"{connectionType} WebSocket is not connected.");
                }
            }
            else
            {
                Debug.LogError($"{connectionType} WebSocket is not initialized.");
            }

            return false;
        }

        /// <summary>
        /// Send a JSON Serializable Object asynchronously.
        /// </summary>
        /// <param name="webSocket">The web socket to send the object through.</param>
        /// <param name="obj">The object to send through.</param>
        /// <returns></returns>
        private async Task<bool> SendObject(WebSocket webSocket, object obj)
        {
            try
            {
                string jsonObj = JsonUtilities.ToJson(obj);

                await Task.Run(() => webSocket.Send(jsonObj));
                return true; // If no exception is thrown, the send was successful
            }
            catch (Exception ex)
            {
                // Log the exception or handle it as needed
                Debug.LogError($"Error sending message: {ex.Message}");
                return false; // An exception indicates the send failed
            }
        }

        /// <summary>
        /// Handles when an incoming message is received from the server through a specified web socket.
        /// </summary>
        /// <param name="connectionType">The type of web socket connection.</param>
        /// <param name="webSocket">The web socket that received the incoming message.</param>
        /// <param name="e">The message event args passed from web socket OnMessage event.</param>
        /// <returns></returns>
        public async Task HandleIncomingMessage(ConnectionType connectionType, WebSocket webSocket, MessageEventArgs e)
        {
            var messageData = e.Data;

            // Try to deserialize the message as different types
            try
            {
                // Parse into a JObject to avoid the dynamic runtime binder
                JObject jsonMessage;
                try
                {
                    jsonMessage = JObject.Parse(messageData);
                }
                catch (Exception parseEx)
                {
                    throw new JsonException($"Message parse failed: {parseEx.Message}");
                }

                if (jsonMessage == null)
                {
                    throw new JsonException("Message was null.");
                }

                string typeString = (string)jsonMessage["type"] ?? null;
                JToken data = jsonMessage["data"];

                // Check that the message type was correct.
                if (typeString == null)
                {
                    throw new Exception("The incoming message did not have a type string attached to it!");
                }


                // Attempt to deserialize the data based on the type.
                Type type = TypeUtilities.GetTypeByName(typeString);

                if (type == null)
                {
                    throw new Exception($"The incoming message's type was not found! {type}");
                }

                // Deserialize the data into the expected type.
                object typedData = null;
                try
                {
                    typedData = JsonUtilities.FromJson(data.ToString(), type);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error deserializing message of type {type.Name}: {ex.Message}");
                }

                if (typedData != null)
                    DispatchInboundMessage(typedData);

            }
            catch (Exception ex)
            {
                Debug.LogError($"Error deserializing incoming message: {ex.Message}");
            }
        }

        /// <summary>
        /// Invokes the registered UnityEvent for the message's type.
        /// </summary>
        private void DispatchInboundMessage(object typedData)
        {
            Type type = typedData.GetType();

            if (!this.InboundMessagesDict.TryGetValue(type, out var unityEventObj))
                return;

            var genericUnityEventType = typeof(UnityEvent<>).MakeGenericType(type);

            if (genericUnityEventType.IsInstanceOfType(unityEventObj))
            {
                try
                {
                    var invokeMethod = genericUnityEventType.GetMethod("Invoke");
                    invokeMethod.Invoke(unityEventObj, new[] { typedData });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error invoking UnityEvent: {ex.Message}");
                }
            }
            else
            {
                Debug.LogError($"UnityEvent for type {type} not found or mismatched.");
            }
        }

        #endregion Web Socket

        #region Local Test Connection

        /// <summary>
        /// Connects the game connection to the in-app OfflineGameStateManager's MessageHandler over
        /// loopback TCP, retrying until it is listening. Connecting also sends the
        /// <see cref="QueueDTO"/> that identifies this player, so the game starts right away.
        /// </summary>
        private async Task<bool> ConnectLocalGame()
        {
            ConnectionType connectionType = ConnectionType.Game;
            NeedsConnection[connectionType] = true;
            int port = ServerConfig.Instance.Port;

            while (NeedsConnection[connectionType] && !IsConnected[connectionType])
            {
                var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                }
                catch (SocketException ex)
                {
                    client.Close();
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogWarning($"Local test game connection to {IPAddress.Loopback}:{port} failed ({ex.Message}). Retrying...");
                    });
                    await Task.Delay(this.RetryConnectionDelaySeconds * 1000);
                    continue;
                }

                lock (_localGameLock)
                {
                    _localGameClient = client;
                    // No BOM: the other end parses each line as JSON.
                    _localGameWriter = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                }

                // Same process, so there is no meaningful latency.
                EstimatedLatencyDict[connectionType] = 0f;
                IsConnected[connectionType] = true;
                new Thread(() => ReceiveLocalGameLoop(client)) { IsBackground = true, Name = "Local test game connection" }.Start();

                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.Log($"Local test game connection opened to {IPAddress.Loopback}:{port}.");
                });

                SendLocalGame(new QueueDTO(PlayerID));
            }

            return true;
        }

        private void ReceiveLocalGameLoop(TcpClient client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        DispatchInboundMessage(DTOSerializer.Deserialize(line));
                    }
                    catch (Exception ex)
                    {
                        MainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError($"Error deserializing local test message: {ex.Message}");
                        });
                    }
                }
            }
            catch (IOException) { } // Connection dropped or closed.
            catch (ObjectDisposedException) { }

            lock (_localGameLock)
            {
                // A newer connection may already have replaced this one.
                if (_localGameClient != client)
                    return;

                _localGameClient = null;
                _localGameWriter = null;
            }

            client.Close();
            IsConnected[ConnectionType.Game] = false;

            if (NeedsConnection[ConnectionType.Game])
            {
                MainThreadDispatcher.Enqueue(() =>
                {
                    Debug.LogWarning("Local test game connection closed. Reconnecting...");
                });
                _ = ConnectLocalGame();
            }
        }

        private bool SendLocalGame(object dto)
        {
            lock (_localGameLock)
            {
                if (_localGameWriter == null)
                {
                    Debug.LogError("Local test game connection is not connected.");
                    return false;
                }

                try
                {
                    _localGameWriter.WriteLine(DTOSerializer.Serialize(dto));
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    // The receive thread reports the disconnect and reconnects.
                    _localGameClient?.Close();
                    Debug.LogError($"Error sending local test message: {ex.Message}");
                    return false;
                }
            }
        }

        private bool CloseLocalGame()
        {
            NeedsConnection[ConnectionType.Game] = false;

            lock (_localGameLock)
            {
                _localGameClient?.Close(); // Unblocks the receive thread.
                _localGameClient = null;
                _localGameWriter = null;
            }

            IsConnected[ConnectionType.Game] = false;
            return true;
        }

        #endregion Local Test Connection

        /// <summary>
        /// Handle closing any server connections that need closing on application closing.
        /// </summary>
        private async void OnApplicationQuit()
        {
            // Clean up all supported WebSocket connections on application quit
            foreach (ConnectionType connectionType in Enum.GetValues(typeof(ConnectionType)))
            {
                try
                {
                    var closedProperly = await this.CloseWebSocket(connectionType);
                    if (!closedProperly)
                    {
                        Debug.LogWarning($"{connectionType.ToString()} WebSocket connection was interrupted on close.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Exception occurred while closing {connectionType.ToString()} WebSocket connection: {ex.Message}");
                }
            }
        }
    }
}
