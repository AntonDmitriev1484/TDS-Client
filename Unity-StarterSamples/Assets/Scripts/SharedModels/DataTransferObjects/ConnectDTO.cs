using System;

namespace SharedModels.WebCommunication.DataTransferObjects
{
    /// <summary>
    /// The first message a client sends on a new connection, saying which player it is. Stands in
    /// for the player ID the real server takes from the WebSocket request.
    /// </summary>
    [Serializable]
    public class ConnectDTO
    {
        public int PlayerID;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public ConnectDTO() { }

        public ConnectDTO(int playerID)
        {
            PlayerID = playerID;
        }
    }
}
