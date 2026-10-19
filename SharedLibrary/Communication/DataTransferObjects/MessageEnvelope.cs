using Newtonsoft.Json;
using System;

namespace SharedLibrary.Communication.DataTransferObjects
{
    [Serializable]
    public class MessageEnvelope<T>
    {
        [JsonProperty("timestamp")]
        public float Timestamp { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; }
    }
}