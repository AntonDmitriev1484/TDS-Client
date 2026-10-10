using System;
using System.Collections.Generic;
using SharedModels.Game.Actions;
using SharedModels.WebCommunication.DataTransferObjects;

namespace SharedModels.Game
{
    /// <summary>
    /// The game state, held by both the server (authoritative) and each client (a mirror). It owns
    /// the cubes and applies <see cref="SingleAction"/>s to them. It knows nothing about scenes,
    /// hands or sockets: managers listen to its events to update visuals or forward messages.
    /// </summary>
    public class CubeGameMaster
    {

        #region Properties

        public bool IsAuthoritative { get; }
        public List<int> PlayerIDs { get; } = new List<int>();
        public Dictionary<string, CubeState> Cubes { get; } = new Dictionary<string, CubeState>();

        #endregion Properties

        #region Events

        public event Action<CubeState> OnCubeAdded = delegate { };
        public event Action<CubeState> OnGrabPermissionsChanged = delegate { };
        public event Action<CubeState> OnCubePoseChanged = delegate { };
        public event Action<SingleAction> PostHandleActionEvent = delegate { };

        #endregion Events

        #region Constructor

        public CubeGameMaster(bool isAuthoritative)
        {
            IsAuthoritative = isAuthoritative;
        }

        #endregion Constructor

        #region Initialization

        /// <summary>
        /// Server only: sets up the players and the starting cubes.
        /// </summary>
        public void ServerInitialize(IEnumerable<int> playerIDs, IEnumerable<CubeState> cubes)
        {
            if (!IsAuthoritative)
                throw new InvalidOperationException("Only the authoritative game master can initialize the game.");

            PlayerIDs.Clear();
            PlayerIDs.AddRange(playerIDs);

            Cubes.Clear();
            foreach (CubeState cube in cubes)
            {
                Cubes.Add(cube.Id, cube);
                OnCubeAdded.Invoke(cube);
            }
        }

        /// <summary>
        /// Overwrites this game state with a snapshot from the server. Cubes not seen before are added.
        /// </summary>
        public void ApplyGameState(GameStateDTO gameStateDTO)
        {
            PlayerIDs.Clear();
            PlayerIDs.AddRange(gameStateDTO.PlayerIDs);

            foreach (CubeState incoming in gameStateDTO.Cubes)
            {
                if (!Cubes.TryGetValue(incoming.Id, out CubeState cube))
                {
                    cube = incoming.GetDeepCopy();
                    Cubes.Add(cube.Id, cube);
                    OnCubeAdded.Invoke(cube);
                    continue;
                }

                cube.Pose = incoming.Pose;
                cube.GrabPermissions = new Dictionary<int, bool>(incoming.GrabPermissions);

                // Permissions first, so a client that just lost a cube drops it before the pose is applied.
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

            cube.GrabPermissions[playerID] = true;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleReleaseAction(ReleaseCubeAction releaseCubeAction, CubeState cube)
        {
            int playerID = releaseCubeAction.PlayerID;

            if (!cube.HasGrabPermission(playerID))
                return false;

            cube.GrabPermissions[playerID] = false;
            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        protected virtual bool HandleMoveAction(MoveCubeAction moveCubeAction, CubeState cube)
        {
            if (!cube.HasGrabPermission(moveCubeAction.PlayerID))
                return false;

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
                throw new InvalidOperationException("Only the authoritative game master can revoke grab permissions.");

            if (!Cubes.TryGetValue(cubeID, out CubeState cube) || !cube.IsHeld)
                return false;

            foreach (int playerID in new List<int>(cube.GrabPermissions.Keys))
                cube.GrabPermissions[playerID] = false;

            OnGrabPermissionsChanged.Invoke(cube);

            return true;
        }

        #endregion Authoritative Methods
    }
}
