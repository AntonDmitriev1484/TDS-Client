using System.Collections.Concurrent;

namespace Server.Manager.Game
{
    /// <summary>
    /// A coordinator for all games going on. Creates games and holds game instances in a dictionary for easy access.
    /// In our special case, we only have one game instance and Game Coordinator will always add new players to that game instance.
    /// However, this class is designed to support multiple game instances if needed in the future.
    /// </summary>
    public class GameCoordinator
    {

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ServerSettings _serverSettings;

        /// <summary>
        /// A dictionary of the ongoing games by game IDs.
        /// </summary>
        private ConcurrentDictionary<int, ServerGameStateManager> _gameInstances = new ConcurrentDictionary<int, ServerGameStateManager>();
        /// <summary>
        /// A dictionary of the ongoing game for a player by player ID.
        /// </summary>
        private ConcurrentDictionary<int, int> _gameIDByPlayerID = new ConcurrentDictionary<int, int>();


        public GameCoordinator(IServiceScopeFactory scopeFactory, ServerSettings serverSettings)
        {
            _scopeFactory = scopeFactory;
            _serverSettings = serverSettings;
        }

        /// <summary>
        /// Generates a new, unique game ID.
        /// </summary>
        /// <returns>The unique game ID.</returns>
        private int GenerateUniqueGameID()
        {
            int newID;
            do
            {
                newID = new Random().Next(1, int.MaxValue);
            } while (_gameInstances.ContainsKey(newID));

            return newID;
        }

        /// <summary>
        /// Adds a game manager to this coordinator. Generates a new, unique number game ID for the game.
        /// </summary>
        /// <param name="gameStateManager">The manager of the game to add.</param>
        /// <returns></returns>
        public int AddGame(ServerGameStateManager gameStateManager)
        {
            int newGameID = GenerateUniqueGameID();

            // Add to the game instances by game ID.
            while (!_gameInstances.TryAdd(newGameID, gameStateManager))
            {
                newGameID = GenerateUniqueGameID();
            }

            // Add to the game instances by player IDs.
            foreach (int playerID in gameStateManager.GameState.PlayerIDs.ToList())
            {
                if (!_gameIDByPlayerID.TryAdd(playerID, newGameID))
                {
                    Console.WriteLine("The player already has a game instance associated with it!");
                }
            }

            gameStateManager.ID = newGameID;

            return newGameID;
        }

        /// <summary>
        /// Removes a game manager from this coordinator. Should be called when a game finishes.
        /// </summary>
        /// <param name="gameID">The ID of the game to remove.</param>
        /// <returns>True if the game is successfully found and removed. False if otherwise.</returns>
        public bool RemoveGame(int gameID)
        {
            ServerGameStateManager gameStateManager;

            bool successfulRemoval = _gameInstances.TryRemove(gameID, out gameStateManager);

            if (successfulRemoval && gameStateManager != null)
            {
                foreach (int playerID in gameStateManager.GameState.PlayerIDs.ToList())
                {
                    _gameIDByPlayerID.TryRemove(playerID, out _);
                }
            }
            else
            {
                Console.Error.WriteLine("Failed to find the specified game to remove!");
            }

            return successfulRemoval;
        }


        /// <summary>
        /// Determines whether a game with the specified ID exists.
        /// </summary>
        /// <remarks>Performs a lookup in the internal game instances dictionary.</remarks>
        /// <param name="gameID">The game identifier to check for existence.</param>
        /// <returns>true if a game with the specified ID exists; otherwise, false.</returns>
        public bool GameExists(int gameID)
        {
            return _gameInstances.ContainsKey(gameID);
        }

        /// <summary>
        /// Grab a game from the list of ongoing games.
        /// </summary>
        /// <param name="gameID">The ID of the game to get.</param>
        /// <returns>The game requested, or null if it could not be found.</returns>
        public ServerGameStateManager? GetGame(int gameID)
        {
            ServerGameStateManager? gameStateManager = null;

            _gameInstances.TryGetValue(gameID, out gameStateManager);

            return gameStateManager;
        }

        /// <summary>
        /// Grab a game from the dictionary of games by player ID.
        /// </summary>
        /// <param name="playerID">The ID of the player to get the game for.</param>
        /// <returns>The game requested, or null if it could not be found.</returns>
        public ServerGameStateManager? GetGameByPlayerID(int playerID)
        {
            int gameID;

            _gameIDByPlayerID.TryGetValue(playerID, out gameID);

            ServerGameStateManager? gameStateManager = null;

            _gameInstances.TryGetValue(gameID, out gameStateManager);

            return gameStateManager;
        }

        /// <summary>
        /// Generate a game for a set of players.
        /// </summary>
        /// <param name="playerIDs">The list of player IDs for the players to generate the game for.</param>
        /// <returns>The manager for the generated game instance.</returns>
        public async Task GenerateGame(List<int> playerIDs)
        {
            try
            {
                ServerGameStateManager gameStateManager = new ServerGameStateManager(_scopeFactory, _serverSettings, playerIDs);
                this.AddGame(gameStateManager);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in generating game: {ex.Message}");
            }
        }

        /// <summary>
        /// Adds the player with the specified playerID to the game identified by gameID asynchronously.
        /// </summary>
        /// <remarks>If the game is not found, a message is written to the console and no player is added.
        /// The actual add operation is performed by the game's ServerGameStateManager.</remarks>
        /// <param name="playerID">Identifier of the player to add.</param>
        /// <param name="gameID">Identifier of the target game.</param>
        /// <returns>A task that completes when the player has been added to the game.</returns>
        public async Task AddPlayerToGame(int playerID, int gameID)
        {
            ServerGameStateManager? gameStateManager = GetGame(gameID);
            if (gameStateManager == null)
            {
                Console.WriteLine($"Game with ID: {gameID} not found.");
                return;
            }

            _gameIDByPlayerID.TryAdd(playerID, gameID);

            gameStateManager.AddPlayer(playerID);

            Console.WriteLine($"Player with ID: {playerID} added to game with ID: {gameID}.");
        }

        /// <summary>
        /// Gets the ID of the first game instance, or null if no instances are present.
        /// </summary>
        /// <remarks>Writes a message to the console when no instances are found. If multiple instances
        /// exist, the returned ID corresponds to the first element in the collection's enumeration order.</remarks>
        /// <returns>The ID of the first game instance, or null when no instances exist.</returns>
        public int? GetGameIDOfFirstInstance()
        {
            if (_gameInstances.IsEmpty)
            {
                Console.WriteLine("No game instances found.");
                return null;
            }
            return _gameInstances.Keys.First();
        }
    }
}