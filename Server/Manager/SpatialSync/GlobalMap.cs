using System.Numerics;
using Server.Utilities;

namespace Server.Manager.SpatialSync
{
    /// <summary>Tuning values for building the global map. Defaults follow ORB-SLAM where it has an equivalent.</summary>
    public sealed class GlobalMappingParameters
    {
        /// <summary>Key frames with fewer ORB features than this are too weak to map and are dropped.</summary>
        public int MinFeaturesToInsert { get; init; } = 50;

        /// <summary>A new key frame must be at least this far from the player's previous key frame (metres) ...</summary>
        public float MinTranslationMeters { get; init; } = 0.03f;

        /// <summary>... or rotated at least this much (degrees), otherwise it adds no new information and is skipped.</summary>
        public float MinRotationDegrees { get; init; } = 3f;

        /// <summary>How many of the player's most recent key frames a new key frame is matched against.</summary>
        public int NeighborKeyFrameCount { get; init; } = 3;

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
    }

    /// <summary>A 3D point of the map, triangulated from two or more key frames with known camera poses.</summary>
    public sealed class MapPoint
    {
        public int Id { get; init; }
        public int SubMapId { get; init; }

        /// <summary>Position in the owning sub-map's own coordinate frame (the player's tracking space).</summary>
        public Vector3 Position { get; set; }

        /// <summary>Representative 256-bit ORB descriptor (4 ulongs) used for matching new frames against the map.</summary>
        public ulong[] Descriptor { get; set; } = new ulong[ORBFeatureSet.DescriptorLongs];

        /// <summary>Key frame ID -> index of the feature in that key frame that observes this point.</summary>
        public Dictionary<int, int> Observations { get; } = new();

        /// <summary>Mean unit vector from the point towards the cameras that see it.</summary>
        public Vector3 MeanViewDirection { get; set; }

        public int ReferenceKeyFrameId { get; set; }

        /// <summary>Sequence number of the key frame that created the point (for culling young, unconfirmed points).</summary>
        public int CreatedSequence { get; init; }
    }

    /// <summary>A key frame in the map: camera pose, ORB features and which feature sees which map point.</summary>
    public sealed class MapKeyFrame
    {
        public int Id { get; init; }
        public int SubMapId { get; init; }
        public int PlayerID { get; init; }

        /// <summary>The ID the client gave this key frame.</summary>
        public long ClientKeyFrameID { get; init; }
        public DateTime Timestamp { get; init; }

        /// <summary>Order of insertion within the sub-map, starting at 0.</summary>
        public int Sequence { get; init; }

        /// <summary>Camera pose in the sub-map frame (camera-to-world).</summary>
        public Vector3 Position { get; init; }
        public Quaternion Rotation { get; init; }
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
        public int[] MapPointIds { get; init; } = Array.Empty<int>();

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
    /// (their device's tracking space). Once two sub-maps are matched, one gets a <see cref="ToGlobal"/> transform.
    /// </summary>
    public sealed class SubMap
    {
        public int Id { get; init; }
        public int PlayerID { get; init; }
        public Dictionary<int, MapKeyFrame> KeyFrames { get; } = new();
        public List<MapKeyFrame> OrderedKeyFrames { get; } = new();
        public Dictionary<int, MapPoint> Points { get; } = new();

        /// <summary>IDs of points young enough that they may still be culled.</summary>
        public List<int> RecentPointIds { get; } = new();

        /// <summary>Sub-map frame -> global frame. Identity until the sub-map has been aligned with another.</summary>
        public Matrix4x4 ToGlobal { get; set; } = Matrix4x4.Identity;
        public bool IsAligned { get; set; }
    }

    public sealed record KeyFrameInsertResult(bool Inserted, string Reason, int Features, int NewMapPoints, int TrackedMapPoints);

    // ----- Immutable view of the map for the debug window (already in the global frame) -----

    public sealed record MapSnapshotKeyFrame(int Id, int Sequence, Vector3 Position, Vector3 Forward);

    public sealed record MapSnapshotSubMap(int Id, int PlayerID, bool IsAligned, MapSnapshotKeyFrame[] KeyFrames, Vector3[] Points);

    public sealed record GlobalMapSnapshot(long Version, MapSnapshotSubMap[] SubMaps);
}
