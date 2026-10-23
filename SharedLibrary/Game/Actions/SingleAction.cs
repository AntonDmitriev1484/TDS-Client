using System;

namespace SharedLibrary.Game.Actions
{
    /// <summary>
    /// Represents the most atomic game action possible. Every change a player makes to the game
    /// state goes through one of these, on the client and on the server alike.
    ///
    /// Fields are [SerializeField] rather than properties so JsonUtility can serialize them.
    /// </summary>
    [Serializable]
    public abstract class SingleAction
    {
        int _playerID;

        public int PlayerID => _playerID;

        double _serverTime;

        public double ServerTime => _serverTime;

        public abstract SingleActionType SingleActionType { get; }

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        protected SingleAction() { }

        protected SingleAction(int playerID, double serverTime)
        {
            _playerID = playerID;
            _serverTime = serverTime;
        }

        /// <summary>
        /// Get description text for this action.
        /// </summary>
        public virtual string FormatActionText()
        {
            return $"{SingleActionType} by player {PlayerID}";
        }
    }

    /// <summary>
    /// An action that targets one cube.
    /// </summary>
    [Serializable]
    public abstract class CubeAction : SingleAction
    {
        string _cubeID;

        public string CubeID => _cubeID;

        protected CubeAction() { }

        protected CubeAction(int playerID, double serverTime, string cubeID) : base(playerID, serverTime)
        {
            _cubeID = cubeID;
        }

        public override string FormatActionText()
        {
            return $"{base.FormatActionText()} on cube {CubeID}";
        }
    }

    public enum SingleActionType
    {
        GrabCube,
        ReleaseCube,
        MoveCube,
    }
}
