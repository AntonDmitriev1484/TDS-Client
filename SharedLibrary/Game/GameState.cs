using SharedLibrary.Communication.DataTransferObjects;
using SharedLibrary.Game.Actions;
using SharedLibrary.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedLibrary.Game
{
    /// <summary>
    /// The game state, held by both the server (authoritative) and each client (a mirror). It owns
    /// the cubes and applies <see cref="SingleAction"/>s to them. It knows nothing about scenes,
    /// hands or sockets: managers listen to its events to update visuals or forward messages.
    /// </summary>
    public class GameState
    {

        #region Properties

        public bool IsAuthoritative { get; }
        public List<int> PlayerIDs { get; } = new List<int>();
        public Dictionary<string, CubeState> Cubes { get; } = new Dictionary<string, CubeState>();

        /// <summary>The current round, starting at 1. Each score advances it by one.</summary>
        public int Round { get; set; } = 1;
        /// <summary>Score per player. The key is the player ID.</summary>
        public Dictionary<int, int> Scores { get; } = new Dictionary<int, int>();
        /// <summary>Each player's goal. The key is the player ID.</summary>
        public Dictionary<int, GoalCubeState> Goals { get; } = new Dictionary<int, GoalCubeState>();
        /// <summary>The board cubes spawn on; null until the game starts.</summary>
        public BoardState Board { get; set; }

        public double GameStartTime { get; set; }
        public double ServerClockOffset { get; set; }
        public double CurrentAppliedOffset { get; set; }
        public double LatestOffsetUpdateTime { get; set; }
        private double _offsetAccumulationRate = 0.10; // How much a new tick from server can alter the offset accumulation rate (to prevent ping spikes from altering offset significantly).
        private double _maxSlewLimit = 0.005; // Maximum percent of frame time alteration allowed between server ticks.
        private double _minOffsetUpdateInterval = 0.025;



        #endregion Properties

        #region Events

        public event Action<CubeState> OnCubeAdded = delegate { };
        public event Action<CubeState> OnCubeRemoved = delegate { };
        public event Action<CubeState> OnGrabPermissionsChanged = delegate { };
        public event Action<CubeState> OnCubePoseChanged = delegate { };
        public event Action<SingleAction> PostHandleActionEvent = delegate { };

        #endregion Events

        #region Constructor

        public GameState(bool isAuthoritative)
        {
            IsAuthoritative = isAuthoritative;
            GameStartTime = TimeUtilities.GetTime();
            LatestOffsetUpdateTime = GameStartTime;
            ServerClockOffset = 0.0;
            CurrentAppliedOffset = 0.0;
        }

        #endregion Constructor

        #region Initialization

        /// <summary>
        /// Server only: sets up the players, the starting cubes, the board and the goals. Every player
        /// and every goal's player starts on a score of 0.
        /// </summary>
        public void ServerInitialize(IEnumerable<int> playerIDs, IEnumerable<CubeState> cubes, BoardState board = null, IEnumerable<GoalCubeState> goals = null)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game state can initialize the game.");

            PlayerIDs.Clear();
            PlayerIDs.AddRange(playerIDs);

            Round = 1;
            Board = board;

            Goals.Clear();
            if (goals != null)
            {
                foreach (GoalCubeState goal in goals)
                    Goals.Add(goal.PlayerID, goal);
            }

            Scores.Clear();
            foreach (int playerID in PlayerIDs.Concat(Goals.Keys))
                Scores[playerID] = 0;

            Cubes.Clear();
            foreach (CubeState cube in cubes)
                AddCube(cube);
        }

        #endregion Initialization

        #region Handle GameState

        /// <summary>
        /// Overwrites this game state with a snapshot from the server. Cubes not seen before are added.
        /// </summary>
        public void ApplyGameState(float pingDelay, GameStateDTO gameStateDTO)
        {
            PlayerIDs.Clear();
            PlayerIDs.AddRange(gameStateDTO.PlayerLogicalTimestamps.Keys.ToList());


            double estimateServerTime = gameStateDTO.ServerTime + (pingDelay / 2.0);
            double rawOffset = estimateServerTime - TimeUtilities.GetTime();
            ServerClockOffset = (ServerClockOffset * (1 - _offsetAccumulationRate)) + (rawOffset * _offsetAccumulationRate);

            if (TimeUtilities.GetTime() - LatestOffsetUpdateTime > _minOffsetUpdateInterval)
            {
                double driftError = ServerClockOffset - CurrentAppliedOffset;
                double slewAdjustment = Math.Min(Math.Max(driftError, -_maxSlewLimit), _maxSlewLimit);
                CurrentAppliedOffset += slewAdjustment;
                LatestOffsetUpdateTime = TimeUtilities.GetTime();
            }

            Round = gameStateDTO.Round;
            CopyScoresAndGoals(gameStateDTO.Scores, gameStateDTO.Goals.Values, gameStateDTO.Board);

            var incomingCubeIDs = new HashSet<string>(gameStateDTO.Cubes.Select(cube => cube.ID));
            foreach (string cubeID in Cubes.Keys.Where(id => !incomingCubeIDs.Contains(id)).ToList())
                RemoveCube(cubeID);

            foreach (CubeState incoming in gameStateDTO.Cubes)
            {
                if (!Cubes.TryGetValue(incoming.ID, out CubeState cube))
                {
                    cube = incoming.GetDeepCopy();
                    Cubes.Add(cube.ID, cube);
                    OnCubeAdded.Invoke(cube);
                    continue;
                }

                cube.PoseLastUpdatedServerTime = incoming.PoseLastUpdatedServerTime;
                cube.Pose = incoming.Pose;
                cube.GrabPermissions = new Dictionary<int, CubeFollowTarget>(incoming.GrabPermissions);

                // Permissions first, so a client that just lost a cube drops it before the pose is applied.
                OnGrabPermissionsChanged.Invoke(cube);
                OnCubePoseChanged.Invoke(cube);
            }
        }

        /// <summary>
        /// Replaces this mirrored state with an authoritative state before client predictions are replayed.
        /// Unlike <see cref="ApplyGameState"/>, this intentionally does not compare timestamps because
        /// the current state may contain locally predicted values with newer client-estimated times.
        /// </summary>
        public void ReplaceFrom(GameState authoritativeState)
        {
            PlayerIDs.Clear();
            PlayerIDs.AddRange(authoritativeState.PlayerIDs);
            GameStartTime = authoritativeState.GameStartTime;
            ServerClockOffset = authoritativeState.ServerClockOffset;
            CurrentAppliedOffset = authoritativeState.CurrentAppliedOffset;
            LatestOffsetUpdateTime = authoritativeState.LatestOffsetUpdateTime;
            Round = authoritativeState.Round;
            CopyScoresAndGoals(authoritativeState.Scores, authoritativeState.Goals.Values, authoritativeState.Board);

            foreach (string cubeID in new List<string>(Cubes.Keys))
            {
                if (!authoritativeState.Cubes.ContainsKey(cubeID))
                    RemoveCube(cubeID);
            }

            foreach (CubeState authoritativeCube in authoritativeState.Cubes.Values)
            {
                if (!Cubes.TryGetValue(authoritativeCube.ID, out CubeState cube))
                {
                    cube = authoritativeCube.GetDeepCopy();
                    Cubes.Add(cube.ID, cube);
                    OnCubeAdded.Invoke(cube);
                    continue;
                }

                cube.PoseLastUpdatedServerTime = authoritativeCube.PoseLastUpdatedServerTime;
                cube.Pose = authoritativeCube.Pose;
                cube.GrabPermissions = new Dictionary<int, CubeFollowTarget>(authoritativeCube.GrabPermissions);
                OnGrabPermissionsChanged.Invoke(cube);
                OnCubePoseChanged.Invoke(cube);
            }
        }

        void CopyScoresAndGoals(Dictionary<int, int> scores, IEnumerable<GoalCubeState> goals, BoardState board)
        {
            Scores.Clear();
            foreach (var kvp in scores)
                Scores[kvp.Key] = kvp.Value;

            Goals.Clear();
            foreach (GoalCubeState goal in goals)
                Goals[goal.PlayerID] = goal.GetDeepCopy();

            Board = board?.GetDeepCopy();
        }

        /// <summary>
        /// Handles an incoming single action.
        /// </summary>
        /// <param name="singleAction">The single action.</param>
        /// <returns>True if the action is accepted. False if not.</returns>
        public bool HandleSingleAction(SingleAction singleAction)
        {
            if (!(singleAction is CubeAction cubeAction) || !Cubes.TryGetValue(cubeAction.CubeID, out CubeState cube))
                return false;

            bool successfulAction = false;

            if (singleAction is GrabCubeAction grabCubeAction)
            {
                successfulAction = HandleGrabAction(grabCubeAction, cube);
            }
            else if (singleAction is ReleaseCubeAction releaseCubeAction)
            {
                successfulAction = HandleReleaseAction(releaseCubeAction, cube);
            }
            else if (singleAction is MoveCubeAction moveCubeAction)
            {
                successfulAction = HandleMoveAction(moveCubeAction, cube);
            }
            // A ScoreAction is never accepted here: only the server creates and applies them.

            if (successfulAction)
                PostHandleActionEvent.Invoke(singleAction);

            return successfulAction;
        }

        /// <summary>
        /// First come, first served: granted only if nobody else holds the cube. Grabbing a cube the
        /// player already holds (e.g. passing it to the other hand) succeeds without changing anything.
        /// </summary>
        protected virtual bool HandleGrabAction(GrabCubeAction grabCubeAction, CubeState cube)
        {
            int playerID = grabCubeAction.PlayerID;

            // Check if the user has permission to grab the cube.
            if (!cube.GrabPermissions.ContainsKey(playerID))
                return false;

            int? holder = cube.HolderPlayerID;

            // If the player is already holding the cube, return true.
            if (holder == playerID)
                return true;

            // If another player is holding the cube already, check if:
            // 2 seconds have passed if authoritative.
            // 2.5 seconds have passed if client-side (to protect against latency between server and client).
            float timeBeforeNextGrabAllowed = IsAuthoritative ? 2.0f : 2.5f;
            if (holder != null && (grabCubeAction.ServerTime - cube.GrabLastUpdatedServerTime < 2.0f))
                return false;

            foreach (int pID in cube.GrabPermissions.Keys.ToList())
            {
                cube.GrabPermissions[pID] = CubeFollowTarget.None;
            }

            cube.GrabPermissions[playerID] = grabCubeAction.FollowTarget;
            cube.GrabLastUpdatedServerTime = grabCubeAction.ServerTime;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleReleaseAction(ReleaseCubeAction releaseCubeAction, CubeState cube)
        {
            int playerID = releaseCubeAction.PlayerID;

            if (!cube.HasGrabPermission(playerID))
                return false;

            cube.GrabPermissions[playerID] = CubeFollowTarget.None;
            cube.GrabLastUpdatedServerTime = releaseCubeAction.ServerTime;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleMoveAction(MoveCubeAction moveCubeAction, CubeState cube)
        {
            if (!cube.HasGrabPermission(moveCubeAction.PlayerID))
                return false;

            cube.PoseLastUpdatedServerTime = moveCubeAction.ServerTime;
            cube.Pose = moveCubeAction.Pose;
            OnCubePoseChanged.Invoke(cube);

            return true;
        }

        #endregion Handle GameState

        #region Authoritative Methods

        /// <summary>
        /// Server only: takes grab permission away from whoever holds the cube. Clients learn of this
        /// through the next game state update and drop the cube.
        /// </summary>
        /// <returns>True if someone was holding the cube.</returns>
        public bool RevokeGrabPermissions(string cubeID)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game state can revoke grab permissions.");

            if (!Cubes.TryGetValue(cubeID, out CubeState cube) || !cube.IsHeld)
                return false;

            foreach (int playerID in new List<int>(cube.GrabPermissions.Keys))
                cube.GrabPermissions[playerID] = CubeFollowTarget.None;

            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        /// <summary>
        /// Server only: adds a cube to the game. Clients learn of it through the next game state update.
        /// </summary>
        public void ServerAddCube(CubeState cube)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game state can add cubes.");

            AddCube(cube);
        }

        /// <summary>
        /// Server only: removes a cube from the game. Clients learn of this through the next game
        /// state update and destroy the cube.
        /// </summary>
        /// <returns>True if the cube existed.</returns>
        public bool ServerRemoveCube(string cubeID)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game state can remove cubes.");

            return RemoveCube(cubeID);
        }

        #endregion Authoritative Methods

        #region Cubes

        void AddCube(CubeState cube)
        {
            Cubes.Add(cube.ID, cube);
            OnCubeAdded.Invoke(cube);
        }

        bool RemoveCube(string cubeID)
        {
            if (!Cubes.TryGetValue(cubeID, out CubeState cube))
                return false;

            Cubes.Remove(cubeID);
            OnCubeRemoved.Invoke(cube);

            return true;
        }

        #endregion Cubes
    }
}
