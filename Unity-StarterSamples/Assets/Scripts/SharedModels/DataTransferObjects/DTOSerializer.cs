using System;
using UnityEngine;

namespace SharedModels.WebCommunication.DataTransferObjects
{
    /// <summary>
    /// Wire format for DTOs: <c>{"type": "&lt;DTO class name&gt;", "data": "&lt;DTO as JSON&gt;"}</c>.
    /// The data is a nested JSON string rather than an object because JsonUtility can't hold an
    /// object of unknown type.
    /// </summary>
    public static class DTOSerializer
    {
        [Serializable]
        class Envelope
        {
            public string type;
            public string data;
        }

        public static string Serialize(object dto)
        {
            return JsonUtility.ToJson(new Envelope { type = dto.GetType().Name, data = JsonUtility.ToJson(dto) });
        }

        public static object Deserialize(string json)
        {
            Envelope envelope = JsonUtility.FromJson<Envelope>(json);

            switch (envelope.type)
            {
                case nameof(ConnectDTO): return JsonUtility.FromJson<ConnectDTO>(envelope.data);
                case nameof(SingleActionDTO): return JsonUtility.FromJson<SingleActionDTO>(envelope.data);
                case nameof(GameStateDTO): return JsonUtility.FromJson<GameStateDTO>(envelope.data);
                default: throw new ArgumentException($"Unknown DTO type '{envelope.type}'.", nameof(json));
            }
        }
    }
}
