using System.Diagnostics;
using System.Numerics;
using Server.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;

namespace Server.Manager.SpatialSync
{
    /// <summary>
    /// Builds the global map from incoming key frames, following ORB-SLAM / SLAM-Share. Every key frame is turned into ORB
    /// features, matched against the same player's recent key frames, and the matches are triangulated into 3D map points
    /// using the camera poses the headset reports (so no pose estimation or initialisation is needed). Each player
    /// has their own sub-map in their own tracking space; aligning sub-maps with each other comes next.
    /// </summary>
    public class GlobalMappingManager
    {
        private readonly DebugImageDisplayService? _debugImageDisplayService;
        private readonly GlobalMappingParameters _parameters;
        private readonly object _mapLock = new();

        private readonly Dictionary<int, SubMap> _subMaps = new();       // sub-map ID -> sub-map
        private readonly Dictionary<int, int> _subMapIdByPlayer = new(); // player ID -> sub-map ID
        private int _nextKeyFrameId = 1;
        private int _nextMapPointId = 1;
        private int _nextSubMapId = 1;
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

                // Match against the most recent key frames of this player (a stand-in for the covisibility neighbours until the map is bigger).
                var neighbors = new List<MapKeyFrame>();
                for (int i = subMap!.OrderedKeyFrames.Count - 1; i >= 0 && neighbors.Count < _parameters.NeighborKeyFrameCount; i--)
                    neighbors.Add(subMap.OrderedKeyFrames[i]);

                var touched = new HashSet<int>();
                int newPoints = 0;
                int trackedPoints = 0;

                foreach (MapKeyFrame neighbor in neighbors)
                {
                    ORBMatchResult match = ORBUtilities.CompareFeatures(keyFrame!.Features, neighbor.Features, _parameters.MatchParameters);
                    foreach (ORBMatch m in match.Matches)
                    {
                        int qi = m.QueryIndex;
                        int ri = m.ReferenceIndex;
                        int queryPoint = keyFrame.MapPointIds[qi];
                        int referencePoint = neighbor.MapPointIds[ri];

                        if (queryPoint < 0 && referencePoint >= 0)
                        {
                            // The neighbour already sees a map point here: this key frame sees it too, if it projects onto the keypoint.
                            if (subMap.Points.TryGetValue(referencePoint, out MapPoint? point) && ProjectsOnto(point.Position, keyFrame, qi))
                            {
                                Observe(point, keyFrame, qi);
                                touched.Add(point.Id);
                                trackedPoints++;
                            }
                        }
                        else if (queryPoint >= 0 && referencePoint < 0)
                        {
                            if (subMap.Points.TryGetValue(queryPoint, out MapPoint? point) && ProjectsOnto(point.Position, neighbor, ri))
                            {
                                Observe(point, neighbor, ri);
                                touched.Add(point.Id);
                            }
                        }
                        else if (queryPoint < 0 && referencePoint < 0)
                        {
                            if (TryTriangulate(neighbor, ri, keyFrame, qi, out Vector3 position))
                            {
                                MapPoint point = CreateMapPoint(subMap, keyFrame, position);
                                Observe(point, neighbor, ri);
                                Observe(point, keyFrame, qi);
                                touched.Add(point.Id);
                                newPoints++;
                            }
                        }
                        // Both already have (different) points: they are probably duplicates; fusing them is a later step.
                    }
                }

                subMap.KeyFrames[keyFrame!.Id] = keyFrame;
                subMap.OrderedKeyFrames.Add(keyFrame);

                foreach (int pointId in touched)
                    if (subMap.Points.TryGetValue(pointId, out MapPoint? point))
                        RefreshMapPoint(subMap, point);

                UpdateCovisibility(subMap, keyFrame);
                CullRecentMapPoints(subMap, keyFrame.Sequence);

                _mapVersion++;
                return new KeyFrameInsertResult(true, "", features.Count, newPoints, trackedPoints);
            }
        }

        /// <summary>A copy of the whole map, with every sub-map moved into the global frame, safe to read from any thread.</summary>
        public GlobalMapSnapshot GetSnapshot()
        {
            lock (_mapLock)
                return CreateSnapshotLocked();
        }

        /// <summary>Read-only access to a player's sub-map (for tests and the upcoming alignment step). Hold no references across calls.</summary>
        public SubMap? GetSubMapOfPlayer(int playerID)
        {
            lock (_mapLock)
                return _subMapIdByPlayer.TryGetValue(playerID, out int id) ? _subMaps[id] : null;
        }

        #endregion Methods


        #region Key frame construction

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

            var rotation = new Quaternion(dto.CameraPose.Rotation.X, dto.CameraPose.Rotation.Y, dto.CameraPose.Rotation.Z, dto.CameraPose.Rotation.W);
            if (rotation.LengthSquared() < 1e-6f || float.IsNaN(rotation.X))
            {
                reason = "invalid camera rotation";
                return false;
            }
            rotation = Quaternion.Normalize(rotation);
            var position = new Vector3(dto.CameraPose.Position.X, dto.CameraPose.Position.Y, dto.CameraPose.Position.Z);

            if (!_subMapIdByPlayer.TryGetValue(playerID, out int subMapId))
            {
                subMapId = _nextSubMapId++;
                _subMapIdByPlayer[playerID] = subMapId;
                _subMaps[subMapId] = new SubMap { Id = subMapId, PlayerID = playerID };
            }
            subMap = _subMaps[subMapId];

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
                Id = _nextKeyFrameId++,
                SubMapId = subMap.Id,
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
                MapPointIds = ids,
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

        #endregion Key frame construction


        #region Map points

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

        private MapPoint CreateMapPoint(SubMap subMap, MapKeyFrame creator, Vector3 position)
        {
            var point = new MapPoint
            {
                Id = _nextMapPointId++,
                SubMapId = subMap.Id,
                Position = position,
                ReferenceKeyFrameId = creator.Id,
                CreatedSequence = creator.Sequence,
            };
            subMap.Points[point.Id] = point;
            subMap.RecentPointIds.Add(point.Id);
            return point;
        }

        private static void Observe(MapPoint point, MapKeyFrame keyFrame, int featureIndex)
        {
            point.Observations[keyFrame.Id] = featureIndex;
            keyFrame.MapPointIds[featureIndex] = point.Id;
        }

        /// <summary>Recompute a point's representative descriptor and mean viewing direction after its observations changed.</summary>
        private static void RefreshMapPoint(SubMap subMap, MapPoint point)
        {
            var descriptors = new List<ulong[]>(point.Observations.Count);
            Vector3 viewSum = Vector3.Zero;

            foreach (var (keyFrameId, featureIndex) in point.Observations)
            {
                if (!subMap.KeyFrames.TryGetValue(keyFrameId, out MapKeyFrame? keyFrame))
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
            foreach (int pointId in keyFrame.MapPointIds)
            {
                if (pointId < 0 || !subMap.Points.TryGetValue(pointId, out MapPoint? point))
                    continue;

                foreach (int otherId in point.Observations.Keys)
                {
                    if (otherId == keyFrame.Id)
                        continue;
                    shared[otherId] = shared.GetValueOrDefault(otherId) + 1;
                }
            }

            if (shared.Count == 0)
                return;

            // ORB-SLAM always keeps the strongest link, even if it is below the threshold, so the graph stays connected.
            int strongestId = shared.MaxBy(kv => kv.Value).Key;
            foreach (var (otherId, count) in shared)
            {
                if (count < _parameters.MinCovisibleMapPoints && otherId != strongestId)
                    continue;

                keyFrame.Covisibility[otherId] = count;
                subMap.KeyFrames[otherId].Covisibility[keyFrame.Id] = count;
            }
        }

        /// <summary>ORB-SLAM's recent map point culling: young points that too few key frames confirm are removed.</summary>
        private void CullRecentMapPoints(SubMap subMap, int currentSequence)
        {
            for (int i = subMap.RecentPointIds.Count - 1; i >= 0; i--)
            {
                int pointId = subMap.RecentPointIds[i];
                if (!subMap.Points.TryGetValue(pointId, out MapPoint? point))
                {
                    subMap.RecentPointIds.RemoveAt(i);
                    continue;
                }

                int age = currentSequence - point.CreatedSequence;
                if (age >= _parameters.CullAfterKeyFrames && point.Observations.Count < _parameters.MinObservationsToKeep)
                {
                    RemoveMapPoint(subMap, point);
                    subMap.RecentPointIds.RemoveAt(i);
                }
                else if (age >= _parameters.CullAfterKeyFrames + 1)
                {
                    subMap.RecentPointIds.RemoveAt(i); // confirmed: no longer a culling candidate
                }
            }
        }

        private static void RemoveMapPoint(SubMap subMap, MapPoint point)
        {
            foreach (var (keyFrameId, featureIndex) in point.Observations)
            {
                if (subMap.KeyFrames.TryGetValue(keyFrameId, out MapKeyFrame? keyFrame) && keyFrame.MapPointIds[featureIndex] == point.Id)
                    keyFrame.MapPointIds[featureIndex] = -1;
            }
            subMap.Points.Remove(point.Id);
        }

        #endregion Map points


        #region Snapshots

        private GlobalMapSnapshot CreateSnapshotLocked()
        {
            var subMaps = new List<MapSnapshotSubMap>(_subMaps.Count);
            foreach (SubMap subMap in _subMaps.Values.OrderBy(s => s.Id))
            {
                Matrix4x4 toGlobal = subMap.ToGlobal;

                var keyFrames = new MapSnapshotKeyFrame[subMap.OrderedKeyFrames.Count];
                for (int i = 0; i < keyFrames.Length; i++)
                {
                    MapKeyFrame kf = subMap.OrderedKeyFrames[i];
                    Vector3 forward = Vector3.TransformNormal(Vector3.Transform(Vector3.UnitZ, kf.Rotation), toGlobal);
                    if (forward.LengthSquared() > 1e-12f)
                        forward = Vector3.Normalize(forward);
                    keyFrames[i] = new MapSnapshotKeyFrame(kf.Id, kf.Sequence, Vector3.Transform(kf.Position, toGlobal), forward);
                }

                var points = new Vector3[subMap.Points.Count];
                int p = 0;
                foreach (MapPoint point in subMap.Points.Values)
                    points[p++] = Vector3.Transform(point.Position, toGlobal);

                subMaps.Add(new MapSnapshotSubMap(subMap.Id, subMap.PlayerID, subMap.IsAligned, keyFrames, points));
            }

            return new GlobalMapSnapshot(_mapVersion, subMaps.ToArray());
        }

        #endregion Snapshots

    }
}
