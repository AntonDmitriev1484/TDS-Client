using SharedLibrary.Game;
using System;
using System.Collections.Generic;

namespace SharedLibrary.Communication.DataTransferObjects
{
    /// <summary>
    /// A full snapshot of the game state. Sent to start the game, and to resync a client whose
    /// action was rejected. Receiving one is how a client learns it lost grab permission.
    /// </summary>
    [Serializable]
    public class GameStateDTO
    {
        public double ServerTime;
        public Dictionary<int, long> PlayerLogicalTimestamps = new Dictionary<int, long>();
        public List<CubeState> Cubes = new List<CubeState>();
        public int Round;
        public Dictionary<int, int> Scores = new Dictionary<int, int>();
        public Dictionary<int, GoalCubeState> Goals = new Dictionary<int, GoalCubeState>();
        public BoardState Board;

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public GameStateDTO() { }

        public GameStateDTO(double serverTime, Dictionary<int, long> playerLogicalTimestamps, GameState gameState)
        {
            ServerTime = serverTime;
            PlayerLogicalTimestamps = new Dictionary<int, long>(playerLogicalTimestamps);

            foreach (CubeState cube in gameState.Cubes.Values)
                Cubes.Add(cube.GetDeepCopy());

            Round = gameState.Round;
            Scores = new Dictionary<int, int>(gameState.Scores);
            foreach (var kvp in gameState.Goals)
                Goals.Add(kvp.Key, kvp.Value.GetDeepCopy());
            Board = gameState.Board?.GetDeepCopy();
        }
    }
}
