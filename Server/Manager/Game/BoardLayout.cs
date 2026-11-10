using Server.Manager.SpatialSync;
using SharedLibrary.Game;

namespace Server.Manager.Game
{
    /// <summary>
    /// Where the board and goals go, in the shared (global) frame: the point they are laid out around, and which way the board points.
    /// The board's +Z axis (<see cref="Forward"/>) runs from player 1's end to player 2's end, so each player's goal is at their own end.
    /// </summary>
    public readonly struct BoardLayout
    {
        /// <summary>How far above the floor the layout's center is (meters).</summary>
        public const float CenterHeightAboveFloor = 1.75f;

        /// <summary>Two heads closer than this (horizontally) give no usable direction between them (meters).</summary>
        const float MinHeadSeparation = 0.1f;

        /// <summary>The point the board and goals are laid out around: between the players, <see cref="CenterHeightAboveFloor"/> above the floor.</summary>
        public Vector3 Center { get; }

        /// <summary>Turn about the vertical axis (radians) that points the board's +Z from player 1's end toward player 2's.</summary>
        public float Yaw { get; }

        public BoardLayout(Vector3 center, float yaw)
        {
            Center = center;
            Yaw = yaw;
        }

        /// <summary>Unit horizontal vector from player 1's end toward player 2's end.</summary>
        public Vector3 Forward => new Vector3(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));

        /// <summary>The board's rotation, which player 1's goal shares: +Z toward player 2.</summary>
        public Quaternion Player1Rotation => YawRotation(Yaw);

        /// <summary>Player 2's goal faces back across the board, toward player 1.</summary>
        public Quaternion Player2Rotation => YawRotation(Yaw + MathF.PI);

        /// <summary>
        /// The layout used when nobody's head position is known: player 1 at the origin of the shared frame facing +Z, player 2 mirrored 1 m
        /// further along +Z, with the center halfway between them at head height (y = 0).
        /// </summary>
        public static BoardLayout Default => new BoardLayout(new Vector3(0f, 0f, 0.5f), 0f);

        /// <summary>
        /// The layout between two heads: centered on the horizontal midpoint of the heads, <see cref="CenterHeightAboveFloor"/> above
        /// <paramref name="floorY"/>, and pointing from player 1's head toward player 2's.
        /// </summary>
        public static BoardLayout BetweenHeads(Vector3 player1Head, Vector3 player2Head, float floorY)
        {
            var center = new Vector3((player1Head.X + player2Head.X) * 0.5f, floorY + CenterHeightAboveFloor, (player1Head.Z + player2Head.Z) * 0.5f);

            float dx = player2Head.X - player1Head.X;
            float dz = player2Head.Z - player1Head.Z;
            if (dx * dx + dz * dz < MinHeadSeparation * MinHeadSeparation)
            {
                Console.WriteLine("[BoardLayout] The players' heads are nearly on top of each other; pointing the board along +Z.");
                return new BoardLayout(center, 0f);
            }

            return new BoardLayout(center, MathF.Atan2(dx, dz));
        }

        /// <summary>
        /// The layout for one player: the center <paramref name="distanceAhead"/> in front of their head (horizontally),
        /// <see cref="CenterHeightAboveFloor"/> above <paramref name="floorY"/>, with the board pointing the way they face.
        /// </summary>
        public static BoardLayout InFrontOf(SharedLibrary.Game.Actions.Pose head, float floorY, float distanceAhead)
        {
            Vector3 facing = head.Rotation * new Vector3(0f, 0f, 1f);
            float yaw = facing.X * facing.X + facing.Z * facing.Z > 1e-6f ? MathF.Atan2(facing.X, facing.Z) : 0f;
            var layout = new BoardLayout(Vector3.Zero, yaw);
            Vector3 ahead = head.Position + layout.Forward * distanceAhead;
            return new BoardLayout(new Vector3(ahead.X, floorY + CenterHeightAboveFloor, ahead.Z), yaw);
        }

        /// <summary>
        /// The layout from the players' head positions in a formed global map. With both players' heads it is <see cref="BetweenHeads"/>;
        /// with only player 1's or only player 2's, <see cref="InFrontOf"/> that player (as if the other stood across the board); with neither,
        /// <see cref="Default"/>. The floor is the global map's lowest FLOOR anchor, or y = 0 if it has none.
        /// </summary>
        public static BoardLayout FromGlobalMap(GlobalMappingManager map, int player1ID, int player2ID, float distanceAhead)
        {
            var player1Head = map.GetPlayerGlobalPose(player1ID);
            var player2Head = map.GetPlayerGlobalPose(player2ID);

            float floorY = 0f;
            var floors = map.GetGlobalAnchors().Where(anchor => anchor.Label == "FLOOR").ToList();
            if (floors.Count > 0)
                floorY = floors.Min(anchor => anchor.GlobalPose.Position.Y);
            else
                Console.WriteLine("[BoardLayout] The global map has no FLOOR anchor; taking the floor to be at y = 0.");

            BoardLayout layout;
            if (player1Head.HasValue && player2Head.HasValue)
                layout = BetweenHeads(player1Head.Value.Position, player2Head.Value.Position, floorY);
            else if (player1Head.HasValue)
                layout = InFrontOf(player1Head.Value, floorY, distanceAhead);
            else if (player2Head.HasValue)
            {
                // Player 2's end is the one nearest them, so the board points back toward them from the far side.
                BoardLayout facing = InFrontOf(player2Head.Value, floorY, distanceAhead);
                layout = new BoardLayout(facing.Center, facing.Yaw + MathF.PI);
            }
            else
            {
                Console.WriteLine($"[BoardLayout] Neither player {player1ID}'s nor player {player2ID}'s head is in the global map; using the default layout.");
                return Default;
            }

            Console.WriteLine($"[BoardLayout] Board centered at ({layout.Center.X:0.00}, {layout.Center.Y:0.00}, {layout.Center.Z:0.00}) " +
                              $"(floor at y = {floorY:0.00}), yaw {layout.Yaw * 180f / MathF.PI:0.0} deg.");
            return layout;
        }

        static Quaternion YawRotation(float yaw) => new Quaternion(0f, MathF.Sin(yaw * 0.5f), 0f, MathF.Cos(yaw * 0.5f));
    }
}
