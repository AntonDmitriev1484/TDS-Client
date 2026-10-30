using Newtonsoft.Json;
using Server.Manager;
using Server.Manager.SpatialSync;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using SharedLibrary.Utilities;
using System.Net.WebSockets;
using System.Text;

namespace Server.Handler
{
    /// <summary>
    /// Handles incoming requests from clients and outgoing requests made by <see cref="ServerSpatialSyncManager"/>.
    /// Pulls connection information from <see cref="SpatialSyncCoordinator"/> to send messages to players.
    /// </summary>
    public class SpatialSyncConnectionHandler
    {

        private readonly IServiceScopeFactory _scopeFactory;

        public SpatialSyncConnectionHandler(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        #region Handle Connections

        /// <summary>
        /// Handles an incoming web socket connection from a client for a multi-user application connection.
        /// </summary>
        /// <param name="webSocket">The web socket.</param>
        /// <param name="playerID">The player ID.</param>
        /// <returns></returns>
        public async Task HandleWebSocketConnection(WebSocket webSocket, int playerID) // *TODO** Change WebSocket to RUDP when RUDP is implemented.
        {
            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result;

            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var spatialSyncConnectionManager = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionManager>();

                    if (!spatialSyncConnectionManager.AddSocket(webSocket))
                    {
                        throw new Exception("The WebSocket could not be added to the Game Connection Manager.");
                    }

                    spatialSyncConnectionManager.AddSpatialSyncSocket(playerID, webSocket);

                    // TODO: Remove this and make spatial sync begin when players have been matched but before game starts.
                    var spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();
                    int? firstSpatialSyncInstanceNullable = spatialSyncCoordinator.GetSessionIDOfFirstInstance();
                    int firstSpatialSyncInstance = firstSpatialSyncInstanceNullable ?? 0;

                    if (firstSpatialSyncInstanceNullable != null)
                    {
                        await spatialSyncCoordinator.AddPlayerToSession(playerID, firstSpatialSyncInstance);
                    }
                    else
                    {
                        await spatialSyncCoordinator.GenerateSpatialSyncSession(new List<int> { playerID });
                    }
                }

                while (true)
                {
                    using var messageStream = new MemoryStream();

                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        messageStream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var message = Encoding.UTF8.GetString(messageStream.ToArray());

                    try
                    {
                        var jsonMessage = JsonUtilities.FromJson<dynamic>(message);

                        if (jsonMessage == null)
                        {
                            throw new JsonException("Message was null.");
                        }

                        string typeString = (string)jsonMessage.type ?? null;
                        var data = jsonMessage.data;

                        await HandleInBoundMessage(webSocket, playerID, typeString, data);
                    }
                    catch (JsonException ex)
                    {
                        await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Invalid message format. Expected an object type and an object."));
                        Console.Error.WriteLine($"Error deserializing message: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"WebSocket error: {ex.Message}");
            }
            finally
            {

                // Rmove the session socket.
                using (var scope = _scopeFactory.CreateScope())
                {
                    var spatialSyncConnectionManager = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionManager>();
                    spatialSyncConnectionManager.RemoveSocket(webSocket);
                    spatialSyncConnectionManager.RemoveSessionSocket(playerID);

                    var spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();
                    var spatialSyncManager = spatialSyncCoordinator.GetSessionByPlayerID(playerID);

                    if (spatialSyncManager != null)
                    {
                        spatialSyncManager.OnPlayerDisconnected(playerID);
                    }

                    var queuesManager = scope.ServiceProvider.GetRequiredService<QueueManager>();
                }

                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                webSocket.Dispose();
            }
        }

        #endregion Handle Connections

        #region Process DTOs

        /// <summary>
        /// Handle an incoming web socket message.
        /// </summary>
        /// <param name="webSocket">The web socket that received the message.</param>
        /// <param name="playerID">The ID of the player that made initial connection.</param>
        /// <param name="typeString">The message's data type in a string format.</param>
        /// <param name="data">The message data.</param>
        /// <returns></returns>
        /// <exception cref="JsonException">The message did not deserialize correctly.</exception>
        private async Task HandleInBoundMessage(WebSocket webSocket, int playerID, string typeString, dynamic data)
        {
            try
            {
                // Handle the message based on its type
                if (typeString == typeof(KeyFrameDTO).Name)
                {
                    KeyFrameDTO keyFrameDTO = data.ToObject<KeyFrameDTO>();

                    await ProcessKeyFrameAsync(webSocket, playerID, keyFrameDTO);
                }
                else
                {
                    await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Data type is not supported."));
                    return;
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Json Exception: {ex.Message}");
                await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Data type could not be deserialized."));
                return;
            }
        }


        /// <summary>
        /// Processes an incoming single action DTO. Send a success/error if the single action DTO is accepted.
        /// </summary>
        /// <param name="webSocket">The web socket that received a single action DTO.</param>
        /// <param name="actionDTO">The single action DTO.</param>
        /// <returns></returns>
        private async Task ProcessKeyFrameAsync(WebSocket webSocket, int playerID, KeyFrameDTO keyFrameDTO)
        {
            Console.WriteLine($"\nIncoming KeyFrame for:\nPlayer ID: {playerID}\nWidth x Height: {keyFrameDTO.Width}x{keyFrameDTO.Height}\n");

            ServerSpatialSyncManager? spatialSyncManagerNullable;

            using (var scope = _scopeFactory.CreateScope())
            {
                var spatialSyncCoordinator = scope.ServiceProvider.GetRequiredService<SpatialSyncCoordinator>();

                spatialSyncManagerNullable = spatialSyncCoordinator.GetSessionByPlayerID(playerID);
            }

            ServerSpatialSyncManager spatialSyncManager = spatialSyncManagerNullable;

            if (spatialSyncManagerNullable != null && spatialSyncManager != null)
            {
                // Enqueue the action to be processed by the spatial synchronization loop.
                spatialSyncManager.EnqueueKeyFrame(playerID, keyFrameDTO);

                // Acknowledge receipt — actual acceptance and broadcasting will occur when the action is processed in the state sync loop.
                await this.SendObjectAsync(webSocket, new ResponseDTO(true, "Action queued for processing."));
            }
            else
            {
                await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Failed to find spatial synchronization session instance for the player!"));
                Console.Error.WriteLine("Failed to find spatial synchronization session instance for the player!");
                return;
            }
        }

        #endregion Handle DTOs

        #region Send Messages

        /// <summary>
        /// Sends an object as a message through the given web socket.
        /// </summary>
        /// <param name="playerID">The player ID to send the message to.</param>
        /// <param name="obj">The object to send.</param>
        /// <returns></returns>
        public async Task<bool> SendObjectAsync<T>(int playerID, T obj)
        {
            WebSocket webSocket;
            using (var scope = _scopeFactory.CreateScope())
            {
                var spatialSyncConnectionManager = scope.ServiceProvider.GetRequiredService<SpatialSyncConnectionManager>();
                webSocket = spatialSyncConnectionManager.GetSessionSocketByPlayerID(playerID);
            }

            if (webSocket == null)
            {
                return false;
            }

            return await this.SendObjectAsync(webSocket, obj);

        }

        /// <summary>
        /// Sends an object as a message through the given web socket.
        /// </summary>
        /// <param name="webSocket">The web socket to send the message through.</param>
        /// <param name="obj">The object to send.</param>
        /// <returns></returns>
        public async Task<bool> SendObjectAsync<T>(WebSocket webSocket, T obj)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return false;
            }


            Type objType = obj.GetType();

            MessageEnvelope<T> message = new MessageEnvelope<T>
            {
                Type = objType.Name,
                Data = obj
            };

            var serializedMessage = JsonUtilities.ToJson(message);
            var buffer = Encoding.UTF8.GetBytes(serializedMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);

            return true;
        }

        #endregion Send Messages

    }
}