using SharedLibrary.Game.Actions;

namespace SharedLibrary.SpatialSync
{
    public struct CameraIntrinsics
    {
        public Vector2 FocalLength;

        public Vector2 PrincipalPoint;

        public Vector2Int SensorResolution;

        public Pose LensOffset;
    }

    public struct Vector2
    {
        /// <summary>
        /// X component of the vector.
        /// </summary>
        public float x;

        /// <summary>
        /// Y component of the vector.
        /// </summary>
        public float y;
    }

    public struct Vector2Int
    {
        /// <summary>
        /// X component of the integer vector.
        /// </summary>
        public int x;

        /// <summary>
        /// Y component of the integer vector.
        /// </summary>
        public int y;
    }
}
