using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// A rotation followed by a translation: moves points from one coordinate frame into another (a device's tracking space
    /// into the map frame, or one sub-map's frame into another's).
    /// </summary>
    public readonly struct RigidTransform
    {
        public Quaternion Rotation { get; }
        public Vector3 Translation { get; }

        public RigidTransform(Quaternion rotation, Vector3 translation)
        {
            Rotation = Quaternion.Normalize(rotation);
            Translation = translation;
        }

        public static RigidTransform Identity => new(Quaternion.Identity, Vector3.Zero);

        public Vector3 Apply(Vector3 point) => Vector3.Transform(point, Rotation) + Translation;
        public Vector3 ApplyToDirection(Vector3 direction) => Vector3.Transform(direction, Rotation);
        public Quaternion Apply(Quaternion orientation) => Quaternion.Normalize(Rotation * orientation);

        /// <summary>The transform that applies <paramref name="first"/> and then this one.</summary>
        public RigidTransform Compose(RigidTransform first) => new(Rotation * first.Rotation, Apply(first.Translation));

        public RigidTransform Inverse()
        {
            Quaternion inverse = Quaternion.Conjugate(Rotation);
            return new RigidTransform(inverse, -Vector3.Transform(Translation, inverse));
        }

        /// <summary>Rotation about the vertical axis, in degrees (meaningful for the yaw-only transforms the merge produces).</summary>
        public float YawDegrees
        {
            get
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, Rotation);
                return MathF.Atan2(forward.X, forward.Z) * (180f / MathF.PI);
            }
        }

        public Matrix4x4 ToMatrix()
        {
            Matrix4x4 matrix = Matrix4x4.CreateFromQuaternion(Rotation);
            matrix.Translation = Translation;
            return matrix;
        }

        public override string ToString() => $"yaw {YawDegrees:0.0} deg, shift ({Translation.X:0.00}, {Translation.Y:0.00}, {Translation.Z:0.00}) m";
    }

    /// <summary>Tuning values for the stereo depth, the player maps and the merging of maps.</summary>
    public sealed class GlobalMappingParameters
    {
        // ----- Which key frames are used -----

        /// <summary>A new key frame must be at least this far from the player's previous key frame (metres) ...</summary>
        public float MinTranslationMeters { get; init; } = 0.03f;

        /// <summary>... or rotated at least this much (degrees), otherwise it adds no new information and is skipped.</summary>
        public float MinRotationDegrees { get; init; } = 3f;

        /// <summary>
        /// Set if the decoded key frame image has its first row at the BOTTOM of the scene. The Unity client sends the image
        /// upright (first row = top), which is what the camera intrinsics assume.
        /// </summary>
        public bool FlipImageVertically { get; init; } = false;

        // ----- Depth from the stereo pair -----

        /// <summary>The distance between the two cameras must be within these bounds, in meters, or the pair is not used. The Quest 3's are about 0.10 m apart.</summary>
        public float StereoMinBaselineMeters { get; init; } = 0.03f;
        public float StereoMaxBaselineMeters { get; init; } = 0.30f;

        /// <summary>
        /// Use the two cameras' own reported poses (each at its own capture time) as the stereo geometry, instead of the fixed offset between
        /// their lens offsets, which assumes the two images were taken at the same moment. The fixed offset is used if a reported pose is not valid,
        /// or the two geometries differ by more than the limits below (a tracking glitch).
        /// </summary>
        public bool StereoUseReportedPoses { get; init; } = true;
        public float StereoReportedMaxRotationDegrees { get; init; } = 10f;
        public float StereoReportedMaxPositionMeters { get; init; } = 0.05f;

        /// <summary>The size the two images are rectified to and matched at. Smaller is faster; depth is less precise.</summary>
        public int StereoWidth { get; init; } = 640;
        public int StereoHeight { get; init; } = 480;

        /// <summary>Distances outside this range (meters) are not used. The nearest one sets the largest disparity searched, at most <see cref="StereoMaxDisparity"/>.</summary>
        public float StereoMinRangeMeters { get; init; } = 0.4f;
        public float StereoMaxRangeMeters { get; init; } = 4.5f;
        public int StereoMaxDisparity { get; init; } = 128;

        /// <summary>Pixels between the depth values taken from the matcher's output, on the right camera's image. 4 gives about 19,000 points per 640x480 frame.</summary>
        public int StereoStride { get; init; } = 8;

        /// <summary>
        /// Each depth value is the median disparity of a block of this many pixels (of the right camera's own image) around its grid position, not
        /// of the single pixel: it needs <see cref="StereoBlockMinValidFraction"/> of the block matched, and most of those must agree with the median
        /// (<see cref="StereoBlockAgreeFraction"/>), which also drops blocks that straddle a depth edge. 0 or 1 = single pixels, with the neighbour edge test.
        /// </summary>
        public int StereoBlockPixels { get; init; } = 16;
        public float StereoBlockMinValidFraction { get; init; } = 0.4f;
        public float StereoBlockAgreeFraction { get; init; } = 0.7f;

        /// <summary>Measure and remove a vertical offset between the two rectified images (up to this many pixels), as the head moves between the two captures.</summary>
        public bool StereoAlignVertical { get; init; } = true;
        public float StereoMaxVerticalShiftPixels { get; init; } = 6f;

        /// <summary>A depth is dropped if a pixel two steps away in any direction has a disparity that differs by more than this many pixels or this fraction of the disparity (depth edges).</summary>
        public float StereoEdgePixels { get; init; } = 1.5f;
        public float StereoEdgeFraction { get; init; } = 0.08f;

        // ----- Semi-global matching -----

        public int SgmP1 { get; init; } = 4;
        public int SgmP2 { get; init; } = 20;
        public int SgmUniquenessPercent { get; init; } = 8;
        public float SgmLeftRightTolerancePixels { get; init; } = 1.5f;
        public int SgmSpeckleSize { get; init; } = 120;
        public float SgmSpeckleDifferencePixels { get; init; } = 1f;

        /// <summary>4 or 8 aggregation directions; 8 is a little better and twice as slow.</summary>
        public int SgmPaths { get; init; } = 4;

        // ----- Point clouds of the maps -----

        /// <summary>The points of one key frame are thinned to one per cube of this size, at most <see cref="MaxPointsPerKeyFrame"/> of them.</summary>
        public float KeyFrameVoxelMeters { get; init; } = 0.04f;
        public int MaxPointsPerKeyFrame { get; init; } = 8000;

        /// <summary>The points of a whole sub-map are fused to one (averaged) per cube of this size; the fused cloud is what is aligned and displayed.</summary>
        /// <summary>Merging and refining use only points that at least this many different key frames measured (about the same place); points seen once are noise or mismatches.</summary>
        /// <summary>A sub-map with more fused points than this is fused with bigger cubes for the comparison, to keep it fast.</summary>
        public int MergeMaxPoints { get; init; } = 25000;

        public int MergeMinKeyFramesPerPoint { get; init; } = 2;

        public float FusedVoxelMeters { get; init; } = 0.05f;

        /// <summary>Map points (the 3D viewer and the top-down map): one per cube of this size...</summary>
        public float MapViewVoxelMeters { get; init; } = 0.04f;

        /// <summary>...and only where at least this many different key frames measured a point in that cube.</summary>
        public int MapViewMinKeyFrames { get; init; } = 2;

        // ----- Merging sub-maps -----
        // Two sub-maps are aligned from their 3D shape and brightness alone (both devices are gravity-aligned and metric, so only a turn about
        // the vertical axis and a translation are unknown): a coarse search over yaw and position with FFT correlation of top-down views, then
        // ICP on the point clouds, then a check of how well the clouds agree (geometry and brightness).

        /// <summary>A sub-map needs at least this many key frames and fused points before it is compared with another.</summary>
        public int MergeMinKeyFrames { get; init; } = 2;
        public int MergeMinPoints { get; init; } = 600;

        /// <summary>A merge is attempted again after the player's sub-map has this many more key frames.</summary>
        public int MergeAttemptEveryKeyFrames { get; init; } = 1;

        /// <summary>Top-down grid of the coarse search: this many cells per side (a power of two) of this size; the cell size grows if the maps are larger than the grid.</summary>
        public int MergeGridSize { get; init; } = 128;
        public float MergeGridCellMeters { get; init; } = 0.15f;

        /// <summary>Yaw steps of the coarse search, and how many of its best candidates are compared and refined.</summary>
        public float MergeYawStepDegrees { get; init; } = 3f;

        /// <summary>
        /// The coarse search scores a placement by how well the two maps agree per square metre that both looked at (see <see cref="ViewedRegion"/>),
        /// not in total, so a small true overlap is not outscored by a big wrong one. A placement whose shared viewed area is smaller than
        /// MergeMinOverlapSquareMeters, or than MergeMinOverlapFraction of the smaller map's viewed area, is scored as if it were that big.
        /// </summary>
        public float MergeMinOverlapSquareMeters { get; init; } = 4f;
        public float MergeMinOverlapFraction { get; init; } = 0.25f;
        public int MergeCandidates { get; init; } = 8;
        // Rays (used by the freshness check below): a ray from the camera to a measured point passes through the voxels along it, from FreeStartMeters
        // out to FreeFarnessMeters short of the point (so the point's own noise does not carve the surface itself) or FreeMaxRangeMeters. Grid cells
        // without a depth carve nothing, and neither do the valid cells next to them.
        public float FreeFarnessMeters { get; init; } = 0.2f;
        public float FreeStartMeters { get; init; } = 0.3f;
        public float FreeMaxRangeMeters { get; init; } = 4f;

        // Freshness: a point starts with PointLives lives. A new key frame whose ray passes through the point's voxel (and ends more than
        // FreeFarnessMeters beyond it) saw through it, so the point loses a life; at none it is removed. A point the new key frame measures again
        // gets all its lives back (PointRefreshOnReseen).
        // Shared anchors: distinctive objects (a spot whose surroundings look unlike the rest of the map) with a frozen copy of the voxels within
        // AnchorRadiusMeters. Another map looks for each anchor in its own points; the placements of the maps that at least AnchorMinAgree different
        // anchors agree on are tried like the other candidates, and AnchorTrustAgree of them make a placement trusted. With AnchorsRequireForMerge
        // the maps are only merged on such a placement.
        public bool AnchorsEnabled { get; init; } = true;
        public bool AnchorsRequireForMerge { get; init; } = false;
        public float AnchorRadiusMeters { get; init; } = 1f;
        /// <summary>Anchors are at least this far apart.</summary>
        public float AnchorSpacingMeters { get; init; } = 0.8f;
        public int AnchorMinPoints { get; init; } = 150;
        public int AnchorMaxPerMap { get; init; } = 48;
        public int AnchorCheckEveryKeyFrames { get; init; } = 2;
        public int AnchorMaxNewPerCheck { get; init; } = 8;
        public int AnchorMaxLocatePerDirection { get; init; } = 8;
        public int AnchorMaxCandidates { get; init; } = 2;
        public int AnchorMinMatchedPoints { get; init; } = 150;
        public float AnchorMinInlierFraction { get; init; } = 0.4f;
        public float AnchorMinGrayAgreement { get; init; } = 0.2f;
        public int AnchorMinAgree { get; init; } = 2;
        public int AnchorTrustAgree { get; init; } = 3;

        // Where anchors go: candidate spots on a grid (AnchorSiteGridMeters); each is described by a signature of the voxels within
        // AnchorSignatureRadiusMeters (how many are at which distance and height, and how bright they are). A spot's uniqueness is how far its signature
        // is from that of the most similar other spot at least AnchorSpacingMeters away (a wall, the floor or a row of identical chairs look like each
        // other; a lone object does not). It needs AnchorMinStandingPoints voxels above the floor (more than just floor), a uniqueness of at least
        // AnchorMinUniqueness (0 to 1), and at least AnchorMinComparisonSites other spots to compare with.
        public float AnchorSiteGridMeters { get; init; } = 0.5f;
        public float AnchorSignatureRadiusMeters { get; init; } = 0.7f;
        public int AnchorMinStandingPoints { get; init; } = 30;
        public float AnchorMinUniqueness { get; init; } = 0.12f;
        public int AnchorMinComparisonSites { get; init; } = 3;

        /// <summary>The same parameters with the search made lighter, for looking for a small anchor in a map (the anchors themselves are what gives it confidence): fewer refined candidates, a lighter ICP.</summary>
        public GlobalMappingParameters ForAnchors()
        {
            var copy = (GlobalMappingParameters)MemberwiseClone();
            typeof(GlobalMappingParameters).GetProperty(nameof(MergeSnapshotCandidates))!.SetValue(copy, 0);
            typeof(GlobalMappingParameters).GetProperty(nameof(MergeRefinedCandidates))!.SetValue(copy, Math.Min(MergeRefinedCandidates, 2));
            typeof(GlobalMappingParameters).GetProperty(nameof(IcpMaxPoints))!.SetValue(copy, Math.Min(IcpMaxPoints, 2500));
            typeof(GlobalMappingParameters).GetProperty(nameof(IcpIterations))!.SetValue(copy, Math.Min(IcpIterations, 10));
            return copy;
        }

        public bool PointFreshnessEnabled { get; init; } = true;
        public int PointLives { get; init; } = 3;
        public bool PointRefreshOnReseen { get; init; } = true;

        // Snapshots: a few single views of the map (the depth points of a key frame and its neighbour, in the map frame with the corrected pose) are each
        // looked for in the other map. Where several different snapshots land on the same placement of the two maps, that placement is a strong candidate
        // (and, with enough agreeing snapshots, is trusted even if the whole-map comparison is thrown off by drift).
        public int MergeSnapshotCount { get; init; } = 4;
        /// <summary>Key frames in one snapshot: the picked one and the ones just before it from the same player.</summary>
        public int MergeSnapshotKeyFrames { get; init; } = 2;
        public int MergeSnapshotMinPoints { get; init; } = 600;
        /// <summary>Snapshots are picked at least this far apart (metres) or turned this far apart (degrees).</summary>
        public float MergeSnapshotSeparationMeters { get; init; } = 0.5f;
        public float MergeSnapshotSeparationDegrees { get; init; } = 30f;
        /// <summary>A snapshot's placement counts as a vote if this many of its points agree, this fraction of those in range, with brightness correlation MinMergeGrayAgreement.</summary>
        public int MergeSnapshotMinMatched { get; init; } = 300;
        public float MergeSnapshotMinInlierFraction { get; init; } = 0.35f;
        /// <summary>Votes agree if the placements differ by less than this (metres at the other map's centre, degrees).</summary>
        public float MergeSnapshotAgreeMeters { get; init; } = 0.3f;
        public float MergeSnapshotAgreeDegrees { get; init; } = 5f;
        /// <summary>How many placements from the snapshots are refined and checked besides the ones from the top-down search.</summary>
        public int MergeSnapshotCandidates { get; init; } = 4;
        /// <summary>This many different snapshots agreeing makes the placement pass even when the whole-map check does not (0 = the whole-map check always decides).</summary>
        public int MergeSnapshotMinVotes { get; init; } = 3;

        // Pose correction: each new key frame is lined up against the points already in the map (ICP, yaw + translation) starting from the pose the
        // headset reported, and takes the corrected pose if the fit is good and the correction small.
        public bool PoseCorrectionEnabled { get; init; } = true;
        public float PoseCorrectionMaxShiftMeters { get; init; } = 0.15f;
        public float PoseCorrectionMaxYawDegrees { get; init; } = 4f;
        public float PoseCorrectionStartMeters { get; init; } = 0.12f;
        public float PoseCorrectionEndMeters { get; init; } = 0.03f;
        public int PoseCorrectionIterations { get; init; } = 10;
        /// <summary>The map points used as reference: those of key frames whose camera is within this distance of the new one...</summary>
        public float PoseCorrectionReferenceRadiusMeters { get; init; } = 3f;
        /// <summary>...at most this many of them (the latest).</summary>
        public int PoseCorrectionMaxReferenceKeyFrames { get; init; } = 20;
        public int PoseCorrectionMinReferencePoints { get; init; } = 1500;
        public int PoseCorrectionMinMatchedPoints { get; init; } = 400;
        public float PoseCorrectionMinMatchedFraction { get; init; } = 0.35f;

        public int MergeRefinedCandidates { get; init; } = 3;

        /// <summary>The weight of the brightness (as opposed to the shape) of the top-down view in the coarse search.</summary>
        public float MergeTextureWeight { get; init; } = 1f;

        // ICP: the other cloud is matched point to point against this one with a distance limit that shrinks, fitting the closest part of the matches each step.
        public int IcpIterations { get; init; } = 15;
        public float IcpStartDistanceMeters { get; init; } = 0.30f;
        public float IcpEndDistanceMeters { get; init; } = 0.06f;
        public float IcpKeepFraction { get; init; } = 0.8f;
        public int IcpMaxPoints { get; init; } = 6000;

        // The check: a point of the other cloud agrees if this cloud has a point within MergeInlierMeters; only the part of the other cloud
        // near this cloud counts (MergeRegionRadiusMeters), since the two maps need not cover the same area.
        /// <summary>After the ICP the position is searched again by brightness: within this distance (0 turns it off) in steps of this size, then fine-tuned.</summary>
        public float MergeTextureSearchMeters { get; init; } = 0.45f;
        public float MergeTextureSearchStepMeters { get; init; } = 0.075f;

        public float MergeInlierMeters { get; init; } = 0.10f;
        public float MergeRegionRadiusMeters { get; init; } = 0.5f;
        public int MinMergeMatchedPoints { get; init; } = 600;
        public float MinMergeInlierFraction { get; init; } = 0.3f;

        /// <summary>Matched points whose brightness (after removing the average difference between the two maps) differs by less than this agree; this fraction of them must.</summary>
        public float MinMergeGrayAgreement { get; init; } = 0.3f;

        /// <summary>A merge needs a second estimate, from a later attempt, that agrees with the first within these limits and window.</summary>
        public float MergeConfirmationTranslationMeters { get; init; } = 0.3f;
        public float MergeConfirmationYawDegrees { get; init; } = 5f;
        public float MergeConfirmationWindowSeconds { get; init; } = 60f;

        // ----- Refining after the merge -----
        // The transform of a player whose sub-map was merged is only an estimate. Periodically the player's latest points are registered
        // against the other players' points, and the transform corrected a part of the way.

        public int RefinementIntervalKeyFrames { get; init; } = 5;
        public int RefinementWindowKeyFrames { get; init; } = 8;
        public float RefinementStepFraction { get; init; } = 0.5f;
        public float RefinementMinShiftMeters { get; init; } = 0.02f;
        public float RefinementMinYawDegrees { get; init; } = 0.5f;
        public float RefinementMaxShiftMeters { get; init; } = 0.75f;
        public float RefinementMaxYawDegrees { get; init; } = 15f;
        public int RefinementMinMatchedPoints { get; init; } = 800;
    }

    /// <summary>A 3D point of a depth map, in a map's frame, with the brightness of the pixel it came from and the player who saw it.</summary>
    /// <summary>A measured point. <paramref name="Lives"/> is its freshness: it loses one each time a later key frame sees through it (see <see cref="PointFreshness"/>).</summary>
    public readonly record struct CloudPoint(Vector3 Position, float Gray, int PlayerID, byte Lives = 3);

    public sealed class MapKeyFrame
    {
        public int ID { get; init; }
        public int SubMapID { get; set; }
        public int PlayerID { get; init; }

        /// <summary>The key frame's ID as the client numbered it (for log messages).</summary>
        public long ClientKeyFrameID { get; init; }
        public DateTime Timestamp { get; init; }

        /// <summary>Order in which key frames joined the sub-map.</summary>
        public int Sequence { get; set; }

        /// <summary>Camera pose in the frame of the sub-map the key frame belongs to (cameras: x right, y up, z forward, as in Unity).</summary>
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        private Quaternion _inverseRotation;

        public int Width { get; init; }
        public int Height { get; init; }

        // Pinhole intrinsics for the decoded image: pixels, origin at the top-left, x right, y down.
        public float Fx { get; init; }
        public float Fy { get; init; }
        public float Cx { get; init; }
        public float Cy { get; init; }
        public bool FlipVertically { get; init; }

        /// <summary>The key frame's depth as 3D points in the sub-map's frame (thinned).</summary>
        public CloudPoint[] Points { get; set; } = Array.Empty<CloudPoint>();

        /// <summary>
        /// Where the key frame measured a depth, away from grid cells without a depth (in the sub-map's frame, thinned): the space between the camera
        /// (<see cref="Position"/>) and a little short of each of these points was seen through, so it is empty.
        /// </summary>
        public Vector3[] FreeRayEnds { get; set; } = Array.Empty<Vector3>();

        /// <summary>Must be called after the pose is set or changed.</summary>
        public void Prepare() => _inverseRotation = Quaternion.Conjugate(Quaternion.Normalize(Rotation));

        /// <summary>Unit viewing ray through the given pixel (image coordinates, y down), in the frame the pose is in.</summary>
        public Vector3 PixelToWorldRay(float x, float y)
        {
            float v = FlipVertically ? Height - 1 - y : y;
            // Camera axes: x right, y up, z forward (Unity). Image y points down, so negate.
            var dirCamera = new Vector3((x - Cx) / Fx, (Cy - v) / Fy, 1f);
            return Vector3.Normalize(Vector3.Transform(dirCamera, Rotation));
        }

        /// <summary>Project a point into this key frame. Returns false if it is behind the camera.</summary>
        public bool TryProject(Vector3 worldPoint, out float x, out float y, out float depth)
        {
            Vector3 pc = Vector3.Transform(worldPoint - Position, _inverseRotation);
            depth = pc.Z;
            x = y = 0f;
            if (depth <= 1e-4f)
                return false;

            x = Fx * pc.X / depth + Cx;
            float v = Cy - Fy * pc.Y / depth;
            y = FlipVertically ? Height - 1 - v : v;
            return true;
        }
    }

    /// <summary>
    /// A set of key frames (and their depth points) that share one coordinate frame. Each player starts in a sub-map of their own
    /// (their device's tracking space). When two sub-maps are found to overlap they are merged into one: the absorbed sub-map's key
    /// frames and points are moved into the surviving sub-map's frame, which is then the shared frame of all the players in it.
    /// </summary>
    public sealed class SubMap
    {
        public int ID { get; init; }

        public HashSet<int> PlayerIDs { get; } = new();
        public Dictionary<int, MapKeyFrame> KeyFrames { get; } = new();
        public List<MapKeyFrame> OrderedKeyFrames { get; } = new();

        /// <summary>The shared anchors made in this sub-map (and, after merges, in the sub-maps merged into it), in its frame.</summary>
        public List<MapAnchor> Anchors { get; } = new();
        public int NextSequence { get; set; }

        /// <summary>Transform from this sub-map's frame into the global frame (identity until the global map is formed).</summary>
        public Matrix4x4 ToGlobal { get; set; } = Matrix4x4.Identity;

        /// <summary>True once this sub-map is the result of a merge.</summary>
        public bool IsAligned { get; set; }

        /// <summary>Counts every change of the sub-map's contents (a key frame added, or points moved).</summary>
        public long Version { get; set; }

        /// <summary>Counts the changes of the sub-map's frame or of where its points are: a merge, or a player's points being moved.</summary>
        public long FrameEpoch { get; set; }

        /// <summary>The fused point cloud as of <see cref="FusedVersion"/>.</summary>
        public FusedCloud? Fused { get; set; }
        public long FusedVersion { get; set; } = -1;
    }

    public readonly record struct DepthPoint(float X, float Y, float Depth);

    /// <summary>A grid position of a depth map that has no depth, and why (a <see cref="DepthStatus"/> value).</summary>
    public readonly record struct RejectedPoint(float X, float Y, int Reason);

    public sealed record KeyFrameInsertResult(bool Inserted, string Reason, int Points, bool Merged = false, string MergeDescription = "");

    // ----- Immutable view of the map for the debug window (already in the global frame) -----

    public sealed record MapSnapshotKeyFrame(int ID, int PlayerID, int Sequence, Vector3 Position, Vector3 Forward);

    public sealed record MapSnapshotPoint(Vector3 Position, int PlayerID, float Gray = -1f);

    /// <summary>
    /// A shared anchor for the 3D view: where it stands, the radius it covers, and the voxels it holds (<paramref name="Core"/>: within the radius its
    /// uniqueness was judged over; <paramref name="Context"/>: the rest of what it stores), all in the frame of the map being shown. <paramref name="Style"/> (overlay only): 1 = at the whole-map alignment, 2 = where the anchor was found and it agrees with the best placement the anchors agree on, 3 = found but disagrees.
    /// </summary>
    public sealed record MapSnapshotAnchor(int ID, int PlayerID, Vector3 Position, float Radius, Vector3[] Core, Vector3[] Context, int Style = 0);

    /// <summary>
    /// <paramref name="Anchors"/> are the shared anchors of this map; <paramref name="OverlayAnchors"/> are those of the map it was last
    /// compared with, placed where the best alignment of that attempt put them (described by <paramref name="OverlayNote"/>).
    /// </summary>
    public sealed record MapSnapshotSubMap(int ID, int[] PlayerIDs, bool IsAligned, MapSnapshotKeyFrame[] KeyFrames, MapSnapshotPoint[] Points, MapSnapshotPoint[]? DensePoints = null,
        MapSnapshotAnchor[]? Anchors = null, MapSnapshotAnchor[]? OverlayAnchors = null, string? OverlayNote = null);

    public sealed record GlobalMapSnapshot(long Version, MapSnapshotSubMap[] SubMaps);
}
