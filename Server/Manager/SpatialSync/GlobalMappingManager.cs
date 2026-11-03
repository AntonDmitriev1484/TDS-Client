using Server.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Diagnostics;
using System.Numerics;

namespace Server.Manager.SpatialSync
{
    /// <summary>
    /// Builds the global map from incoming key frames, following ORB-SLAM / SLAM-Share. Every key frame is turned into ORB
    /// features and compared with the player's sub-map: each existing map point is projected through the camera pose the
    /// headset reports and its descriptor is compared with the keypoints near where it lands. Features the map does not
    /// explain yet are matched against covisible key frames and triangulated into new map points. Each player has their own
    /// sub-map in their own tracking space. When a keyframe's map points match another sub-map's, the transform between
    /// the two frames is estimated (RANSAC, yaw + translation), verified against the image, and the sub-maps are merged
    /// into one shared map.
    /// </summary>
    public class GlobalMappingManager
    {
        private readonly DebugImageDisplayService? _debugImageDisplayService;
        private readonly GlobalMappingParameters _parameters;
        private readonly object _mapLock = new();

        private readonly Dictionary<int, SubMap> _subMaps = new();       // sub-map ID -> sub-map
        private readonly Dictionary<int, int> _subMapIDByPlayer = new(); // player ID -> sub-map ID
        private readonly Dictionary<int, RigidTransform> _playerToMap = new(); // player ID -> device tracking space to map frame
        private int _nextKeyFrameID = 1;
        private int _nextMapPointID = 1;
        private int _nextSubMapID = 1;
        private long _mapVersion;

        #region Properties

        public bool DebugMode { get; set; }

        public int KeyFrameCount { get { lock (_mapLock) return _subMaps.Values.Sum(s => s.KeyFrames.Count); } }
        public int MapPointCount { get { lock (_mapLock) return _subMaps.Values.Sum(s => s.Points.Count); } }

        #endregion Properties

        #region Initialization

        public GlobalMappingManager(bool debugMode) : this(debugMode, null)
        {
        }

        public GlobalMappingManager(bool debugMode, GlobalMappingParameters? parameters)
        {
            DebugMode = debugMode;
            _parameters = parameters ?? new GlobalMappingParameters();
            _debugImageDisplayService = debugMode ? new DebugImageDisplayService() : null;
        }

        #endregion Initialization

        #region Methods

        public void HandleNewKeyFrame(int playerID, KeyFrameDTO keyFrameDTO)
        {
            if (DebugMode)
            {
                _debugImageDisplayService!.ShowFrame(playerID, keyFrameDTO);
            }

            var stopwatch = Stopwatch.StartNew();

            // Decode and extract outside the lock; it is the expensive part and touches no shared state.
            if (keyFrameDTO.ImageData is null || !KeyFrameImageDecoder.TryDecodeGrayscale(keyFrameDTO.ImageData, out byte[] gray, out int width, out int height))
            {
                Console.WriteLine($"[GlobalMap] Player {playerID}: key frame {keyFrameDTO.ID} has no decodable image; skipped.");
                return;
            }

            ORBFeatureSet features = ORBUtilities.ExtractFeatures(gray, width, height, _parameters.ExtractorParameters);
            long extractMs = stopwatch.ElapsedMilliseconds;

            KeyFrameInsertResult result;
            GlobalMapSnapshot? snapshot = null;
            lock (_mapLock)
            {
                result = InsertKeyFrame(playerID, keyFrameDTO, features);
                if (result.Inserted && DebugMode)
                    snapshot = CreateSnapshotLocked();
            }

            Console.WriteLine($"[GlobalMap] Player {playerID} key frame {keyFrameDTO.ID}: {(result.Inserted ? "inserted" : "skipped (" + result.Reason + ")")}, " +
                              $"{result.Features} features (extract {extractMs} ms), +{result.NewMapPoints} new / {result.TrackedMapPoints} tracked points, " +
                              $"map: {KeyFrameCount} key frames, {MapPointCount} points, total {stopwatch.ElapsedMilliseconds} ms");

            if (result.Merged)
                Console.WriteLine($"[GlobalMap] {result.MergeDescription}");

            if (snapshot != null)
            {
                _debugImageDisplayService!.ShowMap(snapshot);
            }
        }

        /// <summary>
        /// Insert a key frame whose features are already extracted: matches it with the player's recent key frames,
        /// associates or triangulates map points, updates covisibility and culls weak young points.
        /// </summary>
        public KeyFrameInsertResult InsertKeyFrame(int playerID, KeyFrameDTO dto, ORBFeatureSet features)
        {
            lock (_mapLock)
            {
                if (features.Count < _parameters.MinFeaturesToInsert)
                    return new KeyFrameInsertResult(false, "too few features", features.Count, 0, 0);

                if (!TryBuildKeyFrame(playerID, dto, features, out MapKeyFrame? keyFrame, out SubMap? subMap, out string reason))
                    return new KeyFrameInsertResult(false, reason, features.Count, 0, 0);

                var touched = new HashSet<int>();

                // 1. Compare the key frame's features with the sub-map: project every map point through this pose and match descriptors nearby.
                int trackedPoints = MatchMapPoints(subMap!, keyFrame!, touched);

                // 2. Features the map does not explain yet are triangulated against the covisible key frames into new map points.
                int newPoints = TriangulateNewPoints(subMap!, keyFrame!, touched);

                subMap.KeyFrames[keyFrame!.ID] = keyFrame;
                subMap.OrderedKeyFrames.Add(keyFrame);

                foreach (int pointID in touched)
                    if (subMap.Points.TryGetValue(pointID, out MapPoint? point))
                        RefreshMapPoint(subMap, point);

                UpdateCovisibility(subMap, keyFrame);
                CullRecentMapPoints(subMap, keyFrame.Sequence);

                _mapVersion++;

                // 3. Does this key frame see something another player's sub-map has mapped too? Then merge the two maps.
                bool merged = TryMergeSubMaps(subMap!, keyFrame, out string mergeDescription);
                return new KeyFrameInsertResult(true, "", features.Count, newPoints, trackedPoints, merged, mergeDescription);
            }
        }

        /// <summary>A copy of the whole map, with every sub-map moved into the global frame, safe to read from any thread.</summary>
        public GlobalMapSnapshot GetSnapshot()
        {
            lock (_mapLock)
                return CreateSnapshotLocked();
        }

        /// <summary>
        /// The transform from a player's own device tracking space into the shared map frame. This is what the client applies to
        /// its pose to line up with the other players. Identity until the player's map has been merged with another player's.
        /// </summary>
        public bool TryGetPlayerToMapTransform(int playerID, out RigidTransform transform)
        {
            lock (_mapLock)
                return _playerToMap.TryGetValue(playerID, out transform);
        }

        /// <summary>Read-only access to a player's sub-map (for tests and the upcoming alignment step). Hold no references across calls.</summary>
        public SubMap? GetSubMapOfPlayer(int playerID)
        {
            lock (_mapLock)
                return _subMapIDByPlayer.TryGetValue(playerID, out int subMapID) ? _subMaps[subMapID] : null;
        }

        #endregion Methods

        #region Key Frame Construction

        private bool TryBuildKeyFrame(int playerID, KeyFrameDTO dto, ORBFeatureSet features, out MapKeyFrame? keyFrame, out SubMap? subMap, out string reason)
        {
            keyFrame = null;
            subMap = null;
            reason = "";

            var ci = dto.CameraIntrinsics;
            if (!(ci.FocalLength.x > 0f) || !(ci.FocalLength.y > 0f))
            {
                reason = "invalid camera intrinsics";
                return false;
            }

            var rotation = new Quaternion(dto.CameraLocalPose.Rotation.X, dto.CameraLocalPose.Rotation.Y, dto.CameraLocalPose.Rotation.Z, dto.CameraLocalPose.Rotation.W);
            if (rotation.LengthSquared() < 1e-6f || float.IsNaN(rotation.X))
            {
                reason = "invalid camera rotation";
                return false;
            }
            rotation = Quaternion.Normalize(rotation);
            var position = new Vector3(dto.CameraLocalPose.Position.X, dto.CameraLocalPose.Position.Y, dto.CameraLocalPose.Position.Z);

            // The pose is in the device's own tracking space; move it into the frame of the map the player belongs to
            // (identity until that player's map has been merged with another player's).
            RigidTransform deviceToMap = _playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity);
            position = deviceToMap.Apply(position);
            rotation = deviceToMap.Apply(rotation);

            if (!_subMapIDByPlayer.TryGetValue(playerID, out int subMapID))
            {
                subMapID = _nextSubMapID++;
                _subMapIDByPlayer[playerID] = subMapID;
                var created = new SubMap { ID = subMapID };
                created.PlayerIDs.Add(playerID);
                _subMaps[subMapID] = created;
                _playerToMap[playerID] = RigidTransform.Identity;
            }
            subMap = _subMaps[subMapID];

            if (subMap.OrderedKeyFrames.Count > 0)
            {
                MapKeyFrame last = subMap.OrderedKeyFrames[^1];
                float moved = Vector3.Distance(last.Position, position);
                float turned = AngleBetweenDegrees(last.Rotation, rotation);
                if (moved < _parameters.MinTranslationMeters && turned < _parameters.MinRotationDegrees)
                {
                    reason = "barely moved";
                    return false;
                }
            }

            // The image the camera delivered is a centred crop of the sensor, so move the principal point into image coordinates
            // (the sensor origin is at the bottom-left; the image origin here is top-left).
            int w = features.Width, h = features.Height;
            float offsetX = ci.SensorResolution.x > 0 ? (ci.SensorResolution.x - w) / 2f : 0f;
            float offsetY = ci.SensorResolution.y > 0 ? (ci.SensorResolution.y - h) / 2f : 0f;
            float cx = ci.PrincipalPoint.x - offsetX;
            float cyTop = h - (ci.PrincipalPoint.y - offsetY);

            var levelScale = new float[features.Count];
            for (int i = 0; i < levelScale.Length; i++)
                levelScale[i] = MathF.Pow(features.ScaleFactor, features.Keypoints[i].Octave);

            var ids = new int[features.Count];
            Array.Fill(ids, -1);

            keyFrame = new MapKeyFrame
            {
                ID = _nextKeyFrameID++,
                SubMapID = subMap.ID,
                PlayerID = playerID,
                ClientKeyFrameID = dto.ID,
                Timestamp = dto.Timestamp,
                Sequence = subMap.OrderedKeyFrames.Count,
                Position = position,
                Rotation = rotation,
                Width = w,
                Height = h,
                Fx = ci.FocalLength.x,
                Fy = ci.FocalLength.y,
                Cx = cx,
                Cy = cyTop,
                FlipVertically = _parameters.FlipImageVertically,
                Features = features,
                MapPointIDs = ids,
                LevelScale = levelScale,
            };
            keyFrame.Prepare();
            return true;
        }

        private static float AngleBetweenDegrees(Quaternion a, Quaternion b)
        {
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        #endregion Key Frame Construction

        #region Map Points

        /// <summary>Does the 3D point land within tolerance of the given keypoint when seen from the key frame?</summary>
        private bool ProjectsOnto(Vector3 point, MapKeyFrame keyFrame, int featureIndex)
        {
            if (!keyFrame.TryProject(point, out float x, out float y, out float depth))
                return false;
            if (depth < _parameters.MinDepthMeters || depth > _parameters.MaxDepthMeters)
                return false;

            ORBKeypoint kp = keyFrame.Features.Keypoints[featureIndex];
            float dx = x - kp.X;
            float dy = y - kp.Y;
            float tolerance = _parameters.ReprojectionTolerancePixels * keyFrame.LevelScale[featureIndex];
            return dx * dx + dy * dy <= tolerance * tolerance;
        }

        /// <summary>
        /// Triangulate one matched feature pair from two known camera poses: the midpoint of the closest approach of the two
        /// viewing rays, accepted only if it has enough parallax, lies in front of both cameras and reprojects within tolerance.
        /// </summary>
        private bool TryTriangulate(MapKeyFrame a, int ia, MapKeyFrame b, int ib, out Vector3 point)
        {
            point = default;

            ORBKeypoint ka = a.Features.Keypoints[ia];
            ORBKeypoint kb = b.Features.Keypoints[ib];
            Vector3 d1 = a.PixelToWorldRay(ka.X, ka.Y);
            Vector3 d2 = b.PixelToWorldRay(kb.X, kb.Y);

            float cosAngle = Vector3.Dot(d1, d2);
            if (cosAngle > MathF.Cos(_parameters.MinParallaxDegrees * (MathF.PI / 180f)))
                return false;

            // Minimise |o1 + t1*d1 - (o2 + t2*d2)|; d1 and d2 are unit length.
            Vector3 w = a.Position - b.Position;
            float d = Vector3.Dot(d1, w);
            float e = Vector3.Dot(d2, w);
            float denominator = 1f - cosAngle * cosAngle;
            if (denominator < 1e-9f)
                return false;

            float t1 = (cosAngle * e - d) / denominator;
            float t2 = (e - cosAngle * d) / denominator;
            if (t1 <= 0f || t2 <= 0f)
                return false;

            Vector3 candidate = 0.5f * ((a.Position + t1 * d1) + (b.Position + t2 * d2));
            if (!ProjectsOnto(candidate, a, ia) || !ProjectsOnto(candidate, b, ib))
                return false;

            point = candidate;
            return true;
        }

        /// <summary>A 3D point that can be projected into a key frame and compared with its keypoints.</summary>
        private readonly record struct ProjectionCandidate(Vector3 Position, Vector3 ViewDirection, float MinDistance, float MaxDistance, ulong[] Descriptor);

        /// <summary>
        /// Match the key frame's features against the sub-map's map points. Returns how many map points were found in this key frame.
        /// </summary>
        private int MatchMapPoints(SubMap subMap, MapKeyFrame keyFrame, HashSet<int> touched)
        {
            if (subMap.Points.Count == 0 || keyFrame.Features.Count == 0)
                return 0;

            MapPoint[] points = subMap.Points.Values.ToArray();
            var candidates = new ProjectionCandidate[points.Length];
            for (int i = 0; i < points.Length; i++)
                candidates[i] = new ProjectionCandidate(points[i].Position, points[i].MeanViewDirection, points[i].MinDistance, points[i].MaxDistance, points[i].Descriptor);

            var visible = new List<int>();
            List<(int Candidate, int Feature, int Distance)> matches = MatchByProjection(keyFrame, candidates, visible);

            foreach (int i in visible)
                points[i].VisibleCount++;

            foreach (var (candidate, feature, _) in matches)
            {
                MapPoint point = points[candidate];
                Observe(point, keyFrame, feature);
                point.FoundCount++;
                touched.Add(point.ID);
            }

            return matches.Count;
        }

        /// <summary>
        /// Each candidate that should be visible (in front of the camera, inside the image, seen from a similar direction and
        /// distance as before) is projected into the key frame, and its descriptor is compared with the keypoints around that
        /// spot on the predicted pyramid levels. The best match must pass the distance limit and the ratio test. A feature can
        /// only belong to one candidate (the closest descriptor wins). Returns (candidate index, feature, distance) for the
        /// matches, and the indices of the candidates that were visible through <paramref name="visible"/>.
        /// </summary>
        private List<(int Candidate, int Feature, int Distance)> MatchByProjection(MapKeyFrame keyFrame, IReadOnlyList<ProjectionCandidate> candidates, List<int>? visible)
        {
            ORBFeatureSet features = keyFrame.Features;
            var grid = new KeypointGrid(features.Keypoints, keyFrame.Width, keyFrame.Height);
            ORBMatchParameters matchParameters = _parameters.MatchParameters;
            float cosMaxViewAngle = MathF.Cos(_parameters.MaxViewAngleDegrees * (MathF.PI / 180f));
            float logScaleFactor = MathF.Log(features.ScaleFactor);

            var nearby = new List<int>();
            var proposals = new List<(int Candidate, int Feature, int Distance)>();

            for (int c = 0; c < candidates.Count; c++)
            {
                ProjectionCandidate candidate = candidates[c];
                if (!keyFrame.TryProject(candidate.Position, out float x, out float y, out _))
                    continue;
                if (x < 0f || x >= keyFrame.Width || y < 0f || y >= keyFrame.Height)
                    continue;

                Vector3 toCamera = keyFrame.Position - candidate.Position;
                float distance = toCamera.Length();
                if (distance < 0.8f * candidate.MinDistance || distance > 1.2f * candidate.MaxDistance)
                    continue;
                if (candidate.ViewDirection != Vector3.Zero && Vector3.Dot(candidate.ViewDirection, toCamera / distance) < cosMaxViewAngle)
                    continue;

                visible?.Add(c);

                // Pyramid level the point should be seen on from this distance (closer -> a finer level).
                int predictedLevel = (int)MathF.Ceiling(MathF.Log(candidate.MaxDistance / distance) / logScaleFactor);
                predictedLevel = Math.Clamp(predictedLevel, 0, Math.Max(0, features.LevelCount - 1));
                float radius = _parameters.ProjectionSearchRadiusPixels * MathF.Pow(features.ScaleFactor, predictedLevel);
                grid.Query(x, y, radius, predictedLevel - 1, predictedLevel, nearby);

                int best = int.MaxValue, second = int.MaxValue, bestFeature = -1;
                foreach (int feature in nearby)
                {
                    int d = ORBUtilities.HammingDistance(candidate.Descriptor, features.GetDescriptor(feature));
                    if (d < best)
                    {
                        second = best;
                        best = d;
                        bestFeature = feature;
                    }
                    else if (d < second)
                    {
                        second = d;
                    }
                }

                if (bestFeature < 0 || best > matchParameters.MaxDistance)
                    continue;
                if (second != int.MaxValue && best > matchParameters.RatioThreshold * second)
                    continue;

                proposals.Add((c, bestFeature, best));
            }

            // Two candidates may want the same feature: the closest descriptor wins.
            proposals.Sort((l, r) => l.Distance.CompareTo(r.Distance));
            var usedFeatures = new HashSet<int>();
            var matches = new List<(int Candidate, int Feature, int Distance)>(proposals.Count);
            foreach (var proposal in proposals)
                if (usedFeatures.Add(proposal.Feature))
                    matches.Add(proposal);

            return matches;
        }

        /// <summary>
        /// Triangulate map points from the features no map point explains yet. They are matched against the key frames that
        /// share the most map points with this one (plus the latest key frame), using only the neighbours' unexplained features too.
        /// </summary>
        private int TriangulateNewPoints(SubMap subMap, MapKeyFrame keyFrame, HashSet<int> touched)
        {
            List<MapKeyFrame> neighbors = SelectNeighbors(subMap, keyFrame);
            if (neighbors.Count == 0)
                return 0;

            List<int> freeNew = FreeFeatures(keyFrame);
            if (freeNew.Count < 2)
                return 0;
            ORBFeatureSet newSubset = BuildSubset(keyFrame.Features, freeNew);

            int created = 0;
            foreach (MapKeyFrame neighbor in neighbors)
            {
                List<int> freeNeighbor = FreeFeatures(neighbor);
                if (freeNeighbor.Count < 2)
                    continue;

                ORBMatchResult match = ORBUtilities.CompareFeatures(newSubset, BuildSubset(neighbor.Features, freeNeighbor), _parameters.MatchParameters);
                foreach (ORBMatch m in match.Matches)
                {
                    int qi = freeNew[m.QueryIndex];
                    int ri = freeNeighbor[m.ReferenceIndex];
                    if (keyFrame.MapPointIDs[qi] >= 0 || neighbor.MapPointIDs[ri] >= 0)
                        continue; // already explained by an earlier neighbour in this loop

                    if (TryTriangulate(neighbor, ri, keyFrame, qi, out Vector3 position))
                    {
                        MapPoint point = CreateMapPoint(subMap, keyFrame, qi, position);
                        Observe(point, neighbor, ri);
                        Observe(point, keyFrame, qi);
                        touched.Add(point.ID);
                        created++;
                    }
                }
            }

            return created;
        }

        /// <summary>
        /// The key frames to triangulate against: those sharing the most map points with the new key frame, plus the latest key
        /// frame. With no shared points yet (the first key frames of a sub-map) it falls back to the latest key frames.
        /// </summary>
        private List<MapKeyFrame> SelectNeighbors(SubMap subMap, MapKeyFrame keyFrame)
        {
            var shared = new Dictionary<int, int>();
            foreach (int pointID in keyFrame.MapPointIDs)
            {
                if (pointID < 0 || !subMap.Points.TryGetValue(pointID, out MapPoint? point))
                    continue;
                foreach (int otherID in point.Observations.Keys)
                    if (otherID != keyFrame.ID)
                        shared[otherID] = shared.GetValueOrDefault(otherID) + 1;
            }

            var neighbors = new List<MapKeyFrame>();
            foreach (var (otherID, _) in shared.OrderByDescending(kv => kv.Value).Take(_parameters.NeighborKeyFrameCount))
                if (subMap.KeyFrames.TryGetValue(otherID, out MapKeyFrame? other))
                    neighbors.Add(other);

            int latestWanted = neighbors.Count == 0 ? _parameters.NeighborKeyFrameCount : 1;
            for (int i = subMap.OrderedKeyFrames.Count - 1; i >= 0 && latestWanted > 0; i--, latestWanted--)
                if (!neighbors.Contains(subMap.OrderedKeyFrames[i]))
                    neighbors.Add(subMap.OrderedKeyFrames[i]);

            return neighbors;
        }

        private static List<int> FreeFeatures(MapKeyFrame keyFrame)
        {
            var free = new List<int>(keyFrame.MapPointIDs.Length);
            for (int i = 0; i < keyFrame.MapPointIDs.Length; i++)
                if (keyFrame.MapPointIDs[i] < 0)
                    free.Add(i);
            return free;
        }

        /// <summary>A feature set made of the chosen features only (index i of the result is <c>indices[i]</c> of the source).</summary>
        private static ORBFeatureSet BuildSubset(ORBFeatureSet source, List<int> indices)
        {
            var keypoints = new ORBKeypoint[indices.Count];
            var descriptors = new ulong[indices.Count * ORBFeatureSet.DescriptorLongs];
            for (int i = 0; i < indices.Count; i++)
            {
                keypoints[i] = source.Keypoints[indices[i]];
                Array.Copy(source.Descriptors, indices[i] * ORBFeatureSet.DescriptorLongs, descriptors, i * ORBFeatureSet.DescriptorLongs, ORBFeatureSet.DescriptorLongs);
            }
            return new ORBFeatureSet(source.Width, source.Height, source.ScaleFactor, source.LevelCount, keypoints, descriptors);
        }

        private MapPoint CreateMapPoint(SubMap subMap, MapKeyFrame creator, int featureIndex, Vector3 position)
        {
            // Like ORB-SLAM: the distance range the point can be recognised from follows from the pyramid level it was detected on.
            float distance = Vector3.Distance(creator.Position, position);
            float maxDistance = distance * creator.LevelScale[featureIndex];
            float minDistance = maxDistance / MathF.Pow(creator.Features.ScaleFactor, Math.Max(0, creator.Features.LevelCount - 1));

            var point = new MapPoint
            {
                ID = _nextMapPointID++,
                SubMapID = subMap.ID,
                Position = position,
                ReferenceKeyFrameID = creator.ID,
                CreatedSequence = creator.Sequence,
                MinDistance = minDistance,
                MaxDistance = maxDistance,
            };
            subMap.Points[point.ID] = point;
            subMap.RecentPointIDs.Add(point.ID);
            return point;
        }

        /// <summary>Uniform grid over a key frame's keypoints for fast "keypoints near this pixel" queries.</summary>
        private sealed class KeypointGrid
        {
            private const int CellSize = 32;
            private readonly ORBKeypoint[] _keypoints;
            private readonly List<int>?[] _cells;
            private readonly int _columns;
            private readonly int _rows;

            public KeypointGrid(ORBKeypoint[] keypoints, int width, int height)
            {
                _keypoints = keypoints;
                _columns = (width + CellSize - 1) / CellSize;
                _rows = (height + CellSize - 1) / CellSize;
                _cells = new List<int>?[_columns * _rows];

                for (int i = 0; i < keypoints.Length; i++)
                {
                    int cx = Math.Clamp((int)(keypoints[i].X / CellSize), 0, _columns - 1);
                    int cy = Math.Clamp((int)(keypoints[i].Y / CellSize), 0, _rows - 1);
                    (_cells[cy * _columns + cx] ??= new List<int>()).Add(i);
                }
            }

            /// <summary>Fills <paramref name="result"/> with the keypoints within the radius that were detected on pyramid levels minOctave..maxOctave.</summary>
            public void Query(float x, float y, float radius, int minOctave, int maxOctave, List<int> result)
            {
                result.Clear();
                int minCx = Math.Max(0, (int)MathF.Floor((x - radius) / CellSize));
                int maxCx = Math.Min(_columns - 1, (int)MathF.Floor((x + radius) / CellSize));
                int minCy = Math.Max(0, (int)MathF.Floor((y - radius) / CellSize));
                int maxCy = Math.Min(_rows - 1, (int)MathF.Floor((y + radius) / CellSize));
                float radiusSquared = radius * radius;

                for (int cy = minCy; cy <= maxCy; cy++)
                    for (int cx = minCx; cx <= maxCx; cx++)
                    {
                        List<int>? cell = _cells[cy * _columns + cx];
                        if (cell == null)
                            continue;

                        foreach (int i in cell)
                        {
                            ORBKeypoint kp = _keypoints[i];
                            if (kp.Octave < minOctave || kp.Octave > maxOctave)
                                continue;
                            float dx = kp.X - x, dy = kp.Y - y;
                            if (dx * dx + dy * dy <= radiusSquared)
                                result.Add(i);
                        }
                    }
            }
        }

        private static void Observe(MapPoint point, MapKeyFrame keyFrame, int featureIndex)
        {
            point.Observations[keyFrame.ID] = featureIndex;
            keyFrame.MapPointIDs[featureIndex] = point.ID;
        }

        /// <summary>Recompute a point's representative descriptor and mean viewing direction after its observations changed.</summary>
        private static void RefreshMapPoint(SubMap subMap, MapPoint point)
        {
            var descriptors = new List<ulong[]>(point.Observations.Count);
            Vector3 viewSum = Vector3.Zero;

            foreach (var (keyFrameID, featureIndex) in point.Observations)
            {
                if (!subMap.KeyFrames.TryGetValue(keyFrameID, out MapKeyFrame? keyFrame))
                    continue;

                descriptors.Add(keyFrame.Features.GetDescriptor(featureIndex).ToArray());
                viewSum += Vector3.Normalize(keyFrame.Position - point.Position);
            }

            if (descriptors.Count == 0)
                return;

            if (viewSum.LengthSquared() > 1e-12f)
                point.MeanViewDirection = Vector3.Normalize(viewSum);

            // Like ORB-SLAM: the observed descriptor with the smallest median distance to all the others.
            int best = 0;
            if (descriptors.Count > 2)
            {
                int bestMedian = int.MaxValue;
                var distances = new int[descriptors.Count];
                for (int i = 0; i < descriptors.Count; i++)
                {
                    for (int j = 0; j < descriptors.Count; j++)
                        distances[j] = ORBUtilities.HammingDistance(descriptors[i], descriptors[j]);
                    Array.Sort(distances);
                    int median = distances[distances.Length / 2];
                    if (median < bestMedian)
                    {
                        bestMedian = median;
                        best = i;
                    }
                }
            }
            point.Descriptor = descriptors[best];
        }

        /// <summary>Link the new key frame to every key frame it shares at least a few map points with.</summary>
        private void UpdateCovisibility(SubMap subMap, MapKeyFrame keyFrame)
        {
            var shared = new Dictionary<int, int>();
            foreach (int pointID in keyFrame.MapPointIDs)
            {
                if (pointID < 0 || !subMap.Points.TryGetValue(pointID, out MapPoint? point))
                    continue;

                foreach (int otherID in point.Observations.Keys)
                {
                    if (otherID == keyFrame.ID)
                        continue;
                    shared[otherID] = shared.GetValueOrDefault(otherID) + 1;
                }
            }

            if (shared.Count == 0)
                return;

            // ORB-SLAM always keeps the strongest link, even if it is below the threshold, so the graph stays connected.
            int strongestID = shared.MaxBy(kv => kv.Value).Key;
            foreach (var (otherID, count) in shared)
            {
                if (count < _parameters.MinCovisibleMapPoints && otherID != strongestID)
                    continue;

                keyFrame.Covisibility[otherID] = count;
                subMap.KeyFrames[otherID].Covisibility[keyFrame.ID] = count;
            }
        }

        /// <summary>ORB-SLAM's recent map point culling: young points that too few key frames confirm are removed.</summary>
        private void CullRecentMapPoints(SubMap subMap, int currentSequence)
        {
            for (int i = subMap.RecentPointIDs.Count - 1; i >= 0; i--)
            {
                int pointID = subMap.RecentPointIDs[i];
                if (!subMap.Points.TryGetValue(pointID, out MapPoint? point))
                {
                    subMap.RecentPointIDs.RemoveAt(i);
                    continue;
                }

                int age = currentSequence - point.CreatedSequence;
                bool rarelyFound = (float)point.FoundCount / point.VisibleCount < _parameters.MinFoundRatio;
                if (rarelyFound || (age >= _parameters.CullAfterKeyFrames && point.Observations.Count < _parameters.MinObservationsToKeep))
                {
                    RemoveMapPoint(subMap, point);
                    subMap.RecentPointIDs.RemoveAt(i);
                }
                else if (age >= _parameters.CullAfterKeyFrames + 1)
                {
                    subMap.RecentPointIDs.RemoveAt(i); // confirmed: no longer a culling candidate
                }
            }
        }

        private static void RemoveMapPoint(SubMap subMap, MapPoint point)
        {
            foreach (var (keyFrameID, featureIndex) in point.Observations)
            {
                if (subMap.KeyFrames.TryGetValue(keyFrameID, out MapKeyFrame? keyFrame) && keyFrame.MapPointIDs[featureIndex] == point.ID)
                    keyFrame.MapPointIDs[featureIndex] = -1;
            }
            subMap.Points.Remove(point.ID);
        }

        #endregion Map Points

        #region Merging sub-maps

        /// <summary>
        /// Look for another sub-map that overlaps this key frame's surroundings. The map points around the key frame (its
        /// local window) are matched by descriptor against every other sub-map's points; the matched 3D points give the transform
        /// between the two frames. If it holds up, the two sub-maps are merged. Merges at most one sub-map per call.
        /// </summary>
        private bool TryMergeSubMaps(SubMap subMap, MapKeyFrame keyFrame, out string description)
        {
            description = "";
            if (_subMaps.Count < 2)
                return false;

            List<MapPoint> window = CollectLocalWindow(subMap, keyFrame);
            if (window.Count < _parameters.MinMergeInliers)
                return false;

            foreach (SubMap other in _subMaps.Values.Where(s => s.ID != subMap.ID).OrderBy(s => s.ID).ToList())
            {
                if (!TryAlignSubMaps(keyFrame, window, other, out RigidTransform otherToThis, out var pairs, out int verified))
                    continue;

                // The sub-map with the lower ID (the older one) survives, so a merged map keeps the older player's frame.
                SubMap survivor, absorbed;
                RigidTransform absorbedToSurvivor;
                var fusePairs = new List<(MapPoint Keep, MapPoint Remove)>(pairs.Count);
                if (other.ID < subMap.ID)
                {
                    survivor = other;
                    absorbed = subMap;
                    absorbedToSurvivor = otherToThis.Inverse();
                    foreach (var pair in pairs)
                        fusePairs.Add((pair.Other, pair.Window));
                }
                else
                {
                    survivor = subMap;
                    absorbed = other;
                    absorbedToSurvivor = otherToThis;
                    foreach (var pair in pairs)
                        fusePairs.Add((pair.Window, pair.Other));
                }

                string absorbedPlayers = string.Join(", ", absorbed.PlayerIDs.Order());
                string survivorPlayers = string.Join(", ", survivor.PlayerIDs.Order());
                MergeInto(survivor, absorbed, absorbedToSurvivor, fusePairs);

                description = $"Merged map {absorbed.ID} (players {absorbedPlayers}) into map {survivor.ID} (players {survivorPlayers}): " +
                              $"{pairs.Count} matching points, {verified} confirmed in the image, transform {absorbedToSurvivor}. " +
                              $"Shared map now has {survivor.OrderedKeyFrames.Count} key frames and {survivor.Points.Count} points.";
                return true;
            }

            return false;
        }

        /// <summary>The map points seen by the key frame and by its most covisible neighbours.</summary>
        private List<MapPoint> CollectLocalWindow(SubMap subMap, MapKeyFrame keyFrame)
        {
            var keyFrames = new List<MapKeyFrame> { keyFrame };
            foreach (var (otherID, _) in keyFrame.Covisibility.OrderByDescending(kv => kv.Value).Take(_parameters.NeighborKeyFrameCount))
                if (subMap.KeyFrames.TryGetValue(otherID, out MapKeyFrame? other))
                    keyFrames.Add(other);

            var seen = new HashSet<int>();
            var window = new List<MapPoint>();
            foreach (MapKeyFrame kf in keyFrames)
                foreach (int pointID in kf.MapPointIDs)
                    if (pointID >= 0 && subMap.Points.TryGetValue(pointID, out MapPoint? point) && seen.Add(pointID))
                        window.Add(point);

            return window;
        }

        /// <summary>
        /// Estimate the transform from <paramref name="other"/>'s frame into the key frame's frame. The window points are matched
        /// by descriptor against all of the other sub-map's points (ratio test, one-to-one). RANSAC then finds the yaw + translation
        /// that the most pairs agree on (both devices are gravity-aligned and metric, so no tilt or scale is needed), the fit is
        /// refined on the inliers, and finally the transform is verified by projecting the other sub-map's points into the key frame.
        /// </summary>
        private bool TryAlignSubMaps(MapKeyFrame keyFrame, List<MapPoint> window, SubMap other, out RigidTransform otherToThis,
            out List<(MapPoint Window, MapPoint Other)> pairs, out int verified)
        {
            otherToThis = RigidTransform.Identity;
            pairs = new List<(MapPoint Window, MapPoint Other)>();
            verified = 0;

            if (other.Points.Count < _parameters.MinMergeInliers)
                return false;

            MapPoint[] otherPoints = other.Points.Values.ToArray();
            ORBMatchParameters matchParameters = _parameters.MatchParameters;

            // ----- Descriptor matches: window point -> other sub-map point -----
            int n = window.Count;
            var bestIndex = new int[n];
            var bestDistance = new int[n];
            var accepted = new bool[n];
            Parallel.For(0, n, i =>
            {
                int best = int.MaxValue, second = int.MaxValue, bestJ = -1;
                ulong[] descriptor = window[i].Descriptor;
                for (int j = 0; j < otherPoints.Length; j++)
                {
                    int d = ORBUtilities.HammingDistance(descriptor, otherPoints[j].Descriptor);
                    if (d < best)
                    {
                        second = best;
                        best = d;
                        bestJ = j;
                    }
                    else if (d < second)
                    {
                        second = d;
                    }
                }

                bestIndex[i] = bestJ;
                bestDistance[i] = best;
                accepted[i] = bestJ >= 0 && best <= matchParameters.MaxDistance
                              && (second == int.MaxValue || best <= matchParameters.RatioThreshold * second);
            });

            var winnerForOther = new Dictionary<int, int>(); // other point index -> window index (closest descriptor wins)
            for (int i = 0; i < n; i++)
                if (accepted[i] && (!winnerForOther.TryGetValue(bestIndex[i], out int w) || bestDistance[i] < bestDistance[w]))
                    winnerForOther[bestIndex[i]] = i;

            var thisSide = new List<MapPoint>(winnerForOther.Count);   // window point (key frame's frame)
            var otherSide = new List<MapPoint>(winnerForOther.Count);  // matching point (other sub-map's frame)
            foreach (var (otherIndex, windowIndex) in winnerForOther)
            {
                thisSide.Add(window[windowIndex]);
                otherSide.Add(otherPoints[otherIndex]);
            }

            if (thisSide.Count < _parameters.MinMergeInliers)
                return false;

            // ----- RANSAC over yaw + translation -----
            float threshold = _parameters.MergeInlierDistanceMeters;
            var random = new Random(12345);
            RigidTransform best3 = RigidTransform.Identity;
            int bestInliers = 0;
            int count = thisSide.Count;
            var sample = new int[2];

            for (int iteration = 0; iteration < _parameters.MergeRansacIterations; iteration++)
            {
                sample[0] = random.Next(count);
                sample[1] = random.Next(count);
                if (sample[0] == sample[1])
                    continue;

                Vector3 sa = thisSide[sample[0]].Position, sb = thisSide[sample[1]].Position;
                Vector3 oa = otherSide[sample[0]].Position, ob = otherSide[sample[1]].Position;
                // The two points must be well apart horizontally (to fix the yaw) and equally far apart in both maps.
                if ((new Vector2(ob.X - oa.X, ob.Z - oa.Z)).LengthSquared() < 0.01f)
                    continue;
                if (MathF.Abs(Vector3.Distance(sa, sb) - Vector3.Distance(oa, ob)) > 2f * threshold)
                    continue;

                if (!TrySolveYawTranslation(thisSide, otherSide, sample, out RigidTransform candidate))
                    continue;

                int inliers = CountInliers(candidate, thisSide, otherSide, threshold, null);
                if (inliers > bestInliers)
                {
                    bestInliers = inliers;
                    best3 = candidate;
                }
            }

            if (bestInliers < _parameters.MinMergeInliers)
                return false;

            // Refine on all inliers.
            var inlierIndices = new List<int>(count);
            for (int round = 0; round < 3; round++)
            {
                inlierIndices.Clear();
                CountInliers(best3, thisSide, otherSide, threshold, inlierIndices);
                if (inlierIndices.Count < 2 || !TrySolveYawTranslation(thisSide, otherSide, inlierIndices, out RigidTransform refined))
                    break;
                best3 = refined;
            }

            inlierIndices.Clear();
            CountInliers(best3, thisSide, otherSide, threshold, inlierIndices);
            if (inlierIndices.Count < _parameters.MinMergeInliers)
                return false;

            // ----- Verify against the image: the other sub-map's points should land on matching keypoints in this key frame -----
            var candidates = new ProjectionCandidate[otherPoints.Length];
            for (int i = 0; i < otherPoints.Length; i++)
            {
                MapPoint p = otherPoints[i];
                candidates[i] = new ProjectionCandidate(best3.Apply(p.Position), best3.ApplyToDirection(p.MeanViewDirection), p.MinDistance, p.MaxDistance, p.Descriptor);
            }

            verified = MatchByProjection(keyFrame, candidates, null).Count;
            if (verified < _parameters.MinMergeVerificationMatches)
                return false;

            otherToThis = best3;
            foreach (int i in inlierIndices)
                pairs.Add((thisSide[i], otherSide[i]));
            return true;
        }

        private static int CountInliers(RigidTransform transform, List<MapPoint> target, List<MapPoint> source, float threshold, List<int>? indices)
        {
            float thresholdSquared = threshold * threshold;
            int count = 0;
            for (int i = 0; i < target.Count; i++)
            {
                if (Vector3.DistanceSquared(transform.Apply(source[i].Position), target[i].Position) <= thresholdSquared)
                {
                    count++;
                    indices?.Add(i);
                }
            }
            return count;
        }

        /// <summary>
        /// Least-squares yaw (rotation about the vertical axis) and translation taking the source points onto the target points.
        /// Closed form: the yaw comes from the horizontal components around the centroids, the translation from the centroids.
        /// </summary>
        private static bool TrySolveYawTranslation(List<MapPoint> target, List<MapPoint> source, IList<int> indices, out RigidTransform transform)
        {
            transform = RigidTransform.Identity;
            if (indices.Count < 2)
                return false;

            Vector3 targetCentroid = Vector3.Zero, sourceCentroid = Vector3.Zero;
            foreach (int i in indices)
            {
                targetCentroid += target[i].Position;
                sourceCentroid += source[i].Position;
            }
            targetCentroid /= indices.Count;
            sourceCentroid /= indices.Count;

            double dot = 0, cross = 0;
            foreach (int i in indices)
            {
                Vector3 s = target[i].Position - targetCentroid;
                Vector3 t = source[i].Position - sourceCentroid;
                dot += s.X * t.X + s.Z * t.Z;
                cross += s.X * t.Z - s.Z * t.X;
            }

            if (dot * dot + cross * cross < 1e-10)
                return false;

            float yaw = (float)Math.Atan2(cross, dot);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            transform = new RigidTransform(rotation, targetCentroid - Vector3.Transform(sourceCentroid, rotation));
            return true;
        }

        /// <summary>
        /// Move every key frame and map point of <paramref name="absorbed"/> into <paramref name="survivor"/>'s frame and make
        /// it part of the survivor. Players of the absorbed sub-map get their device-to-map transform updated so their next key
        /// frames arrive in the shared frame. Matched duplicate points are fused.
        /// </summary>
        private void MergeInto(SubMap survivor, SubMap absorbed, RigidTransform absorbedToSurvivor, List<(MapPoint Keep, MapPoint Remove)> fusePairs)
        {
            int nextSequence = survivor.OrderedKeyFrames.Count;
            foreach (MapKeyFrame keyFrame in absorbed.OrderedKeyFrames)
            {
                keyFrame.Position = absorbedToSurvivor.Apply(keyFrame.Position);
                keyFrame.Rotation = absorbedToSurvivor.Apply(keyFrame.Rotation);
                keyFrame.Prepare();
                keyFrame.SubMapID = survivor.ID;
                keyFrame.Sequence = nextSequence++;
                survivor.KeyFrames[keyFrame.ID] = keyFrame;
                survivor.OrderedKeyFrames.Add(keyFrame);
            }

            foreach (MapPoint point in absorbed.Points.Values)
            {
                point.Position = absorbedToSurvivor.Apply(point.Position);
                point.MeanViewDirection = absorbedToSurvivor.ApplyToDirection(point.MeanViewDirection);
                point.SubMapID = survivor.ID;
                if (survivor.KeyFrames.TryGetValue(point.ReferenceKeyFrameID, out MapKeyFrame? reference))
                    point.CreatedSequence = reference.Sequence;
                survivor.Points[point.ID] = point;
            }
            survivor.RecentPointIDs.AddRange(absorbed.RecentPointIDs);

            foreach (int playerID in absorbed.PlayerIDs)
            {
                survivor.PlayerIDs.Add(playerID);
                _subMapIDByPlayer[playerID] = survivor.ID;
                _playerToMap[playerID] = absorbedToSurvivor.Compose(_playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity));
            }
            survivor.IsAligned = true;
            _subMaps.Remove(absorbed.ID);

            // Fuse the points both maps had: the survivor's point takes over every observation of the absorbed one.
            var affected = new HashSet<int>();
            foreach (var (keep, remove) in fusePairs)
            {
                if (keep.ID == remove.ID || !survivor.Points.ContainsKey(keep.ID) || !survivor.Points.ContainsKey(remove.ID))
                    continue;

                foreach (var (keyFrameID, featureIndex) in remove.Observations.ToList())
                {
                    MapKeyFrame keyFrame = survivor.KeyFrames[keyFrameID];
                    if (!keep.Observations.ContainsKey(keyFrameID))
                    {
                        keep.Observations[keyFrameID] = featureIndex;
                        keyFrame.MapPointIDs[featureIndex] = keep.ID;
                    }
                    else if (keyFrame.MapPointIDs[featureIndex] == remove.ID)
                    {
                        keyFrame.MapPointIDs[featureIndex] = -1;
                    }
                }

                keep.FoundCount += remove.FoundCount;
                keep.VisibleCount += remove.VisibleCount;
                survivor.Points.Remove(remove.ID);
                RefreshMapPoint(survivor, keep);
                foreach (int keyFrameID in keep.Observations.Keys)
                    affected.Add(keyFrameID);
            }

            // Fused points now link key frames of both original maps: update their covisibility.
            foreach (int keyFrameID in affected)
                UpdateCovisibility(survivor, survivor.KeyFrames[keyFrameID]);

            _mapVersion++;
        }

        #endregion Merging sub-maps


        #region Snapshots

        private GlobalMapSnapshot CreateSnapshotLocked()
        {
            var subMaps = new List<MapSnapshotSubMap>(_subMaps.Count);
            foreach (SubMap subMap in _subMaps.Values.OrderBy(s => s.ID))
            {
                Matrix4x4 toGlobal = subMap.ToGlobal;

                var keyFrames = new MapSnapshotKeyFrame[subMap.OrderedKeyFrames.Count];
                for (int i = 0; i < keyFrames.Length; i++)
                {
                    MapKeyFrame kf = subMap.OrderedKeyFrames[i];
                    Vector3 forward = Vector3.TransformNormal(Vector3.Transform(Vector3.UnitZ, kf.Rotation), toGlobal);
                    if (forward.LengthSquared() > 1e-12f)
                        forward = Vector3.Normalize(forward);
                    keyFrames[i] = new MapSnapshotKeyFrame(kf.ID, kf.PlayerID, kf.Sequence, Vector3.Transform(kf.Position, toGlobal), forward);
                }

                var points = new MapSnapshotPoint[subMap.Points.Count];
                int p = 0;
                foreach (MapPoint point in subMap.Points.Values)
                {
                    // A point is drawn in the colour of the player whose key frame created it.
                    int owner = subMap.KeyFrames.TryGetValue(point.ReferenceKeyFrameID, out MapKeyFrame? reference) ? reference.PlayerID : -1;
                    points[p++] = new MapSnapshotPoint(Vector3.Transform(point.Position, toGlobal), owner);
                }

                subMaps.Add(new MapSnapshotSubMap(subMap.ID, subMap.PlayerIDs.Order().ToArray(), subMap.IsAligned, keyFrames, points));
            }

            return new GlobalMapSnapshot(_mapVersion, subMaps.ToArray());
        }

        #endregion Snapshots

    }
}
