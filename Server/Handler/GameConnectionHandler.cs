using Newtonsoft.Json;
using Server.Manager;
using Server.Manager.Game;
using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Communication.DataTransferObjects.Game;
using SharedLibrary.Utilities;
using System.Net.WebSockets;
using System.Text;

namespace Server.Handler
{
    /// <summary>
    /// Handles incoming requests from clients and outgoing requests made by <see cref="ServerGameStateManager"/>.
    /// Pulls connection information from <see cref="GameConnectionManager"/> to send messages to players.
    /// </summary>
    public class GameConnectionHandler
    {

        private readonly IServiceScopeFactory _scopeFactory;

        public GameConnectionHandler(IServiceScopeFactory scopeFactory)
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
                    var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                    if (!gameConnectionManager.AddSocket(webSocket))
                    {
                        throw new Exception("The WebSocket could not be added to the Game Connection Manager.");
                    }

                    gameConnectionManager.AddGameSocket(playerID, webSocket);
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
                        Console.WriteLine(message + "\n\n");

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

                // Rmove the game socket and dequeue the player from any queues it may be in.
                using (var scope = _scopeFactory.CreateScope())
                {
                    var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();
                    gameConnectionManager.RemoveSocket(webSocket);
                    gameConnectionManager.RemoveGameSocket(playerID);

                    var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();
                    var gameStateManager = gameCoordinator.GetGameByPlayerID(playerID);

                    if (gameStateManager != null)
                    {
                        gameStateManager.OnPlayerDisconnected(playerID);
                    }

                    var queuesManager = scope.ServiceProvider.GetRequiredService<QueueManager>();
                    queuesManager.DequeuePlayer(playerID);
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
                if (typeString == typeof(QueueDTO).Name)
                {
                    QueueDTO queueDTO = data.ToObject<QueueDTO>();

                    if (queueDTO.PlayerID != playerID)
                    {
                        await this.SendObjectAsync(webSocket, new ResponseDTO(false, "DTO's player ID does not match the web socket's player ID."));
                        return;
                    }

                    await ProcessQueueAsync(webSocket, playerID, queueDTO);
                }
                else if (typeString == typeof(DequeueDTO).Name)
                {
                    DequeueDTO dequeueDTO = data.ToObject<DequeueDTO>();

                    if (dequeueDTO.PlayerID != playerID)
                    {
                        await this.SendObjectAsync(webSocket, new ResponseDTO(false, "DTO's player ID does not match the web socket's player ID."));
                        return;
                    }

                    await ProcessDequeueAsync(webSocket, playerID, dequeueDTO);
                }
                else if (typeString == typeof(SingleActionDTO).Name)
                {
                    SingleActionDTO actionDTO = JsonUtilities.FromJson<SingleActionDTO>(data.ToString());

                    if (actionDTO.Action.PlayerID != playerID)
                    {
                        await this.SendObjectAsync(webSocket, new ResponseDTO(false, "DTO's player ID does not match the web socket's player ID."));
                        return;
                    }

                    // Process the typed data
                    await ProcessSingleActionAsync(webSocket, actionDTO);
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
        private async Task ProcessSingleActionAsync(WebSocket webSocket, SingleActionDTO actionDTO)
        {
            Console.WriteLine($"\nSingle action taken for:\nPlayer ID: {actionDTO.Action.PlayerID}\nSingle Action Type: {actionDTO.Action.SingleActionType}\n");

            int playerID = actionDTO.Action.PlayerID;

            ServerGameStateManager? gameManagerNullable;

            using (var scope = _scopeFactory.CreateScope())
            {
                var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();

                gameManagerNullable = gameCoordinator.GetGameByPlayerID(playerID);
            }

            ServerGameStateManager gameManager = gameManagerNullable;

            if (gameManagerNullable != null && gameManager != null)
            {
                // Enqueue the action to be processed by the authoritative game loop.
                gameManager.EnqueueSingleAction(actionDTO.Action, actionDTO.ClientLogicalTimestamp, playerID);

                // Acknowledge receipt — actual acceptance and broadcasting will occur when the action is processed in the state sync loop.
                await this.SendObjectAsync(webSocket, new ResponseDTO(true, "Action queued for processing."));
            }
            else
            {
                await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Failed to find XR state instance for the player!"));
                Console.Error.WriteLine("Failed to find XR state instance for the player!");
                return;
            }
        }

        /// <summary>
        /// Processes an incoming queue DTO. Send a success/error if the queue game DTO is accepted.
        /// </summary>
        /// <param name="webSocket">The web socket that received the queue game DTO.</param>
        /// <param name="playerID">The player's ID.</param>
        /// <param name="queueDTO">The queue DTO.</param>
        /// <returns></returns>
        private async Task ProcessQueueAsync(WebSocket webSocket, int playerID, QueueDTO queueDTO)
        {
            bool? successfulAction;

            using (var scope = _scopeFactory.CreateScope())
            {
                var queueManager = scope.ServiceProvider.GetRequiredService<QueueManager>();

                // Handle request to enter queue.
                Console.WriteLine($"\nQueue game for:\nPlayer ID: {playerID}\n");
                successfulAction = queueManager.EnqueuePlayer(playerID);

            }

            if (successfulAction == null || successfulAction == false)
            {

                await this.SendObjectAsync(webSocket, new ResponseDTO(false, "Failed to process queue DTO for player."));
                Console.Error.WriteLine("Failed to process queue DTO for player.");
                return;
            }

            await this.SendObjectAsync(webSocket, new ResponseDTO(true, "Successfully processed queue DTO for player."));
            Console.Error.WriteLine("Successfully processed queue DTO for player.");
            return;
        }

        /// <summary>
        /// Processes an incoming queue DTO. Send a success/error if the queue game DTO is accepted.
        /// </summary>
        /// <param name="webSocket">The web socket that received the queue game DTO.</param>
        /// <param name="playerID">The player's ID.</param>
        /// <param name="dequeueDTO">The dequeue DTO.</param>
        /// <returns></returns>
        private async Task ProcessDequeueAsync(WebSocket webSocket, int playerID, DequeueDTO dequeueDTO)
        {
            bool? successfulAction;

            using (var scope = _scopeFactory.CreateScope())
            {
                var queueManager = scope.ServiceProvider.GetRequiredService<QueueManager>();

                // Handle request to leave queue.
                Console.WriteLine($"\nDequeue game for:\nPlayer ID: {playerID}\n");
                queueManager.DequeuePlayer(playerID);
            }

            await this.SendObjectAsync(webSocket, new ResponseDTO(true, "Successfully processed queue DTO for player."));
            Console.Error.WriteLine("Successfully processed queue DTO for player.");
            return;
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
                var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();
                webSocket = gameConnectionManager.GetGameSocketByPlayerID(playerID);
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