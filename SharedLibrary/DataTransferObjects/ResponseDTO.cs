using Newtonsoft.Json;

namespace SharedModels.WebCommunication.DataTransferObjects
{
    public class ResponseDTO
    {
        [JsonProperty]
        public bool Success { get; set; }

        [JsonProperty]
        public string Message { get; set; }

        /// <summary>
        /// Parameterless constructor for JSON Deserialization between web server and client
        /// </summary>
        public ResponseDTO() { }

        [JsonConstructor]
        public ResponseDTO(bool success, string message)
        {
            Success = success;
            Message = message;
        }
    }
}