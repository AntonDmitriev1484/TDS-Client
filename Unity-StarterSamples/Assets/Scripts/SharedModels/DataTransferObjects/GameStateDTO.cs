using System;
using System.Collections.Generic;
using SharedModels.Game;

namespace SharedModels.WebCommunication.DataTransferObjects
{
    /// <summary>
    /// A full snapshot of the game state. Sent to start the game, and to resync a client whose
    /// action was rejected. Receiving one is how a client learns it lost grab permission.
    /// </summary>
    [Serializable]
    public class GameStateDTO
    {
        public List<int> PlayerIDs = new List<int>();
        public List<CubeState> Cubes = new List<CubeState>();

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public GameStateDTO() { }

        public GameStateDTO(CubeGameMaster gameMaster)
        {
            PlayerIDs.AddRange(gameMaster.PlayerIDs);

            foreach (CubeState cube in gameMaster.Cubes.Values)
                Cubes.Add(cube.GetDeepCopy());
        }
    }
}
