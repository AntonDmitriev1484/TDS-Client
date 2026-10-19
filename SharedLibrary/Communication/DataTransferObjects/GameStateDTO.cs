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
        public float ServerTime;
        public List<int> PlayerIDs = new List<int>();
        public List<CubeState> Cubes = new List<CubeState>();

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public GameStateDTO() { }

        public GameStateDTO(float serverTime, GameState gameState)
        {
            ServerTime = serverTime;
            PlayerIDs.AddRange(gameState.PlayerIDs);

            foreach (CubeState cube in gameState.Cubes.Values)
                Cubes.Add(cube.GetDeepCopy());
        }
    }
}
