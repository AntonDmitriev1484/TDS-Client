using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// What a sub-map has actually looked at, as a top-down footprint (cells of <see cref="Cell"/> metres, each with the height range seen). Every key
    /// frame contributes the bounding polygon (convex hull) of its camera position and the points it measured, so the space between the camera and the
    /// surfaces counts as viewed, and the parts of a room nobody looked at do not. Two maps are only compared where both looked: a point of one map
    /// is only judged against the other map if it lies in the other's viewed region. Built from the key frames, so it needs no per-voxel work and
    /// grows with the area covered, not with the volume.
    /// </summary>
    public sealed class ViewedRegion
    {
        public const float Cell = 0.25f;

        private const float VerticalMargin = 0.3f;
        private const int MaxPointsPerFrame = 600;
        private const float KeepNearestFraction = 0.98f;

        private readonly Dictionary<long, (float MinY, float MaxY)> _cells;

        private ViewedRegion(Dictionary<long, (float MinY, float MaxY)> cells) => _cells = cells;

        public int CellCount => _cells.Count;

        public float AreaSquareMeters => _cells.Count * Cell * Cell;

        private static long Key(int ix, int iz) => ((long)ix << 32) | (uint)iz;

        private static int CellOf(float v) => (int)MathF.Floor(v / Cell);

        /// <summary>Whether the top-down position (x, z) is inside the viewed footprint.</summary>
        public bool ContainsXZ(float x, float z) => _cells.ContainsKey(Key(CellOf(x), CellOf(z)));

        /// <summary>Whether <paramref name="position"/> is inside the viewed footprint and within the height range seen there (plus a margin).</summary>
        public bool Contains(Vector3 position)
        {
            if (!_cells.TryGetValue(Key(CellOf(position.X), CellOf(position.Z)), out var range))
                return false;
            return position.Y >= range.MinY - VerticalMargin && position.Y <= range.MaxY + VerticalMargin;
        }

        /// <summary>The viewed region of the given key frames (key frames without a pose or without points do not count).</summary>
        public static ViewedRegion Build(IReadOnlyList<MapKeyFrame> frames)
        {
            var cells = new Dictionary<long, (float MinY, float MaxY)>();
            var footprint = new List<Vector2>();
            foreach (MapKeyFrame frame in frames)
            {
                if (frame.Points.Length == 0 || frame.Rotation.LengthSquared() < 0.5f)
                    continue;

                // Without the farthest few points (a stray far match would stretch the footprint across the room), thinned for speed.
                Vector3 camera = frame.Position;
                int step = Math.Max(1, frame.Points.Length / MaxPointsPerFrame);
                var sampled = new List<(Vector3 Position, float Distance)>(Math.Min(frame.Points.Length, MaxPointsPerFrame) + 1);
                for (int i = 0; i < frame.Points.Length; i += step)
                {
                    Vector3 position = frame.Points[i].Position;
                    float dx = position.X - camera.X, dz = position.Z - camera.Z;
                    sampled.Add((position, dx * dx + dz * dz));
                }
                sampled.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                int keep = Math.Max(1, (int)(sampled.Count * KeepNearestFraction));

                float minY = camera.Y, maxY = camera.Y;
                footprint.Clear();
                footprint.Add(new Vector2(camera.X, camera.Z));
                for (int i = 0; i < keep; i++)
                {
                    Vector3 position = sampled[i].Position;
                    footprint.Add(new Vector2(position.X, position.Z));
                    minY = MathF.Min(minY, position.Y);
                    maxY = MathF.Max(maxY, position.Y);
                }

                AddPolygon(cells, ConvexHull(footprint), minY, maxY);
            }
            return new ViewedRegion(cells);
        }

        private static void AddPolygon(Dictionary<long, (float MinY, float MaxY)> cells, List<Vector2> hull, float minY, float maxY)
        {
            void Mark(int ix, int iz)
            {
                long key = Key(ix, iz);
                cells[key] = cells.TryGetValue(key, out var range) ? (MathF.Min(range.MinY, minY), MathF.Max(range.MaxY, maxY)) : (minY, maxY);
            }

            foreach (Vector2 vertex in hull)
                Mark(CellOf(vertex.X), CellOf(vertex.Y));
            if (hull.Count < 3)
                return;

            float minX = hull.Min(v => v.X), maxX = hull.Max(v => v.X), minZ = hull.Min(v => v.Y), maxZ = hull.Max(v => v.Y);
            for (int iz = CellOf(minZ); iz <= CellOf(maxZ); iz++)
                for (int ix = CellOf(minX); ix <= CellOf(maxX); ix++)
                {
                    var centre = new Vector2((ix + 0.5f) * Cell, (iz + 0.5f) * Cell);
                    bool inside = true;
                    for (int k = 0; k < hull.Count && inside; k++)
                    {
                        Vector2 a = hull[k], b = hull[(k + 1) % hull.Count];
                        if ((b.X - a.X) * (centre.Y - a.Y) - (b.Y - a.Y) * (centre.X - a.X) < 0f) // hull is counter-clockwise: inside is on the left of every edge
                            inside = false;
                    }
                    if (inside)
                        Mark(ix, iz);
                }
        }

        /// <summary>Convex hull (counter-clockwise) of the points, by Andrew's monotone chain.</summary>
        private static List<Vector2> ConvexHull(List<Vector2> points)
        {
            var sorted = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (sorted.Count < 3)
                return sorted;

            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var hull = new List<Vector2>(sorted.Count * 2);
            foreach (Vector2 q in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[^2], hull[^1], q) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            int lower = hull.Count + 1;
            for (int i = sorted.Count - 2; i >= 0; i--)
            {
                Vector2 q = sorted[i];
                while (hull.Count >= lower && Cross(hull[^2], hull[^1], q) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }
    }
}
