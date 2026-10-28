using System;

namespace SharedLibrary.Communication.Connection
{
    public enum ConnectionType
    {
        Game,
        SpatialSync,
    }

    public static class ConnectionTypeExtensions
    {
        public static string APICallEndpoint = "/api";

        public static string ToPath(this ConnectionType connectionType)
        {
            return connectionType switch
            {
                ConnectionType.Game => APICallEndpoint + "/game",
                ConnectionType.SpatialSync => APICallEndpoint + "/spatial-sync",
                _ => throw new ArgumentOutOfRangeException(nameof(connectionType), connectionType, null)
            };
        }

        public static string TestConnectionEndpoint = APICallEndpoint + "/test-connection";
        public static string LoginPlayerEndpoint = APICallEndpoint + "/login";

    }
}