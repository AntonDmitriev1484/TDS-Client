namespace Assets.Scripts.Utilities
{
    internal static class Conversions
    {
        /// <summary>
        /// Convert a UnityEngine.Pose to the shared Pose (SharedLibrary.Game.Actions.Pose).
        /// </summary>
        public static SharedLibrary.Game.Actions.Pose ToSharedPose(UnityEngine.Pose unityPose)
        {
            var pos = new SharedLibrary.Game.Vector3(unityPose.position.x, unityPose.position.y, unityPose.position.z);
            var rot = new SharedLibrary.Game.Quaternion(unityPose.rotation.x, unityPose.rotation.y, unityPose.rotation.z, unityPose.rotation.w);
            return new SharedLibrary.Game.Actions.Pose(pos, rot);
        }

        /// <summary>
        /// Convert a Unity position & rotation to the shared Pose.
        /// </summary>
        public static SharedLibrary.Game.Actions.Pose ToSharedPose(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation)
        {
            var pos = new SharedLibrary.Game.Vector3(position.x, position.y, position.z);
            var rot = new SharedLibrary.Game.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            return new SharedLibrary.Game.Actions.Pose(pos, rot);
        }

        /// <summary>
        /// Convert a Unity Vector3 position to a System.Numerics.Vector3 used by shared types.
        /// </summary>
        public static SharedLibrary.Game.Vector3 ToSharedPosition(UnityEngine.Vector3 position)
        {
            return new SharedLibrary.Game.Vector3(position.x, position.y, position.z);
        }

        /// <summary>
        /// Convert a Unity Quaternion to a System.Numerics.Quaternion used by shared types.
        /// </summary>
        public static SharedLibrary.Game.Quaternion ToSharedRotation(UnityEngine.Quaternion rotation)
        {
            return new SharedLibrary.Game.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
        }

        /// <summary>
        /// Convert a shared Pose (SharedLibrary.Game.Actions.Pose) to UnityEngine.Pose.
        /// </summary>
        public static UnityEngine.Pose ToUnityPose(SharedLibrary.Game.Actions.Pose sharedPose)
        {
            var pos = new UnityEngine.Vector3(sharedPose.Position.X, sharedPose.Position.Y, sharedPose.Position.Z);
            var rot = new UnityEngine.Quaternion(sharedPose.Rotation.X, sharedPose.Rotation.Y, sharedPose.Rotation.Z, sharedPose.Rotation.W);
            return new UnityEngine.Pose(pos, rot);
        }

        /// <summary>
        /// Get the position of a shared Pose as a Unity Vector3.
        /// </summary>
        public static UnityEngine.Vector3 ToUnityPosition(SharedLibrary.Game.Actions.Pose sharedPose)
        {
            return new UnityEngine.Vector3(sharedPose.Position.X, sharedPose.Position.Y, sharedPose.Position.Z);
        }

        /// <summary>
        /// Convert a shared Vector3 to a Unity Vector3.
        /// </summary>
        public static UnityEngine.Vector3 ToUnityVector(SharedLibrary.Game.Vector3 vector)
        {
            return new UnityEngine.Vector3(vector.X, vector.Y, vector.Z);
        }

        /// <summary>
        /// Get the rotation of a shared Pose as a Unity Quaternion.
        /// </summary>
        public static UnityEngine.Quaternion ToUnityRotation(SharedLibrary.Game.Actions.Pose sharedPose)
        {
            return new UnityEngine.Quaternion(sharedPose.Rotation.X, sharedPose.Rotation.Y, sharedPose.Rotation.Z, sharedPose.Rotation.W);
        }
    }
}
