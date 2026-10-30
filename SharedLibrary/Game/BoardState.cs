using SharedLibrary.Game.Actions;
using System;

namespace SharedLibrary.Game
{
    /// <summary>
    /// The board cubes spawn on: a single flat cube divided into a <see cref="GridSize"/> x
    /// <see cref="GridSize"/> grid of spawn locations across its top face.
    /// </summary>
    [Serializable]
    public class BoardState
    {

        #region Properties

        public RGBAColor Color;

        /// <summary>The pose of the board's center.</summary>
        public Pose Pose;

        /// <summary>Number of grid cells along each horizontal edge of the board.</summary>
        public int GridSize;

        /// <summary>Board dimensions in its local frame (meters): X is width, Y is thickness, Z is depth.</summary>
        public Vector3 BoardSize;

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public BoardState() { }

        public BoardState(RGBAColor color, Pose pose, int gridSize, Vector3 boardSize)
        {
            Color = color;
            Pose = pose;
            GridSize = gridSize;
            BoardSize = boardSize;
        }

        #endregion Constructors

        /// <summary>
        /// Converts a discrete grid location to the world position of a cube of
        /// <paramref name="cubeSize"/> resting on the board's top face at the center of that cell.
        /// </summary>
        public Vector3 GridToPosition(int x, int z, float cubeSize)
        {
            float cellWidth = BoardSize.X / GridSize;
            float cellDepth = BoardSize.Z / GridSize;

            var local = new Vector3(
                -BoardSize.X * 0.5f + (x + 0.5f) * cellWidth,
                (BoardSize.Y + cubeSize) * 0.5f,
                -BoardSize.Z * 0.5f + (z + 0.5f) * cellDepth);

            return Pose.Position + Pose.Rotation * local;
        }

        /// <summary>
        /// Get a deep copy of this object, so a DTO never shares data with the <see cref="GameState"/> that built it.
        /// </summary>
        public BoardState GetDeepCopy()
        {
            return new BoardState(Color, Pose, GridSize, BoardSize);
        }
    }
}
