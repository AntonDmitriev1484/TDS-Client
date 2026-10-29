using System;

namespace SharedLibrary.Communication.DataTransferObjects.Game
{
    /// <summary>
    /// A DTO for request to leave queue by client.
    /// </summary>
    [Serializable]
    public class DequeueDTO
    {
        public int PlayerID;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public DequeueDTO() { }

        public DequeueDTO(int playerID)
        {
            PlayerID = playerID;
        }
    }
}
