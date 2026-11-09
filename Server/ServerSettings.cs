namespace Server
{
    public sealed class ServerSettings
    {
        public string URL { get; init; }
        public bool DebugMode { get; init; }
        public int MinPlayersPerSession { get; init; }
    }
}
