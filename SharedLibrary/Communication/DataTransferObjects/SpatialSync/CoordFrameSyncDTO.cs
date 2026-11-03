using SharedLibrary.Game.Actions;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// A DTO response that includes the global pose for a key frame sent to pull a spatial synchronization response.
    /// </summary>
    public class CoordFrameSyncDTO : SyncResponseDTO
    {
        public Pose GlobalPose;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public CoordFrameSyncDTO() { }

        public CoordFrameSyncDTO(long keyFrameID, Pose cameraLocalPose, Pose globalPose) : base(keyFrameID, cameraLocalPose)
        {
            GlobalPose = globalPose;
        }
    }
}
