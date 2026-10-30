using SharedLibrary.Game.Actions;
using System;
using System.Collections.Generic;

namespace SharedLibrary.Game
{
    /// <summary>
    /// The state of one cube, held identically by the server and every client: where it is and
    /// which player may hold it.
    /// </summary>
    [Serializable]
    public class CubeState
    {

        #region Properties

        public string ID;

        /// <summary>
        /// Grab permission per player. The key is the player ID, and the value is whether that player
        /// may hold the cube. At most one entry is true at a time.
        /// </summary>
        [NonSerialized]
        public Dictionary<int, CubeFollowTarget> GrabPermissions = new Dictionary<int, CubeFollowTarget>();

        // JsonUtility can't serialize dictionaries, so the permissions travel as two parallel lists.
        List<int> _permissionPlayerIDs = new List<int>();
        List<CubeFollowTarget> _permissionValues = new List<CubeFollowTarget>();

        public double PoseLastUpdatedServerTime; // The last time the pose was updated in server time.
        public double GrabLastUpdatedServerTime; // The last time the grab permissions was updated in server time.

        public Pose Pose;

        /// <summary>Edge length of the cube (meters).</summary>
        public float CubeSize = DefaultCubeSize;

        public const float DefaultCubeSize = 0.2f;

        /// <summary>The <see cref="BoardState"/> grid cell (x, z) the cube spawned on.</summary>
        public (int X, int Z) GridLocation;

        /// <summary>The ID of the player holding grab permission, or null if nobody does.</summary>
        public int? HolderPlayerID
        {
            get
            {
                foreach (var kvp in GrabPermissions)
                {
                    if (kvp.Value != CubeFollowTarget.None)
                        return kvp.Key;
                }

                return null;
            }
        }

        public bool IsHeld => HolderPlayerID != null;

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Parameterless constructor for JSON deserialization between server and client.
        /// </summary>
        public CubeState() { }

        /// <summary>
        /// A cube that nobody holds yet. Every player gets a false grab permission entry.
        /// </summary>
        public CubeState(string id, double poseLastUpdatedServerTime, double grabLastUpdatedServerTime, Pose pose, IEnumerable<int> playerIDs)
        {
            ID = id;
            PoseLastUpdatedServerTime = poseLastUpdatedServerTime;
            GrabLastUpdatedServerTime = grabLastUpdatedServerTime;
            Pose = pose;

            foreach (int playerID in playerIDs)
                GrabPermissions[playerID] = CubeFollowTarget.None;
        }

        /// <summary>
        /// A cube that nobody holds yet, spawned on <paramref name="gridLocation"/> of the board.
        /// </summary>
        public CubeState(string id, double poseLastUpdatedServerTime, double grabLastUpdatedServerTime, Pose pose, IEnumerable<int> playerIDs,
            (int X, int Z) gridLocation, float cubeSize = DefaultCubeSize)
            : this(id, poseLastUpdatedServerTime, grabLastUpdatedServerTime, pose, playerIDs)
        {
            GridLocation = gridLocation;
            CubeSize = cubeSize;
        }

        #endregion Constructors

        public bool HasGrabPermission(int playerID) => GrabPermissions.TryGetValue(playerID, out CubeFollowTarget allowed) && allowed != CubeFollowTarget.None;

        /// <summary>
        /// Get a deep copy of this object, so a DTO never shares data with the <see cref="GameState"/> that built it.
        /// </summary>
        public CubeState GetDeepCopy()
        {
            return new CubeState
            {
                ID = ID,
                PoseLastUpdatedServerTime = PoseLastUpdatedServerTime,
                GrabLastUpdatedServerTime = GrabLastUpdatedServerTime,
                Pose = Pose,
                CubeSize = CubeSize,
                GridLocation = GridLocation,
                GrabPermissions = new Dictionary<int, CubeFollowTarget>(GrabPermissions),
            };
        }

        #region Serialization

        public void OnBeforeSerialize()
        {
            _permissionPlayerIDs.Clear();
            _permissionValues.Clear();

            if (GrabPermissions == null)
                return;

            foreach (var kvp in GrabPermissions)
            {
                _permissionPlayerIDs.Add(kvp.Key);
                _permissionValues.Add(kvp.Value);
            }
        }

        public void OnAfterDeserialize()
        {
            GrabPermissions = new Dictionary<int, CubeFollowTarget>();

            for (int i = 0; i < _permissionPlayerIDs.Count && i < _permissionValues.Count; i++)
                GrabPermissions[_permissionPlayerIDs[i]] = _permissionValues[i];
        }

        #endregion Serialization
    }

    public enum CubeFollowTarget
    {
        None,
        RightHand,
        LeftHand,
    }

    public enum GrabbedState
    {
        None,
        OurPlayer,
        AnotherPlayer,
    }
}
