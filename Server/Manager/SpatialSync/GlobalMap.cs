using System.Numerics;
using Server.Utilities;

namespace Server.Manager.SpatialSync
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

    /// <summary>Tuning values for building the global map. Defaults follow ORB-SLAM where it has an equivalent.</summary>
    public sealed class GlobalMappingParameters
    {
        /// <summary>Key frames with fewer ORB features than this are too weak to map and are dropped.</summary>
        public int MinFeaturesToInsert { get; init; } = 50;

        /// <summary>A new key frame must be at least this far from the player's previous key frame (metres) ...</summary>
        public float MinTranslationMeters { get; init; } = 0.03f;

        /// <summary>... or rotated at least this much (degrees), otherwise it adds no new information and is skipped.</summary>
        public float MinRotationDegrees { get; init; } = 3f;

        /// <summary>How many covisible key frames a new key frame is triangulated against (the strongest links, plus the latest key frame).</summary>
        public int NeighborKeyFrameCount { get; init; } = 5;

        /// <summary>
        /// When a map point is projected into a new key frame, its descriptor is compared with the keypoints within this many
        /// pixels of where it lands (scaled up for coarser pyramid levels). The headset pose is not perfect, so keep some slack.
        /// </summary>
        public float ProjectionSearchRadiusPixels { get; init; } = 12f;

        /// <summary>A map point is only expected to be seen from within this angle of the directions it was seen from before.</summary>
        public float MaxViewAngleDegrees { get; init; } = 60f;

        /// <summary>A young map point that was found in fewer than this fraction of the key frames it should be visible in is culled.</summary>
        public float MinFoundRatio { get; init; } = 0.25f;

        /// <summary>Two viewing rays closer together than this cannot be triangulated reliably.</summary>
        public float MinParallaxDegrees { get; init; } = 1f;

        public float MinDepthMeters { get; init; } = 0.1f;
        public float MaxDepthMeters { get; init; } = 15f;

        /// <summary>
        /// Maximum reprojection error of a map point, in pixels at the keypoint's pyramid level 0, multiplied by the
        /// keypoint's pyramid scale (coarser octaves are less precise).
        /// </summary>
        public float ReprojectionTolerancePixels { get; init; } = 3f;

        /// <summary>Two key frames are covisible when they share at least this many map points.</summary>
        public int MinCovisibleMapPoints { get; init; } = 15;

        /// <summary>A map point older than <see cref="CullAfterKeyFrames"/> key frames must be seen by at least this many key frames.</summary>
        public int MinObservationsToKeep { get; init; } = 3;
        public int CullAfterKeyFrames { get; init; } = 2;

        /// <summary>
        /// Set if the decoded key frame image has its first row at the BOTTOM of the scene. The Unity client sends the image
        /// upright (first row = top), which is what the camera intrinsics assume.
        /// </summary>
        public bool FlipImageVertically { get; init; } = false;

        public ORBExtractorParameters ExtractorParameters { get; init; } = new() { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        public ORBMatchParameters MatchParameters { get; init; } = ORBMatchParameters.Default;

        // ----- Merging sub-maps -----

        /// <summary>Descriptor-matched map point pairs between two sub-maps that must agree on one transform for them to be merged.</summary>
        public int MinMergeInliers { get; init; } = 20;

        /// <summary>A matched pair agrees with a candidate transform if the points land within this distance of each other.</summary>
        public float MergeInlierDistanceMeters { get; init; } = 0.15f;

        public int MergeRansacIterations { get; init; } = 300;

        /// <summary>
        /// After a transform is found, the other sub-map's points are projected into the new key frame; at least this many must
        /// land on matching keypoints. This checks the transform against the image itself, not only against the 3D points.
        /// </summary>
        public int MinMergeVerificationMatches { get; init; } = 15;
    }

    /// <summary>A 3D point of the map, triangulated from two or more key frames with known camera poses.</summary>
    public sealed class MapPoint
    {
        public int ID { get; init; }
        public int SubMapID { get; set; }

        /// <summary>Position in the owning sub-map's own coordinate frame (the player's tracking space).</summary>
        public Vector3 Position { get; set; }

        /// <summary>Representative 256-bit ORB descriptor (4 ulongs) used for matching new frames against the map.</summary>
        public ulong[] Descriptor { get; set; } = new ulong[ORBFeatureSet.DescriptorLongs];

        /// <summary>Key frame ID -> index of the feature in that key frame that observes this point.</summary>
        public Dictionary<int, int> Observations { get; } = new();

        /// <summary>Mean unit vector from the point towards the cameras that see it.</summary>
        public Vector3 MeanViewDirection { get; set; }

        public int ReferenceKeyFrameID { get; set; }

        /// <summary>
        /// Range of distances from which the point can be recognised (from the pyramid level it was first detected on). A point
        /// is only searched for in a key frame whose camera is within this range.
        /// </summary>
        public float MinDistance { get; init; }
        public float MaxDistance { get; init; }

        /// <summary>Key frames the point should have been visible in, and the ones it was actually found in (both start at 1).</summary>
        public int VisibleCount { get; set; } = 1;
        public int FoundCount { get; set; } = 1;

        /// <summary>Sequence number of the key frame that created the point (for culling young, unconfirmed points).</summary>
        public int CreatedSequence { get; set; }
    }

    /// <summary>A key frame in the map: camera pose, ORB features and which feature sees which map point.</summary>
    public sealed class MapKeyFrame
    {
        public int ID { get; init; }
        public int SubMapID { get; set; }
        public int PlayerID { get; init; }

        /// <summary>The ID the client gave this key frame.</summary>
        public long ClientKeyFrameID { get; init; }
        public DateTime Timestamp { get; init; }

        /// <summary>Order of insertion within the sub-map, starting at 0.</summary>
        public int Sequence { get; set; }

        /// <summary>Camera pose in the sub-map frame (camera-to-world).</summary>
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

        public ORBFeatureSet Features { get; init; } = ORBFeatureSet.Empty;

        /// <summary>Per feature: ID of the map point it observes, or -1.</summary>
        public int[] MapPointIDs { get; init; } = Array.Empty<int>();

        /// <summary>Per feature: pyramid scale (ScaleFactor ^ octave).</summary>
        public float[] LevelScale { get; init; } = Array.Empty<float>();

        /// <summary>Other key frame ID -> number of map points shared with it.</summary>
        public Dictionary<int, int> Covisibility { get; } = new();

        public void Prepare() => _inverseRotation = Quaternion.Conjugate(Quaternion.Normalize(Rotation));

        /// <summary>Unit viewing ray in sub-map space through the given pixel (level-0 image coordinates, y down).</summary>
        public Vector3 PixelToWorldRay(float x, float y)
        {
            float v = FlipVertically ? Height - 1 - y : y;
            // Camera axes: x right, y up, z forward (Unity). Image y points down, so negate.
            var dirCamera = new Vector3((x - Cx) / Fx, (Cy - v) / Fy, 1f);
            return Vector3.Normalize(Vector3.Transform(dirCamera, Rotation));
        }

        /// <summary>Project a sub-map point into this key frame. Returns false if it is behind the camera.</summary>
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
    /// A set of key frames and map points that share one coordinate frame. Each player starts in a sub-map of their own
    /// (their device's tracking space). When two sub-maps are found to overlap they are merged into one: the absorbed
    /// sub-map's key frames and points are moved into the surviving sub-map's frame, which is then the shared frame of
    /// all the players in it.
    /// </summary>
    public sealed class SubMap
    {
        public int ID { get; init; }

        /// <summary>Every player whose key frames are in this sub-map (more than one after a merge).</summary>
        public HashSet<int> PlayerIDs { get; } = new();
        public Dictionary<int, MapKeyFrame> KeyFrames { get; } = new();
        public List<MapKeyFrame> OrderedKeyFrames { get; } = new();
        public Dictionary<int, MapPoint> Points { get; } = new();

        /// <summary>IDs of points young enough that they may still be culled.</summary>
        public List<int> RecentPointIDs { get; } = new();

        /// <summary>Sub-map frame -> global frame. Identity: merged sub-maps already share the surviving sub-map's frame.</summary>
        public Matrix4x4 ToGlobal { get; set; } = Matrix4x4.Identity;

        /// <summary>True once other players' sub-maps have been merged into this one.</summary>
        public bool IsAligned { get; set; }
    }

    public sealed record KeyFrameInsertResult(bool Inserted, string Reason, int Features, int NewMapPoints, int TrackedMapPoints, bool Merged = false, string MergeDescription = "");

    // ----- Immutable view of the map for the debug window (already in the global frame) -----

    public sealed record MapSnapshotKeyFrame(int ID, int PlayerID, int Sequence, Vector3 Position, Vector3 Forward);

    public sealed record MapSnapshotPoint(Vector3 Position, int PlayerID);

    public sealed record MapSnapshotSubMap(int ID, int[] PlayerIDs, bool IsAligned, MapSnapshotKeyFrame[] KeyFrames, MapSnapshotPoint[] Points);

    public sealed record GlobalMapSnapshot(long Version, MapSnapshotSubMap[] SubMaps);
}
