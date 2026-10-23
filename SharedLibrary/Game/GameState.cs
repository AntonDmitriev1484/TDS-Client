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
        /// Server only: sets up the players and the starting cubes.
        /// </summary>
        public void ServerInitialize(IEnumerable<int> playerIDs, IEnumerable<CubeState> cubes)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game state can initialize the game.");

            PlayerIDs.Clear();
            PlayerIDs.AddRange(playerIDs);

            Cubes.Clear();
            foreach (CubeState cube in cubes)
            {
                Cubes.Add(cube.ID, cube);
                OnCubeAdded.Invoke(cube);
            }
        }

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


            foreach (CubeState incoming in gameStateDTO.Cubes)
            {
                if (!Cubes.TryGetValue(incoming.ID, out CubeState cube))
                {
                    cube = incoming.GetDeepCopy();
                    Cubes.Add(cube.ID, cube);
                    OnCubeAdded.Invoke(cube);
                    continue;
                }

                // Only update if the server time of the incoming is after the cube's current server time.
                if (cube.LastUpdatedServerTime < incoming.LastUpdatedServerTime)
                {
                    cube.LastUpdatedServerTime = incoming.LastUpdatedServerTime;
                    cube.Pose = incoming.Pose;
                    cube.GrabPermissions = new Dictionary<int, CubeFollowTarget>(incoming.GrabPermissions);

                    // Permissions first, so a client that just lost a cube drops it before the pose is applied.
                    OnGrabPermissionsChanged.Invoke(cube);
                    OnCubePoseChanged.Invoke(cube);
                }
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

            foreach (string cubeID in new List<string>(Cubes.Keys))
            {
                if (!authoritativeState.Cubes.ContainsKey(cubeID))
                    Cubes.Remove(cubeID);
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

                cube.LastUpdatedServerTime = authoritativeCube.LastUpdatedServerTime;
                cube.Pose = authoritativeCube.Pose;
                cube.GrabPermissions = new Dictionary<int, CubeFollowTarget>(authoritativeCube.GrabPermissions);
                OnGrabPermissionsChanged.Invoke(cube);
                OnCubePoseChanged.Invoke(cube);
            }
        }

        #endregion Initialization

        #region Handle SingleAction

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

            if (!cube.GrabPermissions.ContainsKey(playerID))
                return false;

            int? holder = cube.HolderPlayerID;

            if (holder == playerID)
                return true;

            if (holder != null)
                return false;

            cube.GrabPermissions[playerID] = grabCubeAction.FollowTarget;
            cube.LastUpdatedServerTime = grabCubeAction.ServerTime;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleReleaseAction(ReleaseCubeAction releaseCubeAction, CubeState cube)
        {
            int playerID = releaseCubeAction.PlayerID;

            if (!cube.HasGrabPermission(playerID))
                return false;

            cube.GrabPermissions[playerID] = CubeFollowTarget.None;
            cube.LastUpdatedServerTime = releaseCubeAction.ServerTime;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleMoveAction(MoveCubeAction moveCubeAction, CubeState cube)
        {
            if (!cube.HasGrabPermission(moveCubeAction.PlayerID))
                return false;

            cube.LastUpdatedServerTime = moveCubeAction.ServerTime;
            cube.Pose = moveCubeAction.Pose;
            OnCubePoseChanged.Invoke(cube);

            return true;
        }

        #endregion Handle SingleAction

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

        #endregion Authoritative Methods
    }
}
