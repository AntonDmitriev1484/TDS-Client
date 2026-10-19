using SharedLibrary.Utilities;
using System;

namespace SharedLibrary.Communication.DataTransferObjects
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
            return JsonUtilities.ToJson(new Envelope { type = dto.GetType().Name, data = JsonUtilities.ToJson(dto) });
        }

        public static object Deserialize(string json)
        {
            Envelope envelope = JsonUtilities.FromJson<Envelope>(json);

            switch (envelope.type)
            {
                case nameof(QueueDTO): return JsonUtilities.FromJson<QueueDTO>(envelope.data);
                case nameof(DequeueDTO): return JsonUtilities.FromJson<DequeueDTO>(envelope.data);
                case nameof(SingleActionDTO): return JsonUtilities.FromJson<SingleActionDTO>(envelope.data);
                case nameof(GameStateDTO): return JsonUtilities.FromJson<GameStateDTO>(envelope.data);
                default: throw new ArgumentException($"Unknown DTO type '{envelope.type}'.", nameof(json));
            }
        }
    }
}
