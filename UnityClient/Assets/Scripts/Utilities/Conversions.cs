namespace Assets.Scripts.Utilities
{
    internal static class Conversions
    {
        /// <summary>
        /// Convert a UnityEngine.Pose to the shared Pose (SharedModels.Game.Pose).
        /// </summary>
        public static SharedModels.Game.Pose ToSharedPose(UnityEngine.Pose unityPose)
        {
            var pos = new System.Numerics.Vector3(unityPose.position.x, unityPose.position.y, unityPose.position.z);
            var rot = new System.Numerics.Quaternion(unityPose.rotation.x, unityPose.rotation.y, unityPose.rotation.z, unityPose.rotation.w);
            return new SharedModels.Game.Pose(pos, rot);
        }

        /// <summary>
        /// Convert a Unity position & rotation to the shared Pose.
        /// </summary>
        public static SharedModels.Game.Pose ToSharedPose(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation)
        {
            var pos = new System.Numerics.Vector3(position.x, position.y, position.z);
            var rot = new System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            return new SharedModels.Game.Pose(pos, rot);
        }

        /// <summary>
        /// Convert a Unity Vector3 position to a System.Numerics.Vector3 used by shared types.
        /// </summary>
        public static System.Numerics.Vector3 ToSharedPosition(UnityEngine.Vector3 position)
        {
            return new System.Numerics.Vector3(position.x, position.y, position.z);
        }

        /// <summary>
        /// Convert a Unity Quaternion to a System.Numerics.Quaternion used by shared types.
        /// </summary>
        public static System.Numerics.Quaternion ToSharedRotation(UnityEngine.Quaternion rotation)
        {
            return new System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        }

        /// <summary>
        /// Convert a shared Pose (SharedModels.Game.Pose) to UnityEngine.Pose.
        /// </summary>
        public static UnityEngine.Pose ToUnityPose(SharedModels.Game.Pose sharedPose)
        {
            var pos = new UnityEngine.Vector3(sharedPose.position.X, sharedPose.position.Y, sharedPose.position.Z);
            var rot = new UnityEngine.Quaternion(sharedPose.rotation.X, sharedPose.rotation.Y, sharedPose.rotation.Z, sharedPose.rotation.W);
            return new UnityEngine.Pose(pos, rot);
        }

        /// <summary>
        /// Get the position of a shared Pose as a Unity Vector3.
        /// </summary>
        public static UnityEngine.Vector3 ToUnityPosition(SharedModels.Game.Pose sharedPose)
        {
            return new UnityEngine.Vector3(sharedPose.position.X, sharedPose.position.Y, sharedPose.position.Z);
        }

        /// <summary>
        /// Get the rotation of a shared Pose as a Unity Quaternion.
        /// </summary>
        public static UnityEngine.Quaternion ToUnityRotation(SharedModels.Game.Pose sharedPose)
        {
            return new UnityEngine.Quaternion(sharedPose.rotation.X, sharedPose.rotation.Y, sharedPose.rotation.Z, sharedPose.rotation.W);
        }
    }
}
