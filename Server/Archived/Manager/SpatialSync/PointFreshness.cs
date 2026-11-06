using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// Freshness of the map's points. When a new key frame measures a depth along a ray, everything that lies on that ray well short of the
    /// measured point should have been seen as a surface and was not: a point there is out of date (something moved, or it was a bad measurement)
    /// and loses a life. A point with no lives left is removed. A point the new key frame measures again is fresh and gets its lives back.
    /// </summary>
    public static class PointFreshness
    {
        public readonly record struct Result(int Struck, int Removed, int Refreshed);

        /// <summary>
        /// Apply the new key frame to the points of the other key frames. Their point arrays are replaced (never changed in place), so a reader
        /// that holds an old array is not disturbed..
        /// </summary>
        public static Result Apply(IReadOnlyList<MapKeyFrame> frames, MapKeyFrame newFrame, GlobalMappingParameters p)
        {
            if (!p.PointFreshnessEnabled || newFrame.FreeRayEnds.Length == 0)
                return default;

            float voxel = MathF.Max(0.005f, p.FusedVoxelMeters);
            HashSet<long> seenThrough = FreeSpace.CarveRays(newFrame.Position, newFrame.FreeRayEnds, voxel, p);
            if (seenThrough.Count == 0)
                return default;
            var measured = new HashSet<long>();
            foreach (CloudPoint point in newFrame.Points)
                measured.Add(FreeSpace.VoxelKey(point.Position, voxel));
            // A point is also fresh where a ray of the new frame ends.
            foreach (Vector3 end in newFrame.FreeRayEnds)
                measured.Add(FreeSpace.VoxelKey(end, voxel));

            byte full = (byte)Math.Clamp(p.PointLives, 1, 255);
            int struck = 0, removed = 0, refreshed = 0;
            Parallel.For(0, frames.Count, f =>
            {
                MapKeyFrame frame = frames[f];
                if (ReferenceEquals(frame, newFrame))
                    return;
                CloudPoint[] points = frame.Points;
                CloudPoint[]? result = null;
                int count = 0, s = 0, r = 0, h = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    CloudPoint point = points[i];
                    CloudPoint? next = point;
                    long key = FreeSpace.VoxelKey(point.Position, voxel);
                    if (measured.Contains(key))
                    {
                        if (p.PointRefreshOnReseen && point.Lives < full)
                        {
                            next = point with { Lives = full };
                            h++;
                        }
                    }
                    else if (seenThrough.Contains(key))
                    {
                        s++;
                        if (point.Lives <= 1)
                        {
                            next = null;
                            r++;
                        }
                        else
                            next = point with { Lives = (byte)(point.Lives - 1) };
                    }

                    if (result == null && (next == null || !next.Value.Equals(point)))
                    {
                        result = new CloudPoint[points.Length];
                        Array.Copy(points, result, i);
                        count = i;
                    }
                    if (result != null && next != null)
                        result[count++] = next.Value;
                }
                if (result != null)
                    frame.Points = count == result.Length ? result : result[..count];
                Interlocked.Add(ref struck, s);
                Interlocked.Add(ref removed, r);
                Interlocked.Add(ref refreshed, h);
            });
            return new Result(struck, removed, refreshed);
        }
    }
}
