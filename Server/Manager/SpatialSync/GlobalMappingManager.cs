using Server.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Numerics;
using SharedPose = SharedLibrary.Game.Actions.Pose;
using SharedQuaternion = SharedLibrary.Game.Quaternion;
using SharedVector3 = SharedLibrary.Game.Vector3;

namespace Server.Manager.SpatialSync
{
    /// <summary>A scene anchor in the global map: what it is, how big, and where it is in the global frame (set when it was first added, then kept).</summary>
    public sealed record GlobalAnchor(string Key, string Label, SharedVector3 Size, SharedPose GlobalPose, int FirstSeenByPlayerID, int PlayersSeenBy);

    /// <summary>Where a headset's tracking space sits in the global frame (<see cref="Transform"/> maps its tracking space into the global frame).</summary>
    public sealed record PlayerAlignment(SharedPose Transform, int MatchedAnchors, float ResidualMeters, DateTime UpdatedUtc);

    /// <summary>
    /// The global map, built from the headsets' scene anchors (the walls, floor, ceiling, doors, windows and furniture of the room scan on each Quest,
    /// <see cref="KeyFrameAnchorDTO"/>). Nothing is shared between the headsets: each reports the anchors of its own scan in its own tracking space.
    /// The first headset's tracking space is the global frame and its anchors are the map. Any other headset is placed in it by matching its anchors with
    /// the map's by label, size and position (<see cref="SceneAnchorAligner"/>), which gives the transform from its tracking space to the global frame.
    /// Once every player of the session is placed the global map is formed, and from then on each key frame gets its global pose
    /// (<see cref="HandleNewKeyFrame"/>), which is what the client needs to line its shared scene up. Anchors a placed headset has that the map does not
    /// are added to it, so a headset that sees only part of the room still matches.
    /// </summary>
    public class GlobalMappingManager
    {
        private readonly object _lock = new();
        private readonly SceneAnchorMapParameters _parameters;

        /// <summary>The most recent anchor report of each player (kept after it is used, so it can be looked at any time).</summary>
        private readonly Dictionary<int, KeyFrameAnchorDTO> _latestReports = new();

        /// <summary>The anchors of the global map, in the global frame. An anchor is added once and then stays where it was put.</summary>
        private readonly List<SceneAnchor> _globalAnchors = new();
        private readonly Dictionary<string, (int FirstPlayer, HashSet<int> Players)> _globalAnchorInfo = new();

        /// <summary>Which global anchor each headset anchor (player, the headset's own anchor id) was matched with or became.</summary>
        private readonly Dictionary<(int Player, string Uuid), string> _anchorOwners = new();

        /// <summary>Where each placed headset's tracking space is in the global frame.</summary>
        private readonly Dictionary<int, (PoseTransform Transform, int Matched, float Residual, DateTime Updated)> _alignments = new();

        private readonly Dictionary<int, long> _lastWorkMs = new();

        /// <summary>The keys of the anchors of each player's latest report that were matched with the map, and why a player was last not placed (for the viewer).</summary>
        private readonly Dictionary<int, HashSet<string>> _matchedKeys = new();
        private readonly Dictionary<int, string> _lastFailure = new();
        private long _version;
        private readonly Dictionary<int, long> _lastFailureLogMs = new();

        public bool DebugMode { get; set; }

        /// <summary>True once every player of the session is placed in the global map (and stays true).</summary>
        public bool IsGlobalMapFormed { get; private set; }

        /// <summary>The players the global map waits for (all of them must be placed before it is formed). Without it, two placed players are enough.</summary>
        public Func<IReadOnlyCollection<int>>? ExpectedPlayerIDs { get; set; }

        /// <summary>Changes every time a report is taken in (so a viewer knows when there is something new to draw).</summary>
        public long Version { get { lock (_lock) return _version; } }

        public int AnchorCount { get { lock (_lock) return _globalAnchors.Count; } }
        public int PlacedPlayerCount { get { lock (_lock) return _alignments.Count; } }

        public GlobalMappingManager(bool debugMode) : this(debugMode, null) { }

        public GlobalMappingManager(bool debugMode, SceneAnchorMapParameters? parameters)
        {
            DebugMode = debugMode;
            _parameters = parameters ?? new SceneAnchorMapParameters();
        }

        #region Consume anchor data

        /// <summary>
        /// Take in a player's scene anchor report: remember it as the player's most recent one, and use it to place the player (and any new anchors it
        /// holds) in the global map. Returns the global pose of the report's <see cref="KeyFrameAnchorDTO.CameraLocalPose"/> once the global map is
        /// formed (every player placed), or null before that or if this player is not placed.
        /// </summary>
        public SharedPose? HandleNewKeyFrame(int playerID, KeyFrameAnchorDTO report)
        {
            if (report == null)
                return null;

            lock (_lock)
            {
                ConsumeAnchorData(playerID, report);
                if (!IsGlobalMapFormed)
                    return null;
                return TryGetGlobalPose(playerID, report.CameraLocalPose, out SharedPose globalPose) ? globalPose : null;
            }
        }

        /// <summary>
        /// Save a player's report as their most recent one and update the global map with it: the first report with enough anchors defines the global
        /// frame, later ones place their player in it (and keep it up to date) and add the anchors the map does not have yet.
        /// </summary>
        public void ConsumeAnchorData(int playerID, KeyFrameAnchorDTO report)
        {
            lock (_lock)
            {
                _latestReports[playerID] = report;
                _version++;

                List<SceneAnchor> anchors = ParseAnchors(playerID, report);

                // The first headset with a room: its tracking space is the global frame, and its anchors are the map.
                if (_globalAnchors.Count == 0)
                {
                    if (anchors.Count < _parameters.MinAnchorsForReference)
                        return;
                    foreach (SceneAnchor anchor in anchors)
                        AddGlobalAnchor(playerID, anchor, anchor);
                    _matchedKeys[playerID] = anchors.Select(a => a.Key).ToHashSet();
                    SetAlignment(playerID, PoseTransform.Identity, anchors.Count, 0f, "its tracking space is the global frame");
                    UpdateFormed();
                    return;
                }

                if (anchors.Count == 0)
                    return;

                bool placed = _alignments.TryGetValue(playerID, out var current);
                long now = Environment.TickCount64;
                float interval = placed ? _parameters.RefitIntervalSeconds : _parameters.RetryIntervalSeconds;
                if (_lastWorkMs.TryGetValue(playerID, out long last) && now - last < interval * 1000f)
                    return;
                _lastWorkMs[playerID] = now;

                // A placed headset is refitted from where it was (this also follows a headset whose tracking space moved a little). If that no longer
                // holds (the space was recentered, say), or the headset is not placed yet, search from scratch.
                SceneAlignment? result = placed ? SceneAnchorAligner.Refine(_globalAnchors, anchors, current.Transform, _parameters) : null;
                string failure = "";
                result ??= SceneAnchorAligner.Search(_globalAnchors, anchors, _parameters, out failure);

                if (result == null)
                {
                    _lastFailure[playerID] = failure;
                    if (!_lastFailureLogMs.TryGetValue(playerID, out long lastLog) || now - lastLog > 5000)
                    {
                        _lastFailureLogMs[playerID] = now;
                        Console.WriteLine($"[SceneAnchors] player {playerID} not " + (placed ? "re-aligned" : "placed") + $" ({anchors.Count} anchors): {failure}");
                    }
                    return;
                }

                _lastFailure.Remove(playerID);
                _matchedKeys[playerID] = result.Matches.Where(m => m.Same).Select(m => m.Local.Key).ToHashSet();
                SetAlignment(playerID, result.Transform, result.SignificantMatches, result.RmsResidual, null, result.Evidence);
                MergeAnchors(playerID, anchors, result);
                LogRelationsSoon();
                UpdateFormed();
            }
        }

        private static List<SceneAnchor> ParseAnchors(int playerID, KeyFrameAnchorDTO report)
        {
            var anchors = new List<SceneAnchor>();
            if (report.Anchors == null)
                return anchors;
            foreach (SceneAnchorDTO dto in report.Anchors)
                if (SceneAnchor.TryCreate(playerID, dto, out SceneAnchor anchor))
                    anchors.Add(anchor);
            return anchors;
        }

        /// <summary>
        /// The headset is placed: note which global anchor each of its matched anchors is, and add the ones the map has nothing for (in the global
        /// frame), so headsets that only see part of the room still connect through what they share with the map.
        /// </summary>
        private void MergeAnchors(int playerID, List<SceneAnchor> anchors, SceneAlignment result)
        {
            var matched = new Dictionary<string, SceneAnchor>();
            foreach (SceneMatch match in result.Matches.Where(m => m.Same))
            {
                matched[match.Local.Key] = match.Global;
                _anchorOwners[(playerID, match.Local.Uuid)] = match.Global.Key;
                _globalAnchorInfo[match.Global.Key].Players.Add(playerID);
            }

            foreach (SceneAnchor anchor in anchors)
            {
                if (matched.ContainsKey(anchor.Key) || _anchorOwners.ContainsKey((playerID, anchor.Uuid)))
                    continue;
                AddGlobalAnchor(playerID, anchor, anchor.Transformed(result.Transform, anchor.Key));
                Log($"[SceneAnchors] {anchor.Label} anchor added to the global map by player {playerID} ({_globalAnchors.Count} anchors)");
            }
        }

        private void AddGlobalAnchor(int playerID, SceneAnchor local, SceneAnchor global)
        {
            _globalAnchors.Add(global);
            _globalAnchorInfo[global.Key] = (playerID, new HashSet<int> { playerID });
            _anchorOwners[(playerID, local.Uuid)] = global.Key;
        }

        #endregion Consume anchor data

        #region Global poses

        /// <summary>The global pose of a pose in the player's tracking space. False if the player is not placed in the global map.</summary>
        public bool TryGetGlobalPose(int playerID, SharedPose localPose, out SharedPose globalPose)
        {
            globalPose = default;
            lock (_lock)
            {
                if (!_alignments.TryGetValue(playerID, out var alignment))
                    return false;
                globalPose = alignment.Transform.Then(PoseTransform.From(localPose)).ToShared();
                return true;
            }
        }

        /// <summary>Where the player is in the global frame: their most recent reported pose, placed in it. Null if the player has no report or is not placed.</summary>
        public SharedPose? GetPlayerGlobalPose(int playerID)
        {
            lock (_lock)
            {
                if (!_latestReports.TryGetValue(playerID, out KeyFrameAnchorDTO? report))
                    return null;
                return TryGetGlobalPose(playerID, report.CameraLocalPose, out SharedPose global) ? global : null;
            }
        }

        /// <summary>
        /// Where one player is in relation to another: the pose of <paramref name="otherPlayerID"/> in the frame of <paramref name="playerID"/>'s
        /// headset (x right, y up, z ahead of that player, as the headset was in its last report), from the two players' most recent reports. False if
        /// either player has no report or is not placed in the global map yet. The reports were not taken at the same moment, so a player who has been
        /// moving since their last report is a little off.
        /// </summary>
        public bool TryGetRelativePose(int playerID, int otherPlayerID, out SharedPose relative)
        {
            relative = default;
            lock (_lock)
            {
                SharedPose? mine = GetPlayerGlobalPose(playerID), theirs = GetPlayerGlobalPose(otherPlayerID);
                if (!mine.HasValue || !theirs.HasValue)
                    return false;
                PoseTransform inverseMine = PoseTransform.From(mine.Value).Inverse();
                relative = inverseMine.Then(PoseTransform.From(theirs.Value)).ToShared();
                return true;
            }
        }

        /// <summary>A line per pair of placed players: how far apart they are, and where the second is in relation to the first. For the log.</summary>
        public string DescribePlayerRelations()
        {
            lock (_lock)
            {
                var lines = new List<string>();
                int[] players = _alignments.Keys.Where(_latestReports.ContainsKey).OrderBy(id => id).ToArray();
                for (int i = 0; i < players.Length; i++)
                    for (int j = i + 1; j < players.Length; j++)
                        if (TryGetRelativePose(players[i], players[j], out SharedPose rel))
                        {
                            var v = new Vector3(rel.Position.X, rel.Position.Y, rel.Position.Z);
                            lines.Add($"player {players[j]} is {v.Length():0.00} m from player {players[i]} ({v.X:0.00} right, {v.Y:0.00} up, {v.Z:0.00} ahead of them)");
                        }
                return lines.Count == 0 ? "fewer than two players placed" : string.Join("; ", lines);
            }
        }

        /// <summary>Where the player's tracking space is in the global frame (null if the player is not placed yet).</summary>
        public PlayerAlignment? GetPlayerAlignment(int playerID)
        {
            lock (_lock)
                return _alignments.TryGetValue(playerID, out var a) ? new PlayerAlignment(a.Transform.ToShared(), a.Matched, a.Residual, a.Updated) : null;
        }

        /// <summary>The player's most recent anchor report, or null if they have not sent one.</summary>
        public KeyFrameAnchorDTO? GetLatestAnchorData(int playerID)
        {
            lock (_lock)
                return _latestReports.TryGetValue(playerID, out var report) ? report : null;
        }

        /// <summary>The anchors of the global map with where they are in the global frame.</summary>
        public IReadOnlyList<GlobalAnchor> GetGlobalAnchors()
        {
            lock (_lock)
                return _globalAnchors.Select(a =>
                {
                    var info = _globalAnchorInfo[a.Key];
                    return new GlobalAnchor(a.Key, a.Label, new SharedVector3(a.Size.X, a.Size.Y, a.Size.Z), a.Pose.ToShared(), info.FirstPlayer, info.Players.Count);
                }).ToList();
        }

        #endregion Global poses


        #region Viewer snapshot

        /// <summary>
        /// What a viewer shows: every player that is placed in the global map, merged into one sub-map in the global frame (each player's own latest
        /// anchors moved there by their alignment), and a sub-map of their own, in their own tracking space, for every player that is not placed yet.
        /// </summary>
        public SceneMapSnapshot GetSceneMapSnapshot()
        {
            lock (_lock)
            {
                var subMaps = new List<SceneSubMap>();

                int[] placed = _alignments.Keys.Where(_latestReports.ContainsKey).OrderBy(id => id).ToArray();
                if (placed.Length > 0)
                {
                    string note = $"{_globalAnchors.Count} anchors in the map" + (IsGlobalMapFormed ? ", map formed" : ", waiting for the other players");
                    subMaps.Add(BuildSubMap("global", "Global map", true, placed, note));
                }

                foreach (int id in _latestReports.Keys.Where(id => !_alignments.ContainsKey(id)).OrderBy(id => id))
                {
                    int count = _latestReports[id].Anchors?.Count ?? 0;
                    string note = count == 0 ? "no scene anchors yet (is the room loaded on the headset?)"
                        : _lastFailure.TryGetValue(id, out string? failure) && failure.Length > 0 ? $"not merged: {failure}"
                        : _globalAnchors.Count == 0 ? "waiting for the first player's room to become the global frame" : "not merged yet";
                    subMaps.Add(BuildSubMap($"player{id}", $"Player {id}'s own scan (own tracking space)", false, new[] { id }, note));
                }

                return new SceneMapSnapshot(_version, subMaps.ToArray());
            }
        }

        private SceneSubMap BuildSubMap(string key, string title, bool isGlobal, int[] playerIDs, string note)
        {
            var anchors = new List<SceneViewAnchor>();
            var players = new List<SceneViewPlayer>();
            foreach (int playerID in playerIDs)
            {
                KeyFrameAnchorDTO report = _latestReports[playerID];
                PoseTransform transform = _alignments.TryGetValue(playerID, out var alignment) ? alignment.Transform : PoseTransform.Identity;
                _matchedKeys.TryGetValue(playerID, out HashSet<string>? matchedKeys);

                foreach (SceneAnchor anchor in ParseAnchors(playerID, report))
                {
                    PoseTransform pose = transform.Then(anchor.Pose);
                    anchors.Add(new SceneViewAnchor(playerID, anchor.Label, anchor.Kind.ToString(), pose.Position, pose.Rotation, anchor.Size, matchedKeys != null && matchedKeys.Contains(anchor.Key)));
                }

                PoseTransform head = transform.Then(PoseTransform.From(report.CameraLocalPose));
                players.Add(new SceneViewPlayer(playerID, head.Position, Vector3.Transform(Vector3.UnitZ, head.Rotation)));
            }
            return new SceneSubMap(key, title, isGlobal, playerIDs, anchors.ToArray(), players.ToArray(), note);
        }

        #endregion Viewer snapshot

        #region Players

        /// <summary>
        /// A player left. Their report and placement are dropped; the anchors they added stay in the global map, so the others (and the player
        /// if they come back) are still placed in the same global frame.
        /// </summary>
        public bool RemovePlayer(int playerID)
        {
            lock (_lock)
            {
                bool had = _alignments.Remove(playerID);
                had |= _latestReports.Remove(playerID);
                _lastWorkMs.Remove(playerID);
                _lastFailureLogMs.Remove(playerID);
                _matchedKeys.Remove(playerID);
                _lastFailure.Remove(playerID);
                _version++;
                foreach (var key in _anchorOwners.Keys.Where(k => k.Player == playerID).ToList())
                    _anchorOwners.Remove(key);
                return had;
            }
        }

        private void UpdateFormed()
        {
            if (IsGlobalMapFormed)
                return;

            IReadOnlyCollection<int>? expected = ExpectedPlayerIDs?.Invoke();
            bool formed = expected != null && expected.Count > 0
                ? expected.All(_alignments.ContainsKey)
                : _alignments.Count >= 2;
            if (formed)
            {
                IsGlobalMapFormed = true;
                Console.WriteLine($"[SceneAnchors] global map formed: {_alignments.Count} players placed, {_globalAnchors.Count} anchors");
            }
        }

        private void SetAlignment(int playerID, PoseTransform transform, int matched, float residual, string? note, float evidence = 0f)
        {
            bool isNew = !_alignments.TryGetValue(playerID, out var previous);
            _alignments[playerID] = (transform, matched, residual, DateTime.UtcNow);

            if (isNew || DebugMode && (previous.Transform.Position - transform.Position).Length() > 0.02f)
            {
                SharedPose shared = transform.ToShared();
                Console.WriteLine($"[SceneAnchors] player {playerID} placed in the global map " + (note ?? $"by {matched} matching anchor(s), {evidence:0.0} points of evidence, residual {residual * 100:0.0} cm") +
                                  $": yaw {transform.Yaw * 180f / MathF.PI:0.0} deg, shift ({shared.Position.X:0.00}, {shared.Position.Y:0.00}, {shared.Position.Z:0.00}) m");
            }
        }

        private long _lastRelationsLogMs;

        /// <summary>Debug mode: now and then, say where the players are in relation to each other.</summary>
        private void LogRelationsSoon()
        {
            if (!DebugMode || Environment.TickCount64 - _lastRelationsLogMs < 2000)
                return;
            _lastRelationsLogMs = Environment.TickCount64;
            Console.WriteLine($"[SceneAnchors] {DescribePlayerRelations()}");
        }

        private void Log(string message)
        {
            if (DebugMode)
                Console.WriteLine(message);
        }

        #endregion Players
    }

    /// <summary>A rotation and a position: a pose, or the transform that takes one frame's coordinates into another's.</summary>
    internal readonly struct PoseTransform
    {
        public Quaternion Rotation { get; }
        public Vector3 Position { get; }

        public PoseTransform(Quaternion rotation, Vector3 position)
        {
            Rotation = rotation;
            Position = position;
        }

        public static PoseTransform Identity => new(Quaternion.Identity, Vector3.Zero);

        public bool IsFinite => float.IsFinite(Position.X + Position.Y + Position.Z) && float.IsFinite(Rotation.X + Rotation.Y + Rotation.Z + Rotation.W);

        public Vector3 Apply(Vector3 point) => Vector3.Transform(point, Rotation) + Position;

        /// <summary>This transform applied after <paramref name="inner"/>: (this ∘ inner)(p) = this(inner(p)).</summary>
        public PoseTransform Then(PoseTransform inner) => new(Quaternion.Normalize(Rotation * inner.Rotation), Apply(inner.Position));

        public PoseTransform Inverse()
        {
            Quaternion inverse = Quaternion.Inverse(Rotation);
            return new PoseTransform(inverse, Vector3.Transform(-Position, inverse));
        }

        /// <summary>The turn about the vertical axis of this transform's rotation, in radians.</summary>
        public float Yaw
        {
            get
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, Rotation);
                return MathF.Atan2(forward.X, forward.Z);
            }
        }

        public static PoseTransform From(SharedPose pose)
        {
            var rotation = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
            rotation = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
            return new PoseTransform(rotation, new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z));
        }

        public SharedPose ToShared() =>
            new(new SharedVector3(Position.X, Position.Y, Position.Z), new SharedQuaternion(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W));
    }
}
