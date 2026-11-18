using System;
// Using local Vector3/Quaternion types in SharedLibrary.Game

namespace SharedLibrary.Game.Actions
{
    /// <summary>
    /// Requests grab permission for a cube. Granted only if no other player holds it.
    /// The action also carries where the cube sits relative to the hand that grabs it (<see cref="FollowPose"/>), worked out once when the
    /// player pinched. Everywhere the action is applied (including when a client replays it on top of a newer server state) the cube is
    /// held at that same offset, instead of working it out again from wherever the cube and hand happen to be by then.
    /// </summary>
    [Serializable]
    public class GrabCubeAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.GrabCube;
        public CubeFollowTarget FollowTarget;

        /// <summary>
        /// The cube's pose in the space of the hand that grabs it (<see cref="FollowTarget"/>), at the moment of the grab: the position and
        /// rotation it keeps relative to the hand while held. Only meaningful if <see cref="HasFollowPose"/>.
        /// </summary>
        public Pose FollowPose;

        /// <summary>Whether <see cref="FollowPose"/> was set. False for a grab made without one, whose offset the client works out itself.</summary>
        public bool HasFollowPose;

        public GrabCubeAction() { }

        public GrabCubeAction(int playerID, double serverTime, string cubeID, CubeFollowTarget cubeFollowTarget) : base(playerID, serverTime, cubeID)
        {
            if (cubeFollowTarget == CubeFollowTarget.None)
            {
                throw new Exception($"Follow target {cubeFollowTarget} is not supported (Use ReleaseCubeAction func instead).");
            }

            FollowTarget = cubeFollowTarget;
        }

        /// <param name="followPose">The cube's pose relative to the grabbing hand at the moment of the grab.</param>
        public GrabCubeAction(int playerID, double serverTime, string cubeID, CubeFollowTarget cubeFollowTarget, Pose followPose)
            : this(playerID, serverTime, cubeID, cubeFollowTarget)
        {
            FollowPose = followPose;
            HasFollowPose = true;
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
