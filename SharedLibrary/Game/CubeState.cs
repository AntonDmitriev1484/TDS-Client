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
        public Dictionary<int, bool> GrabPermissions = new Dictionary<int, bool>();

        // JsonUtility can't serialize dictionaries, so the permissions travel as two parallel lists.
        List<int> _permissionPlayerIDs = new List<int>();
        List<bool> _permissionValues = new List<bool>();

        public Pose Pose;

        /// <summary>The ID of the player holding grab permission, or null if nobody does.</summary>
        public int? HolderPlayerID
        {
            get
            {
                foreach (var kvp in GrabPermissions)
                {
                    if (kvp.Value)
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
        public CubeState(string id, Pose pose, IEnumerable<int> playerIDs)
        {
            ID = id;
            Pose = pose;

            foreach (int playerID in playerIDs)
                GrabPermissions[playerID] = false;
        }

        #endregion Constructors

        public bool HasGrabPermission(int playerID) => GrabPermissions.TryGetValue(playerID, out bool allowed) && allowed;

        /// <summary>
        /// Get a deep copy of this object, so a DTO never shares data with the <see cref="GameState"/> that built it.
        /// </summary>
        public CubeState GetDeepCopy()
        {
            return new CubeState
            {
                ID = ID,
                Pose = Pose,
                GrabPermissions = new Dictionary<int, bool>(GrabPermissions),
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
            GrabPermissions = new Dictionary<int, bool>();

            for (int i = 0; i < _permissionPlayerIDs.Count && i < _permissionValues.Count; i++)
                GrabPermissions[_permissionPlayerIDs[i]] = _permissionValues[i];
        }

        #endregion Serialization
    }
}
