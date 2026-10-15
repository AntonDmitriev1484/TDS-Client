using System;
using SharedModels.Game.Actions;
using SharedModels.Utilities;

namespace SharedModels.WebCommunication.DataTransferObjects
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
        SingleActionType _actionType;
        string _actionJson;

        [NonSerialized] SingleAction _action;

        public SingleAction Action => _action ??= DeserializeAction(_actionType, _actionJson);

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public SingleActionDTO() { }

        public SingleActionDTO(SingleAction action)
        {
            _action = action;
            _actionType = action.SingleActionType;
            _actionJson = JsonUtilities.ToJson(action);
        }

        static SingleAction DeserializeAction(SingleActionType actionType, string json)
        {
            switch (actionType)
            {
                case SingleActionType.GrabCube: return JsonUtilities.FromJson<GrabCubeAction>(json);
                case SingleActionType.ReleaseCube: return JsonUtilities.FromJson<ReleaseCubeAction>(json);
                case SingleActionType.MoveCube: return JsonUtilities.FromJson<MoveCubeAction>(json);
                default: throw new ArgumentOutOfRangeException(nameof(actionType), $"Unknown SingleActionType: {actionType}");
            }
        }
    }
}
