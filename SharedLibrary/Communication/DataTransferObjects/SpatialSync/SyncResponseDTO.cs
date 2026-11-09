using SharedLibrary.Game.Actions;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// A DTO response for a key frame sent to pull a synchronization response. Optionally includes a global pose if the global map is ready for the player.
    /// </summary>
    public class SyncResponseDTO
    {
        public long KeyFrameID;
        public Pose CameraLocalPose;

        public bool HasGlobalPose => GlobalPose.HasValue;
        public Pose? GlobalPose;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public SyncResponseDTO() { }

        public SyncResponseDTO(long keyFrameID, Pose cameraLocalPose)
        {
            KeyFrameID = keyFrameID;
            CameraLocalPose = cameraLocalPose;
            GlobalPose = null;
        }

        public SyncResponseDTO(long keyFrameID, Pose cameraLocalPose, Pose globalPose)
        {
            KeyFrameID = keyFrameID;
            CameraLocalPose = cameraLocalPose;
            GlobalPose = globalPose;
        }
    }
}
