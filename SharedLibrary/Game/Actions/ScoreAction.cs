using System;

namespace SharedLibrary.Game.Actions
{
    /// <summary>
    /// A player scored with a cube. Created only by the server, when the cube has stayed inside that
    /// player's goal long enough; <see cref="GameState.HandleSingleAction"/> rejects any sent by a client.
    /// </summary>
    [Serializable]
    public class ScoreAction : CubeAction
    {
        public override SingleActionType SingleActionType => SingleActionType.Score;

        public ScoreAction() { }

        public ScoreAction(int playerID, double serverTime, string cubeID) : base(playerID, serverTime, cubeID) { }
    }
}
