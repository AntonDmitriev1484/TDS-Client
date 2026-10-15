using Newtonsoft.Json;
using System;

namespace SharedModels.WebCommunication.DataTransferObjects
{
    [Serializable]
    public class MessageEnvelope<T>
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; }
    }
}