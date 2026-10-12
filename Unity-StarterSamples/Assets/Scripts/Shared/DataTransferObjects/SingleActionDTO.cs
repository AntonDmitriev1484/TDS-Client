using System;
using SharedModels.Game.Actions;
using UnityEngine;

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
        [SerializeField] SingleActionType _actionType;
        [SerializeField] string _actionJson;

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
            _actionJson = JsonUtility.ToJson(action);
        }

        static SingleAction DeserializeAction(SingleActionType actionType, string json)
        {
            switch (actionType)
            {
                case SingleActionType.GrabCube: return JsonUtility.FromJson<GrabCubeAction>(json);
                case SingleActionType.ReleaseCube: return JsonUtility.FromJson<ReleaseCubeAction>(json);
                case SingleActionType.MoveCube: return JsonUtility.FromJson<MoveCubeAction>(json);
                default: throw new ArgumentOutOfRangeException(nameof(actionType), $"Unknown SingleActionType: {actionType}");
            }
        }
    }
}
