using SharedLibrary.Game.Actions;
using System;

namespace SharedLibrary.Game
{
    /// <summary>
    /// A player's goal: a semi-transparent cube that scores for that player when a cube is held inside it.
    /// </summary>
    [Serializable]
    public class GoalCubeState
    {

        #region Properties

        /// <summary>The player this goal scores for.</summary>
        public int PlayerID;

        public RGBAColor Color;

        /// <summary>The pose of the goal's center.</summary>
        public Pose Pose;

        /// <summary>Edge length of the goal cube (meters).</summary>
        public float CubeSize;

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public GoalCubeState() { }

        public GoalCubeState(int playerID, RGBAColor color, Pose pose, float cubeSize)
        {
            PlayerID = playerID;
            Color = color;
            Pose = pose;
            CubeSize = cubeSize;
        }

        #endregion Constructors

        /// <summary>True if <paramref name="point"/> lies within the goal's bounds.</summary>
        public bool Contains(Vector3 point)
        {
            Vector3 local = Quaternion.Inverse(Pose.Rotation) *(point - Pose.Position);
            float half = CubeSize * 0.5f;
            return Math.Abs(local.X) <= half && Math.Abs(local.Y) <= half && Math.Abs(local.Z) <= half;
        }

        /// <summary>
        /// Get a deep copy of this object, so a DTO never shares data with the <see cref="GameState"/> that built it.
        /// </summary>
        public GoalCubeState GetDeepCopy()
        {
            return new GoalCubeState(PlayerID, Color, Pose, CubeSize);
        }
    }
}
