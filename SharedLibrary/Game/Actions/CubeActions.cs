using System;
// Using local Vector3/Quaternion types in SharedLibrary.Game

namespace SharedLibrary.Game.Actions
{
    /// <summary>
    /// Requests grab permission for a cube. Granted only if no other player holds it.
    /// </summary>
    [Serializable]
    public class GrabCubeAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.GrabCube;
        public CubeFollowTarget FollowTarget;

        public GrabCubeAction() { }

        public GrabCubeAction(int playerID, double serverTime, string cubeID, CubeFollowTarget cubeFollowTarget) : base(playerID, serverTime, cubeID)
        {
            if (cubeFollowTarget == CubeFollowTarget.None)
            {
                throw new Exception($"Follow target {cubeFollowTarget} is not supported (Use ReleaseCubeAction func instead).");
            }

            FollowTarget = cubeFollowTarget;
        }
    }

    /// <summary>
    /// Gives up grab permission for a cube. Only valid for the player who holds it.
    /// </summary>
    [Serializable]
    public class ReleaseCubeAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.ReleaseCube;

        public ReleaseCubeAction() { }

        public ReleaseCubeAction(int playerID, double serverTime, string cubeID) : base(playerID, serverTime, cubeID) { }
    }

    /// <summary>
    /// Sets a cube's pose. Only valid for the player who holds it.
    /// </summary>
    [Serializable]
    public class MoveCubeAction : CubeAction
    {
        Vector3 _position;
        Quaternion _rotation;

        public Pose Pose => new Pose(_position, _rotation);

        public override SingleActionType SingleActionType => SingleActionType.MoveCube;

        public MoveCubeAction() { }

        public MoveCubeAction(int playerID, double serverTime, string cubeID, Pose pose) : base(playerID, serverTime, cubeID)
        {
            _position = pose.Position;
            _rotation = pose.Rotation;
        }
    }
}
