using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// Helpers for the point freshness check (<see cref="PointFreshness"/>): the voxels along the line from a camera to a measured point, short of
    /// the point. (There is no map of empty space any more; only the voxels of one new key frame's rays are carved, and only to see which
    /// older points that key frame saw through.)
    /// </summary>
    public static class FreeSpace
    {
        private const int Offset = 1 << 20, Mask = (1 << 21) - 1;

        private static long Pack(int x, int y, int z) => ((long)((x + Offset) & Mask)) | ((long)((y + Offset) & Mask) << 21) | ((long)((z + Offset) & Mask) << 42);

        private static long Key(Vector3 v, float voxel) => Pack((int)MathF.Floor(v.X / voxel), (int)MathF.Floor(v.Y / voxel), (int)MathF.Floor(v.Z / voxel));

        /// <summary>The key of the voxel (of the given size) that contains <paramref name="position"/>, as <see cref="CarveRays"/> returns them.</summary>
        public static long VoxelKey(Vector3 position, float voxel) => Key(position, voxel);

        /// <summary>The voxels one key frame's rays pass through: from <see cref="GlobalMappingParameters.FreeStartMeters"/> out to <see cref="GlobalMappingParameters.FreeFarnessMeters"/> short of the measured point (or the maximum range).</summary>
        public static HashSet<long> CarveRays(Vector3 origin, Vector3[] ends, float voxel, GlobalMappingParameters p)
        {
            var set = new HashSet<long>();
            foreach (Vector3 end in ends)
            {
                Vector3 d = end - origin;
                float distance = d.Length();
                if (distance < 1e-3f)
                    continue;
                float stop = MathF.Min(distance - p.FreeFarnessMeters, p.FreeMaxRangeMeters);
                if (stop <= p.FreeStartMeters)
                    continue;
                Vector3 direction = d / distance;
                for (float r = p.FreeStartMeters; r <= stop; r += voxel * 0.5f) // half a voxel per step: no voxel the ray crosses is skipped
                    set.Add(Key(origin + direction * r, voxel));
            }
            return set;
        }
    }
}
