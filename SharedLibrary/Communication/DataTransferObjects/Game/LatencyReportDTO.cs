using System;

namespace SharedLibrary.Communication.DataTransferObjects.Game
{
    /// <summary>
    /// A client telling the server its own estimate of its one-way latency to the server (half its average ping round trip, in
    /// seconds). The server has no way to measure this itself, and uses it for the study's latency metric.
    /// </summary>
    [Serializable]
    public class LatencyReportDTO
    {
        public int PlayerID;
        public float EstimatedLatencySeconds;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public LatencyReportDTO() { }

        public LatencyReportDTO(int playerID, float estimatedLatencySeconds)
        {
            PlayerID = playerID;
            EstimatedLatencySeconds = estimatedLatencySeconds;
        }
    }
}
