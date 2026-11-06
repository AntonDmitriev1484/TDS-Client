using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// A shared anchor: a distinctive place in a map with a frozen copy of the surroundings. The anchor has its own frame (origin on the floor at the spot,
    /// the same directions as the map it was made in); <see cref="Pose"/> takes points from that frame into the frame of the sub-map it belongs to,
    /// and <see cref="Cloud"/> holds the voxels around it in the anchor's frame. Another map can look for the anchor's cloud in its own points; where
    /// it finds it ties the two maps together. Immutable: a merge makes a moved copy.
    /// </summary>
    public sealed class MapAnchor
    {
        public int ID { get; init; }

        /// <summary>The player who saw most of what stands around the anchor.</summary>
        public int OwnerPlayerID { get; init; }

        /// <summary>The sub-map the anchor is in now.</summary>
        public int SubMapID { get; init; }

        /// <summary>Anchor frame to sub-map frame.</summary>
        public RigidTransform Pose { get; init; }

        /// <summary>The voxels around the anchor (as they were when it was made), in the anchor's frame.</summary>
        public FusedCloud Cloud { get; init; } = null!;

        public DateTime Created { get; init; } = DateTime.UtcNow;

        /// <summary>Where the anchor is in its sub-map (on the floor).</summary>
        public Vector3 Position => Pose.Translation;

        /// <summary>The same anchor after its sub-map was moved by <paramref name="transform"/> into the frame of sub-map <paramref name="subMapID"/>.</summary>
        public MapAnchor Moved(RigidTransform transform, int subMapID) => new()
        {
            ID = ID,
            OwnerPlayerID = OwnerPlayerID,
            SubMapID = subMapID,
            Pose = transform.Compose(Pose),
            Cloud = Cloud,
            Created = Created,
        };
    }

    /// <summary>Where one anchor was found: for an anchor of the other map, <paramref name="Found"/> takes the anchor's frame into this map's frame; <paramref name="TheirsToMine"/> is the placement of the whole other map that follows from it.</summary>
    public sealed record AnchorPlacement(MapAnchor Anchor, bool OfOtherMap, RigidTransform Found, RigidTransform TheirsToMine, AlignmentScore Score);

    /// <summary>A placement of one map in another that several anchors agree on (the transform takes the other map into this one).</summary>
    public sealed record AnchorCandidate(RigidTransform Transform, int Votes, string Description);

    /// <summary>A spot in a map where an anchor could be made: on the floor, with how unlike the rest of the map its surroundings are (0 to 1).</summary>
    public readonly record struct AnchorSite(Vector3 Position, int PlayerID, float Uniqueness);

    /// <summary>Where anchors are made, and how.</summary>
    public static class AnchorBuilder
    {
        private const int RadialBins = 5, HeightBins = 7, GrayBins = 4;
        private const float HeightBinMeters = 0.3f, ColumnMeters = 0.25f;

        private sealed class Candidate
        {
            public Vector3 Position;
            public float[] Shape = new float[RadialBins * HeightBins];
            public float[] Gray = new float[GrayBins];
            public int Standing;
            public int Player;
            public float Uniqueness;
        }

        /// <summary>
        /// The most distinctive spots of <paramref name="cloud"/> that are at least <see cref="GlobalMappingParameters.AnchorSpacingMeters"/> from every
        /// existing anchor and from each other, most distinctive first: up to <see cref="GlobalMappingParameters.AnchorMaxNewPerCheck"/> and what
        /// there is room for. A spot is described by a signature of the voxels around it (how many are at which distance and height, and how bright
        /// they are, all independent of the direction); its uniqueness is how far that is from the most similar other spot, so a wall, the floor or a
        /// row of identical chairs score low and a lone object scores high.
        /// </summary>
        public static List<AnchorSite> PlanSites(FusedCloud cloud, IReadOnlyList<MapAnchor> existing, GlobalMappingParameters p, out string note)
        {
            var sites = new List<AnchorSite>();
            note = "";
            int room = Math.Min(p.AnchorMaxNewPerCheck, p.AnchorMaxPerMap - existing.Count);
            if (room <= 0 || cloud.Count == 0)
                return sites;

            float floor = cloud.FloorY, radius = MathF.Max(0.3f, p.AnchorSignatureRadiusMeters), grid = MathF.Max(0.1f, p.AnchorSiteGridMeters);
            var columns = new Dictionary<(int, int), List<int>>();
            var cells = new HashSet<(int, int)>();
            for (int i = 0; i < cloud.Count; i++)
            {
                Vector3 q = cloud.Positions[i];
                float height = q.Y - floor;
                if (height < -0.3f || height > 2.4f)
                    continue;
                var column = ((int)MathF.Floor(q.X / ColumnMeters), (int)MathF.Floor(q.Z / ColumnMeters));
                if (!columns.TryGetValue(column, out List<int>? list))
                    columns[column] = list = new List<int>();
                list.Add(i);
                cells.Add(((int)MathF.Floor(q.X / grid), (int)MathF.Floor(q.Z / grid)));
            }

            // Describe every spot on the grid that has something standing around it.
            var candidates = new List<Candidate>();
            int reach = (int)MathF.Ceiling(radius / ColumnMeters);
            foreach (var cell in cells)
            {
                var c = new Candidate { Position = new Vector3((cell.Item1 + 0.5f) * grid, floor, (cell.Item2 + 0.5f) * grid) };
                var players = new Dictionary<int, int>();
                int cx = (int)MathF.Floor(c.Position.X / ColumnMeters), cz = (int)MathF.Floor(c.Position.Z / ColumnMeters);
                for (int dx = -reach; dx <= reach; dx++)
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        if (!columns.TryGetValue((cx + dx, cz + dz), out List<int>? list))
                            continue;
                        foreach (int i in list)
                        {
                            Vector3 q = cloud.Positions[i];
                            float ddx = q.X - c.Position.X, ddz = q.Z - c.Position.Z;
                            float distance = MathF.Sqrt(ddx * ddx + ddz * ddz);
                            if (distance >= radius)
                                continue;
                            float height = q.Y - floor;
                            int r = Math.Min(RadialBins - 1, (int)(distance / radius * RadialBins));
                            int h = Math.Clamp((int)MathF.Floor(height / HeightBinMeters), 0, HeightBins - 1);
                            c.Shape[r * HeightBins + h]++;
                            c.Gray[Math.Clamp((int)(cloud.Gray[i] / 64f), 0, GrayBins - 1)]++;
                            if (height > 0.15f)
                            {
                                c.Standing++;
                                players[cloud.Players[i]] = players.GetValueOrDefault(cloud.Players[i]) + 1;
                            }
                        }
                    }
                if (c.Standing < p.AnchorMinStandingPoints)
                    continue;
                Normalise(c.Shape);
                Normalise(c.Gray);
                c.Player = players.OrderByDescending(kv => kv.Value).First().Key;
                candidates.Add(c);
            }

            // How unlike the other spots each one is.
            float spacing2 = p.AnchorSpacingMeters * p.AnchorSpacingMeters;
            foreach (Candidate c in candidates)
            {
                float nearest = float.MaxValue;
                int compared = 0;
                foreach (Candidate o in candidates)
                {
                    if (Horizontal2(o.Position, c.Position) < spacing2)
                        continue;
                    compared++;
                    nearest = MathF.Min(nearest, Distance(c, o));
                }
                c.Uniqueness = compared >= p.AnchorMinComparisonSites ? nearest : 0f;
            }

            foreach (Candidate c in candidates.OrderByDescending(c => c.Uniqueness))
            {
                if (sites.Count >= room || c.Uniqueness < p.AnchorMinUniqueness)
                    break;
                if (existing.Any(a => Horizontal2(a.Position, c.Position) < spacing2) || sites.Any(s => Horizontal2(s.Position, c.Position) < spacing2))
                    continue;
                sites.Add(new AnchorSite(c.Position, c.Player, c.Uniqueness));
            }

            float best = candidates.Count > 0 ? candidates.Max(c => c.Uniqueness) : 0f;
            note = $"{candidates.Count} spots looked at, the most unique {best:0.00} (need {p.AnchorMinUniqueness:0.00})";
            return sites;
        }

        private static void Normalise(float[] values)
        {
            float sum = values.Sum();
            if (sum > 0)
                for (int i = 0; i < values.Length; i++)
                    values[i] /= sum;
        }

        /// <summary>How different two signatures are, 0 (the same) to 1: half the summed absolute difference of the distributions, shape mostly and brightness a little.</summary>
        private static float Distance(Candidate a, Candidate b)
        {
            float shape = 0, gray = 0;
            for (int i = 0; i < a.Shape.Length; i++)
                shape += MathF.Abs(a.Shape[i] - b.Shape[i]);
            for (int i = 0; i < a.Gray.Length; i++)
                gray += MathF.Abs(a.Gray[i] - b.Gray[i]);
            return 0.5f * (0.75f * shape + 0.25f * gray);
        }

        private static float Horizontal2(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        /// <summary>
        /// An anchor at <paramref name="site"/>, with the voxels of <paramref name="cloud"/> within the anchor radius (the anchor's frame has its origin
        /// on the floor at the site and the same directions as the map). Null if there are too few of them.
        /// </summary>
        public static MapAnchor? Create(int id, FusedCloud cloud, AnchorSite site, int subMapID, GlobalMappingParameters p)
        {
            float floor = cloud.FloorY;
            var pose = new RigidTransform(Quaternion.Identity, new Vector3(site.Position.X, floor, site.Position.Z));
            float radius2 = p.AnchorRadiusMeters * p.AnchorRadiusMeters;
            FusedCloud local = cloud.Subset(q =>
            {
                float dx = q.X - pose.Translation.X, dz = q.Z - pose.Translation.Z;
                float height = q.Y - floor;
                return dx * dx + dz * dz <= radius2 && height > -0.3f && height < 3.2f;
            }, pose.Inverse());
            if (local.Count < p.AnchorMinPoints)
                return null;
            return new MapAnchor { ID = id, OwnerPlayerID = site.PlayerID, SubMapID = subMapID, Pose = pose, Cloud = local };
        }
    }

    /// <summary>Finds the anchors of two maps in each other, and the placements of the maps that several anchors agree on.</summary>
    public static class AnchorLocalizer
    {
        private sealed record Estimate(RigidTransform Transform, AlignmentScore Score, string Source);

        /// <summary>
        /// The placements of <paramref name="theirs"/> in <paramref name="mine"/> (transforms from their frame into mine) that at least
        /// <see cref="GlobalMappingParameters.AnchorMinAgree"/> different anchors agree on, the best first. Each anchor of the other map is looked
        /// for in this map's points and each anchor of this map in the other's; a located anchor gives one estimate of the placement.
        /// </summary>
        public static List<AnchorCandidate> Locate(FusedCloud mine, IReadOnlyList<MapAnchor> myAnchors, FusedCloud theirs, IReadOnlyList<MapAnchor> theirAnchors,
                                                   GlobalMappingParameters p, out string log) =>
            Locate(mine, myAnchors, theirs, theirAnchors, p, out log, out _);

        /// <param name="placements">Every anchor that was found, with where (for showing them).</param>
        public static List<AnchorCandidate> Locate(FusedCloud mine, IReadOnlyList<MapAnchor> myAnchors, FusedCloud theirs, IReadOnlyList<MapAnchor> theirAnchors,
                                                   GlobalMappingParameters p, out string log, out List<AnchorPlacement> placements)
        {
            placements = new List<AnchorPlacement>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            GlobalMappingParameters light = p.ForAnchors();
            var estimates = new List<Estimate>();
            var notes = new List<string>();
            int tried = 0;

            IEnumerable<MapAnchor> Pick(IReadOnlyList<MapAnchor> anchors) => anchors.OrderByDescending(a => a.Cloud.Count).Take(Math.Max(0, p.AnchorMaxLocatePerDirection));
            var mineList = Pick(myAnchors).ToList();
            var theirList = Pick(theirAnchors).ToList();

            // Alternate between the two directions so that a few anchors give evidence from both sides.
            for (int i = 0; i < Math.Max(mineList.Count, theirList.Count); i++)
            {
                foreach (bool theirSide in new[] { true, false })
                {
                    var list = theirSide ? theirList : mineList;
                    if (i >= list.Count)
                        continue;
                    MapAnchor anchor = list[i];
                    tried++;
                    // The anchor's cloud is looked for in the other map: the result takes the anchor's frame into that map's frame.
                    AlignmentResult? found = CloudAlignment.Align(theirSide ? mine : theirs, anchor.Cloud, light, out _);
                    string name = $"anchor {anchor.ID} (map {anchor.SubMapID}, player {anchor.OwnerPlayerID})";
                    if (found == null || found.Score.Matched < p.AnchorMinMatchedPoints || found.Score.InlierFraction < p.AnchorMinInlierFraction
                        || found.Score.GrayAgreement < p.AnchorMinGrayAgreement)
                    {
                        notes.Add($"{name}: not found" + (found != null ? $" ({found.Score})" : ""));
                        continue;
                    }

                    // Their anchor: anchor frame -> their frame (Pose), and -> my frame (found), so their frame -> my frame is found after Pose^-1.
                    // My anchor: anchor frame -> my frame (Pose), and -> their frame (found), so their frame -> my frame is Pose after found^-1.
                    RigidTransform theirsToMine = theirSide ? found.Transform.Compose(anchor.Pose.Inverse()) : anchor.Pose.Compose(found.Transform.Inverse());
                    estimates.Add(new Estimate(theirsToMine, found.Score, name));
                    placements.Add(new AnchorPlacement(anchor, theirSide, found.Transform, theirsToMine, found.Score));
                    notes.Add($"{name}: found, {found.Score}");
                }

                if (BestClusterSize(estimates, theirs.Centroid, p) > p.AnchorTrustAgree)
                    break; // enough evidence already
            }

            var result = new List<AnchorCandidate>();
            var remaining = new List<Estimate>(estimates);
            for (int round = 0; round < Math.Max(1, p.AnchorMaxCandidates) && remaining.Count > 0; round++)
            {
                List<Estimate>? best = null;
                foreach (Estimate e in remaining)
                {
                    var cluster = remaining.Where(o => CloudAlignment.PlacementsAgree(e.Transform, o.Transform, theirs.Centroid, p)).ToList();
                    if (best == null || cluster.Count > best.Count)
                        best = cluster;
                }
                if (best == null || best.Count < Math.Max(1, p.AnchorMinAgree))
                    break;
                Estimate strongest = best.OrderByDescending(e => (double)e.Score.Matched * e.Score.InlierFraction * e.Score.GrayAgreement).First();
                result.Add(new AnchorCandidate(strongest.Transform, best.Count, $"{best.Count} anchors agree, {strongest.Transform}"));
                remaining.RemoveAll(best.Contains);
            }

            log = $"{estimates.Count} of {tried} anchors located ({myAnchors.Count} of this map, {theirAnchors.Count} of the other)"
                  + (result.Count > 0 ? $"; best placement agreed on by {result[0].Votes} anchors" : "; no placement agreed on by enough anchors")
                  + $" [{timer.ElapsedMilliseconds} ms]" + (notes.Count > 0 ? "\n    " + string.Join("\n    ", notes) : "");
            return result;
        }

        private static int BestClusterSize(List<Estimate> estimates, Vector3 reference, GlobalMappingParameters p)
        {
            int best = 0;
            foreach (Estimate e in estimates)
                best = Math.Max(best, estimates.Count(o => CloudAlignment.PlacementsAgree(e.Transform, o.Transform, reference, p)));
            return best;
        }
    }
}
