using System;

namespace SharedLibrary.Game.Actions
{
    /// <summary>
    /// Lightweight Pose: position + rotation. Mirrors Unity's Pose shape used in the project.
    /// Fields are lowercase to match existing usages (pose.position, pose.rotation).
    /// </summary>
    [Serializable]
    public struct Pose(Vector3 position, Quaternion rotation)
    {
        public Vector3 Position = position;
        public Quaternion Rotation = rotation;
    }
}
