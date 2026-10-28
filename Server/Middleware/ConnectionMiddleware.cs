using Server.Handler;
using Server.Manager;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Utilities;
using System.Net.WebSockets;

namespace Server.Middleware
{
    public class ConnectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IServiceScopeFactory _scopeFactory;

        public ConnectionMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
        {
            _next = next;
            _scopeFactory = scopeFactory;
        }


        /// <summary>
        /// Checks and handles incoming web socket requests.
        /// </summary>
        /// <param name="context">The HTTP Context.</param>
        /// <returns></returns>
        public async Task InvokeAsync(HttpContext context)
        {
            // If it's a WebSocket request, handle it
            if (context.WebSockets.IsWebSocketRequest)
            {
                // Extract playerID from query string (playerID or playerId) or from X-Player-ID header
                string playerIdStr = null;
                if (context.Request.Query.TryGetValue("playerID", out var qv) || context.Request.Query.TryGetValue("playerId", out qv))
                {
                    playerIdStr = qv.ToString();
                }
                else if (context.Request.Headers.TryGetValue("X-Player-ID", out var hv))
                {
                    playerIdStr = hv.ToString();
                }

                if (!int.TryParse(playerIdStr, out var playerID) || playerID <= 0)
                {
                    // Missing or invalid player id — reject the websocket upgrade request
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Missing or invalid playerID in query string or X-Player-ID header.");
                    return;
                }

                // Attach playerID to the HttpContext so downstream handlers can use it
                context.Items["playerID"] = playerID;

                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await AcceptWebSocketConnection(webSocket, context);
                return;
            }

            // If it's an HTTPS request, handle API endpoints such as test-connection and login
            if (context.Request.IsHttps)
            {
                var handled = await HandleHTTPSRequest(context);
                if (handled)
                {
                    return;
                }
                // If not handled, fall through to next middleware
            }

            // If it's not a WebSocket or handled HTTPS request, pass control to the next middleware
            await _next(context);
        }

        /// <summary>
        /// Handles HTTPS API endpoints (test connection and login).
        /// Returns true if the request was handled and no further middleware should run.
        /// </summary>
        private async Task<bool> HandleHTTPSRequest(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            // Test connection endpoint expected to return a boolean (true) as JSON
            if (string.Equals(path, ConnectionTypeExtensions.TestConnectionEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonUtilities.ToJson(true));
                return true;
            }

            // Login endpoint: create a new player ID and return it as JSON
            if (string.Equals(path, ConnectionTypeExtensions.LoginPlayerEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                    // Grab remote IP address and register the player
                    string remoteIp = context.Connection?.RemoteIpAddress?.ToString() ?? string.Empty;
                    int playerID = gameConnectionManager.AddPlayer(remoteIp);

                    if (playerID <= 0)
                    {
                        Console.WriteLine($"Refused to add player: remote IP was empty or invalid. Remote IP='{remoteIp}'");
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(JsonUtilities.ToJson("Refused to add player: remote IP was empty or invalid."));
                        return true;
                    }

                    Console.WriteLine($"Login handled for player {playerID} from IP {remoteIp}.");

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonUtilities.ToJson(playerID));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Accept a web socket connection with a new client.
        /// </summary>
        /// <param name="webSocket">The web socket object for the new client connection.</param>
        /// <param name="context">The HTTP Context for the new client connection.</param>
        /// <returns></returns>
        public async Task AcceptWebSocketConnection(WebSocket webSocket, HttpContext context)
        {
            try
            {
                int playerID = -1;

                if (context.Items.TryGetValue("playerID", out var playerIdObj) && playerIdObj is int)
                {
                    playerID = playerIdObj as int? ?? -1;
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }

                if (webSocket == null) { throw new WebSocketException("The WebSocket object was null."); }

                using (var scope = _scopeFactory.CreateScope())
                {
                    var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                    if (!gameConnectionManager.AddSocket(webSocket))
                    {
                        throw new Exception("The WebSocket could not be added to the Game Connection Manager.");
                    }
                }

                var path = context.Request.Path.Value;

                if (path == null) { throw new WebSocketException("HTTPS path provided was null."); }


                // Route based on the request path
                if (path.StartsWith(ConnectionType.Game.ToPath(), StringComparison.OrdinalIgnoreCase))
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var gameConnectionHandler = scope.ServiceProvider.GetRequiredService<GameConnectionHandler>();
                        await gameConnectionHandler.HandleWebSocketConnection(webSocket, playerID);
                    }
                }
                else if (path.StartsWith(ConnectionType.SpatialSync.ToPath(), StringComparison.OrdinalIgnoreCase))
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var spatialSyncConnectionHandler = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionHandler>();
                        await spatialSyncConnectionHandler.HandleWebSocketConnection(webSocket, playerID);
                    }
                }
                else
                {
                    // Optionally handle other paths or close the connection
                    await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Unsupported path", CancellationToken.None);
                }
            }
            catch (WebSocketException ex)
            {
                Console.Error.WriteLine("Failed to connect as a WebSocket request.");
                await context.Response.WriteAsync(ex.ToString());
            }
            finally
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var webSocketConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                    webSocketConnectionManager.RemoveSocket(webSocket);
                }
            }

        }

        /// <summary>
        /// Ends all web socket connections on server shutdown.
        /// </summary>
        /// <returns></returns>
        public async Task HandleServerShutdown()
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                foreach (var webSocket in gameConnectionManager.GetAllSockets())
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await CloseWebSocket(webSocket, WebSocketCloseStatus.NormalClosure, ConnectionMessages.ServerShutdownMessage);
                    }
                }
            }
        }

        /// <summary>
        /// Send a close message to a webSocket on server shutdown.
        /// </summary>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        public static async Task CloseWebSocket(WebSocket webSocket, WebSocketCloseStatus closeStatus, string message)
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(closeStatus, message, CancellationToken.None);
            }
        }
    }
}