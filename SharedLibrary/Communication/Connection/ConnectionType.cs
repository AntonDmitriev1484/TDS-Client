using System;

namespace SharedLibrary.Communication.Connection
{
    public enum ConnectionType
    {
        Application,
    }

    public static class ConnectionTypeExtensions
    {
        public static string APICallEndpoint = "/api";

        public static string ToPath(this ConnectionType connectionType)
        {
            return connectionType switch
            {
                ConnectionType.Application => APICallEndpoint + "/app",
                _ => throw new ArgumentOutOfRangeException(nameof(connectionType), connectionType, null)
            };
        }

        public static string TestConnectionEndpoint = APICallEndpoint + "/test-connection";
        public static string QueueApplicationEndpoint = APICallEndpoint + "/queue";

    }
}