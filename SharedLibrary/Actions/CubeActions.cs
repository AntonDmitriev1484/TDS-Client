using System;
using System.Numerics;

namespace SharedModels.Game.Actions
{
    /// <summary>
    /// Requests grab permission for a cube. Granted only if no other player holds it.
    /// </summary>
    [Serializable]
    public class GrabCubeAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.GrabCube;

        public GrabCubeAction() { }

        public GrabCubeAction(int playerID, string cubeID) : base(playerID, cubeID) { }
    }

    /// <summary>
    /// Gives up grab permission for a cube. Only valid for the player who holds it.
    /// </summary>
    [Serializable]
    public class ReleaseCubeAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.ReleaseCube;

        public ReleaseCubeAction() { }

        public ReleaseCubeAction(int playerID, string cubeID) : base(playerID, cubeID) { }
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

        public MoveCubeAction(int playerID, string cubeID, Pose pose) : base(playerID, cubeID)
        {
            _position = pose.position;
            _rotation = pose.rotation;
        }
    }
}
