using System;
using System.Numerics;

namespace SharedModels.Game
{
    /// <summary>
    /// Lightweight Pose: position + rotation. Mirrors Unity's Pose shape used in the project.
    /// Fields are lowercase to match existing usages (pose.position, pose.rotation).
    /// </summary>
    [Serializable]
    public struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;

        public Pose(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }
    }
}
