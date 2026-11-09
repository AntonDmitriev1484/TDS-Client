using SharedLibrary.Game.Actions;
using System;

namespace SharedLibrary.Communication.DataTransferObjects.Game
{
    /// <summary>
    /// Carries one <see cref="SingleAction"/> between client and server.
    ///
    /// JsonUtility has no polymorphism, so the action is stored as its type plus its own JSON and
    /// rebuilt as the right subclass on the receiving end.
    /// </summary>
    [Serializable]
    public class SingleActionDTO
    {
        public SingleActionType ActionType { get; set; }

        public SingleAction Action { get; set; }

        public long ClientLogicalTimestamp { get; set; }

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public SingleActionDTO() { }

        public SingleActionDTO(SingleAction action, long clientLogicalTimestamp = 0)
        {
            ActionType = action.SingleActionType;
            Action = action;
            ClientLogicalTimestamp = clientLogicalTimestamp;
        }
    }
}
