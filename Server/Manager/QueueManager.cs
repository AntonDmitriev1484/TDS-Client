using System.Collections.Concurrent;

namespace Server.Manager
{
    /// <summary>
    /// Handles adding users to a queue for the main game session.
    /// </summary>
    public class QueueManager
    {

        private readonly IServiceScopeFactory _scopeFactory;

        private readonly ConcurrentDictionary<int, bool> _queue;


        public QueueManager(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;

            _queue = new ConcurrentDictionary<int, bool>();

            ProcessQueue();
        }

        /// <summary>
        /// Enqueue a player for the given application type.
        /// </summary>
        /// <param name="playerID">The ID of the player to queue.</param>
        /// <returns>True if the player could be queued, false otherwise (if the player is already in the queue or in a game).</returns>
        public bool EnqueuePlayer(int playerID)
        {

            // Check to see if the player is already in the game.


            // Try to add it to this current queue.
            bool successfulAdd = _queue.TryAdd(playerID, true);

            if (!successfulAdd)
            {
                Console.WriteLine($"Player with ID: {playerID} is already in the queue!");
                return false;
            }

            Console.WriteLine($"Player with ID: {playerID} has been added to the queue.");

            return true;
        }

        /// <summary>
        /// Remove a player from any queues it may be in.
        /// </summary>
        /// <param name="playerID">The ID of the player.</param>
        public void DequeuePlayer(int playerID)
        {
            _queue.TryRemove(playerID, out _);
        }

        /// <summary>
        /// Process each queue and attempt to match players.
        /// This repeats infinitely, either restarting every 5 seconds or after all matchings have been made in the past interation.
        /// Current implementation only allows for a single game instance, so new players in queue are automatically connected to the same game instance.
        /// </summary>
        public async Task ProcessQueue()
        {
            List<Task> tasks = new List<Task>();

            tasks.Add(Task.Delay(5000));

            List<int> playersToMatch = _queue.Keys.ToList();


            if (playersToMatch.Count > 1)
            {
                // If a game instance already exists, add all players to that game instance.
                int? firstInstanceID;

                using (var scope = _scopeFactory.CreateScope())
                {
                    var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();
                    firstInstanceID = gameCoordinator.GetGameIDOfFirstInstance();
                    if (firstInstanceID != null)
                    {
                        foreach (int playerID in playersToMatch)
                        {
                            Task addPlayerTask = gameCoordinator.AddPlayerToGame(playerID, firstInstanceID.Value);
                            tasks.Add(addPlayerTask);
                        }
                    }
                }

                // Otherwise, create a new game instance and add all players to it.
                if (firstInstanceID == null)
                {
                    Task matchPlayersTask = HandleMatchedPlayers(playersToMatch);
                    tasks.Add(matchPlayersTask);
                }
            }

            await Task.WhenAll(tasks);

            ProcessQueue();
        }

        /// <summary>
        /// Handles when a set of players have been matched for a given game type.
        /// </summary>
        /// <param name="playerIDs">The ID of the players.</param>
        private async Task HandleMatchedPlayers(List<int> playerIDs)
        {

            // Dequeue each of the players being matched.
            foreach (var playerID in playerIDs)
            {
                DequeuePlayer(playerID);
            }


            // Create a game.
            using (var scope = _scopeFactory.CreateScope())
            {
                var gameCoordinator = scope.ServiceProvider.GetRequiredService<GameCoordinator>();
                await gameCoordinator.GenerateGame(playerIDs);
            }
        }
    }
}