using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// The points of a sub-map fused to one (averaged) point per small cube, with the brightness of each. Immutable once built, so it can be
    /// used on another thread while the map keeps changing.
    /// </summary>
    public sealed class FusedCloud
    {
        public Vector3[] Positions { get; }
        public float[] Gray { get; }
        public int[] Players { get; }
        public Vector3 Centroid { get; }

        /// <summary>How many measurements were averaged into each point (about one per key frame that saw that cube): the density of the map there.</summary>
        public int[] Counts { get; }

        /// <summary>The size of the cubes the points were fused in.</summary>
        public float Voxel { get; }

        /// <summary>The height of the floor: a low percentile of the points' heights (y).</summary>
        public float FloorY { get; }

        public int Count => Positions.Length;

        /// <summary>The mean position of the cameras that saw the points (the centroid of the points if it is not known): the direction a surface faces is chosen by it.</summary>
        public Vector3 ViewCenter { get; private set; }

        /// <summary>What the key frames behind this cloud looked at (null for a cloud made from loose points, such as an anchor's): maps are only compared where both looked.</summary>
        public ViewedRegion? Viewed { get; private set; }

        private FusedCloud(Vector3[] positions, float[] gray, int[] players, int[] counts, float voxel)
        {
            Positions = positions;
            Gray = gray;
            Players = players;
            Counts = counts;
            Voxel = voxel;

            if (positions.Length > 0)
            {
                Vector3 sum = Vector3.Zero;
                foreach (Vector3 p in positions)
                    sum += p;
                Centroid = sum / positions.Length;

                var heights = new float[positions.Length];
                for (int i = 0; i < heights.Length; i++)
                    heights[i] = positions[i].Y;
                Array.Sort(heights);
                FloorY = heights[(int)(heights.Length * 0.03f)];
            }
        }

        private struct Accumulator
        {
            public Vector3 Sum;
            public float GraySum;
            public int Count;
            public int Player;
        }

        /// <summary>
        /// Fuse the points of the key frames: one point per cube of <paramref name="voxel"/> meters, at the mean position and brightness. With
        /// <paramref name="minKeyFrames"/> above 1, only points in a region (cubes of twice that size, so measurement noise does not split
        /// the support) where that many different key frames measured something are kept, which leaves out points seen once.
        /// </summary>
        public static FusedCloud Build(IEnumerable<MapKeyFrame> keyFrames, float voxel, int minKeyFrames = 1)
        {
            voxel = MathF.Max(0.005f, voxel);
            var frames = keyFrames as IReadOnlyList<MapKeyFrame> ?? keyFrames.ToList();

            Dictionary<(int, int, int), (int Last, int Count)>? support = null;
            if (minKeyFrames > 1)
            {
                float coarse = voxel * 2f;
                support = new Dictionary<(int, int, int), (int, int)>();
                for (int f = 0; f < frames.Count; f++)
                {
                    foreach (CloudPoint point in frames[f].Points)
                    {
                        var key = ((int)MathF.Floor(point.Position.X / coarse), (int)MathF.Floor(point.Position.Y / coarse), (int)MathF.Floor(point.Position.Z / coarse));
                        support.TryGetValue(key, out var seen);
                        if (seen.Count == 0 || seen.Last != f)
                            support[key] = (f, seen.Count + 1);
                    }
                }
            }

            var cells = new Dictionary<(int, int, int), Accumulator>();
            foreach (MapKeyFrame keyFrame in frames)
            {
                foreach (CloudPoint point in keyFrame.Points)
                {
                    if (support != null)
                    {
                        float coarse = voxel * 2f;
                        var coarseKey = ((int)MathF.Floor(point.Position.X / coarse), (int)MathF.Floor(point.Position.Y / coarse), (int)MathF.Floor(point.Position.Z / coarse));
                        if (support[coarseKey].Count < minKeyFrames)
                            continue;
                    }
                    var key = ((int)MathF.Floor(point.Position.X / voxel), (int)MathF.Floor(point.Position.Y / voxel), (int)MathF.Floor(point.Position.Z / voxel));
                    ref Accumulator cell = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(cells, key, out bool exists);
                    if (!exists)
                        cell.Player = point.PlayerID;
                    cell.Sum += point.Position;
                    cell.GraySum += point.Gray;
                    cell.Count++;
                }
            }

            var positions = new Vector3[cells.Count];
            var gray = new float[cells.Count];
            var players = new int[cells.Count];
            var counts = new int[cells.Count];
            int i = 0;
            foreach (Accumulator cell in cells.Values)
            {
                positions[i] = cell.Sum / cell.Count;
                gray[i] = cell.GraySum / cell.Count;
                players[i] = cell.Player;
                counts[i] = cell.Count;
                i++;
            }
            var cloud = new FusedCloud(positions, gray, players, counts, voxel);

            // Where the cameras were (key frames without a pose, as in FromPoints, do not count).
            Vector3 cameras = Vector3.Zero;
            int posed = 0;
            foreach (MapKeyFrame keyFrame in frames)
                if (keyFrame.Points.Length > 0 && keyFrame.Rotation.LengthSquared() > 0.5f)
                {
                    cameras += keyFrame.Position;
                    posed++;
                }
            cloud.ViewCenter = posed > 0 ? cameras / posed : cloud.Centroid;
            cloud.Viewed = ViewedRegion.Build(frames);
            return cloud;
        }

        /// <summary>The points of this cloud that satisfy <paramref name="keep"/>, moved by <paramref name="transform"/>, as a cloud of their own (without a viewed region).</summary>
        public FusedCloud Subset(Func<Vector3, bool> keep, RigidTransform transform)
        {
            var indices = new List<int>();
            for (int i = 0; i < Positions.Length; i++)
                if (keep(Positions[i]))
                    indices.Add(i);
            var positions = new Vector3[indices.Count];
            var gray = new float[indices.Count];
            var players = new int[indices.Count];
            var counts = new int[indices.Count];
            for (int k = 0; k < indices.Count; k++)
            {
                positions[k] = transform.Apply(Positions[indices[k]]);
                gray[k] = Gray[indices[k]];
                players[k] = Players[indices[k]];
                counts[k] = Counts[indices[k]];
            }
            var cloud = new FusedCloud(positions, gray, players, counts, Voxel);
            cloud.ViewCenter = transform.Apply(ViewCenter);
            return cloud;
        }

        /// <summary>A cloud of the given points (for the points of a few key frames, say).</summary>
        public static FusedCloud FromPoints(IEnumerable<CloudPoint> points, float voxel)
        {
            var frame = new MapKeyFrame { Points = points.ToArray() };
            return Build(new[] { frame }, voxel);
        }
    }

    /// <summary>How well one cloud, moved by a transform, agrees with another.</summary>
    public readonly record struct AlignmentScore(int Region, int Matched, float InlierFraction, float GrayAgreement)
    {
        public override string ToString() => $"{Matched} of {Region} points within range agree ({InlierFraction:P0}), brightness correlation {GrayAgreement:0.00}";
    }

    public sealed record AlignmentResult(RigidTransform Transform, AlignmentScore Score, bool Passed, string Summary, bool AnchorTrusted = false);

    /// <summary>
    /// Alignment of two point clouds that differ by a turn about the vertical axis and a translation (two gravity-aligned, metric tracking spaces).
    /// A coarse search finds candidate poses by correlating top-down views (the shape of the vertical structures and their brightness) for many
    /// yaw angles with the FFT; the best candidates are refined with ICP and checked point by point.
    /// </summary>
    public static class CloudAlignment
    {
        // ------------------------------------------------------------------ point grid

        private sealed class PointGrid
        {
            private readonly Dictionary<(int, int, int), List<int>> _cells = new();
            private readonly Vector3[] _points;
            private readonly float _cell;

            public PointGrid(Vector3[] points, float cell)
            {
                _points = points;
                _cell = cell;
                for (int i = 0; i < points.Length; i++)
                {
                    var key = Key(points[i]);
                    if (!_cells.TryGetValue(key, out List<int>? list))
                        _cells[key] = list = new List<int>(4);
                    list.Add(i);
                }
            }

            private (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / _cell), (int)MathF.Floor(p.Y / _cell), (int)MathF.Floor(p.Z / _cell));

            /// <summary>The index of the closest point within <paramref name="maxDistance"/> (which must not exceed the grid's cell size), or -1.</summary>
            public int Nearest(Vector3 p, float maxDistance, out float distance)
            {
                int best = -1;
                float bestSquared = maxDistance * maxDistance;
                var (cx, cy, cz) = Key(p);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? list))
                                foreach (int i in list)
                                {
                                    float d2 = Vector3.DistanceSquared(p, _points[i]);
                                    if (d2 <= bestSquared)
                                    {
                                        bestSquared = d2;
                                        best = i;
                                    }
                                }
                distance = MathF.Sqrt(bestSquared);
                return best;
            }

            public bool AnyWithin(Vector3 p) => _cells.ContainsKey(Key(p)) || HasNeighbour(Key(p));

            private bool HasNeighbour((int, int, int) key)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (_cells.ContainsKey((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz)))
                                return true;
                return false;
            }
        }

        // ------------------------------------------------------------------ the check

        /// <summary>
        /// How well <paramref name="source"/>, moved by <paramref name="transform"/>, agrees with <paramref name="target"/>: the source points inside
        /// the region the target looked at (so not the parts the target never saw) that have a target point within <see cref="GlobalMappingParameters.MergeInlierMeters"/>,
        /// and how well the brightness of those points correlates between the two maps.
        /// </summary>
        public static AlignmentScore Evaluate(FusedCloud target, FusedCloud source, RigidTransform transform, GlobalMappingParameters p)
        {
            var targetGrid = new PointGrid(target.Positions, MathF.Max(p.MergeInlierMeters, 0.02f));
            var regionGrid = new PointGrid(target.Positions, MathF.Max(p.MergeRegionRadiusMeters, 0.1f));
            return Evaluate(target, targetGrid, regionGrid, source, transform, p);
        }

        private static AlignmentScore Evaluate(FusedCloud target, PointGrid targetGrid, PointGrid regionGrid, FusedCloud source, RigidTransform transform, GlobalMappingParameters p, int maxPoints = 0)
        {
            int step = Math.Max(1, source.Count / Math.Max(1, maxPoints > 0 ? maxPoints : p.IcpMaxPoints));
            int region = 0;
            ViewedRegion? seen = target.Viewed; // judged only where the target looked; without one, near the target's points
            var sourceGray = new List<float>();
            var targetGray = new List<float>();
            for (int i = 0; i < source.Count; i += step)
            {
                Vector3 moved = transform.Apply(source.Positions[i]);
                if (seen != null ? !seen.Contains(moved) : !regionGrid.AnyWithin(moved))
                    continue;
                region++;
                int match = targetGrid.Nearest(moved, p.MergeInlierMeters, out _);
                if (match >= 0)
                {
                    sourceGray.Add(source.Gray[i]);
                    targetGray.Add(target.Gray[match]);
                }
            }

            if (region == 0 || sourceGray.Count == 0)
                return new AlignmentScore(region * step, 0, 0f, 0f);

            return new AlignmentScore(region * step, sourceGray.Count * step, sourceGray.Count / (float)region, Correlation(sourceGray, targetGray));
        }

        /// <summary>
        /// Pearson correlation of the brightness of matched points (0 if they are unrelated or inversely related). Unlike a difference
        /// threshold this does not depend on the two maps' exposure, and unrelated surfaces score near 0 however few grey levels they have.
        /// </summary>
        private static float Correlation(List<float> a, List<float> b)
        {
            int n = a.Count;
            if (n < 10)
                return 0f;
            double meanA = a.Average(), meanB = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - meanA, db = b[i] - meanB;
                sab += da * db;
                saa += da * da;
                sbb += db * db;
            }
            if (saa < 1e-6 || sbb < 1e-6)
                return 0f;
            return (float)Math.Clamp(sab / Math.Sqrt(saa * sbb), 0.0, 1.0);
        }

        private static bool Passes(AlignmentScore s, GlobalMappingParameters p) =>
            s.Matched >= p.MinMergeMatchedPoints && s.InlierFraction >= p.MinMergeInlierFraction && s.GrayAgreement >= p.MinMergeGrayAgreement;

        // ------------------------------------------------------------------ ICP

        /// <summary>
        /// Refine <paramref name="start"/> (the transform from the source's frame into the target's) with ICP restricted to yaw + translation: each
        /// step matches every (thinned) source point to the closest target point within a distance limit that shrinks, and fits the closest part of
        /// the matches. Returns null if the clouds stop matching.
        /// </summary>
        public static RigidTransform? Register(FusedCloud target, FusedCloud source, RigidTransform start, GlobalMappingParameters p, out string summary,
            float startDistance = -1f, float endDistance = -1f, int iterationCount = -1)
        {
            float startMeters = startDistance > 0f ? startDistance : p.IcpStartDistanceMeters;
            float endMeters = endDistance > 0f ? endDistance : p.IcpEndDistanceMeters;
            // Only the target near the source matters.
            Vector3 low = new(float.MaxValue), high = new(float.MinValue);
            int sourceStep = Math.Max(1, source.Count / Math.Max(1, p.IcpMaxPoints));
            var sourcePoints = new List<Vector3>();
            for (int i = 0; i < source.Count; i += sourceStep)
            {
                Vector3 moved = start.Apply(source.Positions[i]);
                low = Vector3.Min(low, moved);
                high = Vector3.Max(high, moved);
                sourcePoints.Add(source.Positions[i]);
            }
            float margin = startMeters + 0.5f;
            low -= new Vector3(margin);
            high += new Vector3(margin);
            var targetPoints = new List<Vector3>();
            foreach (Vector3 t in target.Positions)
                if (t.X >= low.X && t.Y >= low.Y && t.Z >= low.Z && t.X <= high.X && t.Y <= high.Y && t.Z <= high.Z)
                    targetPoints.Add(t);

            if (targetPoints.Count < 200 || sourcePoints.Count < 200)
            {
                summary = $"too few points to register ({targetPoints.Count} in the target area, {sourcePoints.Count} in the source)";
                return null;
            }

            float cell = MathF.Max(startMeters, 0.05f);
            var grid = new PointGrid(targetPoints.ToArray(), cell);
            RigidTransform current = start;
            var matchedSource = new List<Vector3>();
            var matchedTarget = new List<Vector3>();
            var distances = new List<float>();
            int iterations = Math.Max(1, iterationCount > 0 ? iterationCount : p.IcpIterations);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                float t = iterations > 1 ? iteration / (float)(iterations - 1) : 1f;
                float maxDistance = MathF.Min(cell, startMeters + t * (endMeters - startMeters));

                matchedSource.Clear();
                matchedTarget.Clear();
                distances.Clear();
                foreach (Vector3 s in sourcePoints)
                {
                    int match = grid.Nearest(current.Apply(s), maxDistance, out float distance);
                    if (match >= 0)
                    {
                        matchedSource.Add(s);
                        matchedTarget.Add(targetPoints[match]);
                        distances.Add(distance);
                    }
                }
                if (matchedSource.Count < 100)
                {
                    summary = $"the clouds stopped matching at iteration {iteration + 1} ({matchedSource.Count} matches within {maxDistance:0.00} m)";
                    return null;
                }

                float[] sorted = distances.ToArray();
                Array.Sort(sorted);
                float limit = sorted[Math.Clamp((int)(sorted.Length * p.IcpKeepFraction), 1, sorted.Length) - 1];
                var keptSource = new List<Vector3>();
                var keptTarget = new List<Vector3>();
                for (int i = 0; i < matchedSource.Count; i++)
                {
                    if (distances[i] <= limit)
                    {
                        keptSource.Add(matchedSource[i]);
                        keptTarget.Add(matchedTarget[i]);
                    }
                }
                if (!TrySolveYawTranslation(keptSource, keptTarget, out RigidTransform next))
                {
                    summary = $"the fit failed at iteration {iteration + 1}";
                    return null;
                }
                current = next;
            }

            summary = $"ICP on {sourcePoints.Count} / {targetPoints.Count} points";
            return current;
        }

        // ------------------------------------------------------------------ pose correction of a single key frame

        /// <summary>What lining a key frame up with the map did: the correction, and how well the points fit before and after it.</summary>
        public sealed record PoseCorrectionResult(RigidTransform Transform, float BeforeMeanDistance, float AfterMeanDistance, float BeforeFraction, float AfterFraction, int Matched);

        /// <summary>
        /// Line the points of a new key frame (already placed with the pose the headset reported) up with the map points around them: ICP restricted to
        /// yaw + translation with a small search distance. Returns the correction to apply to the key frame (null if there was nothing to register).
        /// The fit before and after is the mean distance to the nearest map point (within 10 cm) and the fraction of points that have one.
        /// </summary>
        public static PoseCorrectionResult? EstimatePoseCorrection(FusedCloud reference, FusedCloud frame, GlobalMappingParameters p, out string summary)
        {
            RigidTransform? registered = Register(reference, frame, RigidTransform.Identity, p, out summary, p.PoseCorrectionStartMeters, p.PoseCorrectionEndMeters, p.PoseCorrectionIterations);
            if (registered == null)
                return null;

            var grid = new PointGrid(reference.Positions, 0.10f);
            var (beforeFraction, beforeMean, _) = Fit(grid, frame, RigidTransform.Identity, 0.10f);
            var (afterFraction, afterMean, matched) = Fit(grid, frame, registered.Value, 0.10f);
            return new PoseCorrectionResult(registered.Value, beforeMean, afterMean, beforeFraction, afterFraction, matched);
        }

        private static (float Fraction, float MeanDistance, int Matched) Fit(PointGrid grid, FusedCloud frame, RigidTransform transform, float maxDistance)
        {
            int step = Math.Max(1, frame.Count / 4000);
            int total = 0, matched = 0;
            double sum = 0;
            for (int i = 0; i < frame.Count; i += step)
            {
                total++;
                if (grid.Nearest(transform.Apply(frame.Positions[i]), maxDistance, out float distance) >= 0)
                {
                    matched++;
                    sum += distance;
                }
            }
            return (total == 0 ? 0f : matched / (float)total, matched == 0 ? maxDistance : (float)(sum / matched), matched * step);
        }

        /// <summary>The yaw and translation that bring the <paramref name="source"/> points onto the <paramref name="target"/> points, least squares, in closed form.</summary>
        public static bool TrySolveYawTranslation(IReadOnlyList<Vector3> source, IReadOnlyList<Vector3> target, out RigidTransform transform)
        {
            transform = RigidTransform.Identity;
            int n = source.Count;
            if (n < 2)
                return false;

            double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
            for (int i = 0; i < n; i++)
            {
                ax += source[i].X; ay += source[i].Y; az += source[i].Z;
                bx += target[i].X; by += target[i].Y; bz += target[i].Z;
            }
            ax /= n; ay /= n; az /= n; bx /= n; by /= n; bz /= n;

            // Rotation about +Y by t maps (x, z) to (x cos t + z sin t, -x sin t + z cos t); maximise the sum of b . R a.
            double c = 0, s = 0;
            for (int i = 0; i < n; i++)
            {
                double dax = source[i].X - ax, daz = source[i].Z - az, dbx = target[i].X - bx, dbz = target[i].Z - bz;
                c += dax * dbx + daz * dbz;
                s += daz * dbx - dax * dbz;
            }
            if (c * c + s * s < 1e-12)
                return false;

            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.Atan2(s, c));
            Vector3 rotatedCentroid = Vector3.Transform(new Vector3((float)ax, (float)ay, (float)az), rotation);
            transform = new RigidTransform(rotation, new Vector3((float)bx, (float)by, (float)bz) - rotatedCentroid);
            return !float.IsNaN(transform.Translation.X);
        }

        // ------------------------------------------------------------------ the whole alignment

        /// <summary>
        /// Align <paramref name="source"/> to <paramref name="target"/>: the transform from the source's frame into the target's, with how well the
        /// two agree there. <see cref="AlignmentResult.Passed"/> says whether the agreement is good enough to trust it.
        /// </summary>
        public static AlignmentResult? Align(FusedCloud target, FusedCloud source, GlobalMappingParameters p, out string log, IReadOnlyList<FusedCloud>? targetSnapshots = null,
                                                   IReadOnlyList<AnchorCandidate>? anchorCandidates = null)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var candidates = CoarseSearch(target, source, p, out string coarse);
            log = coarse + $" [coarse {timer.ElapsedMilliseconds} ms]";

            var targetGrid = new PointGrid(target.Positions, MathF.Max(p.MergeInlierMeters, 0.02f));
            var regionGrid = new PointGrid(target.Positions, MathF.Max(p.MergeRegionRadiusMeters, 0.1f));

            // Rank the coarse candidates by how well the clouds agree as they are, and refine the best few.
            var ranked = candidates
                .Select(c => (Transform: c, Score: Evaluate(target, targetGrid, regionGrid, source, c, p), Label: "top-down"))
                .OrderByDescending(c => c.Score.Matched)
                .Take(Math.Max(1, p.MergeRefinedCandidates))
                .ToList();

            // Placements from the snapshots: each snapshot (a single view of the target map) is looked for in the source map, and the placements that
            // several different snapshots agree on are tried as well. A snapshot is a view of the target, so it is found as a transform from the
            // target's frame into the source's; the inverse is the one wanted here. A placement that is the same as one of the top-down ones is not
            // added again, but keeps its votes.
            var support = new Dictionary<int, (int Votes, RigidTransform Placement)>();
            var anchorSupport = new Dictionary<int, (int Votes, RigidTransform Placement)>();
            if (targetSnapshots != null && targetSnapshots.Count > 0 && p.MergeSnapshotCandidates > 0)
            {
                List<SnapshotCluster> clusters = LocateSnapshots(source, targetSnapshots, source.FloorY - target.FloorY, p, out string snapshotLog);
                log += $"\n    {snapshotLog} [snapshots {timer.ElapsedMilliseconds} ms]";
                int added = 0;
                foreach (SnapshotCluster cluster in clusters)
                {
                    int duplicate = ranked.FindIndex(r => PlacementsAgree(r.Transform, cluster.Placement, source.Centroid, p));
                    if (duplicate >= 0)
                    {
                        support[duplicate] = (cluster.Votes, cluster.Placement);
                        continue;
                    }
                    if (added >= p.MergeSnapshotCandidates)
                        continue;
                    ranked.Add((cluster.Placement, Evaluate(target, targetGrid, regionGrid, source, cluster.Placement, p), $"snapshots: {cluster.Votes} of {targetSnapshots.Count} agree"));
                    support[ranked.Count - 1] = (cluster.Votes, cluster.Placement);
                    added++;
                }
            }

            // Placements the anchors agree on (found by the caller: the anchors of the two maps looked for in each other's points).
            if (anchorCandidates != null)
                foreach (AnchorCandidate c in anchorCandidates)
                {
                    int duplicate = ranked.FindIndex(r => PlacementsAgree(r.Transform, c.Transform, source.Centroid, p));
                    if (duplicate >= 0)
                    {
                        anchorSupport[duplicate] = (c.Votes, c.Transform);
                        continue;
                    }
                    ranked.Add((c.Transform, Evaluate(target, targetGrid, regionGrid, source, c.Transform, p), $"anchors: {c.Votes} agree"));
                    anchorSupport[ranked.Count - 1] = (c.Votes, c.Transform);
                }

            if (ranked.Count == 0)
                return null;

            // Step 1: ICP from every start (in parallel). The vertical shift decides the height; if the floor-based one is wrong (no floor
            // in view) the plain "same height" start is tried too.
            var jobs = new List<(int Candidate, RigidTransform Start)>();
            for (int c = 0; c < ranked.Count; c++)
            {
                jobs.Add((c, ranked[c].Transform));
                if (MathF.Abs(target.FloorY - source.FloorY) > 0.05f)
                    jobs.Add((c, new RigidTransform(ranked[c].Transform.Rotation, new Vector3(ranked[c].Transform.Translation.X, 0f, ranked[c].Transform.Translation.Z))));
            }

            var icpResults = new (RigidTransform Final, bool Refined, string Note, double Quality)[jobs.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
            Parallel.For(0, jobs.Count, options, i =>
            {
                RigidTransform? refined = Register(target, source, jobs[i].Start, p, out string icp);
                RigidTransform final = refined ?? jobs[i].Start;
                AlignmentScore quick = Evaluate(target, targetGrid, regionGrid, source, final, p, 6000);
                icpResults[i] = (final, refined != null, icp, quick.Matched * quick.InlierFraction);
            });
            log += $" [icp {timer.ElapsedMilliseconds} ms]";

            // Step 2: per candidate, the better of its starts is refined by brightness (in parallel), then checked.
            var chosen = Enumerable.Range(0, ranked.Count)
                .Select(c => Enumerable.Range(0, jobs.Count).Where(i => jobs[i].Candidate == c).OrderByDescending(i => icpResults[i].Quality).First())
                .ToList();
            var finals = new (RigidTransform Final, AlignmentScore Score)[chosen.Count];
            Parallel.For(0, chosen.Count, options, k =>
            {
                RigidTransform final = icpResults[chosen[k]].Final;
                if (p.MergeTextureSearchMeters > 0f)
                    final = TextureRefine(target, targetGrid, regionGrid, source, final, p);
                finals[k] = (final, Evaluate(target, targetGrid, regionGrid, source, final, p));
            });
            log += $" [texture {timer.ElapsedMilliseconds} ms]";

            // The best placement; one that passes beats one that does not. A placement passes if the whole-map check says so, or if enough different
            // snapshots agree on it (and the refined placement still is that one): a few clean single views agreeing is strong evidence even when
            // drift or thick, smeared points make the comparison of the whole maps worse.
            AlignmentResult? best = null;
            double bestQuality = -1;
            bool bestPassed = false;
            for (int k = 0; k < chosen.Count; k++)
            {
                var (final, score) = finals[k];
                double quality = score.Matched * score.InlierFraction * score.GrayAgreement;
                int candidate = jobs[chosen[k]].Candidate;
                support.TryGetValue(candidate, out var votes);
                bool trusted = p.MergeSnapshotMinVotes > 0 && votes.Votes >= p.MergeSnapshotMinVotes && PlacementsAgree(final, votes.Placement, source.Centroid, p);
                anchorSupport.TryGetValue(candidate, out var anchorVotes);
                bool anchorTrusted = p.AnchorTrustAgree > 0 && anchorVotes.Votes >= p.AnchorTrustAgree && PlacementsAgree(final, anchorVotes.Placement, source.Centroid, p);
                bool passed = Passes(score, p) || trusted || anchorTrusted;
                string voteNote = (votes.Votes > 0 ? $", {votes.Votes} snapshots agree{(trusted ? " (trusted)" : "")}" : "")
                    + (anchorVotes.Votes > 0 ? $", {anchorVotes.Votes} anchors agree{(anchorTrusted ? " (trusted)" : "")}" : "");
                log += $"\n    candidate [{ranked[candidate].Label}] {ranked[candidate].Transform} -> {(icpResults[chosen[k]].Refined ? "refined to " + final : "not refined (" + icpResults[chosen[k]].Note + ")")}: {score}{voteNote}";
                if ((passed && !bestPassed) || (passed == bestPassed && quality > bestQuality))
                {
                    bestQuality = quality;
                    bestPassed = passed;
                    best = new AlignmentResult(final, score, passed, $"{final}; {score}{voteNote}", anchorTrusted);
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ snapshots

        private sealed record SnapshotCluster(RigidTransform Placement, int Votes, double Weight);

        private readonly record struct SnapshotVote(int Snapshot, RigidTransform Placement, double Quality);

        /// <summary>Whether two placements put things in about the same place: within the agreement distance at <paramref name="reference"/>, and the same yaw.</summary>
        internal static bool PlacementsAgree(RigidTransform a, RigidTransform b, Vector3 reference, GlobalMappingParameters p)
        {
            float yawDifference = MathF.Abs(MathF.IEEERemainder(a.YawDegrees - b.YawDegrees, 360f));
            return Vector3.Distance(a.Apply(reference), b.Apply(reference)) <= p.MergeSnapshotAgreeMeters && yawDifference <= p.MergeSnapshotAgreeDegrees;
        }

        /// <summary>
        /// Look for each snapshot (the points of one or two key frames, in the frame of their own map) in <paramref name="other"/>: coarse search for
        /// the placement, then ICP and the brightness refinement, then the usual check on just that view. A placement that holds up is a vote.
        /// Votes for about the same placement are grouped. The placements returned are the inverse of what was found (from the other map's frame
        /// into the snapshots'), most agreeing snapshots first.
        /// </summary>
        /// <param name="verticalShift">The height difference of the two maps' floors (other minus snapshots' map); a snapshot cannot tell where the floor is by itself.</param>
        private static List<SnapshotCluster> LocateSnapshots(FusedCloud other, IReadOnlyList<FusedCloud> snapshots, float verticalShift, GlobalMappingParameters p, out string log)
        {
            var otherGrid = new PointGrid(other.Positions, MathF.Max(p.MergeInlierMeters, 0.02f));
            var otherRegion = new PointGrid(other.Positions, MathF.Max(p.MergeRegionRadiusMeters, 0.1f));
            var votes = new List<SnapshotVote>[snapshots.Count];
            var notes = new string[snapshots.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

            Parallel.For(0, snapshots.Count, options, s =>
            {
                votes[s] = new List<SnapshotVote>();
                FusedCloud snapshot = snapshots[s];
                if (snapshot.Count < 200)
                {
                    notes[s] = $"snapshot {s + 1}: only {snapshot.Count} points";
                    return;
                }

                // Starts from the coarse search, each with the floor-based height and with the same height; the two best by agreement are refined.
                var starts = new List<RigidTransform>();
                foreach (RigidTransform t in CoarseSearch(other, snapshot, p, out _))
                {
                    starts.Add(new RigidTransform(t.Rotation, new Vector3(t.Translation.X, verticalShift, t.Translation.Z)));
                    if (MathF.Abs(verticalShift) > 0.05f)
                        starts.Add(new RigidTransform(t.Rotation, new Vector3(t.Translation.X, 0f, t.Translation.Z)));
                }
                var best = starts.Select(t => (Start: t, Score: Evaluate(other, otherGrid, otherRegion, snapshot, t, p, 3000)))
                    .OrderByDescending(e => e.Score.Matched).Take(2).ToList();

                // ICP from both; only the one that agrees better afterwards gets the (slower) brightness refinement.
                RigidTransform? chosen = null;
                double chosenQuality = -1;
                foreach (var (start, _) in best)
                {
                    RigidTransform final = Register(other, snapshot, start, p, out _) ?? start;
                    AlignmentScore quick = Evaluate(other, otherGrid, otherRegion, snapshot, final, p, 3000);
                    double quickQuality = quick.Matched * quick.InlierFraction;
                    if (quickQuality > chosenQuality)
                    {
                        chosenQuality = quickQuality;
                        chosen = final;
                    }
                }

                string result = "no placement";
                if (chosen != null)
                {
                    RigidTransform final = chosen.Value;
                    if (p.MergeTextureSearchMeters > 0f)
                        final = TextureRefine(other, otherGrid, otherRegion, snapshot, final, p);
                    AlignmentScore score = Evaluate(other, otherGrid, otherRegion, snapshot, final, p);
                    bool vote = score.Matched >= p.MergeSnapshotMinMatched && score.InlierFraction >= p.MergeSnapshotMinInlierFraction && score.GrayAgreement >= p.MinMergeGrayAgreement;
                    if (vote)
                        votes[s].Add(new SnapshotVote(s, final.Inverse(), score.Matched * score.InlierFraction * score.GrayAgreement));
                    result = $"{score}{(vote ? "" : " (no vote)")}";
                }
                notes[s] = $"snapshot {s + 1} ({snapshot.Count} points): {result}";
            });

            // Group the votes: the strongest first, each joining the first group whose placement it agrees with.
            Vector3 reference = other.Centroid;
            var groups = new List<(SnapshotVote Lead, HashSet<int> Snapshots, double Weight)>();
            foreach (SnapshotVote vote in votes.SelectMany(v => v).OrderByDescending(v => v.Quality))
            {
                int g = groups.FindIndex(group => PlacementsAgree(group.Lead.Placement, vote.Placement, reference, p));
                if (g < 0)
                    groups.Add((vote, new HashSet<int> { vote.Snapshot }, vote.Quality));
                else
                {
                    groups[g].Snapshots.Add(vote.Snapshot);
                    groups[g] = (groups[g].Lead, groups[g].Snapshots, groups[g].Weight + vote.Quality);
                }
            }
            var clusters = groups.Select(g => new SnapshotCluster(g.Lead.Placement, g.Snapshots.Count, g.Weight))
                .OrderByDescending(c => c.Votes).ThenByDescending(c => c.Weight).ToList();

            log = $"snapshots: {string.Join("; ", notes)}; {votes.Sum(v => v.Count)} votes in {clusters.Count} placements"
                  + (clusters.Count > 0 ? $", the best agreed on by {clusters[0].Votes} of {snapshots.Count} snapshots" : "");
            return clusters;
        }

        // ------------------------------------------------------------------ texture refinement

        /// <summary>
        /// Geometry alone cannot fix a sideways slide along a flat wall or floor: the ICP is happy anywhere along it. The brightness can. Starting
        /// from an ICP result, scan the position around it and then fine-tune position and yaw (pattern search), maximising how many points agree
        /// times how well their brightness correlates.
        /// </summary>
        private static RigidTransform TextureRefine(FusedCloud target, PointGrid targetGrid, PointGrid regionGrid, FusedCloud source, RigidTransform start, GlobalMappingParameters p)
        {
            const int points = 3000;
            double Objective(RigidTransform t)
            {
                AlignmentScore s = Evaluate(target, targetGrid, regionGrid, source, t, p, points);
                return s.Matched * s.InlierFraction * s.GrayAgreement;
            }

            RigidTransform best = start;
            double bestValue = Objective(start);

            float radius = p.MergeTextureSearchMeters, step = p.MergeTextureSearchStepMeters;
            if (radius > 0f && step > 0f)
            {
                int n = (int)MathF.Round(radius / step);
                for (int ix = -n; ix <= n; ix++)
                    for (int iz = -n; iz <= n; iz++)
                    {
                        if (ix == 0 && iz == 0)
                            continue;
                        var t = new RigidTransform(start.Rotation, start.Translation + new Vector3(ix * step, 0f, iz * step));
                        double value = Objective(t);
                        if (value > bestValue)
                        {
                            bestValue = value;
                            best = t;
                        }
                    }
            }

            // Pattern search around the best: shifts and a turn about the middle of the source, halving the step each round.
            Vector3 centre = source.Centroid;
            float moveStep = step > 0f ? step * 0.5f : 0.025f, turnStep = 1.5f;
            for (int round = 0; round < 4; round++, moveStep *= 0.5f, turnStep *= 0.5f)
            {
                for (int pass = 0; pass < 6; pass++)
                {
                    bool improved = false;
                    for (int move = 0; move < 6; move++)
                    {
                        RigidTransform t = move switch
                        {
                            0 => new RigidTransform(best.Rotation, best.Translation + new Vector3(moveStep, 0f, 0f)),
                            1 => new RigidTransform(best.Rotation, best.Translation - new Vector3(moveStep, 0f, 0f)),
                            2 => new RigidTransform(best.Rotation, best.Translation + new Vector3(0f, 0f, moveStep)),
                            3 => new RigidTransform(best.Rotation, best.Translation - new Vector3(0f, 0f, moveStep)),
                            _ => Turned(best, centre, (move == 4 ? turnStep : -turnStep) * (MathF.PI / 180f)),
                        };
                        double value = Objective(t);
                        if (value > bestValue)
                        {
                            bestValue = value;
                            best = t;
                            improved = true;
                        }
                    }
                    if (!improved)
                        break;
                }
            }
            return best;
        }

        /// <summary>The transform with an extra turn about the vertical through the point where it puts <paramref name="sourceCentre"/>.</summary>
        private static RigidTransform Turned(RigidTransform t, Vector3 sourceCentre, float radians)
        {
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
            Vector3 centre = t.Apply(sourceCentre);
            return new RigidTransform(turn * t.Rotation, Vector3.Transform(t.Translation - centre, turn) + centre);
        }

        // ------------------------------------------------------------------ coarse search

        /// <summary>
        /// Candidate transforms from the source's frame into the target's from the correlation of top-down views. The view of a cloud has a cell
        /// for each column of the ground plane, valued by how many height levels (15 cm each, from the floor) of it are occupied minus one (so a
        /// bare floor is empty and walls and furniture show), plus a second view of the brightness of each column.
        /// </summary>
        private static List<RigidTransform> CoarseSearch(FusedCloud target, FusedCloud source, GlobalMappingParameters p, out string log)
        {
            int n = 1;
            while (n < p.MergeGridSize)
                n <<= 1;

            float radiusTarget = HorizontalRadius(target), radiusSource = HorizontalRadius(source);
            float cell = MathF.Max(p.MergeGridCellMeters, 2.2f * (radiusTarget + radiusSource) / n);
            log = $"coarse search on a {n}x{n} grid of {cell * 100:0} cm cells (map radii {radiusTarget:0.0} m and {radiusSource:0.0} m)";

            // Where each map looked, to score a placement per shared viewed square metre instead of in total. Without a viewed region on both clouds
            // (an anchor's cloud, say) the raw score is used, as before.
            bool useOverlap = target.Viewed != null && source.Viewed != null;
            float[] targetMaskRe = Array.Empty<float>(), targetMaskIm = Array.Empty<float>();
            float minOverlapCells = 0f;
            if (useOverlap)
            {
                targetMaskRe = RasterizeViewed(target, 0f, n, cell, radiusTarget);
                targetMaskIm = new float[n * n];
                float targetCells = targetMaskRe.Sum(), sourceCells = RasterizeViewed(source, 0f, n, cell, radiusSource).Sum();
                float cellArea = cell * cell;
                minOverlapCells = MathF.Max(1f, MathF.Max(p.MergeMinOverlapSquareMeters / cellArea, p.MergeMinOverlapFraction * MathF.Min(targetCells, sourceCells)));
                Fft2D.Transform(targetMaskRe, targetMaskIm, n, false);
                log += $"; viewed area {target.Viewed!.AreaSquareMeters:0} m2 and {source.Viewed!.AreaSquareMeters:0} m2, at least {minOverlapCells * cellArea:0.0} m2 shared";
            }

            var (targetStructure, targetTexture) = Rasterize(target, 0f, n, cell, radiusTarget);
            float[] targetStructureRe = targetStructure, targetStructureIm = new float[n * n];
            float[] targetTextureRe = targetTexture, targetTextureIm = new float[n * n];
            Fft2D.Transform(targetStructureRe, targetStructureIm, n, false);
            Fft2D.Transform(targetTextureRe, targetTextureIm, n, false);

            int yawCount = Math.Max(8, (int)MathF.Round(360f / MathF.Max(0.5f, p.MergeYawStepDegrees)));
            const int PeaksPerYaw = 3;
            var peaks = new (float Score, int Yaw, int X, int Z, float Overlap)[yawCount * PeaksPerYaw];
            Array.Fill(peaks, (float.MinValue, 0, 0, 0, 0f));

            Parallel.For(0, yawCount, yawIndex =>
            {
                float yaw = yawIndex * (2f * MathF.PI / yawCount);
                var (sourceStructure, sourceTexture) = Rasterize(source, yaw, n, cell, radiusSource);
                var structureIm = new float[n * n];
                var textureIm = new float[n * n];
                Fft2D.Transform(sourceStructure, structureIm, n, false);
                Fft2D.Transform(sourceTexture, textureIm, n, false);

                // correlation[s] = sum over x of target[x + s] * source[x]  <->  FFT(target) * conj(FFT(source))
                var re = new float[n * n];
                var im = new float[n * n];
                for (int i = 0; i < n * n; i++)
                {
                    re[i] = targetStructureRe[i] * sourceStructure[i] + targetStructureIm[i] * structureIm[i]
                            + p.MergeTextureWeight * (targetTextureRe[i] * sourceTexture[i] + targetTextureIm[i] * textureIm[i]);
                    im[i] = targetStructureIm[i] * sourceStructure[i] - targetStructureRe[i] * structureIm[i]
                            + p.MergeTextureWeight * (targetTextureIm[i] * sourceTexture[i] - targetTextureRe[i] * textureIm[i]);
                }
                Fft2D.Transform(re, im, n, true);

                // Per square metre both maps looked at: the correlation divided by the shared viewed area of that shift (at least the minimum).
                float[] overlap = Array.Empty<float>();
                if (useOverlap)
                {
                    float[] maskRe = RasterizeViewed(source, yaw, n, cell, radiusSource);
                    var maskIm = new float[n * n];
                    Fft2D.Transform(maskRe, maskIm, n, false);
                    var overlapRe = new float[n * n];
                    var overlapIm = new float[n * n];
                    for (int i = 0; i < n * n; i++)
                    {
                        overlapRe[i] = targetMaskRe[i] * maskRe[i] + targetMaskIm[i] * maskIm[i];
                        overlapIm[i] = targetMaskIm[i] * maskRe[i] - targetMaskRe[i] * maskIm[i];
                    }
                    Fft2D.Transform(overlapRe, overlapIm, n, true);
                    overlap = overlapRe;
                    for (int i = 0; i < n * n; i++)
                        re[i] = re[i] / MathF.Max(overlap[i], minOverlapCells);
                }

                // The best few separate peaks of this yaw.
                for (int k = 0; k < PeaksPerYaw; k++)
                {
                    int best = -1;
                    float bestValue = float.MinValue;
                    for (int i = 0; i < n * n; i++)
                    {
                        if (re[i] > bestValue)
                        {
                            bestValue = re[i];
                            best = i;
                        }
                    }
                    if (best < 0 || bestValue <= 0f)
                        break;

                    int bx = best % n, bz = best / n;
                    peaks[yawIndex * PeaksPerYaw + k] = (bestValue, yawIndex, bx, bz, useOverlap ? MathF.Max(0f, overlap[best]) : 0f);
                    for (int dz = -4; dz <= 4; dz++)
                        for (int dx = -4; dx <= 4; dx++)
                            re[((bz + dz + n) % n) * n + (bx + dx + n) % n] = float.MinValue;
                }
            });

            // The best peaks overall that are not near better ones.
            var selected = new List<(float Score, int Yaw, int X, int Z, float Overlap)>();
            foreach (var peak in peaks.Where(q => q.Score > 0f).OrderByDescending(q => q.Score))
            {
                float yaw = peak.Yaw * (360f / yawCount);
                bool nearBetter = selected.Any(s =>
                {
                    float otherYaw = s.Yaw * (360f / yawCount);
                    float yawDifference = MathF.Abs(MathF.IEEERemainder(yaw - otherYaw, 360f));
                    float dx = ShiftOf(peak.X, n) - ShiftOf(s.X, n), dz = ShiftOf(peak.Z, n) - ShiftOf(s.Z, n);
                    return yawDifference <= 12f && MathF.Sqrt(dx * dx + dz * dz) * cell <= 0.9f;
                });
                if (!nearBetter)
                    selected.Add(peak);
                if (selected.Count >= Math.Max(1, p.MergeCandidates))
                    break;
            }

            var result = new List<RigidTransform>();
            Vector3 centroidTarget = target.Centroid, centroidSource = source.Centroid;
            float floorShift = target.FloorY - source.FloorY;
            foreach (var peak in selected)
            {
                float yaw = peak.Yaw * (2f * MathF.PI / yawCount);
                Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
                Vector3 rotatedCentroid = Vector3.Transform(centroidSource, rotation);
                Vector3 translation = new(
                    centroidTarget.X + ShiftOf(peak.X, n) * cell - rotatedCentroid.X,
                    floorShift,
                    centroidTarget.Z + ShiftOf(peak.Z, n) * cell - rotatedCentroid.Z);
                result.Add(new RigidTransform(rotation, translation));
                log += useOverlap
                    ? $"\n    peak: yaw {peak.Yaw * (360f / yawCount):0} deg, score {peak.Score:0.000} per shared cell, {peak.Overlap * cell * cell:0.0} m2 viewed by both"
                    : $"\n    peak: yaw {peak.Yaw * (360f / yawCount):0} deg, score {peak.Score:0.0}";
            }
            return result;
        }

        private static int ShiftOf(int index, int n) => index < n / 2 ? index : index - n;

        /// <summary>The distance from the centroid (horizontally) that contains 98% of the cloud.</summary>
        private static float HorizontalRadius(FusedCloud cloud)
        {
            var distances = new float[cloud.Count];
            for (int i = 0; i < distances.Length; i++)
            {
                Vector3 d = cloud.Positions[i] - cloud.Centroid;
                distances[i] = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            }
            Array.Sort(distances);
            return distances.Length == 0 ? 0f : distances[(int)((distances.Length - 1) * 0.98f)];
        }

        /// <summary>The top-down mask (1 where the cloud's maps looked, 0 elsewhere) on the same grid as <see cref="Rasterize"/>: the cloud turned by <paramref name="yaw"/> about its centroid.</summary>
        private static float[] RasterizeViewed(FusedCloud cloud, float yaw, int n, float cell, float radius)
        {
            var mask = new float[n * n];
            ViewedRegion? viewed = cloud.Viewed;
            if (viewed == null)
                return mask;

            Quaternion back = Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    var offset = new Vector3((ix - n / 2 + 0.5f) * cell, 0f, (iz - n / 2 + 0.5f) * cell);
                    if (offset.X * offset.X + offset.Z * offset.Z > radius * radius * 1.0001f)
                        continue;
                    Vector3 position = Vector3.Transform(offset, back) + cloud.Centroid;
                    if (viewed.ContainsXZ(position.X, position.Z))
                        mask[iz * n + ix] = 1f;
                }
            return mask;
        }

        /// <summary>The top-down views (structure and brightness) of a cloud turned by <paramref name="yaw"/> about its centroid.</summary>
        private static (float[] Structure, float[] Texture) Rasterize(FusedCloud cloud, float yaw, int n, float cell, float radius)
        {
            var masks = new uint[n * n];
            var graySum = new float[n * n];
            var counts = new int[n * n];
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

            float grayMean = cloud.Gray.Length > 0 ? cloud.Gray.Average() : 0f;
            float grayStd = cloud.Gray.Length > 1 ? MathF.Sqrt(cloud.Gray.Sum(g => (g - grayMean) * (g - grayMean)) / cloud.Gray.Length) : 1f;
            grayStd = MathF.Max(grayStd, 1f);

            for (int i = 0; i < cloud.Count; i++)
            {
                Vector3 offset = Vector3.Transform(cloud.Positions[i] - cloud.Centroid, rotation);
                if (offset.X * offset.X + offset.Z * offset.Z > radius * radius * 1.0001f)
                    continue;
                int ix = (int)MathF.Floor(offset.X / cell + n / 2), iz = (int)MathF.Floor(offset.Z / cell + n / 2);
                if (ix < 0 || iz < 0 || ix >= n || iz >= n)
                    continue;

                int bin = Math.Clamp((int)MathF.Floor((cloud.Positions[i].Y - cloud.FloorY) / 0.15f), 0, 31);
                masks[iz * n + ix] |= 1u << bin;
                graySum[iz * n + ix] += (cloud.Gray[i] - grayMean) / grayStd;
                counts[iz * n + ix]++;
            }

            var structure = new float[n * n];
            var texture = new float[n * n];
            for (int i = 0; i < n * n; i++)
            {
                int levels = System.Numerics.BitOperations.PopCount(masks[i]);
                structure[i] = MathF.Min(6f, MathF.Max(0f, levels - 1));
                if (counts[i] > 0 && structure[i] > 0f)
                    texture[i] = graySum[i] / counts[i];
            }
            return (structure, texture);
        }
    }

    /// <summary>In-place 2D FFT of square power-of-two arrays (real and imaginary parts in separate arrays).</summary>
    internal static class Fft2D
    {
        public static void Transform(float[] re, float[] im, int n, bool inverse)
        {
            var rowRe = new float[n];
            var rowIm = new float[n];
            for (int y = 0; y < n; y++)
            {
                Array.Copy(re, y * n, rowRe, 0, n);
                Array.Copy(im, y * n, rowIm, 0, n);
                Fft1D(rowRe, rowIm, inverse);
                Array.Copy(rowRe, 0, re, y * n, n);
                Array.Copy(rowIm, 0, im, y * n, n);
            }
            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                {
                    rowRe[y] = re[y * n + x];
                    rowIm[y] = im[y * n + x];
                }
                Fft1D(rowRe, rowIm, inverse);
                for (int y = 0; y < n; y++)
                {
                    re[y * n + x] = rowRe[y];
                    im[y * n + x] = rowIm[y];
                }
            }
        }

        private static void Fft1D(float[] re, float[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int length = 2; length <= n; length <<= 1)
            {
                double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
                double wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int i = 0; i < n; i += length)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < length / 2; k++)
                    {
                        int a = i + k, b = i + k + length / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = (float)(re[a] - tr);
                        im[b] = (float)(im[a] - ti);
                        re[a] = (float)(re[a] + tr);
                        im[a] = (float)(im[a] + ti);
                        double next = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = next;
                    }
                }
            }

            if (inverse)
                for (int i = 0; i < n; i++)
                {
                    re[i] /= n;
                    im[i] /= n;
                }
        }
    }
}
