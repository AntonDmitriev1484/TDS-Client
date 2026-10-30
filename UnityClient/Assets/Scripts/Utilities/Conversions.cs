namespace Assets.Scripts.Utilities
{
    internal static class Conversions
    {
        /// <summary>
        /// Convert a Unity Vector2 to the shared Vector2.
        /// </summary>
        public static SharedLibrary.SpatialSync.Vector2 ToSharedVector2(UnityEngine.Vector2 vector)
        {
            return new SharedLibrary.SpatialSync.Vector2
            {
                x = vector.x,
                y = vector.y
            };
        }

        /// <summary>
        /// Convert a shared Vector2 to UnityEngine.Vector2.
        /// </summary>
        public static UnityEngine.Vector2 ToUnityVector2(SharedLibrary.SpatialSync.Vector2 vector)
        {
            return new UnityEngine.Vector2(vector.x, vector.y);
        }

        /// <summary>
        /// Convert a Unity Vector2Int to the shared Vector2Int.
        /// </summary>
        public static SharedLibrary.SpatialSync.Vector2Int ToSharedVector2Int(UnityEngine.Vector2Int vector)
        {
            return new SharedLibrary.SpatialSync.Vector2Int
            {
                x = vector.x,
                y = vector.y
            };
        }

        /// <summary>
        /// Convert a shared Vector2Int to UnityEngine.Vector2Int.
        /// </summary>
        public static UnityEngine.Vector2Int ToUnityVector2Int(SharedLibrary.SpatialSync.Vector2Int vector)
        {
            return new UnityEngine.Vector2Int(vector.x, vector.y);
        }

        /// <summary>
        /// Convert Meta XR camera intrinsics to the shared CameraIntrinsics.
        /// </summary>
        public static SharedLibrary.SpatialSync.CameraIntrinsics ToSharedCameraIntrinsics(Meta.XR.PassthroughCameraAccess.CameraIntrinsics cameraIntrinsics)
        {
            return new SharedLibrary.SpatialSync.CameraIntrinsics
            {
                FocalLength = ToSharedVector2(cameraIntrinsics.FocalLength),
                PrincipalPoint = ToSharedVector2(cameraIntrinsics.PrincipalPoint),
                SensorResolution = ToSharedVector2Int(cameraIntrinsics.SensorResolution),
                LensOffset = ToSharedPose(cameraIntrinsics.LensOffset)
            };
        }

        /// <summary>
        /// Convert shared camera intrinsics to Meta XR camera intrinsics.
        /// </summary>
        public static Meta.XR.PassthroughCameraAccess.CameraIntrinsics ToUnityCameraIntrinsics(SharedLibrary.SpatialSync.CameraIntrinsics cameraIntrinsics)
        {
            return new Meta.XR.PassthroughCameraAccess.CameraIntrinsics
            {
                FocalLength = ToUnityVector2(cameraIntrinsics.FocalLength),
                PrincipalPoint = ToUnityVector2(cameraIntrinsics.PrincipalPoint),
                SensorResolution = ToUnityVector2Int(cameraIntrinsics.SensorResolution),
                LensOffset = ToUnityPose(cameraIntrinsics.LensOffset)
            };
        }

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
        /// Get the rotation of a shared Pose as a Unity Quaternion.
        /// </summary>
        public static UnityEngine.Quaternion ToUnityRotation(SharedLibrary.Game.Actions.Pose sharedPose)
        {
            return new UnityEngine.Quaternion(sharedPose.Rotation.X, sharedPose.Rotation.Y, sharedPose.Rotation.Z, sharedPose.Rotation.W);
        }
    }
}
