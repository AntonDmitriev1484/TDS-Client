using System;

namespace SharedLibrary.Communication.DataTransferObjects.Game
{
    /// <summary>
    /// A DTO for request to join queue by client.
    /// </summary>
    [Serializable]
    public class QueueDTO
    {
        public int PlayerID;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public QueueDTO() { }

        public QueueDTO(int playerID)
        {
            PlayerID = playerID;
        }
    }
}
