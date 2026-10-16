using Server.Handler;
using Server.Manager;
using SharedLibrary.Communication.Connection;
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
                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await AcceptWebSocketConnection(webSocket, context);
            }
            else
            {
                // If it's not a WebSocket request, pass control to the next middleware
                await _next(context);
            }
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
                int userID = -1;

                if (context.Items.TryGetValue("userID", out var userIdObj) && userIdObj is int)
                {
                    userID = userIdObj as int? ?? -1;
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
                if (path.StartsWith(ConnectionType.Application.ToPath(), StringComparison.OrdinalIgnoreCase))
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var sessionWebSocketHandler = scope.ServiceProvider.GetRequiredService<GameConnectionHandler>();
                        await sessionWebSocketHandler.HandleWebSocketConnection(webSocket, userID);
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
                var webSocketConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                foreach (var webSocket in webSocketConnectionManager.GetAllSockets())
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