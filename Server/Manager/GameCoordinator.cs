using System.Collections.Concurrent;

namespace WebServer.Manager
{
    /// <summary>
    /// A coordinator for all games going on. Creates games and holds game instances in a dictionary for easy access.
    /// In our special case, we only have one game instance and Game Coordinator will always add new players to that game instance.
    /// However, this class is designed to support multiple game instances if needed in the future.
    /// </summary>
    public class GameCoordinator
    {

        private readonly IServiceScopeFactory _scopeFactory;

        /// <summary>
        /// A dictionary of the ongoing games by game IDs.
        /// </summary>
        private ConcurrentDictionary<int, ServerGameStateManager> _gameInstances = new ConcurrentDictionary<int, ServerGameStateManager>();
        /// <summary>
        /// A dictionary of the ongoing game for a player by player ID.
        /// </summary>
        private ConcurrentDictionary<int, int> _gameIDByPlayerID = new ConcurrentDictionary<int, int>();


        public GameCoordinator(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// Generates a new, unique game ID.
        /// </summary>
        /// <returns>The unique game ID.</returns>
        private int GenerateUniqueGameId()
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
            int newGameID = GenerateUniqueGameId();

            // Add to the game instances by game ID.
            while (!_gameInstances.TryAdd(newGameID, gameStateManager))
            {
                newGameID = GenerateUniqueGameId();
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
            ServerGameStateManager gameStateManager = new ServerGameStateManager(_scopeFactory, playerIDs);

            try
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in generating game: {ex.Message}");
            }
        }
    }
}