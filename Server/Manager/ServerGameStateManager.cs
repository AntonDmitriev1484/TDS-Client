using Server.Handler;
using SharedModels.Game;
using SharedModels.WebCommunication.DataTransferObjects;

/// <summary>
/// Server-side game manager for the online game.
/// It owns the authoritative <see cref="GameState"/>, starts the game, and decides what each player is sent.
/// Inbound messages are handled by <see cref="GameConnectionHandler"/>.
/// The client must connect to this server over a reliable UDP connection.
/// </summary>
public class ServerGameStateManager
{

    #region Properties

    public int ID { get; set; }

    // How far away to spawn from the initial players (meters).
    float _spawnDistance = 1.0f;

    // Empty space between the two starting cubes (meters).
    float _cubeGap = 0.25f;

    /// <summary>The authoritative game state; null until the game starts.</summary>
    public GameState GameState { get; private set; }

    private readonly IServiceScopeFactory _scopeFactory;

    #endregion Properties


    #region Constructor/Destructor

    /// <summary>
    /// Initializes a new ServerGameStateManager, creates a server GameState, and starts game initialization.
    /// </summary>
    /// <remarks>Creates GameState with the server flag set (new GameState(true)) and invokes InitializeGame
    /// to prepare the game.</remarks>
    /// <param name="scopeFactory">Factory for creating IServiceScope instances to resolve scoped services.</param>
    /// <param name="playerIDs">Initial list of player IDs used to populate the game's initial state.</param>
    public ServerGameStateManager(IServiceScopeFactory scopeFactory, List<int> playerIDs)
    {
        _scopeFactory = scopeFactory;
        GameState = new GameState(true);
        InitializeGame(playerIDs);
    }

    #endregion Constructor/Destructor

    #region Initialize Game

    /// <summary>
    /// Builds the authoritative game state and sends it to every connected player. Players who
    /// connect later get it in <see cref="OnPlayerConnected"/>.
    /// <paramref name="playerIDs"/> The list of player IDs who are initially connected to game instance.</param>
    /// </summary>
    void InitializeGame(List<int> playerIDs)
    {
        var cubes = new List<CubeState>();
        Pose[] startingPoses = GetStartingCubePoses();
        for (int i = 0; i < startingPoses.Length; i++)
            cubes.Add(new CubeState($"cube-{i}", startingPoses[i], playerIDs));

        GameState = new GameState(isAuthoritative: true);
        GameState.ServerInitialize(playerIDs, cubes);

        BroadcastGameState();
    }

    /// <summary>Two cubes side by side, <see cref="_spawnDistance"/> in the middle of all users.</summary>
    Pose[] GetStartingCubePoses()
    {
        return new[]
        {
            new Pose(new System.Numerics.Vector3(0.25f, 0, 0), new System.Numerics.Quaternion(0, 0, 0, 1)),
            new Pose(new System.Numerics.Vector3(0, 0, 0), new System.Numerics.Quaternion(0, 0, 0, 1)),
        };
    }

    #endregion Initialize Game

    #region Players

    public bool IsPlayer(int playerID)
    {
        foreach (int id in GameState.PlayerIDs)
        {
            if (id == playerID)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A player joined (or rejoined): send them the whole game state if the game has started.
    /// </summary>
    public void OnPlayerConnected(int playerID)
    {
        if (GameState != null)
            this.AddOutgoingDTO(playerID, new GameStateDTO(GameState));
    }

    /// <summary>
    /// A player dropped: free every cube they were holding so others can grab it.
    /// </summary>
    public void OnPlayerDisconnected(int playerID)
    {
        if (GameState == null)
            return;

        bool changed = false;
        foreach (CubeState cube in GameState.Cubes.Values)
        {
            if (cube.HolderPlayerID == playerID)
                changed |= GameState.RevokeGrabPermissions(cube.Id);
        }

        if (changed)
            BroadcastGameState();
    }

    #endregion Players

    #region Process DTOs

    void BroadcastGameState()
    {
        foreach (int playerID in GameState.PlayerIDs)
            this.AddOutgoingDTO(playerID, new GameStateDTO(GameState));
    }

    #endregion Process DTOs

    #region Send Messages

    public async Task<bool> AddOutgoingDTO(int playerID, object obj)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var sessionConnectionHandler = scope.ServiceProvider.GetRequiredService<GameConnectionHandler>();
            return await sessionConnectionHandler.SendObjectAsync(playerID, obj);
        }

    }

    #endregion Send Messages

}
