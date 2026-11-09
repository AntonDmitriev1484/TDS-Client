using Server.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Numerics;
using SharedPose = SharedLibrary.Game.Actions.Pose;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// Builds the global map from incoming key frames. Each key frame carries the images of the headset's two cameras and their poses as the
    /// headset reports them. The depth of the right image is measured from the pair by semi-global matching, and the depth points are placed
    /// into the player's sub-map with the pose. Each player has their own sub-map in their own tracking space. When a player's sub-map has
    /// grown, it is compared with the other sub-maps by the 3D shape and brightness of their point clouds (coarse search over yaw and
    /// position, ICP, a check, and a confirming second estimate); two sub-maps that agree are merged into one shared map. After a merge each
    /// absorbed player's transform is refined periodically against the other players' points.
    /// </summary>
    public class GlobalMappingManager
    {
        private readonly DebugImageDisplayService? _debugImageDisplayService;
        private readonly GlobalMappingParameters _parameters;
        private readonly object _mapLock = new();

        private long _lastPointCloudTicks;

        private readonly Dictionary<int, SubMap> _subMaps = new();       // sub-map ID -> sub-map
        private readonly Dictionary<int, int> _subMapIDByPlayer = new(); // player ID -> sub-map ID
        private readonly Dictionary<int, RigidTransform> _playerToMap = new(); // player ID -> device tracking space to map frame
        private int _globalSubMapID = -1; // the sub-map that formed the global map (-1 until it exists)
        private readonly Dictionary<int, Vector3> _lastKnownPositionInMap = new(); // player ID -> last reported position, in the frame of the player's sub-map
        private Vector3 _globalOrigin; // the point of the global sub-map's frame that is (0, 0, 0) in the global frame
        private int _nextKeyFrameID = 1;
        private int _nextSubMapID = 1;
        private long _mapVersion;

        #region Properties

        public bool DebugMode { get; set; }

        /// <summary>
        /// True from the moment the global map was first formed: every player of the session had been merged into one shared
        /// map. It stays true afterwards (players who join later wait until their own map has merged into the global map).
        /// </summary>
        public bool IsGlobalMapFormed { get; private set; }

        /// <summary>
        /// The point of the global sub-map's frame that is (0, 0, 0) of the global frame. Set once, when the global map is first
        /// formed, to the average of the last known positions of the players in it. The global frame has the same axes as the
        /// global sub-map's frame; it is only moved by this translation. Zero until the global map is formed.
        /// </summary>
        public Vector3 GlobalOrigin { get { lock (_mapLock) return _globalOrigin; } }

        /// <summary>
        /// Returns the players the session expects to be in the global map. If not set, the players that have sent key frames so
        /// far are used.
        /// </summary>
        public Func<IReadOnlyCollection<int>>? ExpectedPlayerIDs { get; set; }

        /// <summary>
        /// Merging and refining run on a background thread, so a key frame's reply does not wait for them. Set this to run them inside
        /// <see cref="ProcessKeyFrame"/> instead (for tests).
        /// </summary>
        public bool RunMergeSynchronously { get; set; }

        public int KeyFrameCount { get { lock (_mapLock) return _subMaps.Values.Sum(s => s.OrderedKeyFrames.Count); } }
        public int PointCount { get { lock (_mapLock) return _subMaps.Values.Sum(s => s.OrderedKeyFrames.Sum(k => k.Points.Length)); } }
        public int SubMapCount { get { lock (_mapLock) return _subMaps.Count; } }

        #endregion Properties

        #region Initialization

        public GlobalMappingManager(bool debugMode) : this(debugMode, null) { }

        public GlobalMappingManager(bool debugMode, GlobalMappingParameters? parameters)
        {
            DebugMode = debugMode;
            IsGlobalMapFormed = false;
            _parameters = parameters ?? new GlobalMappingParameters();
            _debugImageDisplayService = debugMode ? new DebugImageDisplayService() : null;
        }

        #endregion Initialization

        #region Methods

        /// <summary>
        /// Add a key frame to the map. Returns the camera pose of the key frame in the global frame once the global map has been
        /// formed and this player's map is part of it. Returns null while that is not the case, and the caller then tells the
        /// player the global map is not ready yet.
        /// </summary>
        public SharedPose? HandleNewKeyFrame(int playerID, KeyFrameImageDTO keyFrameDTO)
        {
            ProcessKeyFrame(playerID, keyFrameDTO, out SharedPose? globalPose);
            return globalPose;
        }

        /// <summary>The images of a stereo pair, decoded to 8-bit grayscale: the right camera (the reference) and the left one.</summary>
        private sealed record StereoImages(byte[] Right, int RightWidth, int RightHeight, byte[] Left, int LeftWidth, int LeftHeight);

        /// <summary>
        /// Process one key frame: measure the depth from the two images and put it into the player's sub-map, then (in the background) see
        /// whether the sub-map can be merged with another. A key frame without both images is skipped. Returns what happened and, once the
        /// global map exists, the camera's pose in the global frame.
        /// </summary>
        public KeyFrameInsertResult ProcessKeyFrame(int playerID, KeyFrameImageDTO keyFrameDTO, out SharedPose? globalPose)
        {
            globalPose = null;

            // The right camera is the reference camera: the map is built from its image and the global pose is its pose.
            CameraImageDTO? rightCamera = keyFrameDTO.RightCamera;
            if (rightCamera == null)
            {
                Console.Error.WriteLine($"[GlobalMap] Player {playerID}: key frame {keyFrameDTO.ID} has no right camera; skipped.");
                return new KeyFrameInsertResult(false, "no right camera", 0);
            }

            // Both cameras are needed to measure depth. Without both images the key frame is skipped: it does not enter the map.
            if (!TryDecodeStereoPair(keyFrameDTO, out StereoImages? images, out string skipReason))
            {
                Console.WriteLine($"[GlobalMap] Player {playerID}: key frame {keyFrameDTO.ID} skipped - {skipReason}.");
                if (DebugMode)
                    _debugImageDisplayService!.ShowFrame(playerID, keyFrameDTO);
                lock (_mapLock)
                    RecordLastKnownPosition(playerID, rightCamera.CameraLocalPose);
                globalPose = ResolveGlobalPose(playerID, rightCamera.CameraLocalPose);
                return new KeyFrameInsertResult(false, skipReason, 0);
            }

            bool canInsert;
            string reason;
            lock (_mapLock)
                canInsert = CanInsertKeyFrame(playerID, rightCamera, out reason);

            // The depth is needed to insert the key frame; in debug mode it is also measured for a frame that will not be inserted (the headset
            // is still, say), so the depth points are shown on every frame.
            DepthMap? depth = null;
            MapKeyFrame? depthCamera = null;
            if (canInsert || DebugMode)
            {
                depth = EstimateDepth(keyFrameDTO, images!, out depthCamera, out string depthReason);
                if (canInsert && depth == null)
                {
                    canInsert = false;
                    reason = depthReason;
                }
            }

            // The key frame's points, placed with the headset's pose and then lined up with the points already in the map.
            PreparedKeyFrame? prepared = canInsert ? PrepareKeyFrame(playerID, keyFrameDTO, depth!, depthCamera!, images!.Right) : null;

            KeyFrameInsertResult result;
            GlobalMapSnapshot? snapshot = null;
            Action? work = null;
            lock (_mapLock)
            {
                result = canInsert
                    ? InsertKeyFrame(playerID, keyFrameDTO, depth!, depthCamera!, images!.Right, prepared!, out work)
                    : new KeyFrameInsertResult(false, reason, 0);

                // After the insert, so a merge that already happened has updated the player's transform.
                RecordLastKnownPosition(playerID, rightCamera.CameraLocalPose);

                if (result.Inserted && DebugMode)
                    snapshot = CreateSnapshotLocked();
            }

            // Merging and refining happen away from the lock (they take a while), and in the background unless asked otherwise.
            if (work != null)
                RunWork(work);

            // After the work, in the synchronous case, so the key frame that completes the global map already gets its global pose.
            globalPose = ResolveGlobalPose(playerID, rightCamera.CameraLocalPose);

            // The frame with its depth drawn on it (a key frame that was not inserted has its depth shown too, in debug mode).
            if (DebugMode)
            {
                List<DepthPoint> points = depth != null && depthCamera != null
                    ? depth.Points(depthCamera).Select(p => new DepthPoint(p.X, p.Y, p.Depth)).ToList()
                    : new List<DepthPoint>();
                List<RejectedPoint> rejected = depth != null
                    ? depth.Rejected().Select(r => new RejectedPoint(r.X, r.Y, r.Reason)).ToList()
                    : new List<RejectedPoint>();
                string note = result.Inserted ? "stereo depth" : depth != null ? $"stereo depth, not inserted: {result.Reason}" : $"not inserted: {result.Reason}";
                _debugImageDisplayService!.ShowFrameWithDepthPoints(playerID, keyFrameDTO, images!.RightWidth, images.RightHeight, points, note, rejected);
            }

            if (snapshot != null)
                _debugImageDisplayService!.ShowMap(snapshot);

            if (DebugMode && result.Inserted)
                RefreshPointClouds(false);

            return result;
        }

        /// <summary>Decode the images of both cameras of a key frame. False (with the reason) if either is missing or cannot be decoded.</summary>
        private static bool TryDecodeStereoPair(KeyFrameImageDTO dto, out StereoImages? images, out string reason)
        {
            images = null;
            reason = "";

            CameraImageDTO? right = dto.RightCamera, left = dto.LeftCamera;
            if (right?.ImageData is not { Length: > 0 })
            {
                reason = "the right camera has no image";
                return false;
            }
            if (left?.ImageData is not { Length: > 0 })
            {
                reason = "the left camera has no image";
                return false;
            }
            if (!KeyFrameImageDecoder.TryDecodeGrayscale(right.ImageData, out byte[] rightGray, out int rightWidth, out int rightHeight))
            {
                reason = "the right image cannot be decoded";
                return false;
            }
            if (!KeyFrameImageDecoder.TryDecodeGrayscale(left.ImageData, out byte[] leftGray, out int leftWidth, out int leftHeight))
            {
                reason = "the left image cannot be decoded";
                return false;
            }

            images = new StereoImages(rightGray, rightWidth, rightHeight, leftGray, leftWidth, leftHeight);
            return true;
        }

        /// <summary>
        /// Whether a key frame would enter the map, checked before spending time on its depth: the camera has moved or turned enough since the
        /// player's last key frame. Must be called with the map lock held.
        /// </summary>
        private bool CanInsertKeyFrame(int playerID, CameraImageDTO camera, out string reason)
        {
            reason = "";
            if (!_subMapIDByPlayer.TryGetValue(playerID, out int subMapID) || !_subMaps.TryGetValue(subMapID, out SubMap? subMap) || subMap.OrderedKeyFrames.Count == 0)
                return true;

            var rotation = new Quaternion(camera.CameraLocalPose.Rotation.X, camera.CameraLocalPose.Rotation.Y, camera.CameraLocalPose.Rotation.Z, camera.CameraLocalPose.Rotation.W);
            if (rotation.LengthSquared() < 1e-6f || float.IsNaN(rotation.X))
                return true; // not valid: the depth step will say so

            RigidTransform deviceToMap = _playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity);
            Vector3 position = deviceToMap.Apply(new Vector3(camera.CameraLocalPose.Position.X, camera.CameraLocalPose.Position.Y, camera.CameraLocalPose.Position.Z));
            rotation = deviceToMap.Apply(Quaternion.Normalize(rotation));

            // Compared with the player's own latest key frame (the sub-map's last one may be another player's).
            MapKeyFrame? last = null;
            for (int i = subMap.OrderedKeyFrames.Count - 1; i >= 0; i--)
            {
                if (subMap.OrderedKeyFrames[i].PlayerID == playerID)
                {
                    last = subMap.OrderedKeyFrames[i];
                    break;
                }
            }
            if (last == null)
                return true;

            if (Vector3.Distance(last.Position, position) < _parameters.MinTranslationMeters && AngleBetweenDegrees(last.Rotation, rotation) < _parameters.MinRotationDegrees)
            {
                reason = BarelyMovedReason;
                return false;
            }
            return true;
        }

        private const string BarelyMovedReason = "barely moved";

        private static float AngleBetweenDegrees(Quaternion a, Quaternion b)
        {
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        /// <summary>
        /// Measure the depth of the right image from the stereo pair. <paramref name="rightCamera"/> is the right camera as a key frame in the
        /// device's tracking space (the depth map is relative to the camera, so the frame does not matter for the depth itself).
        /// </summary>
        private DepthMap? EstimateDepth(KeyFrameImageDTO dto, StereoImages images, out MapKeyFrame? rightCamera, out string reason)
        {
            rightCamera = null;
            reason = "";

            CameraImageDTO right = dto.RightCamera!;
            var ci = right.CameraIntrinsics;
            if (!(ci.FocalLength.x > 0f) || !(ci.FocalLength.y > 0f))
            {
                reason = "invalid camera intrinsics";
                return null;
            }

            var rotation = new Quaternion(right.CameraLocalPose.Rotation.X, right.CameraLocalPose.Rotation.Y, right.CameraLocalPose.Rotation.Z, right.CameraLocalPose.Rotation.W);
            if (rotation.LengthSquared() < 1e-6f || float.IsNaN(rotation.X))
            {
                reason = "invalid camera rotation";
                return null;
            }

            // The image is a centred crop of the sensor; the sensor origin is at the bottom-left, the image origin top-left.
            int w = images.RightWidth, h = images.RightHeight;
            float offsetX = ci.SensorResolution.x > 0 ? (ci.SensorResolution.x - w) / 2f : 0f;
            float offsetY = ci.SensorResolution.y > 0 ? (ci.SensorResolution.y - h) / 2f : 0f;
            rightCamera = new MapKeyFrame
            {
                Position = new Vector3(right.CameraLocalPose.Position.X, right.CameraLocalPose.Position.Y, right.CameraLocalPose.Position.Z),
                Rotation = Quaternion.Normalize(rotation),
                Width = w,
                Height = h,
                Fx = ci.FocalLength.x,
                Fy = ci.FocalLength.y,
                Cx = ci.PrincipalPoint.x - offsetX,
                Cy = h - (ci.PrincipalPoint.y - offsetY),
                FlipVertically = _parameters.FlipImageVertically,
            };
            rightCamera.Prepare();

            if (!TryBuildLeftCamera(rightCamera, dto, images.LeftWidth, images.LeftHeight, out MapKeyFrame? leftCamera))
            {
                reason = "the stereo geometry is not valid";
                return null;
            }

            DepthMap? map = StereoDepthEstimator.Estimate(rightCamera, images.Right, leftCamera!, images.Left, _parameters, out string estimateReason, out float shift);
            if (map == null)
            {
                reason = $"no stereo depth ({estimateReason})";
                return null;
            }

            if (DebugMode)
                Console.WriteLine($"[Depth] Key frame {dto.ID}: depth at {map.ValidCount} of {map.GridWidth * map.GridHeight} points ({100f * map.ValidCount / (map.GridWidth * map.GridHeight):0}%); vertical offset between the images removed: {shift:0.00} px.");
            return map;
        }

        /// <summary>A key frame's points in the map frame (placed with the reported pose), and the correction found for them.</summary>
        private sealed record PreparedKeyFrame(CloudPoint[] Points, RigidTransform DeviceToMap, RigidTransform Correction, Vector3[] FreeRayEnds);

        /// <summary>The points of a key frame in the frame of the player's map: thinned to one per cube, with the brightness of the pixel each came from.</summary>
        private CloudPoint[] BuildPoints(DepthMap depth, MapKeyFrame depthCamera, byte[] rightGray, RigidTransform deviceToMap, int playerID)
        {
            var points = new List<CloudPoint>();
            var occupied = new HashSet<(int, int, int)>();
            float voxel = MathF.Max(0.005f, _parameters.KeyFrameVoxelMeters);
            foreach (var (position, px, py, _) in depth.Points(depthCamera))
            {
                Vector3 inMap = deviceToMap.Apply(position);
                if (!occupied.Add(((int)MathF.Floor(inMap.X / voxel), (int)MathF.Floor(inMap.Y / voxel), (int)MathF.Floor(inMap.Z / voxel))))
                    continue;
                int ix = Math.Clamp((int)MathF.Round(px), 0, depthCamera.Width - 1), iy = Math.Clamp((int)MathF.Round(py), 0, depthCamera.Height - 1);
                points.Add(new CloudPoint(inMap, rightGray[iy * depthCamera.Width + ix], playerID, (byte)Math.Clamp(_parameters.PointLives, 1, 255)));
            }
            if (points.Count > _parameters.MaxPointsPerKeyFrame)
            {
                int step = (int)MathF.Ceiling(points.Count / (float)_parameters.MaxPointsPerKeyFrame);
                points = points.Where((_, index) => index % step == 0).ToList();
            }
            return points.ToArray();
        }

        /// <summary>
        /// The measured points a key frame's rays end at, in the frame of the player's map: one per grid cell with a depth, except those next to a
        /// grid cell without one (a cell the matcher gave up on says nothing about what lies in front of it, and its neighbours' depths may be off).
        /// </summary>
        private Vector3[] BuildFreeRayEnds(DepthMap depth, MapKeyFrame depthCamera, RigidTransform deviceToMap)
        {
            int w = depth.GridWidth, h = depth.GridHeight;
            bool hasStatus = depth.Status.Length == depth.Range.Length;
            bool Rejected(int gx, int gy)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h)
                    return false;
                int i = gy * w + gx;
                return depth.Range[i] <= 0f && !(hasStatus && depth.Status[i] == DepthStatus.Outside);
            }

            var ends = new List<Vector3>();
            for (int gy = 0; gy < h; gy++)
                for (int gx = 0; gx < w; gx++)
                {
                    float range = depth.Range[gy * w + gx];
                    if (range <= 0f || Rejected(gx - 1, gy) || Rejected(gx + 1, gy) || Rejected(gx, gy - 1) || Rejected(gx, gy + 1))
                        continue;
                    Vector3 ray = depthCamera.PixelToWorldRay(depth.PixelX(gx), depth.PixelY(gy));
                    ends.Add(deviceToMap.Apply(depthCamera.Position + ray * range));
                }
            return ends.ToArray();
        }

        private static bool SameTransform(RigidTransform a, RigidTransform b) =>
            a.Translation == b.Translation && a.Rotation == b.Rotation;

        // Reported pose and time of each player's previous key frame (for the head speed in the pose correction log).
        private readonly Dictionary<int, (Vector3 Position, Quaternion Rotation, DateTime Time)> _lastReported = new();

        // Size of the corrections, by how fast the head was moving (for the summary in the log).
        private readonly List<(bool Fast, float Centimeters, float Degrees)> _correctionStats = new();

        /// <summary>
        /// Place a key frame's points in the map and line them up with the points already there. The headset's pose is only the starting point: ICP
        /// (yaw + translation) against the points of nearby key frames finds how far the reported pose is off. The correction is used if the fit
        /// got better, is good and the correction is small; otherwise the reported pose stays. What was found is written to the log together with
        /// the head's speed, to tell tracking error from the effects of movement.
        /// </summary>
        private PreparedKeyFrame PrepareKeyFrame(int playerID, KeyFrameImageDTO dto, DepthMap depth, MapKeyFrame depthCamera, byte[] rightGray)
        {
            CameraImageDTO right = dto.RightCamera!;
            RigidTransform deviceToMap;
            var reference = new List<CloudPoint[]>();
            Vector3 cameraInMap;
            double linearSpeed = double.NaN, angularSpeed = double.NaN;
            lock (_mapLock)
            {
                deviceToMap = _playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity);
                cameraInMap = deviceToMap.Apply(depthCamera.Position);

                if (_lastReported.TryGetValue(playerID, out var last))
                {
                    double seconds = (right.Timestamp - last.Time).TotalSeconds;
                    if (seconds > 0.001 && seconds < 5)
                    {
                        linearSpeed = Vector3.Distance(last.Position, depthCamera.Position) / seconds;
                        angularSpeed = AngleBetweenDegrees(last.Rotation, depthCamera.Rotation) / seconds;
                    }
                }
                _lastReported[playerID] = (depthCamera.Position, depthCamera.Rotation, right.Timestamp);

                if (_parameters.PoseCorrectionEnabled && _subMapIDByPlayer.TryGetValue(playerID, out int subMapID) && _subMaps.TryGetValue(subMapID, out SubMap? subMap))
                {
                    for (int i = subMap.OrderedKeyFrames.Count - 1; i >= 0 && reference.Count < _parameters.PoseCorrectionMaxReferenceKeyFrames; i--)
                    {
                        MapKeyFrame other = subMap.OrderedKeyFrames[i];
                        if (Vector3.Distance(other.Position, cameraInMap) <= _parameters.PoseCorrectionReferenceRadiusMeters)
                            reference.Add(other.Points); // the arrays are replaced, never changed, so they can be read after the lock is released
                    }
                }
            }

            CloudPoint[] points = BuildPoints(depth, depthCamera, rightGray, deviceToMap, playerID);
            Vector3[] rayEnds = BuildFreeRayEnds(depth, depthCamera, deviceToMap);
            RigidTransform correction = RigidTransform.Identity;
            string speedText = double.IsNaN(linearSpeed) ? "head speed unknown" : $"head speed {linearSpeed:0.00} m/s, {angularSpeed:0} deg/s";
            if (!_parameters.PoseCorrectionEnabled || reference.Count == 0)
                return new PreparedKeyFrame(points, deviceToMap, correction, rayEnds);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            FusedCloud map = FusedCloud.FromPoints(reference.SelectMany(r => r), 0.04f);
            if (map.Count < _parameters.PoseCorrectionMinReferencePoints)
            {
                Console.WriteLine($"[PoseFix] player {playerID} key frame {dto.ID}: not corrected, only {map.Count} map points around ({reference.Count} key frames); {speedText}.");
                return new PreparedKeyFrame(points, deviceToMap, correction, rayEnds);
            }

            FusedCloud frame = FusedCloud.FromPoints(points, 0.04f);
            CloudAlignment.PoseCorrectionResult? found = CloudAlignment.EstimatePoseCorrection(map, frame, _parameters, out string summary);
            if (found == null)
            {
                Console.WriteLine($"[PoseFix] player {playerID} key frame {dto.ID}: not corrected ({summary}); {speedText}.");
                return new PreparedKeyFrame(points, deviceToMap, correction, rayEnds);
            }

            RigidTransform estimate = found.Transform;
            float shift = estimate.Translation.Length();
            float yaw = MathF.Abs(MathF.IEEERemainder(estimate.YawDegrees, 360f));
            // The shift of the points themselves (the transform also turns about the origin, so its translation alone says little).
            Vector3 centre = frame.Centroid;
            Vector3 moved = estimate.Apply(centre) - centre;
            string fit = $"match {found.BeforeFraction:P0} -> {found.AfterFraction:P0}, mean distance {found.BeforeMeanDistance * 100:0.0} -> {found.AfterMeanDistance * 100:0.0} cm";
            string why = "";
            if (moved.Length() > _parameters.PoseCorrectionMaxShiftMeters || yaw > _parameters.PoseCorrectionMaxYawDegrees)
                why = "the correction is larger than allowed";
            else if (found.Matched < _parameters.PoseCorrectionMinMatchedPoints || found.AfterFraction < _parameters.PoseCorrectionMinMatchedFraction)
                why = "the fit is not good enough";
            else if (found.AfterMeanDistance > found.BeforeMeanDistance * 0.98f)
                why = "it does not fit better than the reported pose";

            Console.WriteLine($"[PoseFix] player {playerID} key frame {dto.ID}: " + (why.Length == 0 ? "corrected by " : $"NOT used ({why}), would move ") +
                              $"{moved.Length() * 100:0.0} cm ({moved.X * 100:0.0}, {moved.Y * 100:0.0}, {moved.Z * 100:0.0}) and turn {yaw:0.0} deg; {fit}; {speedText}; {reference.Count} reference key frames, {map.Count} points; {timer.ElapsedMilliseconds} ms.");

            if (!double.IsNaN(linearSpeed))
            {
                bool fast = linearSpeed > 0.15 || angularSpeed > 15;
                lock (_mapLock)
                {
                    _correctionStats.Add((fast, moved.Length() * 100f, yaw));
                    if (_correctionStats.Count % 25 == 0)
                        LogCorrectionStats();
                }
            }

            if (why.Length == 0)
                correction = estimate;
            _ = shift;
            return new PreparedKeyFrame(points, deviceToMap, correction, rayEnds);
        }

        /// <summary>Summary of the corrections so far: how large they are when the head is slow and when it is fast. Must be called with the map lock held.</summary>
        private void LogCorrectionStats()
        {
            static string Describe(List<(bool Fast, float Centimeters, float Degrees)> list)
            {
                if (list.Count == 0)
                    return "none";
                float[] cm = list.Select(e => e.Centimeters).OrderBy(v => v).ToArray();
                float[] deg = list.Select(e => e.Degrees).OrderBy(v => v).ToArray();
                return $"{list.Count} key frames, median {cm[cm.Length / 2]:0.0} cm / {deg[deg.Length / 2]:0.0} deg, 90th percentile {cm[(int)(0.9 * (cm.Length - 1))]:0.0} cm";
            }
            Console.WriteLine($"[PoseFix] summary of {_correctionStats.Count} corrections: slow head ({Describe(_correctionStats.Where(e => !e.Fast).ToList())}); fast head ({Describe(_correctionStats.Where(e => e.Fast).ToList())}).");
        }

        /// <summary>
        /// Put a key frame (with its depth) into the player's sub-map, creating the sub-map for a new player. Must be called with the map lock
        /// held. <paramref name="work"/> is the merge or refinement that should run now, if any.
        /// </summary>
        private KeyFrameInsertResult InsertKeyFrame(int playerID, KeyFrameImageDTO dto, DepthMap depth, MapKeyFrame depthCamera, byte[] rightGray, PreparedKeyFrame prepared, out Action? work)
        {
            work = null;
            CameraImageDTO right = dto.RightCamera!;

            if (!_subMapIDByPlayer.TryGetValue(playerID, out int subMapID))
            {
                subMapID = _nextSubMapID++;
                _subMapIDByPlayer[playerID] = subMapID;
                var created = new SubMap { ID = subMapID };
                created.PlayerIDs.Add(playerID);
                _subMaps[subMapID] = created;
                _playerToMap[playerID] = RigidTransform.Identity;
            }
            SubMap subMap = _subMaps[subMapID];

            // The pose and the points are in the device's own tracking space; move them into the frame of the map the player belongs to
            // (identity until that player's map has been merged with another player's). If a merge changed that since the points were prepared,
            // they are placed again (without the correction, which was found in the old frame).
            RigidTransform deviceToMap = _playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity);
            CloudPoint[] preparedPoints = prepared.Points;
            Vector3[] preparedRayEnds = prepared.FreeRayEnds;
            RigidTransform correction = prepared.Correction;
            if (!SameTransform(prepared.DeviceToMap, deviceToMap))
            {
                preparedPoints = BuildPoints(depth, depthCamera, rightGray, deviceToMap, playerID);
                preparedRayEnds = BuildFreeRayEnds(depth, depthCamera, deviceToMap);
                correction = RigidTransform.Identity;
            }

            bool corrected = !SameTransform(correction, RigidTransform.Identity);
            var points = new List<CloudPoint>(preparedPoints.Length);
            foreach (CloudPoint point in preparedPoints)
                points.Add(corrected ? point with { Position = correction.Apply(point.Position) } : point);

            var keyFrame = new MapKeyFrame
            {
                ID = _nextKeyFrameID++,
                SubMapID = subMap.ID,
                PlayerID = playerID,
                ClientKeyFrameID = dto.ID,
                Timestamp = right.Timestamp,
                Sequence = subMap.NextSequence++,
                Position = correction.Apply(deviceToMap.Apply(depthCamera.Position)),
                Rotation = correction.Apply(deviceToMap.Apply(depthCamera.Rotation)),
                Width = depthCamera.Width,
                Height = depthCamera.Height,
                Fx = depthCamera.Fx,
                Fy = depthCamera.Fy,
                Cx = depthCamera.Cx,
                Cy = depthCamera.Cy,
                FlipVertically = depthCamera.FlipVertically,
                Points = points.ToArray(),
                FreeRayEnds = corrected ? preparedRayEnds.Select(correction.Apply).ToArray() : preparedRayEnds,
            };
            keyFrame.Prepare();
            subMap.KeyFrames[keyFrame.ID] = keyFrame;
            subMap.OrderedKeyFrames.Add(keyFrame);

            // Points this key frame saw through lose a life (and go at none); the sub-map's cloud is built again
            // from what is left because the version changes below.
            PointFreshness.Result freshness = PointFreshness.Apply(subMap.OrderedKeyFrames, keyFrame, _parameters);
            if (DebugMode && (freshness.Struck > 0 || freshness.Refreshed > 0))
                Console.WriteLine($"[Fresh] map {subMap.ID} key frame {dto.ID}: {freshness.Struck} points lost a life, {freshness.Removed} removed, {freshness.Refreshed} seen again and restored.");
            subMap.Version++;
            _mapVersion++;

            // Decide what to do in the background: look for a map to merge with, or refine this player's transform.
            _keyFramesSinceMergeAttempt[subMap.ID] = _keyFramesSinceMergeAttempt.GetValueOrDefault(subMap.ID) + 1;
            _keyFramesSinceAnchorCheck[subMap.ID] = _keyFramesSinceAnchorCheck.GetValueOrDefault(subMap.ID) + 1;
            if (_refinablePlayers.Contains(playerID))
                _keyFramesSinceRefinement[playerID] = _keyFramesSinceRefinement.GetValueOrDefault(playerID) + 1;

            int mergeSubMapID = subMap.ID;
            if (_subMaps.Count >= 2 && subMap.OrderedKeyFrames.Count >= _parameters.MergeMinKeyFrames
                && _keyFramesSinceMergeAttempt[subMap.ID] >= _parameters.MergeAttemptEveryKeyFrames)
            {
                work = () => TryMergeSubMaps(mergeSubMapID);
            }
            else if (_refinablePlayers.Contains(playerID) && _keyFramesSinceRefinement[playerID] >= _parameters.RefinementIntervalKeyFrames)
            {
                work = () => RefinePlayerTransform(playerID);
            }
            else if (_parameters.AnchorsEnabled && _keyFramesSinceAnchorCheck[subMap.ID] >= Math.Max(1, _parameters.AnchorCheckEveryKeyFrames))
            {
                work = () => CreateAnchors(mergeSubMapID);
            }

            return new KeyFrameInsertResult(true, "", keyFrame.Points.Length);
        }

        /// <summary>For the 3D view: the boxes of the map that was last compared with <see cref="Target"/>, at the best alignment of that attempt.</summary>
        private sealed record MergeOverlay(int TargetSubMapID, MapSnapshotAnchor[] Anchors, string Note);

        private MergeOverlay? _overlay;

        private int _workerBusy;

        /// <summary>Run a merge or refinement: right here for tests, otherwise on a background thread if none is running (it is tried again with the next key frame).</summary>
        private void RunWork(Action work)
        {
            if (RunMergeSynchronously)
            {
                work();
                return;
            }

            if (Interlocked.CompareExchange(ref _workerBusy, 1, 0) != 0)
                return;

            Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[GlobalMap] Merge/refinement failed: {ex}");
                }

                try { RefreshPointClouds(false); } // the viewer shows new anchors, alignments and merges as soon as the work is done
                catch (Exception ex) { Console.Error.WriteLine($"[GlobalMap] Viewer refresh failed: {ex.Message}"); }
                finally
                {
                    Volatile.Write(ref _workerBusy, 0);
                }
            });
        }

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

        /// <summary>
        /// A player left the session. The player stops being part of the map (no pose, no position, no refinement, and the global map
        /// no longer waits for them), but their sub-map stays exactly as it is, with its key frames and points. Other players can still
        /// merge with it, and so can the same player if they come back: a returning player starts a new sub-map (their tracking space
        /// may have been reset) that merges with the kept one like any other. Nothing else in the map is touched.
        /// </summary>
        /// <returns>True if the player had a sub-map.</returns>
        public bool RemovePlayer(int playerID)
        {
            lock (_mapLock)
            {
                bool hadSubMap = false;
                if (_subMapIDByPlayer.Remove(playerID, out int subMapID) && _subMaps.TryGetValue(subMapID, out SubMap? subMap))
                {
                    subMap.PlayerIDs.Remove(playerID);
                    hadSubMap = true;
                    Console.WriteLine($"[GlobalMap] Player {playerID} left; map {subMapID} is kept ({subMap.OrderedKeyFrames.Count} key frames, " +
                                      $"{subMap.OrderedKeyFrames.Sum(k => k.Points.Length)} points, {subMap.PlayerIDs.Count} player(s) still in it).");
                }

                _playerToMap.Remove(playerID);
                _lastKnownPositionInMap.Remove(playerID);
                _refinablePlayers.Remove(playerID);
                _keyFramesSinceRefinement.Remove(playerID);
                return hadSubMap;
            }
        }

        /// <summary>Read-only access to a player's sub-map (for tests). Hold no references across calls.</summary>
        public SubMap? GetSubMapOfPlayer(int playerID)
        {
            lock (_mapLock)
                return _subMapIDByPlayer.TryGetValue(playerID, out int subMapID) ? _subMaps[subMapID] : null;
        }

        #endregion Methods

        #region Player Positions

        /// <summary>
        /// Remember where the player's device was at its latest key frame, in the frame of the player's sub-map. Used to place the
        /// origin of the global frame. Needs the player to have a sub-map (otherwise there is no frame to express the position in).
        /// Must be called with the map lock held.
        /// </summary>
        private void RecordLastKnownPosition(int playerID, SharedPose localPose)
        {
            if (!_subMapIDByPlayer.ContainsKey(playerID) || !_playerToMap.TryGetValue(playerID, out RigidTransform deviceToMap))
                return;

            var position = deviceToMap.Apply(new Vector3(localPose.Position.X, localPose.Position.Y, localPose.Position.Z));
            if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z))
                return;

            _lastKnownPositionInMap[playerID] = position;
        }

        /// <summary>
        /// The last known position of a player, in the frame of the sub-map the player is in right now (this changes when the
        /// sub-map is merged into another one; the position is moved along with it). False if the player has no sub-map yet.
        /// </summary>
        public bool TryGetLastKnownPosition(int playerID, out Vector3 position, out int subMapID)
        {
            lock (_mapLock)
            {
                position = default;
                return _subMapIDByPlayer.TryGetValue(playerID, out subMapID)
                    && _lastKnownPositionInMap.TryGetValue(playerID, out position);
            }
        }

        #endregion Player Positions

        #region Global Map State

        /// <summary>
        /// Checks whether the global map has just been formed, then returns the given local camera pose of a player in the global
        /// frame (null if the global map is not formed or the player's map is not part of it yet).
        /// </summary>
        private SharedPose? ResolveGlobalPose(int playerID, SharedPose localPose)
        {
            lock (_mapLock)
            {
                UpdateGlobalMapState();

                if (!IsGlobalMapFormed
                    || !_subMapIDByPlayer.TryGetValue(playerID, out int subMapID)
                    || subMapID != _globalSubMapID
                    || !_playerToMap.TryGetValue(playerID, out RigidTransform deviceToMap))
                {
                    return null;
                }

                // The pose the headset reported is in its own tracking space; the player's device-to-map transform moves it into the
                // global sub-map's frame, and the global origin then moves that frame's (0, 0, 0) to where the global frame has it.
                Vector3 position = deviceToMap.Apply(new Vector3(localPose.Position.X, localPose.Position.Y, localPose.Position.Z)) - _globalOrigin;
                Quaternion rotation = deviceToMap.Apply(new Quaternion(localPose.Rotation.X, localPose.Rotation.Y, localPose.Rotation.Z, localPose.Rotation.W));

                return new SharedPose(
                    new SharedLibrary.Game.Vector3(position.X, position.Y, position.Z),
                    new SharedLibrary.Game.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
            }
        }

        /// <summary>
        /// The global map is formed the first time every expected player has sent key frames and all of them are in the same sub-map.
        /// Once formed it stays formed. Must be called with the map lock held.
        /// </summary>
        private void UpdateGlobalMapState()
        {
            if (IsGlobalMapFormed)
                return;

            IReadOnlyCollection<int> expected = ExpectedPlayerIDs?.Invoke() ?? _subMapIDByPlayer.Keys.ToArray();

            int subMapID = -1;
            foreach (int playerID in expected)
            {
                // A player who has not sent a key frame yet has no map, so the global map cannot be complete.
                if (!_subMapIDByPlayer.TryGetValue(playerID, out int playerSubMapID))
                    return;
                if (subMapID == -1)
                    subMapID = playerSubMapID;
                else if (subMapID != playerSubMapID)
                    return;
            }

            // The global frame is the global sub-map's frame with its (0, 0, 0) moved to the average of where the players are right now.
            Vector3 sum = Vector3.Zero;
            int counted = 0;
            foreach (int playerID in expected)
            {
                if (_lastKnownPositionInMap.TryGetValue(playerID, out Vector3 playerPosition))
                {
                    sum += playerPosition;
                    counted++;
                }
            }
            _globalOrigin = counted > 0 ? sum / counted : Vector3.Zero;

            _globalSubMapID = subMapID;
            IsGlobalMapFormed = true;
            Console.WriteLine($"[GlobalMap] Global map formed: all {expected.Count} players (" + string.Join(", ", expected.Order()) + $") share map {subMapID}. " +
                              $"Global origin is the average position of {counted} players, <{_globalOrigin.X:0.00}, {_globalOrigin.Y:0.00}, {_globalOrigin.Z:0.00}> in that map's frame.");
        }

        #endregion Global Map State

        #region Merging sub-maps

        private readonly Dictionary<int, int> _keyFramesSinceMergeAttempt = new();
        private readonly Dictionary<int, int> _keyFramesSinceAnchorCheck = new();
        private int _nextAnchorID = 1;

        /// <summary>The last transform found between two sub-maps, waiting for a second estimate to agree with it.</summary>
        private readonly record struct PendingMerge(RigidTransform HigherToLower, DateTime Time);

        // (lower sub-map ID, higher sub-map ID) -> the latest unconfirmed transform from the higher-numbered map's frame into the lower one's.
        private readonly Dictionary<(int, int), PendingMerge> _pendingMerges = new();

        // Players whose device-to-map transform came out of a merge (so it is an estimate that can be refined), and how many of their
        // key frames arrived since it was last refined.
        private readonly HashSet<int> _refinablePlayers = new();
        private readonly Dictionary<int, int> _keyFramesSinceRefinement = new();

        /// <summary>The fused point cloud of a sub-map, built (outside the lock) if it is out of date. Null if the sub-map is gone.</summary>
        private FusedCloud? GetFusedCloud(int subMapID, out long frameEpoch, out int keyFrameCount)
        {
            frameEpoch = 0;
            keyFrameCount = 0;
            List<MapKeyFrame> frames;
            long version;
            lock (_mapLock)
            {
                if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap))
                    return null;
                frameEpoch = subMap.FrameEpoch;
                keyFrameCount = subMap.OrderedKeyFrames.Count;
                if (subMap.Fused != null && subMap.FusedVersion == subMap.Version)
                    return subMap.Fused;
                frames = subMap.OrderedKeyFrames.ToList();
                version = subMap.Version;
            }

            // A large map is fused with bigger cubes so the comparison stays fast (the cost grows with the number of points).
            float voxel = _parameters.FusedVoxelMeters;
            FusedCloud cloud = FusedCloud.Build(frames, voxel, _parameters.MergeMinKeyFramesPerPoint);
            for (int attempt = 0; attempt < 3 && cloud.Count > _parameters.MergeMaxPoints; attempt++)
            {
                voxel *= MathF.Max(1.25f, MathF.Sqrt(cloud.Count / (float)_parameters.MergeMaxPoints));
                cloud = FusedCloud.Build(frames, voxel, _parameters.MergeMinKeyFramesPerPoint);
            }
            lock (_mapLock)
            {
                if (_subMaps.TryGetValue(subMapID, out SubMap? subMap) && subMap.Version == version)
                {
                    subMap.Fused = cloud;
                    subMap.FusedVersion = version;
                }
            }
            return cloud;
        }

        /// <summary>
        /// A few snapshots of a sub-map: the depth points of single key frames (each with the one or two before it from the same player), as they sit
        /// in the map with the corrected pose. The latest key frame is always one (it is what is new); the others are those with the most points that
        /// are not at the same place and looking the same way as one already picked, so the snapshots show different parts of the map.
        /// </summary>
        private List<FusedCloud> BuildSnapshots(int subMapID)
        {
            var groups = new List<CloudPoint[]>();
            lock (_mapLock)
            {
                if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap))
                    return new List<FusedCloud>();

                List<MapKeyFrame> frames = subMap.OrderedKeyFrames;
                int perSnapshot = Math.Max(1, _parameters.MergeSnapshotKeyFrames);
                var picked = new List<int>();
                bool Different(int a, int b)
                {
                    Vector3 forwardA = Vector3.Transform(Vector3.UnitZ, frames[a].Rotation), forwardB = Vector3.Transform(Vector3.UnitZ, frames[b].Rotation);
                    float turn = MathF.Acos(Math.Clamp(Vector3.Dot(forwardA, forwardB), -1f, 1f)) * 180f / MathF.PI;
                    return Vector3.Distance(frames[a].Position, frames[b].Position) >= _parameters.MergeSnapshotSeparationMeters || turn >= _parameters.MergeSnapshotSeparationDegrees;
                }

                // The points of the picked key frame and of the ones just before it from the same player.
                CloudPoint[] Combine(int index)
                {
                    var points = new List<CloudPoint>();
                    int taken = 0;
                    for (int i = index; i >= 0 && taken < perSnapshot; i--)
                    {
                        if (frames[i].PlayerID != frames[index].PlayerID)
                            continue;
                        points.AddRange(frames[i].Points);
                        taken++;
                    }
                    return points.ToArray();
                }

                for (int i = frames.Count - 1; i >= 0 && picked.Count == 0; i--)
                    if (frames[i].Points.Length >= _parameters.MergeSnapshotMinPoints)
                        picked.Add(i);
                foreach (int i in Enumerable.Range(0, frames.Count).OrderByDescending(i => frames[i].Points.Length))
                {
                    if (picked.Count >= Math.Max(1, _parameters.MergeSnapshotCount))
                        break;
                    if (frames[i].Points.Length < _parameters.MergeSnapshotMinPoints || picked.Contains(i) || picked.Any(j => !Different(i, j)))
                        continue;
                    picked.Add(i);
                }
                foreach (int i in picked)
                    groups.Add(Combine(i));
            }

            // Built away from the lock (the point arrays are replaced, never changed, so these stay as they were).
            return groups.Select(points => FusedCloud.FromPoints(points, _parameters.FusedVoxelMeters)).ToList();
        }

        /// <summary>The anchors of a sub-map (a copy of the list; the anchors themselves never change).</summary>
        private List<MapAnchor> GetAnchors(int subMapID)
        {
            lock (_mapLock)
                return _subMaps.TryGetValue(subMapID, out SubMap? subMap) ? subMap.Anchors.ToList() : new List<MapAnchor>();
        }

        /// <summary>
        /// Make shared anchors in a sub-map: at the newest key frames that are far enough from every anchor already there, with a frozen copy of the
        /// voxels around them. Does nothing when there is no room for a new anchor.
        /// </summary>
        private void CreateAnchors(int subMapID)
        {
            if (!_parameters.AnchorsEnabled)
                return;
            List<MapAnchor> existing;
            long epoch;
            lock (_mapLock)
            {
                if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap))
                    return;
                _keyFramesSinceAnchorCheck[subMapID] = 0;
                existing = subMap.Anchors.ToList();
                epoch = subMap.FrameEpoch;
            }
            if (existing.Count >= _parameters.AnchorMaxPerMap)
                return;

            FusedCloud? cloud = GetFusedCloud(subMapID, out long cloudEpoch, out _);
            if (cloud == null || cloud.Count < _parameters.AnchorMinPoints || cloudEpoch != epoch)
            {
                if (DebugMode)
                    Console.WriteLine($"[Anchor] map {subMapID}: no anchor made yet ({cloud?.Count ?? 0} fused points, need {_parameters.AnchorMinPoints}).");
                return;
            }

            List<AnchorSite> sites = AnchorBuilder.PlanSites(cloud, existing, _parameters, out string siteNote);
            if (sites.Count == 0)
            {
                if (DebugMode)
                    Console.WriteLine($"[Anchor] map {subMapID}: no distinctive enough place for an anchor yet ({siteNote}).");
                return;
            }

            var created = new List<MapAnchor>();
            var uniqueness = new Dictionary<int, float>();
            foreach (AnchorSite site in sites)
            {
                int id;
                lock (_mapLock)
                    id = _nextAnchorID++;
                MapAnchor? anchor = AnchorBuilder.Create(id, cloud, site, subMapID, _parameters);
                if (anchor != null)
                {
                    created.Add(anchor);
                    uniqueness[anchor.ID] = site.Uniqueness;
                }
            }
            if (created.Count == 0)
            {
                if (DebugMode)
                    Console.WriteLine($"[Anchor] map {subMapID}: {sites.Count} distinctive place(s), but fewer than {_parameters.AnchorMinPoints} voxels within {_parameters.AnchorRadiusMeters:0.0} m of them.");
                return;
            }

            lock (_mapLock)
            {
                if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap) || subMap.FrameEpoch != epoch)
                    return; // the map was moved by a merge while the anchors were made; they are made again next time
                subMap.Anchors.AddRange(created);
                _mapVersion++;
            }
            foreach (MapAnchor anchor in created)
                Console.WriteLine($"[Anchor] map {subMapID}: anchor {anchor.ID} made at ({anchor.Position.X:0.00}, {anchor.Position.Z:0.00}), uniqueness {uniqueness[anchor.ID]:0.00}, {anchor.Cloud.Count} voxels, mostly seen by player {anchor.OwnerPlayerID} ({existing.Count + created.Count} anchors in this map; {siteNote}).");
        }

        /// <summary>
        /// Where each device's tracking space would be in the frame of map <paramref name="mineID"/> if the other map were placed by
        /// <paramref name="otherToMine"/> (yaw and shift of the device-to-map transform), and how the devices' tracking spaces differ from the first one's.
        /// </summary>
        private string DescribeDevicePoses(int mineID, int otherID, RigidTransform otherToMine)
        {
            var devices = new List<(int PlayerID, RigidTransform ToMine)>();
            lock (_mapLock)
            {
                foreach (var (id, ownMap) in new[] { (mineID, false), (otherID, true) })
                {
                    if (!_subMaps.TryGetValue(id, out SubMap? subMap))
                        continue;
                    foreach (int playerID in subMap.PlayerIDs.Order())
                    {
                        RigidTransform toMap = _playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity);
                        devices.Add((playerID, ownMap ? otherToMine.Compose(toMap) : toMap));
                    }
                }
            }
            if (devices.Count == 0)
                return "no devices";

            static string Describe(RigidTransform t) => $"yaw {MathF.IEEERemainder(t.YawDegrees, 360f):0.0} deg, shift ({t.Translation.X:0.00}, {t.Translation.Y:0.00}, {t.Translation.Z:0.00}) m";
            var parts = devices.Select(d => $"device {d.PlayerID}: {Describe(d.ToMine)}").ToList();
            // How device k's tracking space sits in the first device's: the pose difference the headsets would have to be aligned by.
            RigidTransform first = devices[0].ToMine;
            for (int i = 1; i < devices.Count; i++)
            {
                RigidTransform relative = first.Inverse().Compose(devices[i].ToMine);
                parts.Add($"device {devices[i].PlayerID} vs device {devices[0].PlayerID}: {Describe(relative)} ({relative.Translation.Length():0.00} m apart)");
            }
            return "device poses in map " + mineID + "'s frame (this alignment): " + string.Join("; ", parts);
        }

        /// <summary>
        /// Look for another sub-map that this one overlaps: the two point clouds are aligned (coarse search over yaw and position, then ICP) and the
        /// result is checked. An alignment that holds up is kept as a pending estimate; a later attempt that agrees with it confirms it, and the two
        /// sub-maps are merged. Merges at most one sub-map per call.
        /// </summary>
        private void TryMergeSubMaps(int subMapID)
        {
            CreateAnchors(subMapID);
            FusedCloud? mine;
            long myEpoch;
            List<int> others;
            lock (_mapLock)
            {
                if (!_subMaps.ContainsKey(subMapID))
                    return;
                _keyFramesSinceMergeAttempt[subMapID] = 0;
                others = _subMaps.Values.Where(s => s.ID != subMapID && s.OrderedKeyFrames.Count >= _parameters.MergeMinKeyFrames).Select(s => s.ID).OrderBy(id => id).ToList();
            }
            if (others.Count == 0)
                return;

            mine = GetFusedCloud(subMapID, out myEpoch, out _);
            string tag = $"[Merge] map {subMapID}";
            if (mine == null || mine.Count < _parameters.MergeMinPoints)
            {
                Console.WriteLine($"{tag}: only {mine?.Count ?? 0} fused points so far (need {_parameters.MergeMinPoints}).");
                return;
            }

            List<FusedCloud> snapshots = BuildSnapshots(subMapID);

            foreach (int otherID in others)
            {
                FusedCloud? theirs = GetFusedCloud(otherID, out long theirEpoch, out _);
                if (theirs == null)
                    continue;
                if (theirs.Count < _parameters.MergeMinPoints)
                {
                    Console.WriteLine($"{tag} vs map {otherID}: the other map has only {theirs.Count} fused points (need {_parameters.MergeMinPoints}).");
                    continue;
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // The anchors of the two maps looked for in each other's points: the placements several of them agree on are tried and can be trusted.
                List<AnchorCandidate>? anchorCandidates = null;
                var anchorPlacements = new List<AnchorPlacement>();
                List<MapAnchor> theirAnchorsForOverlay = _parameters.AnchorsEnabled ? GetAnchors(otherID) : new List<MapAnchor>();
                if (_parameters.AnchorsEnabled)
                {
                    List<MapAnchor> myAnchors = GetAnchors(subMapID), theirAnchors = theirAnchorsForOverlay;
                    if (myAnchors.Count + theirAnchors.Count > 0)
                    {
                        anchorCandidates = AnchorLocalizer.Locate(mine, myAnchors, theirs, theirAnchors, _parameters, out string anchorLog, out anchorPlacements);
                        Console.WriteLine($"[Anchors] map {subMapID} vs map {otherID}: {anchorLog.Replace("\n", "\n[Anchors] ")}");
                    }
                }

                AlignmentResult? alignment = CloudAlignment.Align(mine, theirs, _parameters, out string log, snapshots, anchorCandidates);
                Console.WriteLine($"{tag} vs map {otherID}: {mine.Count} / {theirs.Count} points; {log.Replace("\n", "\n" + tag + " ")} [{stopwatch.ElapsedMilliseconds} ms]");
                if (alignment == null)
                    continue;
                Console.WriteLine($"{tag} vs map {otherID}: {DescribeDevicePoses(subMapID, otherID, alignment.Transform)}");

                // With anchors required, a placement that only the whole-map check likes is not enough.
                bool passed = alignment.Passed && (!_parameters.AnchorsRequireForMerge || alignment.AnchorTrusted);

                if (DebugMode)
                {
                    // Show what this attempt found: the other map's boxes where the best alignment puts them, in this map's frame.
                    // Dashed over this map: where each anchor of the other map was found (green if it agrees with the best placement the anchors
                    // agree on, red if not), and the other map's anchors where the best whole-map alignment puts them (magenta).
                    var overlayList = new List<MapSnapshotAnchor>();
                    foreach (AnchorPlacement placement in anchorPlacements.Where(x => x.OfOtherMap))
                    {
                        bool agrees = anchorCandidates != null && anchorCandidates.Count > 0 && CloudAlignment.PlacementsAgree(placement.TheirsToMine, anchorCandidates[0].Transform, theirs.Centroid, _parameters);
                        overlayList.Add(ToSnapshotAnchor(placement.Anchor, q => q, placement.Found, agrees ? 2 : 3));
                    }
                    overlayList.AddRange(theirAnchorsForOverlay.Select(anchor => ToSnapshotAnchor(anchor, alignment.Transform.Apply, null, 1)));
                    MapSnapshotAnchor[] overlayAnchors = overlayList.ToArray();
                    lock (_mapLock)
                        _overlay = new MergeOverlay(subMapID, overlayAnchors, $"map {otherID} at the best alignment: {alignment.Score}{(passed ? "" : " - not good enough")}");
                    RefreshPointClouds(true);
                }

                if (!passed)
                {
                    Console.WriteLine($"{tag} vs map {otherID}: best alignment is not good enough ({alignment.Summary}){(alignment.Passed ? " - it passes the check but the anchors do not agree on it" : "")}; need {_parameters.MinMergeMatchedPoints} points, " +
                                      $"{_parameters.MinMergeInlierFraction:P0} agreeing and a brightness correlation of {_parameters.MinMergeGrayAgreement:0.00}.");
                    lock (_mapLock)
                        _pendingMerges.Remove(PairKey(subMapID, otherID));
                    continue;
                }

                lock (_mapLock)
                {
                    if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap) || !_subMaps.TryGetValue(otherID, out SubMap? other)
                        || subMap.FrameEpoch != myEpoch || other.FrameEpoch != theirEpoch)
                    {
                        Console.WriteLine($"{tag} vs map {otherID}: the maps changed while they were compared; the result is dropped.");
                        continue;
                    }

                    // The transform from the other map's frame into this one's; stored from the higher-numbered map into the lower-numbered one.
                    RigidTransform otherToThis = alignment.Transform;
                    RigidTransform higherToLower = otherID > subMapID ? otherToThis : otherToThis.Inverse();
                    var key = PairKey(subMapID, otherID);
                    Vector3 reference = (otherID > subMapID ? mine : theirs).Centroid; // a point of the lower-numbered map

                    if (!_pendingMerges.TryGetValue(key, out PendingMerge pending)
                        || (DateTime.UtcNow - pending.Time).TotalSeconds > _parameters.MergeConfirmationWindowSeconds
                        || !TransformsAgree(pending.HigherToLower, higherToLower, reference))
                    {
                        _pendingMerges[key] = new PendingMerge(higherToLower, DateTime.UtcNow);
                        Console.WriteLine($"{tag} vs map {otherID}: good alignment ({alignment.Summary}); waiting for a second estimate to confirm it.");
                        continue;
                    }

                    // Confirmed. The sub-map with the lower ID (the older one) survives, so a merged map keeps the older player's frame. Once the
                    // global map has formed its frame is fixed, so the global sub-map always survives, even when a later joiner's map is older.
                    bool otherSurvives = other.ID == _globalSubMapID || (subMap.ID != _globalSubMapID && other.ID < subMap.ID);
                    SubMap survivor = otherSurvives ? other : subMap, absorbed = otherSurvives ? subMap : other;
                    RigidTransform absorbedToSurvivor = otherSurvives ? otherToThis.Inverse() : otherToThis;

                    string absorbedPlayers = string.Join(", ", absorbed.PlayerIDs.Order());
                    string survivorPlayers = string.Join(", ", survivor.PlayerIDs.Order());
                    MergeInto(survivor, absorbed, absorbedToSurvivor);
                    foreach (var pairKey in _pendingMerges.Keys.Where(k => k.Item1 == absorbed.ID || k.Item2 == absorbed.ID || k.Item1 == survivor.ID || k.Item2 == survivor.ID).ToList())
                        _pendingMerges.Remove(pairKey);

                    // The absorbed players' transforms now come from this estimate; they are refined from here on.
                    foreach (int playerID in absorbed.PlayerIDs)
                    {
                        _refinablePlayers.Add(playerID);
                        _keyFramesSinceRefinement[playerID] = 0;
                    }

                    Console.WriteLine($"[GlobalMap] Merged map {absorbed.ID} (players {absorbedPlayers}) into map {survivor.ID} (players {survivorPlayers}): " +
                                      $"{alignment.Summary}; transform {absorbedToSurvivor}. Shared map now has {survivor.OrderedKeyFrames.Count} key frames.");
                    _overlay = null;
                    LastMergeDescription = $"Merged map {absorbed.ID} into map {survivor.ID}: {absorbedToSurvivor}";
                    return;
                }
            }
        }

        /// <summary>Description of the latest merge (for tests and logs).</summary>
        public string LastMergeDescription { get; private set; } = "";

        private static (int, int) PairKey(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>Whether two higher-to-lower transforms agree where the maps are.</summary>
        private bool TransformsAgree(RigidTransform a, RigidTransform b, Vector3 reference)
        {
            float yawDifference = MathF.Abs(MathF.IEEERemainder(a.YawDegrees - b.YawDegrees, 360f));
            return Vector3.Distance(a.Apply(reference), b.Apply(reference)) <= _parameters.MergeConfirmationTranslationMeters
                   && yawDifference <= _parameters.MergeConfirmationYawDegrees;
        }

        /// <summary>
        /// Move every key frame (and its points) of <paramref name="absorbed"/> into <paramref name="survivor"/>'s frame and make it part of the
        /// survivor. Players of the absorbed sub-map get their device-to-map transform updated so their next key frames arrive in the shared
        /// frame. Must be called with the map lock held.
        /// </summary>
        private void MergeInto(SubMap survivor, SubMap absorbed, RigidTransform absorbedToSurvivor)
        {
            int nextSequence = survivor.NextSequence;
            foreach (MapKeyFrame keyFrame in absorbed.OrderedKeyFrames)
            {
                MoveKeyFrame(keyFrame, absorbedToSurvivor);
                keyFrame.SubMapID = survivor.ID;
                keyFrame.Sequence = nextSequence++;
                survivor.KeyFrames[keyFrame.ID] = keyFrame;
                survivor.OrderedKeyFrames.Add(keyFrame);
            }
            survivor.NextSequence = nextSequence;
            foreach (MapAnchor anchor in absorbed.Anchors)
                survivor.Anchors.Add(anchor.Moved(absorbedToSurvivor, survivor.ID));

            foreach (int playerID in absorbed.PlayerIDs)
            {
                survivor.PlayerIDs.Add(playerID);
                _subMapIDByPlayer[playerID] = survivor.ID;
                _playerToMap[playerID] = absorbedToSurvivor.Compose(_playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity));
                if (_lastKnownPositionInMap.TryGetValue(playerID, out Vector3 lastKnownPosition))
                    _lastKnownPositionInMap[playerID] = absorbedToSurvivor.Apply(lastKnownPosition);
            }

            survivor.IsAligned = true;
            survivor.Version++;
            survivor.FrameEpoch++;
            _subMaps.Remove(absorbed.ID);
            _keyFramesSinceMergeAttempt.Remove(absorbed.ID);
            _keyFramesSinceAnchorCheck.Remove(absorbed.ID);
            _keyFramesSinceMergeAttempt[survivor.ID] = 0;
            if (absorbed.ID == _globalSubMapID)
                _globalSubMapID = survivor.ID;
            _mapVersion++;
        }

        /// <summary>Move a key frame's pose and points by a transform (the arrays are replaced, so a reader never sees half of them moved).</summary>
        private static void MoveKeyFrame(MapKeyFrame keyFrame, RigidTransform transform)
        {
            var moved = new CloudPoint[keyFrame.Points.Length];
            for (int i = 0; i < moved.Length; i++)
                moved[i] = keyFrame.Points[i] with { Position = transform.Apply(keyFrame.Points[i].Position) };
            keyFrame.Points = moved;
            keyFrame.FreeRayEnds = keyFrame.FreeRayEnds.Select(transform.Apply).ToArray();
            keyFrame.Position = transform.Apply(keyFrame.Position);
            keyFrame.Rotation = transform.Apply(keyFrame.Rotation);
            keyFrame.Prepare();
        }

        #endregion Merging sub-maps

        #region Refinement after the merge

        /// <summary>
        /// The transform of a player whose sub-map was merged is only an estimate. Register the points of the player's latest key frames against
        /// the other players' points; if they agree well but not exactly, move the player's transform (and everything of theirs in the map) a part
        /// of the way, so the correction is smooth and a bad estimate cannot throw the player far.
        /// </summary>
        private void RefinePlayerTransform(int playerID)
        {
            FusedCloud source, target;
            long epoch;
            int subMapID;
            lock (_mapLock)
            {
                _keyFramesSinceRefinement[playerID] = 0;
                if (!_refinablePlayers.Contains(playerID) || !_subMapIDByPlayer.TryGetValue(playerID, out subMapID) || !_subMaps.TryGetValue(subMapID, out SubMap? subMap) || subMap.PlayerIDs.Count < 2)
                    return;

                List<MapKeyFrame> mine = subMap.OrderedKeyFrames.Where(k => k.PlayerID == playerID).TakeLast(_parameters.RefinementWindowKeyFrames).ToList();
                List<MapKeyFrame> theirs = subMap.OrderedKeyFrames.Where(k => k.PlayerID != playerID).ToList();
                if (mine.Count == 0 || theirs.Count == 0)
                    return;

                epoch = subMap.FrameEpoch;
                source = FusedCloud.Build(mine, _parameters.FusedVoxelMeters, _parameters.MergeMinKeyFramesPerPoint);
                target = FusedCloud.Build(theirs, _parameters.FusedVoxelMeters, _parameters.MergeMinKeyFramesPerPoint);
            }

            RigidTransform? registered = CloudAlignment.Register(target, source, RigidTransform.Identity, _parameters, out string summary);
            if (registered == null)
            {
                Console.WriteLine($"[Refine] player {playerID}: no refinement ({summary}).");
                return;
            }

            AlignmentScore score = CloudAlignment.Evaluate(target, source, registered.Value, _parameters);
            if (score.Matched < _parameters.RefinementMinMatchedPoints || score.InlierFraction < _parameters.MinMergeInlierFraction * 0.8f || score.GrayAgreement < _parameters.MinMergeGrayAgreement * 0.8f)
            {
                Console.WriteLine($"[Refine] player {playerID}: not enough agreement to refine ({score}).");
                return;
            }

            RigidTransform estimate = registered.Value;
            float yawDegrees = MathF.IEEERemainder(estimate.YawDegrees, 360f);
            float shift = Vector3.Distance(estimate.Translation, Vector3.Zero);
            if (shift > _parameters.RefinementMaxShiftMeters || MathF.Abs(yawDegrees) > _parameters.RefinementMaxYawDegrees)
            {
                Console.WriteLine($"[Refine] player {playerID}: the correction ({estimate}) is larger than the limits ({_parameters.RefinementMaxShiftMeters:0.##} m / {_parameters.RefinementMaxYawDegrees:0.#} deg); ignored.");
                return;
            }
            if (shift < _parameters.RefinementMinShiftMeters && MathF.Abs(yawDegrees) < _parameters.RefinementMinYawDegrees)
                return; // already as good as it gets

            // A part of the way: about the centre of the player's latest points, so the turn does not move that place.
            Vector3 center = source.Centroid;
            float fraction = Math.Clamp(_parameters.RefinementStepFraction, 0.05f, 1f);
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * fraction * (MathF.PI / 180f));
            Vector3 movedCenter = estimate.Apply(center);
            Vector3 stepTranslation = center + (movedCenter - center) * fraction - Vector3.Transform(center, rotation);
            var delta = new RigidTransform(rotation, stepTranslation);

            lock (_mapLock)
            {
                if (!_subMaps.TryGetValue(subMapID, out SubMap? subMap) || subMap.FrameEpoch != epoch || !_subMapIDByPlayer.TryGetValue(playerID, out int current) || current != subMapID)
                {
                    Console.WriteLine($"[Refine] player {playerID}: the map changed while it was refined; the correction is dropped.");
                    return;
                }

                ApplyPlayerCorrection(subMap, playerID, delta);
                Console.WriteLine($"[Refine] player {playerID}: moved by {delta} (full estimate {estimate}; {score}).");
            }
        }

        /// <summary>Move everything of one player in a sub-map by <paramref name="delta"/> (in the sub-map's frame). Must be called with the map lock held.</summary>
        private void ApplyPlayerCorrection(SubMap subMap, int playerID, RigidTransform delta)
        {
            foreach (MapKeyFrame keyFrame in subMap.OrderedKeyFrames)
                if (keyFrame.PlayerID == playerID)
                    MoveKeyFrame(keyFrame, delta);

            _playerToMap[playerID] = delta.Compose(_playerToMap.GetValueOrDefault(playerID, RigidTransform.Identity));
            if (_lastKnownPositionInMap.TryGetValue(playerID, out Vector3 lastKnownPosition))
                _lastKnownPositionInMap[playerID] = delta.Apply(lastKnownPosition);

            subMap.Version++;
            subMap.FrameEpoch++;
            _mapVersion++;
        }

        #endregion Refinement after the merge

        #region Stereo geometry and point clouds

        /// <summary>
        /// The left camera of a key frame's stereo pair as a camera in the same frame as <paramref name="right"/>, for the depth estimation. Its pose is
        /// the right camera's pose moved by the offset between the two cameras: taken from the two cameras' reported poses when they are usable,
        /// else from the fixed offset between their lens offsets.
        /// </summary>
        private bool TryBuildLeftCamera(MapKeyFrame right, KeyFrameImageDTO dto, int width, int height, out MapKeyFrame? left)
        {
            left = null;
            CameraImageDTO? l = dto.LeftCamera;
            if (l == null || width <= 0 || height <= 0)
                return false;

            var ci = l.CameraIntrinsics;
            if (!(ci.FocalLength.x > 0f) || !(ci.FocalLength.y > 0f))
                return false;

            // Two estimates of where the left camera is relative to the right one: the fixed offset between the lens offsets (exact only if
            // both images were taken at the same moment) and the cameras' own reported poses (each at its own capture time, so they include
            // what the head did in between, but carry the tracking noise). The reported ones are used when they are valid and close to the
            // fixed offset; the matching then slides a little across the baseline to ride out what is left.
            bool haveFixed = TryRelativeCameraPose(dto.RightCamera!.CameraIntrinsics.LensOffset, ci.LensOffset, out Vector3 fixedOffset, out Quaternion fixedRotation);
            bool haveReported = TryReportedRelativePose(right, l, out Vector3 reportedOffset, out Quaternion reportedRotation);
            if (!haveFixed && !haveReported)
            {
                Console.Error.WriteLine($"[Depth] Key frame {dto.ID}: neither the lens offsets nor the poses of the two cameras are valid; the stereo pair is not used.");
                return false;
            }

            Vector3 offset;
            Quaternion rotation;
            string source;
            if (_parameters.StereoUseReportedPoses && haveReported && (!haveFixed || (
                    Vector3.Distance(reportedOffset, fixedOffset) <= _parameters.StereoReportedMaxPositionMeters &&
                    AngleBetweenDegrees(reportedRotation, fixedRotation) <= _parameters.StereoReportedMaxRotationDegrees)))
            {
                offset = reportedOffset;
                rotation = reportedRotation;
                source = "reported poses";
            }
            else if (haveFixed)
            {
                offset = fixedOffset;
                rotation = fixedRotation;
                source = haveReported && _parameters.StereoUseReportedPoses ? "lens offsets (the reported poses disagree with them)" : "lens offsets";
            }
            else
            {
                offset = reportedOffset;
                rotation = reportedRotation;
                source = "reported poses";
            }

            float baseline = offset.Length();
            if (DebugMode)
            {
                double dtMs = (l.Timestamp - dto.RightCamera.Timestamp).TotalMilliseconds;
                string detail = haveFixed && haveReported
                    ? $"; reported vs lens offsets differ by {Vector3.Distance(reportedOffset, fixedOffset) * 100f:0.0} cm and {AngleBetweenDegrees(reportedRotation, fixedRotation):0.00} deg"
                    : "";
                Console.WriteLine($"[Depth] Key frame {dto.ID}: stereo geometry from the {source}, baseline {baseline * 100f:0.0} cm, left image {dtMs:0} ms after the right{detail}.");
            }
            if (baseline < _parameters.StereoMinBaselineMeters || baseline > _parameters.StereoMaxBaselineMeters)
            {
                Console.Error.WriteLine($"[Depth] Key frame {dto.ID}: the cameras are {baseline:0.000} m apart (expected {_parameters.StereoMinBaselineMeters:0.00}-{_parameters.StereoMaxBaselineMeters:0.00} m); the stereo pair is not used.");
                return false;
            }

            // The image is a centred crop of the sensor; the sensor origin is at the bottom-left, the image origin top-left.
            float offsetX = ci.SensorResolution.x > 0 ? (ci.SensorResolution.x - width) / 2f : 0f;
            float offsetY = ci.SensorResolution.y > 0 ? (ci.SensorResolution.y - height) / 2f : 0f;

            left = new MapKeyFrame
            {
                ID = right.ID,
                SubMapID = right.SubMapID,
                PlayerID = right.PlayerID,
                Position = right.Position + Vector3.Transform(offset, right.Rotation),
                Rotation = Quaternion.Normalize(right.Rotation * rotation),
                Width = width,
                Height = height,
                Fx = ci.FocalLength.x,
                Fy = ci.FocalLength.y,
                Cx = ci.PrincipalPoint.x - offsetX,
                Cy = height - (ci.PrincipalPoint.y - offsetY),
                FlipVertically = _parameters.FlipImageVertically,
            };
            left.Prepare();
            return true;
        }

        /// <summary>
        /// The left camera's own reported pose in the frame of the right camera (<paramref name="right"/> is the right camera in the same
        /// tracking space as the reported pose).
        /// </summary>
        private static bool TryReportedRelativePose(MapKeyFrame right, CameraImageDTO left, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.Identity;

            var pose = left.CameraLocalPose;
            var rl = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
            if (float.IsNaN(rl.X) || rl.LengthSquared() < 1e-6f)
                return false;
            rl = Quaternion.Normalize(rl);

            var pl = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            Quaternion inverse = Quaternion.Conjugate(right.Rotation);
            position = Vector3.Transform(pl - right.Position, inverse);
            rotation = Quaternion.Normalize(inverse * rl);
            return !float.IsNaN(position.X) && !float.IsNaN(rotation.X);
        }

        /// <summary>
        /// The pose of the camera with lens offset <paramref name="other"/> in the frame of the camera with lens offset <paramref name="reference"/>
        /// (lens offsets are camera poses relative to the headset, so both are in the headset's frame).
        /// </summary>
        private static bool TryRelativeCameraPose(SharedPose reference, SharedPose other, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.Identity;

            var rr = new Quaternion(reference.Rotation.X, reference.Rotation.Y, reference.Rotation.Z, reference.Rotation.W);
            var ro = new Quaternion(other.Rotation.X, other.Rotation.Y, other.Rotation.Z, other.Rotation.W);
            if (float.IsNaN(rr.X) || float.IsNaN(ro.X) || rr.LengthSquared() < 1e-6f || ro.LengthSquared() < 1e-6f)
                return false;
            rr = Quaternion.Normalize(rr);
            ro = Quaternion.Normalize(ro);

            var pr = new Vector3(reference.Position.X, reference.Position.Y, reference.Position.Z);
            var po = new Vector3(other.Position.X, other.Position.Y, other.Position.Z);

            Quaternion inverse = Quaternion.Conjugate(rr);
            position = Vector3.Transform(po - pr, inverse);
            rotation = Quaternion.Normalize(inverse * ro);
            return !float.IsNaN(position.X) && !float.IsNaN(rotation.X);
        }

        /// <summary>
        /// Debug display: hand the point cloud windows a copy of the map including the dense points. Building it takes a while, so it is
        /// done at most every quarter second unless <paramref name="force"/> is set.
        /// </summary>
        private void RefreshPointClouds(bool force)
        {
            if (!DebugMode || _debugImageDisplayService == null)
                return;

            long now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref _lastPointCloudTicks) < 250)
                return;
            Interlocked.Exchange(ref _lastPointCloudTicks, now);

            // The map as it is now, then for each sub-map its fused cloud (cached, or built here away from the lock) with the walls and objects found in
            // it, drawn in the 3D view over the points; the dense points are that same cloud.
            GlobalMapSnapshot snapshot = GetSnapshot();
            MergeOverlay? overlay;
            var toGlobal = new Dictionary<int, Matrix4x4>();
            lock (_mapLock)
            {
                overlay = _overlay;
                foreach (SubMap subMap in _subMaps.Values)
                    toGlobal[subMap.ID] = subMap.ToGlobal;
            }

            var subMaps = new List<MapSnapshotSubMap>(snapshot.SubMaps.Length);
            foreach (MapSnapshotSubMap view in snapshot.SubMaps)
            {
                FusedCloud? cloud = GetFusedCloud(view.ID, out _, out _);
                if (cloud == null || !toGlobal.TryGetValue(view.ID, out Matrix4x4 matrix))
                {
                    subMaps.Add(view);
                    continue;
                }

                var dense = new MapSnapshotPoint[cloud.Count];
                for (int i = 0; i < dense.Length; i++)
                    dense[i] = new MapSnapshotPoint(Vector3.Transform(cloud.Positions[i], matrix), cloud.Players[i], cloud.Gray[i]);

                MapSnapshotAnchor[] Place(IEnumerable<MapSnapshotAnchor> anchors) =>
                    anchors.Select(a => a with
                    {
                        Position = Vector3.Transform(a.Position, matrix),
                        Core = a.Core.Select(c => Vector3.Transform(c, matrix)).ToArray(),
                        Context = a.Context.Select(c => Vector3.Transform(c, matrix)).ToArray(),
                    }).ToArray();

                // The shared anchors of this map, with the voxels each one holds.
                MapSnapshotAnchor[] anchors = Place(GetAnchors(view.ID).Select(anchor => ToSnapshotAnchor(anchor, q => q)));

                MapSnapshotAnchor[]? overlayAnchors = overlay != null && overlay.TargetSubMapID == view.ID ? Place(overlay.Anchors) : null;
                subMaps.Add(view with { DensePoints = dense, Anchors = anchors, OverlayAnchors = overlayAnchors, OverlayNote = overlayAnchors != null ? overlay!.Note : null });
            }
            _debugImageDisplayService.ShowPointClouds(new GlobalMapSnapshot(snapshot.Version, subMaps.ToArray()));
        }

        /// <summary>
        /// An anchor for the 3D view: its voxels (moved into the map's frame, then by <paramref name="place"/>), split into those within the radius its
        /// uniqueness was judged over and the rest, thinned to a few thousand for the display.
        /// </summary>
        private MapSnapshotAnchor ToSnapshotAnchor(MapAnchor anchor, Func<Vector3, Vector3> place, RigidTransform? pose = null, int style = 0)
        {
            RigidTransform at = pose ?? anchor.Pose;
            int step = Math.Max(1, anchor.Cloud.Count / 3000);
            float core2 = _parameters.AnchorSignatureRadiusMeters * _parameters.AnchorSignatureRadiusMeters;
            var core = new List<Vector3>();
            var context = new List<Vector3>();
            for (int i = 0; i < anchor.Cloud.Count; i += step)
            {
                Vector3 local = anchor.Cloud.Positions[i];
                Vector3 position = place(at.Apply(local));
                (local.X * local.X + local.Z * local.Z <= core2 ? core : context).Add(position);
            }
            return new MapSnapshotAnchor(anchor.ID, anchor.OwnerPlayerID, place(at.Translation), _parameters.AnchorRadiusMeters, core.ToArray(), context.ToArray(), style);
        }

        #endregion Stereo geometry and point clouds

        #region Snapshots

        private GlobalMapSnapshot CreateSnapshotLocked(bool includeDense = false)
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

                // The map points: a point is only a map point if several different key frames saw something in the same small cube, so a
                // point that a single frame measured (noise, a wrong match, a moving thing) is left out. One point per cube: the average of what saw it,
                // in the colour of the player who saw it first.
                float viewVoxel = MathF.Max(0.02f, _parameters.MapViewVoxelMeters);
                int minFrames = Math.Max(1, _parameters.MapViewMinKeyFrames);
                var viewCells = new Dictionary<(int, int, int), (Vector3 Sum, int Count, int Player, int LastFrame, int Frames, float Gray)>();
                foreach (MapKeyFrame kf in subMap.OrderedKeyFrames)
                    foreach (CloudPoint point in kf.Points)
                    {
                        var key = ((int)MathF.Floor(point.Position.X / viewVoxel), (int)MathF.Floor(point.Position.Y / viewVoxel), (int)MathF.Floor(point.Position.Z / viewVoxel));
                        viewCells.TryGetValue(key, out var cell);
                        if (cell.Count == 0)
                            cell.Player = point.PlayerID;
                        cell.Sum += point.Position;
                        cell.Gray += point.Gray;
                        cell.Count++;
                        if (cell.LastFrame != kf.ID) // the key frames are visited one after the other, so this counts different ones
                        {
                            cell.LastFrame = kf.ID;
                            cell.Frames++;
                        }
                        viewCells[key] = cell;
                    }
                var mapPoints = new List<MapSnapshotPoint>();
                foreach (var cell in viewCells.Values)
                    if (cell.Frames >= minFrames)
                        mapPoints.Add(new MapSnapshotPoint(Vector3.Transform(cell.Sum / cell.Count, toGlobal), cell.Player, cell.Gray / cell.Count));

                MapSnapshotPoint[]? dense = null;
                if (includeDense)
                {
                    FusedCloud fused = subMap.Fused != null && subMap.FusedVersion == subMap.Version ? subMap.Fused : FusedCloud.Build(subMap.OrderedKeyFrames, _parameters.FusedVoxelMeters);
                    dense = new MapSnapshotPoint[fused.Count];
                    for (int i = 0; i < dense.Length; i++)
                        dense[i] = new MapSnapshotPoint(Vector3.Transform(fused.Positions[i], toGlobal), fused.Players[i], fused.Gray[i]);
                }

                subMaps.Add(new MapSnapshotSubMap(subMap.ID, subMap.PlayerIDs.Order().ToArray(), subMap.IsAligned, keyFrames, mapPoints.ToArray(), dense));
            }

            return new GlobalMapSnapshot(_mapVersion, subMaps.ToArray());
        }

        #endregion Snapshots
    }
}
