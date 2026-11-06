using Server.Manager.SpatialSync;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Numerics;

namespace Server.Utilities
{
    /// <summary>How headsets' scene anchors are matched with each other and how the headsets are lined up from them.</summary>
    public sealed class SceneAnchorMapParameters
    {
        /// <summary>The first headset needs at least this many scene anchors for its tracking space to become the global frame.</summary>
        public int MinAnchorsForReference { get; init; } = 3;

        /// <summary>A headset is placed in the global map only if at least this many of its anchors have a good match (<see cref="SignificantScore"/>) in it.</summary>
        public int MinMatchedAnchors { get; init; } = 3;

        #region Evidence

        /// <summary>
        /// Every pair of anchors (one from the map, one from the headset) that overlaps once the headset is lined up earns evidence points: up to 1 for
        /// the same label (by how much they overlap), less for a different label (below). The alignment is the one with the most evidence in total.
        /// </summary>
        public float SameLabelPoints { get; init; } = 1f;

        /// <summary>A real wall against an invisible wall (an inferred one) that overlaps it: the same wall, labelled differently by the two scans.</summary>
        public float WallVariantPoints { get; init; } = 0.6f;

        /// <summary>A door, window or picture against a wall it lies in, or a door against a window: weak evidence (a door lies in every wall).</summary>
        public float OpeningInWallPoints { get; init; } = 0.2f;

        /// <summary>One piece of furniture against another with a different label (a table one scan calls "other", say) that overlaps it.</summary>
        public float FurnitureOtherLabelPoints { get; init; } = 0.3f;

        /// <summary>A pair that scores less than this is not counted as a match at all.</summary>
        public float MinMatchScore { get; init; } = 0.05f;

        /// <summary>A pair that scores at least this is a good match (what <see cref="MinMatchedAnchors"/> counts).</summary>
        public float SignificantScore { get; init; } = 0.3f;

        /// <summary>The alignment is only accepted with at least this much evidence in total...</summary>
        public float MinEvidence { get; init; } = 2f;

        /// <summary>...and at least this much per anchor of the smaller of the two sets (the headset's and the map's).</summary>
        public float MinEvidenceFraction { get; init; } = 0.3f;

        /// <summary>When two different alignments have nearly the same evidence (within the larger of these two margins) none is chosen: the room looks symmetric.</summary>
        public float AmbiguityMarginPoints { get; init; } = 0.5f;
        public float AmbiguityMarginFraction { get; init; } = 0.1f;

        #endregion Evidence

        #region Geometry

        /// <summary>How far (metres) a wall, door or window may sit from the matching one, measured across the surface (along its normal), to still overlap it. Also the height difference for a floor or ceiling.</summary>
        public float SurfaceToleranceMeters { get; init; } = 0.3f;

        /// <summary>How far (degrees) the normal of a wall, door or window may be turned from the matching one.</summary>
        public float NormalToleranceDegrees { get; init; } = 20f;

        /// <summary>Two anchors can only start an alignment together if each of their dimensions is within max(this many metres, <see cref="SizeToleranceFraction"/> of the larger).</summary>
        public float SizeToleranceMeters { get; init; } = 0.5f;
        public float SizeToleranceFraction { get; init; } = 0.4f;

        #endregion Geometry

        #region Candidates

        /// <summary>
        /// A candidate alignment is dropped if the walls (real and invisible) of the headset do not overlap the map's walls by at least this fraction
        /// (of the walls of the smaller set), once the headset is lined up that way. Skipped when either side has no walls.
        /// </summary>
        public float MinWallOverlap { get; init; } = 0.2f;

        /// <summary>This many candidates with the most evidence are refined.</summary>
        public int MaxCandidates { get; init; } = 8;

        #endregion Candidates

        /// <summary>A fit is only accepted if the well-matched anchors agree to within this (root mean square, metres).</summary>
        public float MaxRmsResidualMeters { get; init; } = 0.25f;

        /// <summary>
        /// How well the matched anchors pin the headset down in all three directions, 0 (not at all: e.g. only parallel walls) to 1 (perfectly: e.g. the
        /// floor and two walls at right angles). Fits below this are not accepted, because the headset could be anywhere along the unconstrained direction.
        /// </summary>
        public float MinConditioning { get; init; } = 0.05f;

        /// <summary>How often (seconds) a placed headset's alignment is refitted to its latest anchors.</summary>
        public float RefitIntervalSeconds { get; init; } = 2f;

        /// <summary>How often (seconds) a headset that is not placed is tried again.</summary>
        public float RetryIntervalSeconds { get; init; } = 1f;
    }

    internal enum SceneAnchorKind
    {
        /// <summary>A vertical surface: wall, door, window, picture. Fixes the position across it and the turn about the vertical axis.</summary>
        Wall,
        /// <summary>The floor. Fixes the height.</summary>
        Floor,
        /// <summary>The ceiling. Fixes the height.</summary>
        Ceiling,
        /// <summary>Anything else (furniture): fixes its centre in all three directions, but not as precisely.</summary>
        Volume,
    }

    /// <summary>A scene anchor as the server uses it: a label, a kind, a size and a pose, in some frame (a headset's tracking space or the global one).</summary>
    internal sealed class SceneAnchor
    {
        public string Key { get; init; } = "";
        public string Uuid { get; init; } = "";
        public string Label { get; init; } = "";
        public SceneAnchorKind Kind { get; init; }
        public PoseTransform Pose { get; init; }
        public Vector3 Size { get; init; }

        /// <summary>The horizontal unit normal of a <see cref="SceneAnchorKind.Wall"/>; the vertical axis for a floor or ceiling; zero for a volume.</summary>
        public Vector3 Normal { get; init; }

        public Vector3 Position => Pose.Position;

        public static bool TryCreate(int playerID, SceneAnchorDTO dto, out SceneAnchor anchor)
        {
            anchor = null!;
            if (dto == null || !dto.IsTracked || string.IsNullOrWhiteSpace(dto.AnchorUuid) || string.IsNullOrWhiteSpace(dto.Label))
                return false;

            string label = dto.Label.Trim().ToUpperInvariant();
            if (label == "GLOBAL_MESH")
                return false; // the room's mesh: far too big and detailed to compare

            PoseTransform pose = PoseTransform.From(dto.Pose);
            var size = new Vector3(Math.Abs(dto.Size.X), Math.Abs(dto.Size.Y), Math.Abs(dto.Size.Z));
            if (!pose.IsFinite || !float.IsFinite(size.X + size.Y + size.Z))
                return false;

            SceneAnchorKind kind = KindOf(label);
            Vector3 normal = Vector3.Zero;
            if (kind == SceneAnchorKind.Wall)
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, pose.Rotation);
                if (MathF.Abs(forward.Y) > 0.7f || size.Z > 0.3f)
                    kind = SceneAnchorKind.Volume; // not a vertical surface after all
                else
                    normal = Vector3.Normalize(new Vector3(forward.X, 0f, forward.Z));
            }
            else if (kind != SceneAnchorKind.Volume)
                normal = Vector3.UnitY;

            anchor = new SceneAnchor
            {
                Key = $"{label}#p{playerID}:{dto.AnchorUuid.Trim()}",
                Uuid = dto.AnchorUuid.Trim(),
                Label = label,
                Kind = kind,
                Pose = pose,
                Size = size,
                Normal = normal,
            };
            return true;
        }

        private static SceneAnchorKind KindOf(string label) => label switch
        {
            "FLOOR" => SceneAnchorKind.Floor,
            "CEILING" => SceneAnchorKind.Ceiling,
            "WALL_FACE" or "INVISIBLE_WALL_FACE" or "INNER_WALL_FACE" or "DOOR_FRAME" or "WINDOW_FRAME" or "WALL_ART" => SceneAnchorKind.Wall,
            _ => SceneAnchorKind.Volume,
        };

        /// <summary>This anchor moved by a transform (to the global frame), under a new key.</summary>
        public SceneAnchor Transformed(PoseTransform transform, string key) => new()
        {
            Key = key,
            Uuid = Uuid,
            Label = Label,
            Kind = Kind,
            Pose = transform.Then(Pose),
            Size = Size,
            Normal = Kind == SceneAnchorKind.Wall ? Vector3.Normalize(Vector3.Transform(Normal, transform.Rotation)) : Normal,
        };
    }

    /// <summary>Two scene anchors that overlap: one in the global map, one from a headset, with the evidence points the pair earns.</summary>
    /// <param name="Same">True if they are the same kind of thing (the same label, or a real and an invisible wall): the headset's anchor is then the map's anchor, as opposed to merely lying in it.</param>
    internal readonly record struct SceneMatch(SceneAnchor Global, SceneAnchor Local, float Score, bool Same);

    internal sealed class SceneAlignment
    {
        /// <summary>Takes the headset's tracking space into the global frame.</summary>
        public PoseTransform Transform { get; init; }
        public List<SceneMatch> Matches { get; init; } = new();

        /// <summary>The evidence points of all the matches together: how well the headset's anchors and the map's agree.</summary>
        public float Evidence { get; init; }

        /// <summary>How many matches are good ones.</summary>
        public int SignificantMatches { get; init; }

        public float RmsResidual { get; init; }
        public float Conditioning { get; init; }
    }

    /// <summary>
    /// Lines a headset up with the global map from scene anchors, with nothing shared between them but what the room is. The tracking spaces are
    /// gravity aligned, so only a turn about the vertical axis and a shift are unknown.
    /// <list type="number">
    /// <item>Candidate alignments come from pairing the most telling anchors first (doors, windows, screens, then furniture): each pair of the same label proposes
    /// the turn and shift that puts one on the other. Only if those fail, pairs of walls do.</item>
    /// <item>A candidate is dropped if the walls (real and invisible) do not overlap the map's walls.</item>
    /// <item>The others are scored: every overlapping pair earns evidence by its overlap ratio, in full for the same label and in part for a different one.</item>
    /// <item>The candidates with the most evidence are refined by moving them to where the evidence is highest, and the best is taken if its evidence is
    /// high enough and clearly better than any other alignment.</item>
    /// </list>
    /// </summary>
    internal static class SceneAnchorAligner
    {
        private static readonly string[] TellingLabels = { "DOOR_FRAME", "WINDOW_FRAME", "SCREEN", "WALL_ART" };
        private static readonly string[] FurnitureLabels = { "BED", "COUCH", "TABLE", "STORAGE", "LAMP", "PLANT", "OTHER" };

        #region Search

        /// <summary>
        /// Find the alignment from scratch. Null if nothing is convincing, or two different alignments are about equally convincing (a symmetric room):
        /// <paramref name="failure"/> says which.
        /// </summary>
        public static SceneAlignment? Search(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, SceneAnchorMapParameters p, out string failure)
        {
            failure = "";
            if (map.Count == 0 || local.Count == 0)
            {
                failure = "no anchors to compare";
                return null;
            }

            // First the most telling anchors; only if that finds nothing acceptable, the walls too.
            SceneAlignment? result = Run(map, local, p, GenerateCandidates(map, local, p, includeWalls: false), out failure);
            if (result != null)
                return result;

            string telling = failure;
            result = Run(map, local, p, GenerateCandidates(map, local, p, includeWalls: true), out failure);
            if (result == null && failure.Length == 0)
                failure = telling;
            return result;
        }

        private static SceneAlignment? Run(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, SceneAnchorMapParameters p, List<PoseTransform> candidates, out string failure)
        {
            failure = "";
            if (candidates.Count == 0)
            {
                failure = "no pair of anchors with the same label and a similar size to start an alignment from (is it the same room?)";
                return null;
            }

            // Many pairs propose the same alignment.
            var distinct = new List<PoseTransform>();
            foreach (PoseTransform candidate in candidates)
                if (!distinct.Any(d => Similar(d, candidate)))
                    distinct.Add(candidate);

            // Cull: the walls must overlap.
            bool checkWalls = map.Any(IsWall) && local.Any(IsWall);
            var survivors = new List<(PoseTransform Transform, float Evidence)>();
            foreach (PoseTransform candidate in distinct)
            {
                if (checkWalls && WallOverlap(map, local, candidate, p) < p.MinWallOverlap)
                    continue;
                survivors.Add((candidate, Evidence(map, local, candidate, p)));
            }
            if (survivors.Count == 0)
            {
                failure = $"{distinct.Count} candidate alignment(s), but none has the headset's walls overlapping the map's walls";
                return null;
            }

            // Refine the most promising to where the evidence is highest.
            var refined = new List<SceneAlignment>();
            foreach (var survivor in survivors.OrderByDescending(s => s.Evidence).Take(Math.Max(1, p.MaxCandidates)))
            {
                SceneAlignment? fitted = RefineCore(map, local, survivor.Transform, p);
                if (fitted != null)
                    refined.Add(fitted);
            }

            var best = new List<SceneAlignment>();
            foreach (SceneAlignment r in refined.OrderByDescending(r => r.Evidence))
                if (!best.Any(b => Similar(b.Transform, r.Transform)))
                    best.Add(r);
            if (best.Count == 0)
            {
                failure = "no candidate alignment has any anchors overlapping";
                return null;
            }

            SceneAlignment top = best[0];
            if (!Acceptable(top, map.Count, local.Count, p, out failure))
                return null;

            if (best.Count > 1)
            {
                float margin = Math.Max(p.AmbiguityMarginPoints, p.AmbiguityMarginFraction * top.Evidence);
                if (top.Evidence - best[1].Evidence < margin)
                {
                    failure = $"two different alignments have almost the same evidence ({top.Evidence:0.0} and {best[1].Evidence:0.0} points): the room looks symmetric from these anchors";
                    return null;
                }
            }
            return top;
        }

        /// <summary>Whether an alignment has enough evidence, enough good matches that agree closely, and pins the headset down in all directions.</summary>
        private static bool Acceptable(SceneAlignment a, int mapCount, int localCount, SceneAnchorMapParameters p, out string reason)
        {
            reason = "";
            float needed = Math.Max(p.MinEvidence, p.MinEvidenceFraction * Math.Min(mapCount, localCount));
            if (a.Evidence < needed)
                reason = $"the best alignment has {a.Evidence:0.0} points of evidence, {needed:0.0} are needed";
            else if (a.SignificantMatches < p.MinMatchedAnchors)
                reason = $"only {a.SignificantMatches} anchor(s) match well, {p.MinMatchedAnchors} are needed";
            else if (a.RmsResidual > p.MaxRmsResidualMeters)
                reason = $"the matching anchors disagree by {a.RmsResidual * 100:0} cm";
            else if (a.Conditioning < p.MinConditioning)
                reason = "the matching anchors do not fix the headset in all directions (e.g. only parallel walls)";
            return reason.Length == 0;
        }

        /// <summary>
        /// Improve an alignment (the last one): match the anchors under it, refit, and move it to where the evidence is highest. Null if it no longer holds.
        /// </summary>
        public static SceneAlignment? Refine(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform start, SceneAnchorMapParameters p)
        {
            SceneAlignment? a = RefineCore(map, local, start, p);
            return a != null && Acceptable(a, map.Count, local.Count, p, out _) ? a : null;
        }

        private static SceneAlignment? RefineCore(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform start, SceneAnchorMapParameters p)
        {
            PoseTransform transform = start;
            float evidence = Evidence(map, local, transform, p);

            // Least squares on what overlaps, kept if it earns more evidence.
            for (int i = 0; i < 3; i++)
            {
                List<SceneMatch> matches = Associate(map, local, transform, p);
                var good = matches.Where(m => m.Score >= p.SignificantScore).ToList();
                if (good.Count == 0)
                    break;
                PoseTransform solved = Solve(good, transform, out _, out _);
                float solvedEvidence = Evidence(map, local, solved, p);
                if (solvedEvidence <= evidence)
                    break;
                transform = solved;
                evidence = solvedEvidence;
            }

            transform = ClimbEvidence(map, local, transform, ref evidence, p);

            List<SceneMatch> final = Associate(map, local, transform, p);
            if (final.Count == 0)
                return null;
            var significant = final.Where(m => m.Score >= p.SignificantScore && m.Same).ToList();
            Metrics(transform, significant, out float rms, out float conditioning);
            return new SceneAlignment
            {
                Transform = transform,
                Matches = final,
                Evidence = final.Sum(m => m.Score),
                SignificantMatches = final.Count(m => m.Score >= p.SignificantScore),
                RmsResidual = rms,
                Conditioning = conditioning,
            };
        }

        /// <summary>Coordinate search over the turn and the shift: take any step that adds evidence, and halve the steps when none does.</summary>
        private static PoseTransform ClimbEvidence(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform start, ref float evidence, SceneAnchorMapParameters p)
        {
            float yaw = start.Yaw;
            Vector3 shift = start.Position;
            float yawStep = 3f * MathF.PI / 180f, moveStep = 0.15f;
            const float minYawStep = 0.1f * MathF.PI / 180f, minMoveStep = 0.005f;

            for (int iteration = 0; iteration < 200 && (yawStep > minYawStep || moveStep > minMoveStep); iteration++)
            {
                float bestEvidence = evidence;
                float bestYaw = yaw;
                Vector3 bestShift = shift;

                void Try(float candidateYaw, Vector3 candidateShift)
                {
                    float e = Evidence(map, local, Make(candidateYaw, candidateShift), p);
                    if (e > bestEvidence + 1e-4f)
                    {
                        bestEvidence = e;
                        bestYaw = candidateYaw;
                        bestShift = candidateShift;
                    }
                }

                Try(yaw + yawStep, shift);
                Try(yaw - yawStep, shift);
                Try(yaw, shift + new Vector3(moveStep, 0f, 0f));
                Try(yaw, shift - new Vector3(moveStep, 0f, 0f));
                Try(yaw, shift + new Vector3(0f, moveStep, 0f));
                Try(yaw, shift - new Vector3(0f, moveStep, 0f));
                Try(yaw, shift + new Vector3(0f, 0f, moveStep));
                Try(yaw, shift - new Vector3(0f, 0f, moveStep));

                if (bestEvidence > evidence)
                {
                    yaw = bestYaw;
                    shift = bestShift;
                    evidence = bestEvidence;
                }
                else
                {
                    yawStep *= 0.5f;
                    moveStep *= 0.5f;
                }
            }
            return Make(yaw, shift);
        }

        private static PoseTransform Make(float yaw, Vector3 shift) => new(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw), shift);

        #endregion Search

        #region Candidates

        /// <summary>
        /// The alignments that would put one anchor of the headset onto an anchor of the map with the same label and a similar size: doors, windows,
        /// screens and pictures first, then furniture, and (if asked) walls.
        /// </summary>
        private static List<PoseTransform> GenerateCandidates(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, SceneAnchorMapParameters p, bool includeWalls)
        {
            var candidates = new List<PoseTransform>();
            foreach (SceneAnchor l in local)
            {
                bool telling = TellingLabels.Contains(l.Label), furniture = FurnitureLabels.Contains(l.Label);
                bool wall = includeWalls && l.Kind == SceneAnchorKind.Wall && IsWall(l);
                if (!telling && !furniture && !wall)
                    continue;

                foreach (SceneAnchor g in map)
                {
                    if (g.Label != l.Label || !Compatible(g, l, p))
                        continue;

                    if (l.Kind == SceneAnchorKind.Wall && g.Kind == SceneAnchorKind.Wall)
                    {
                        float yaw = Angle(g.Normal) - Angle(l.Normal);
                        candidates.Add(From(l, g, yaw));
                        if (!wall)
                            candidates.Add(From(l, g, yaw + MathF.PI)); // the two scans may disagree on which way a door or window faces
                    }
                    else if (l.Kind == SceneAnchorKind.Volume && g.Kind == SceneAnchorKind.Volume)
                    {
                        // A box looks the same turned by a quarter, so all four ways round are tried.
                        float yaw = Angle(Flatten(Vector3.Transform(Vector3.UnitZ, g.Pose.Rotation), Vector3.UnitZ)) - Angle(Flatten(Vector3.Transform(Vector3.UnitZ, l.Pose.Rotation), Vector3.UnitZ));
                        for (int k = 0; k < 4; k++)
                            candidates.Add(From(l, g, yaw + k * MathF.PI / 2f));
                    }
                }
            }
            return candidates;
        }

        /// <summary>The alignment with this turn that puts the headset's anchor's centre onto the map's.</summary>
        private static PoseTransform From(SceneAnchor l, SceneAnchor g, float yaw)
        {
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            return new PoseTransform(rotation, g.Position - Vector3.Transform(l.Position, rotation));
        }

        /// <summary>Could these two be the same thing? Same label and a similar size (the extent of a floor or ceiling is not compared: two scans outline a room differently).</summary>
        private static bool Compatible(SceneAnchor g, SceneAnchor l, SceneAnchorMapParameters p)
        {
            if (g.Label != l.Label || g.Kind != l.Kind)
                return false;
            switch (l.Kind)
            {
                case SceneAnchorKind.Floor:
                case SceneAnchorKind.Ceiling:
                    return true;
                case SceneAnchorKind.Wall:
                    return Near(g.Size.X, l.Size.X, p) && Near(g.Size.Y, l.Size.Y, p);
                default:
                    float[] a = { g.Size.X, g.Size.Y, g.Size.Z }, b = { l.Size.X, l.Size.Y, l.Size.Z };
                    Array.Sort(a);
                    Array.Sort(b);
                    return Near(a[0], b[0], p) && Near(a[1], b[1], p) && Near(a[2], b[2], p);
            }
        }

        private static bool Near(float a, float b, SceneAnchorMapParameters p) => MathF.Abs(a - b) <= MathF.Max(p.SizeToleranceMeters, p.SizeToleranceFraction * MathF.Max(a, b));

        private static bool Similar(PoseTransform a, PoseTransform b)
        {
            float yaw = MathF.Abs(WrapAngle(a.Yaw - b.Yaw));
            return yaw < 5f * MathF.PI / 180f && (a.Position - b.Position).Length() < 0.3f;
        }

        #endregion Candidates

        #region Evidence

        /// <summary>The evidence of an alignment: the points of all the matches (each anchor in at most one).</summary>
        private static float Evidence(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform transform, SceneAnchorMapParameters p)
            => Associate(map, local, transform, p).Sum(m => m.Score);

        /// <summary>
        /// Match the headset's anchors with the map's as they would be with this transform: every overlapping pair scores, the best pairs first, each
        /// anchor in at most one pair.
        /// </summary>
        private static List<SceneMatch> Associate(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform transform, SceneAnchorMapParameters p)
        {
            var pairs = new List<(int Local, int Map, float Score)>();
            for (int li = 0; li < local.Count; li++)
            {
                for (int gi = 0; gi < map.Count; gi++)
                {
                    float score = Score(map[gi], local[li], transform, p);
                    if (score >= p.MinMatchScore)
                        pairs.Add((li, gi, score));
                }
            }

            var usedLocal = new HashSet<int>();
            var usedMap = new HashSet<int>();
            var matches = new List<SceneMatch>();
            foreach (var pair in pairs.OrderByDescending(x => x.Score))
            {
                if (usedLocal.Contains(pair.Local) || usedMap.Contains(pair.Map))
                    continue;
                usedLocal.Add(pair.Local);
                usedMap.Add(pair.Map);
                SceneAnchor g = map[pair.Map], l = local[pair.Local];
                matches.Add(new SceneMatch(g, l, pair.Score, SameThing(g.Label, l.Label)));
            }
            return matches;
        }

        /// <summary>The evidence points one pair earns: how much they overlap, times how well the labels agree.</summary>
        private static float Score(SceneAnchor g, SceneAnchor l, PoseTransform transform, SceneAnchorMapParameters p)
        {
            float weight = LabelPoints(g.Label, l.Label, p);
            if (weight <= 0f)
                return 0f;

            bool sameLabel = g.Label == l.Label;
            switch (g.Kind)
            {
                case SceneAnchorKind.Wall:
                    return l.Kind == SceneAnchorKind.Wall ? weight * PlaneOverlap(g, l, transform, p, intersectionOverUnion: sameLabel) : 0f;
                case SceneAnchorKind.Floor:
                case SceneAnchorKind.Ceiling:
                    return l.Kind == g.Kind ? weight * HorizontalOverlap(g, l, transform, p) : 0f;
                default:
                    return l.Kind == SceneAnchorKind.Volume ? weight * VolumeOverlap(g, l, transform, sameLabel) : 0f;
            }
        }

        /// <summary>The points two labels earn at best: all of them for the same label, a share for labels that can be the same thing seen differently.</summary>
        private static float LabelPoints(string globalLabel, string localLabel, SceneAnchorMapParameters p)
        {
            if (globalLabel == localLabel)
                return p.SameLabelPoints;

            bool gWall = IsWallLabel(globalLabel), lWall = IsWallLabel(localLabel);
            if (gWall && lWall)
                return p.WallVariantPoints;
            bool gOpening = IsOpeningLabel(globalLabel), lOpening = IsOpeningLabel(localLabel);
            if ((gWall && lOpening) || (gOpening && lWall) || (gOpening && lOpening))
                return p.OpeningInWallPoints;
            if (FurnitureLabels.Contains(globalLabel) && FurnitureLabels.Contains(localLabel))
                return p.FurnitureOtherLabelPoints;
            return 0f;
        }

        private static bool SameThing(string a, string b) => a == b || (IsWallLabel(a) && IsWallLabel(b));
        private static bool IsWallLabel(string label) => label == "WALL_FACE" || label == "INVISIBLE_WALL_FACE" || label == "INNER_WALL_FACE";
        private static bool IsOpeningLabel(string label) => label == "DOOR_FRAME" || label == "WINDOW_FRAME" || label == "WALL_ART";
        private static bool IsWall(SceneAnchor a) => IsWallLabel(a.Label);

        /// <summary>
        /// How much two vertical surfaces overlap: they must face the same way and lie in the same plane (the closer, the better); the ratio is the
        /// area where they overlap over the area of both together (same label), or over the smaller one (a door in a wall).
        /// </summary>
        private static float PlaneOverlap(SceneAnchor g, SceneAnchor l, PoseTransform transform, SceneAnchorMapParameters p, bool intersectionOverUnion)
        {
            Vector3 normal = Vector3.Transform(l.Normal, transform.Rotation);
            float cosine = Vector3.Dot(normal, g.Normal);
            if (cosine < MathF.Cos(p.NormalToleranceDegrees * MathF.PI / 180f))
                return 0f;

            Vector3 position = transform.Apply(l.Position);
            Vector3 d = position - g.Position;
            float across = MathF.Abs(Vector3.Dot(d, g.Normal));
            if (across > p.SurfaceToleranceMeters)
                return 0f;

            Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, g.Normal));
            float u = Vector3.Dot(d, tangent), v = d.Y; // the headset's anchor's centre in the map's anchor's plane
            float overlapU = IntervalOverlap(u - l.Size.X / 2f, u + l.Size.X / 2f, -g.Size.X / 2f, g.Size.X / 2f);
            float overlapV = IntervalOverlap(v - l.Size.Y / 2f, v + l.Size.Y / 2f, -g.Size.Y / 2f, g.Size.Y / 2f);
            float intersection = overlapU * overlapV;
            float areaL = l.Size.X * l.Size.Y, areaG = g.Size.X * g.Size.Y;
            float denominator = intersectionOverUnion ? areaL + areaG - intersection : Math.Min(areaL, areaG);
            if (denominator < 1e-6f)
                return 0f;

            float agreement = MathF.Exp(-(across / 0.15f) * (across / 0.15f));
            return Math.Clamp(intersection / denominator, 0f, 1f) * agreement;
        }

        /// <summary>How much two floors (or two ceilings) overlap seen from above, if they are at about the same height.</summary>
        private static float HorizontalOverlap(SceneAnchor g, SceneAnchor l, PoseTransform transform, SceneAnchorMapParameters p)
        {
            float dy = MathF.Abs(transform.Apply(l.Position).Y - g.Position.Y);
            if (dy > p.SurfaceToleranceMeters)
                return 0f;

            Footprint fg = FootprintOf(g, PoseTransform.Identity, g.Size.X, g.Size.Y, fallbackAxis: Vector3.UnitY);
            Footprint fl = FootprintOf(l, transform, l.Size.X, l.Size.Y, fallbackAxis: Vector3.UnitY);
            float intersection = fg.IntersectionArea(fl);
            float union = fg.Area + fl.Area - intersection;
            if (union < 1e-6f)
                return 0f;
            float agreement = MathF.Exp(-(dy / 0.15f) * (dy / 0.15f));
            return Math.Clamp(intersection / union, 0f, 1f) * agreement;
        }

        /// <summary>How much two pieces of furniture overlap as boxes: over both together (same label), or over the smaller one.</summary>
        private static float VolumeOverlap(SceneAnchor g, SceneAnchor l, PoseTransform transform, bool intersectionOverUnion)
        {
            float depthG = Math.Max(g.Size.Z, 0.05f), depthL = Math.Max(l.Size.Z, 0.05f);
            Footprint fg = FootprintOf(g, PoseTransform.Identity, g.Size.X, depthG, fallbackAxis: Vector3.UnitZ);
            Footprint fl = FootprintOf(l, transform, l.Size.X, depthL, fallbackAxis: Vector3.UnitZ);

            float yG = g.Position.Y, yL = transform.Apply(l.Position).Y;
            float overlapY = IntervalOverlap(yL - l.Size.Y / 2f, yL + l.Size.Y / 2f, yG - g.Size.Y / 2f, yG + g.Size.Y / 2f);
            float intersection = fg.IntersectionArea(fl) * overlapY;
            float volumeG = fg.Area * g.Size.Y, volumeL = fl.Area * l.Size.Y;
            float denominator = intersectionOverUnion ? volumeG + volumeL - intersection : Math.Min(volumeG, volumeL);
            if (denominator < 1e-6f)
                return 0f;
            return Math.Clamp(intersection / denominator, 0f, 1f);
        }

        /// <summary>
        /// The overlap of the walls (real and invisible, as one kind) of the headset with the map's, as a fraction of the walls of the smaller set,
        /// with this alignment.
        /// </summary>
        private static float WallOverlap(IReadOnlyList<SceneAnchor> map, IReadOnlyList<SceneAnchor> local, PoseTransform transform, SceneAnchorMapParameters p)
        {
            List<SceneAnchor> mapWalls = map.Where(IsWall).ToList(), localWalls = local.Where(IsWall).ToList();
            if (mapWalls.Count == 0 || localWalls.Count == 0)
                return 1f;

            var pairs = new List<(int Local, int Map, float Score)>();
            for (int li = 0; li < localWalls.Count; li++)
                for (int gi = 0; gi < mapWalls.Count; gi++)
                {
                    float score = PlaneOverlap(mapWalls[gi], localWalls[li], transform, p, intersectionOverUnion: false);
                    if (score > 0f)
                        pairs.Add((li, gi, score));
                }

            var usedLocal = new HashSet<int>();
            var usedMap = new HashSet<int>();
            float total = 0f;
            foreach (var pair in pairs.OrderByDescending(x => x.Score))
            {
                if (usedLocal.Contains(pair.Local) || usedMap.Contains(pair.Map))
                    continue;
                usedLocal.Add(pair.Local);
                usedMap.Add(pair.Map);
                total += pair.Score;
            }
            return total / Math.Min(localWalls.Count, mapWalls.Count);
        }

        private static float IntervalOverlap(float a0, float a1, float b0, float b1) => Math.Max(0f, Math.Min(a1, b1) - Math.Max(a0, b0));

        #endregion Evidence

        #region Footprints

        /// <summary>A rectangle seen from above (the footprint of a floor, a ceiling or a piece of furniture).</summary>
        private readonly struct Footprint
        {
            private readonly Vector2[] _corners;
            public float Area { get; }

            public Footprint(Vector2 centre, Vector2 axis, float halfA, float halfB)
            {
                var b = new Vector2(-axis.Y, axis.X);
                _corners = new[]
                {
                    centre - axis * halfA - b * halfB,
                    centre + axis * halfA - b * halfB,
                    centre + axis * halfA + b * halfB,
                    centre - axis * halfA + b * halfB,
                };
                Area = 4f * halfA * halfB;
            }

            /// <summary>The area two footprints share (clipping one rectangle by the edges of the other).</summary>
            public float IntersectionArea(Footprint other)
            {
                List<Vector2> polygon = _corners.ToList();
                for (int i = 0; i < 4 && polygon.Count > 0; i++)
                {
                    Vector2 a = other._corners[i], b = other._corners[(i + 1) % 4];
                    var clipped = new List<Vector2>();
                    for (int j = 0; j < polygon.Count; j++)
                    {
                        Vector2 current = polygon[j], next = polygon[(j + 1) % polygon.Count];
                        float sideCurrent = Cross(b - a, current - a), sideNext = Cross(b - a, next - a);
                        if (sideCurrent >= 0f)
                        {
                            clipped.Add(current);
                            if (sideNext < 0f)
                                clipped.Add(Cut(current, next, sideCurrent, sideNext));
                        }
                        else if (sideNext >= 0f)
                            clipped.Add(Cut(current, next, sideCurrent, sideNext));
                    }
                    polygon = clipped;
                }

                float twice = 0f;
                for (int i = 0; i < polygon.Count; i++)
                    twice += Cross(polygon[i], polygon[(i + 1) % polygon.Count]);
                return MathF.Abs(twice) / 2f;
            }

            private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

            private static Vector2 Cut(Vector2 from, Vector2 to, float sideFrom, float sideTo) => from + (to - from) * (sideFrom / (sideFrom - sideTo));
        }

        /// <summary>
        /// The footprint of an anchor after a transform: centred where it is, turned the way its own horizontal axis points (its x axis, or the fallback
        /// axis if that one points up), <paramref name="sizeA"/> long along that axis and <paramref name="sizeB"/> across it.
        /// </summary>
        private static Footprint FootprintOf(SceneAnchor anchor, PoseTransform transform, float sizeA, float sizeB, Vector3 fallbackAxis)
        {
            Quaternion rotation = transform.Rotation * anchor.Pose.Rotation;
            Vector3 axis = Flatten(Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(fallbackAxis, rotation));
            Vector3 centre = transform.Apply(anchor.Position);
            return new Footprint(new Vector2(centre.X, centre.Z), new Vector2(axis.X, axis.Z), Math.Max(sizeA, 0.05f) / 2f, Math.Max(sizeB, 0.05f) / 2f);
        }

        /// <summary>The horizontal direction of a vector (unit length), or of the fallback if the vector points up or down, or the x axis if both do.</summary>
        private static Vector3 Flatten(Vector3 v, Vector3 fallback)
        {
            var flat = new Vector3(v.X, 0f, v.Z);
            if (flat.LengthSquared() >= 0.05f)
                return Vector3.Normalize(flat);
            flat = new Vector3(fallback.X, 0f, fallback.Z);
            return flat.LengthSquared() >= 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitX;
        }

        private static float Angle(Vector3 horizontal) => MathF.Atan2(horizontal.X, horizontal.Z);

        private static float WrapAngle(float radians)
        {
            while (radians > MathF.PI) radians -= 2f * MathF.PI;
            while (radians < -MathF.PI) radians += 2f * MathF.PI;
            return radians;
        }

        #endregion Footprints

        #region Fitting

        /// <summary>
        /// The transform that best takes the headset's anchors onto their matches. The turn is the average difference between the normals of the walls (what
        /// the walls say about the turn is far more exact than where their centres are). The shift is the least-squares solution of: every surface only
        /// constrains the position across it (a wall its normal, the floor and ceiling the height), a piece of furniture constrains all three directions;
        /// a small pull toward the plain average of the centres settles whatever is left unconstrained.
        /// </summary>
        private static PoseTransform Solve(List<SceneMatch> matches, PoseTransform start, out float rms, out float conditioning)
        {
            double sin = 0, cos = 0;
            int walls = 0;
            foreach (SceneMatch m in matches)
            {
                if (m.Local.Kind != SceneAnchorKind.Wall || m.Global.Kind != SceneAnchorKind.Wall)
                    continue;
                float yaw = Angle(m.Global.Normal) - Angle(m.Local.Normal);
                sin += Math.Sin(yaw) * m.Score;
                cos += Math.Cos(yaw) * m.Score;
                walls++;
            }
            float turn = walls > 0 && sin * sin + cos * cos > 1e-9 ? (float)Math.Atan2(sin, cos) : start.Yaw;
            Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn);

            var m3 = new double[3, 3];
            var rhs = new double[3];
            Vector3 mean = Vector3.Zero;
            foreach (SceneMatch m in matches)
            {
                Vector3 d = m.Global.Position - Vector3.Transform(m.Local.Position, rotation);
                mean += d;
                float w = (m.Global.Kind == SceneAnchorKind.Volume ? 0.5f : 1f) * m.Score;
                foreach (Vector3 n in ConstraintAxes(m.Global))
                {
                    double[] nv = { n.X, n.Y, n.Z };
                    double nd = n.X * d.X + n.Y * d.Y + n.Z * d.Z;
                    for (int i = 0; i < 3; i++)
                    {
                        rhs[i] += w * nv[i] * nd;
                        for (int j = 0; j < 3; j++)
                            m3[i, j] += w * nv[i] * nv[j];
                    }
                }
            }
            mean /= matches.Count;

            conditioning = Conditioning(m3);

            const double ridge = 0.02;
            double[] mv = { mean.X, mean.Y, mean.Z };
            for (int i = 0; i < 3; i++)
            {
                m3[i, i] += ridge;
                rhs[i] += ridge * mv[i];
            }
            double[] t = Solve3(m3, rhs);
            var transform = new PoseTransform(rotation, new Vector3((float)t[0], (float)t[1], (float)t[2]));
            rms = Rms(transform, matches);
            return transform;
        }

        /// <summary>How closely the matches agree under a transform (root mean square, metres), and how well they fix it in all directions (0 to 1).</summary>
        private static void Metrics(PoseTransform transform, List<SceneMatch> matches, out float rms, out float conditioning)
        {
            rms = Rms(transform, matches);
            var m3 = new double[3, 3];
            foreach (SceneMatch m in matches)
            {
                float w = (m.Global.Kind == SceneAnchorKind.Volume ? 0.5f : 1f) * m.Score;
                foreach (Vector3 n in ConstraintAxes(m.Global))
                {
                    double[] nv = { n.X, n.Y, n.Z };
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            m3[i, j] += w * nv[i] * nv[j];
                }
            }
            conditioning = Conditioning(m3);
        }

        private static float Rms(PoseTransform transform, List<SceneMatch> matches)
        {
            if (matches.Count == 0)
                return 0f;
            double sum = 0;
            foreach (SceneMatch m in matches)
            {
                float r = Residual(transform, m);
                sum += r * r;
            }
            return (float)Math.Sqrt(sum / matches.Count);
        }

        /// <summary>How far a matched pair is from lining up, in the directions the anchor constrains.</summary>
        private static float Residual(PoseTransform transform, SceneMatch m)
        {
            Vector3 d = transform.Apply(m.Local.Position) - m.Global.Position;
            return m.Global.Kind switch
            {
                SceneAnchorKind.Wall => MathF.Abs(Vector3.Dot(d, m.Global.Normal)),
                SceneAnchorKind.Floor or SceneAnchorKind.Ceiling => MathF.Abs(d.Y),
                _ => d.Length(),
            };
        }

        private static IEnumerable<Vector3> ConstraintAxes(SceneAnchor anchor)
        {
            if (anchor.Kind == SceneAnchorKind.Volume)
            {
                yield return Vector3.UnitX;
                yield return Vector3.UnitY;
                yield return Vector3.UnitZ;
            }
            else
                yield return anchor.Normal;
        }

        /// <summary>Determinant over (trace/3)^3 of the constraint matrix: 1 when all three directions are fixed equally well, 0 when one is not fixed at all.</summary>
        private static float Conditioning(double[,] m)
        {
            double trace = m[0, 0] + m[1, 1] + m[2, 2];
            if (trace < 1e-9)
                return 0f;
            double det = Det3(m);
            double scale = trace / 3.0;
            return (float)Math.Max(0.0, det / (scale * scale * scale));
        }

        private static double Det3(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
            - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static double[] Solve3(double[,] m, double[] b)
        {
            double det = Det3(m);
            if (Math.Abs(det) < 1e-12)
                return new double[3];
            var result = new double[3];
            for (int k = 0; k < 3; k++)
            {
                var c = (double[,])m.Clone();
                for (int i = 0; i < 3; i++)
                    c[i, k] = b[i];
                result[k] = Det3(c) / det;
            }
            return result;
        }

        #endregion Fitting
    }
}
