using SharedLibrary.Game.Actions;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// A DTO response for a key frame sent to pull a synchronization response.
    /// </summary>
    public class SyncResponseDTO
    {
        public long KeyFrameID;
        public Pose CameraLocalPose;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public SyncResponseDTO() { }

        public SyncResponseDTO(long keyFrameID, Pose cameraLocalPose)
        {
            KeyFrameID = keyFrameID;
            CameraLocalPose = cameraLocalPose;
        }
    }
}
