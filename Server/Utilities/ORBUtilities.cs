using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Server.Utilities
{
    // ======================================================================================
    //  Parameter types
    // ======================================================================================

    /// <summary>Tuning knobs for <see cref="ORBUtilities.ExtractFeatures"/>. Defaults mirror ORB-SLAM.</summary>
    public sealed class ORBExtractorParameters
    {
        public static ORBExtractorParameters Default { get; } = new();

        /// <summary>Approximate number of features to keep over all pyramid levels (soft target, as in ORB-SLAM).</summary>
        public int FeatureCount { get; init; } = 1000;

        /// <summary>Scale ratio between consecutive pyramid levels. Must be &gt; 1.</summary>
        public float ScaleFactor { get; init; } = 1.2f;

        /// <summary>Number of pyramid levels. Levels that would be smaller than 40 px are dropped automatically.</summary>
        public int LevelCount { get; init; } = 8;

        /// <summary>FAST threshold tried first in every grid cell.</summary>
        public int InitialFastThreshold { get; init; } = 20;

        /// <summary>FAST threshold used for cells where the initial threshold found nothing (low-texture regions).</summary>
        public int MinFastThreshold { get; init; } = 7;

        /// <summary>Side length (px) of the grid cells used for FAST detection.</summary>
        public int CellSize { get; init; } = 30;

        /// <summary>&gt; 1 processes pyramid levels in parallel. Leave at 1 if the server already handles requests concurrently.</summary>
        public int MaxDegreeOfParallelism { get; init; } = 1;
    }

    /// <summary>Tuning knobs for <see cref="ORBUtilities.CompareFeatures"/>.</summary>
    public sealed class ORBMatchParameters
    {
        public static ORBMatchParameters Default { get; } = new();

        /// <summary>Lowe ratio test: accept if best &lt; ratio * secondBest. Smaller = stricter.</summary>
        public float RatioThreshold { get; init; } = 0.75f;

        /// <summary>Maximum Hamming distance (out of 256 bits) for a match. ORB-SLAM uses 50 (strict) to 100 (loose).</summary>
        public int MaxDistance { get; init; } = 100;

        /// <summary>If several query features pick the same reference feature, only the closest one survives.</summary>
        public bool OneToOne { get; init; } = true;

        /// <summary>Keep only matches whose keypoint-angle difference falls in the three dominant histogram bins (ORB-SLAM's rotation check).</summary>
        public bool UseRotationConsistency { get; init; } = true;

        /// <summary>RANSAC iterations for the 2D similarity fit (rotation, scale, translation). 0 disables geometric verification.</summary>
        public int RansacIterations { get; init; } = 100;

        /// <summary>Reprojection tolerance (px, level-0 coordinates) for counting a match as a RANSAC inlier.</summary>
        public double InlierThresholdPixels { get; init; } = 6.0;

        /// <summary>Minimum inliers for the geometric fit to be considered valid.</summary>
        public int MinInliers { get; init; } = 8;

        /// <summary>Candidate models with a scale outside [1/x, x] are rejected.</summary>
        public double MaxScaleChange { get; init; } = 4.0;

        /// <summary>&gt; 1 parallelises the brute-force search (only kicks in for large problems).</summary>
        public int MaxDegreeOfParallelism { get; init; } = 1;
    }

    // ======================================================================================
    //  Feature container
    // ======================================================================================

    /// <summary>A detected ORB keypoint. Coordinates are in level-0 (original image) pixels.</summary>
    /// <param name="X">Horizontal position in the original image.</param>
    /// <param name="Y">Vertical position in the original image (image axes: x right, y down).</param>
    /// <param name="Angle">Orientation in degrees, [0, 360), measured from +x towards +y.</param>
    /// <param name="Response">FAST corner score (higher = stronger corner).</param>
    /// <param name="Octave">Pyramid level the keypoint was detected on.</param>
    public readonly record struct ORBKeypoint(float X, float Y, float Angle, float Response, int Octave);

    /// <summary>
    /// ORB features of one image: keypoints plus 256-bit descriptors stored flat as 4 ulongs per feature
    /// (<c>Descriptors[4*i .. 4*i+3]</c> belongs to <c>Keypoints[i]</c>). Use
    /// <c>MemoryMarshal.AsBytes(Descriptors)</c> for a 32-byte-per-feature view when serialising.
    /// </summary>
    public sealed class ORBFeatureSet
    {
        public const int DescriptorLongs = 4;
        public const int DescriptorBytes = 32;

        public static ORBFeatureSet Empty { get; } = new(0, 0, 1f, 0, Array.Empty<ORBKeypoint>(), Array.Empty<ulong>());

        public int Width { get; }
        public int Height { get; }
        public float ScaleFactor { get; }
        public int LevelCount { get; }
        public ORBKeypoint[] Keypoints { get; }
        public ulong[] Descriptors { get; }
        public int Count => Keypoints.Length;

        public ORBFeatureSet(int width, int height, float scaleFactor, int levelCount, ORBKeypoint[] keypoints, ulong[] descriptors)
        {
            ArgumentNullException.ThrowIfNull(keypoints);
            ArgumentNullException.ThrowIfNull(descriptors);
            if (descriptors.Length != keypoints.Length * DescriptorLongs)
            {
                throw new ArgumentException("Descriptors must hold exactly 4 ulongs per keypoint.", nameof(descriptors));
            }

            Width = width;
            Height = height;
            ScaleFactor = scaleFactor;
            LevelCount = levelCount;
            Keypoints = keypoints;
            Descriptors = descriptors;
        }

        public ReadOnlySpan<ulong> GetDescriptor(int index) => Descriptors.AsSpan(index * DescriptorLongs, DescriptorLongs);
    }

    // ======================================================================================
    //  Match result types
    // ======================================================================================

    /// <summary>One descriptor correspondence.</summary>
    public readonly record struct ORBMatch(int QueryIndex, int ReferenceIndex, int Distance);

    /// <summary>
    /// 2D similarity transform (uniform scale, in-plane rotation, translation) mapping
    /// <b>reference</b> pixel coordinates to <b>query</b> pixel coordinates. Image axes (x right, y down).
    /// </summary>
    public readonly record struct Similarity2D(double Scale, double RotationRadians, double TranslationX, double TranslationY)
    {
        public static Similarity2D Identity => new(1.0, 0.0, 0.0, 0.0);

        public double RotationDegrees => RotationRadians * (180.0 / Math.PI);

        public (double X, double Y) Apply(double x, double y)
        {
            double c = Scale * Math.Cos(RotationRadians);
            double s = Scale * Math.Sin(RotationRadians);
            return (c * x - s * y + TranslationX, s * x + c * y + TranslationY);
        }
    }

    /// <summary>Everything ORB-style matching produces when comparing a query frame against a reference.</summary>
    public sealed class ORBMatchResult
    {
        public int QueryCount { get; init; }
        public int ReferenceCount { get; init; }

        /// <summary>Matches that passed ratio test, distance limit, one-to-one and rotation-consistency filters.</summary>
        public ORBMatch[] Matches { get; init; } = Array.Empty<ORBMatch>();

        /// <summary>Subset of <see cref="Matches"/> consistent with <see cref="Transform"/>. Empty when verification is disabled or failed.</summary>
        public ORBMatch[] Inliers { get; init; } = Array.Empty<ORBMatch>();

        /// <summary>Dominant in-plane rotation (degrees, (-180, 180]) from the keypoint-angle histogram: query angle minus reference angle. 0 when there are no matches.</summary>
        public double DominantRotationDegrees { get; init; }

        /// <summary>True when the RANSAC similarity fit produced at least <see cref="ORBMatchParameters.MinInliers"/> inliers.</summary>
        public bool HasTransform { get; init; }

        /// <summary>2D similarity (reference -&gt; query). Identity when <see cref="HasTransform"/> is false.</summary>
        public Similarity2D Transform { get; init; } = Similarity2D.Identity;

        /// <summary>Matches counted as evidence: inliers when verification ran, otherwise all good matches.</summary>
        public int VerifiedMatchCount { get; init; }

        /// <summary>Mean Hamming distance (0..256) over the verified matches; 256 when there are none.</summary>
        public double MeanDistance { get; init; } = 256.0;

        /// <summary>Verified matches divided by min(QueryCount, ReferenceCount), in [0, 1].</summary>
        public double MatchRatio { get; init; }

        /// <summary>Single score in [0, 1]: <c>MatchRatio * (1 - MeanDistance / 256)</c>. 0 when nothing could be verified.</summary>
        public double Similarity { get; init; }

        public int GoodMatchCount => Matches.Length;
        public int InlierCount => Inliers.Length;

        internal static ORBMatchResult CreateEmpty(int queryCount, int referenceCount) =>
            new() { QueryCount = queryCount, ReferenceCount = referenceCount };
    }

    // ======================================================================================
    //  ORBUtilities
    // ======================================================================================

    /// <summary>
    /// Dependency-free ORB (oriented FAST + rotated BRIEF) following ORB-SLAM's extractor:
    /// 8-level pyramid, per-cell FAST with adaptive threshold, octree keypoint distribution,
    /// intensity-centroid orientation and 256-bit steered BRIEF on a Gaussian-blurred level.
    /// All methods are thread-safe (no shared mutable state).
    /// </summary>
    public static class ORBUtilities
    {
        // Padding around every pyramid level. Largest read from a keypoint is the steered BRIEF
        // pattern (|offset| <= round(13 * sqrt 2) = 18) so 19 keeps every access inside the buffer.
        private const int EdgeThreshold = 19;
        private const int HalfPatchSize = 15;
        private const int MinLevelSize = 2 * EdgeThreshold + 2;
        private const int HistogramBins = 30;

        // 256 learned test pairs (x0, y0, x1, y1) from the ORB paper, as shipped in OpenCV's orb.cpp.
        private static readonly sbyte[] Pattern =
        {
            8, -3, 9, 5, 4, 2, 7, -12, -11, 9, -8, 2, 7, -12, 12, -13,
            2, -13, 2, 12, 1, -7, 1, 6, -2, -10, -2, -4, -13, -13, -11, -8,
            -13, -3, -12, -9, 10, 4, 11, 9, -13, -8, -8, -9, -11, 7, -9, 12,
            7, 7, 12, 6, -4, -5, -3, 0, -13, 2, -12, -3, -9, 0, -7, 5,
            12, -6, 12, -1, -3, 6, -2, 12, -6, -13, -4, -8, 11, -13, 12, -8,
            4, 7, 5, 1, 5, -3, 10, -3, 3, -7, 6, 12, -8, -7, -6, -2,
            -2, 11, -1, -10, -13, 12, -8, 10, -7, 3, -5, -3, -4, 2, -3, 7,
            -10, -12, -6, 11, 5, -12, 6, -7, 5, -6, 7, -1, 1, 0, 4, -5,
            9, 11, 11, -13, 4, 7, 4, 12, 2, -1, 4, 4, -4, -12, -2, 7,
            -8, -5, -7, -10, 4, 11, 9, 12, 0, -8, 1, -13, -13, -2, -8, 2,
            -3, -2, -2, 3, -6, 9, -4, -9, 8, 12, 10, 7, 0, 9, 1, 3,
            7, -5, 11, -10, -13, -6, -11, 0, 10, 7, 12, 1, -6, -3, -6, 12,
            10, -9, 12, -4, -13, 8, -8, -12, -13, 0, -8, -4, 3, 3, 7, 8,
            5, 7, 10, -7, -1, 7, 1, -12, 3, -10, 5, 6, 2, -4, 3, -10,
            -13, 0, -13, 5, -13, -7, -12, 12, -13, 3, -11, 8, -7, 12, -4, 7,
            6, -10, 12, 8, -9, -1, -7, -6, -2, -5, 0, 12, -12, 5, -7, 5,
            3, -10, 8, -13, -7, -7, -4, 5, -3, -2, -1, -7, 2, 9, 5, -11,
            -11, -13, -5, -13, -1, 6, 0, -1, 5, -3, 5, 2, -4, -13, -4, 12,
            -9, -6, -9, 6, -12, -10, -8, -4, 10, 2, 12, -3, 7, 12, 12, 12,
            -7, -13, -6, 5, -4, 9, -3, 4, 7, -1, 12, 2, -7, 6, -5, 1,
            -13, 11, -12, 5, -3, 7, -2, -6, 7, -8, 12, -7, -13, -7, -11, -12,
            1, -3, 12, 12, 2, -6, 3, 0, -4, 3, -2, -13, -1, -13, 1, 9,
            7, 1, 8, -6, 1, -1, 3, 12, 9, 1, 12, 6, -1, -9, -1, 3,
            -13, -13, -10, 5, 7, 7, 10, 12, 12, -5, 12, 9, 6, 3, 7, 11,
            5, -13, 6, 10, 2, -12, 2, 3, 3, 8, 4, -6, 2, 6, 12, -13,
            9, -12, 10, 3, -8, 4, -7, 9, -11, 12, -4, -6, 1, 12, 2, -8,
            6, -9, 7, -4, 2, 3, 3, -2, 6, 3, 11, 0, 3, -3, 8, -8,
            7, 8, 9, 3, -11, -5, -6, -4, -10, 11, -5, 10, -5, -8, -3, 12,
            -10, 5, -9, 0, 8, -1, 12, -6, 4, -6, 6, -11, -10, 12, -8, 7,
            4, -2, 6, 7, -2, 0, -2, 12, -5, -8, -5, 2, 7, -6, 10, 12,
            -9, -13, -8, -8, -5, -13, -5, -2, 8, -8, 9, -13, -9, -11, -9, 0,
            1, -8, 1, -2, 7, -4, 9, 1, -2, 1, -1, -4, 11, -6, 12, -11,
            -12, -9, -6, 4, 3, 7, 7, 12, 5, 5, 10, 8, 0, -4, 2, 8,
            -9, 12, -5, -13, 0, 7, 2, 12, -1, 2, 1, 7, 5, 11, 7, -9,
            3, 5, 6, -8, -13, -4, -8, 9, -5, 9, -3, -3, -4, -7, -3, -12,
            6, 5, 8, 0, -7, 6, -6, 12, -13, 6, -5, -2, 1, -10, 3, 10,
            4, 1, 8, -4, -2, -2, 2, -13, 2, -12, 12, 12, -2, -13, 0, -6,
            4, 1, 9, 3, -6, -10, -3, -5, -3, -13, -1, 1, 7, 5, 12, -11,
            4, -2, 5, -7, -13, 9, -9, -5, 7, 1, 8, 6, 7, -8, 7, 6,
            -7, -4, -7, 1, -8, 11, -7, -8, -13, 6, -12, -8, 2, 4, 3, 9,
            10, -5, 12, 3, -6, -5, -6, 7, 8, -3, 9, -8, 2, -12, 2, 8,
            -11, -2, -10, 3, -12, -13, -7, -9, -11, 0, -10, -5, 5, -3, 11, 8,
            -2, -13, -1, 12, -1, -8, 0, 9, -13, -11, -12, -5, -10, -2, -10, 11,
            -3, 9, -2, -13, 2, -3, 3, 2, -9, -13, -4, 0, -4, 6, -3, -10,
            -4, 12, -2, -7, -6, -11, -4, 9, 6, -3, 6, 11, -13, 11, -5, 5,
            11, 11, 12, 6, 7, -5, 12, -2, -1, 12, 0, 7, -4, -8, -3, -2,
            -7, 1, -6, 7, -13, -12, -8, -13, -7, -2, -6, -8, -8, 5, -6, -9,
            -5, -1, -4, 5, -13, 7, -8, 10, 1, 5, 5, -13, 1, 0, 10, -13,
            9, 12, 10, -1, 5, -8, 10, -9, -1, 11, 1, -13, -9, -3, -6, 2,
            -1, -10, 1, 12, -13, 1, -8, -10, 8, -11, 10, -6, 2, -13, 3, -6,
            7, -13, 12, -9, -10, -10, -5, -7, -10, -8, -8, -13, 4, -6, 8, 5,
            3, 12, 8, -13, -4, 2, -3, -3, 5, -13, 10, -12, 4, -13, 5, -1,
            -9, 9, -4, 3, 0, 3, 3, -9, -12, 1, -6, 1, 3, 2, 4, -8,
            -10, -10, -10, 9, 8, -13, 12, 12, -8, -12, -6, -5, 2, 2, 3, 7,
            10, 6, 11, -8, 6, 8, 8, -12, -7, 10, -6, 5, -3, -9, -3, 9,
            -1, -13, -1, 5, -3, -7, -3, 4, -8, -2, -8, 3, 4, 2, 12, 12,
            2, -5, 3, 11, 6, -9, 11, -13, 3, -1, 7, 12, 11, -1, 12, 4,
            -3, 0, -3, 6, 4, -11, 4, 12, 2, -4, 2, 1, -10, -6, -8, 1,
            -13, 7, -11, 1, -13, 12, -11, -13, 6, 0, 11, -13, 0, -1, 1, 4,
            -13, 3, -9, -2, -9, 8, -6, -3, -13, -6, -8, -2, 5, -9, 8, 10,
            2, 7, 3, -9, -1, -6, -1, -1, 9, 5, 11, -2, 11, -3, 12, -8,
            3, 0, 3, 5, -1, 4, 0, 10, 3, -6, 4, 5, -13, 0, -10, 5,
            5, 8, 12, 11, 8, 9, 9, -6, 7, -4, 8, -12, -10, 4, -10, 9,
            7, 3, 12, 4, 9, -7, 10, -2, 7, 0, 12, -2, -1, -6, 0, -11,
        };

        // Bresenham circle of radius 3, clockwise from the top. Indices 0, 4, 8, 12 are the compass points.
        private static readonly int[] CircleDx = { 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3, -3, -3, -2, -1 };
        private static readonly int[] CircleDy = { -3, -3, -2, -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3 };

        // Half-width of the circular orientation patch for each row (same construction as OpenCV).
        private static readonly int[] UMax = BuildUMax();

        // 7-tap Gaussian (sigma = 2) as 8-bit fixed point, centre first; sums to exactly 256.
        private static readonly int[] GaussKernel = BuildGaussKernel();

        // ----------------------------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// Extracts ORB keypoints and 256-bit descriptors from an 8-bit grayscale image.
        /// </summary>
        /// <param name="grayscale">Row-major pixels, <c>width * height</c> bytes, no row padding.</param>
        public static ORBFeatureSet ExtractFeatures(ReadOnlySpan<byte> grayscale, int width, int height, ORBExtractorParameters? parameters = null)
        {
            ORBExtractorParameters p = parameters ?? ORBExtractorParameters.Default;
            ValidateExtractorInput(grayscale.Length, width, height, p);

            // ---- pyramid geometry ----
            var levelSizes = new List<(int W, int H)>(p.LevelCount) { (width, height) };
            var scales = new List<float> { 1f };
            for (int i = 1; i < p.LevelCount; i++)
            {
                double scale = Math.Pow(p.ScaleFactor, i);
                int lw = (int)Math.Round(width / scale);
                int lh = (int)Math.Round(height / scale);
                if (lw < MinLevelSize || lh < MinLevelSize)
                {
                    break;
                }

                levelSizes.Add((lw, lh));
                scales.Add((float)scale);
            }

            int levels = levelSizes.Count;
            int[] budget = ComputeFeatureBudget(p.FeatureCount, p.ScaleFactor, levels);
            var planes = new Plane[levels];
            var results = new LevelResult[levels];

            try
            {
                // ---- build the pyramid (sequential: each level is resized from the previous one) ----
                planes[0] = new Plane(width, height);
                for (int y = 0; y < height; y++)
                {
                    grayscale.Slice(y * width, width).CopyTo(planes[0].Data.AsSpan(planes[0].Origin + y * planes[0].Stride, width));
                }

                FillBorder(planes[0]);

                for (int l = 1; l < levels; l++)
                {
                    planes[l] = new Plane(levelSizes[l].W, levelSizes[l].H);
                    ResizeBilinear(planes[l - 1], planes[l]);
                    FillBorder(planes[l]);
                }

                // ---- per-level detection / orientation / description (independent) ----
                if (p.MaxDegreeOfParallelism > 1 && levels > 1)
                {
                    Parallel.For(0, levels, new ParallelOptions { MaxDegreeOfParallelism = p.MaxDegreeOfParallelism },
                        l => results[l] = ProcessLevel(planes[l], l, scales[l], budget[l], p));
                }
                else
                {
                    for (int l = 0; l < levels; l++)
                    {
                        results[l] = ProcessLevel(planes[l], l, scales[l], budget[l], p);
                    }
                }
            }
            finally
            {
                foreach (Plane? plane in planes)
                {
                    plane?.Release();
                }
            }

            // ---- merge levels (coarse levels last, deterministic order) ----
            int total = 0;
            foreach (LevelResult r in results)
            {
                total += r.Keypoints.Length;
            }

            var keypoints = new ORBKeypoint[total];
            var descriptors = new ulong[total * ORBFeatureSet.DescriptorLongs];
            int offset = 0;
            foreach (LevelResult r in results)
            {
                Array.Copy(r.Keypoints, 0, keypoints, offset, r.Keypoints.Length);
                Array.Copy(r.Descriptors, 0, descriptors, offset * ORBFeatureSet.DescriptorLongs, r.Descriptors.Length);
                offset += r.Keypoints.Length;
            }

            return new ORBFeatureSet(width, height, p.ScaleFactor, levels, keypoints, descriptors);
        }

        /// <summary>
        /// Compares a query frame against a reference (another frame or, later, the merged global map) and returns the
        /// match set with ORB's similarity statistics: Hamming-distance matches (ratio test, distance limit, one-to-one),
        /// rotation-consistency filtering, a RANSAC 2D similarity fit (reference -&gt; query) and aggregate scores.
        /// </summary>
        public static ORBMatchResult CompareFeatures(ORBFeatureSet query, ORBFeatureSet reference, ORBMatchParameters? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(reference);
            ORBMatchParameters p = parameters ?? ORBMatchParameters.Default;
            ValidateMatchParameters(p);

            int nq = query.Count;
            int nr = reference.Count;
            if (nq == 0 || nr == 0)
            {
                return ORBMatchResult.CreateEmpty(nq, nr);
            }

            // ---- 1. brute-force 2-nearest-neighbour in Hamming space + ratio / distance tests ----
            var bestRef = new int[nq];
            var bestDist = new int[nq];
            ulong[] qd = query.Descriptors;
            ulong[] rd = reference.Descriptors;

            void MatchOne(int q)
            {
                int r = FindBestMatch(qd, q, rd, nr, out int best, out int second);
                bool accept = r >= 0
                              && best <= p.MaxDistance
                              && (second == int.MaxValue || best < p.RatioThreshold * second);
                bestRef[q] = accept ? r : -1;
                bestDist[q] = best;
            }

            if (p.MaxDegreeOfParallelism > 1 && (long)nq * nr >= 200_000)
            {
                Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = p.MaxDegreeOfParallelism }, MatchOne);
            }
            else
            {
                for (int q = 0; q < nq; q++)
                {
                    MatchOne(q);
                }
            }

            // ---- 2. one-to-one: when several queries pick the same reference, the closest wins ----
            int[]? owner = null;
            if (p.OneToOne)
            {
                owner = new int[nr];
                Array.Fill(owner, -1);
                for (int q = 0; q < nq; q++)
                {
                    int r = bestRef[q];
                    if (r >= 0 && (owner[r] < 0 || bestDist[q] < bestDist[owner[r]]))
                    {
                        owner[r] = q;
                    }
                }
            }

            var candidates = new List<ORBMatch>(Math.Min(nq, nr));
            for (int q = 0; q < nq; q++)
            {
                int r = bestRef[q];
                if (r >= 0 && (owner is null || owner[r] == q))
                {
                    candidates.Add(new ORBMatch(q, r, bestDist[q]));
                }
            }

            ORBMatch[] matches = candidates.ToArray();

            // ---- 3. rotation consistency (also yields the dominant in-plane rotation) ----
            double dominantRotation = 0.0;
            if (matches.Length > 0)
            {
                matches = FilterByRotation(matches, query.Keypoints, reference.Keypoints, p.UseRotationConsistency, out dominantRotation);
            }

            // ---- 4. geometric verification: 2D similarity RANSAC ----
            ORBMatch[] verified = matches;
            ORBMatch[] inliers = Array.Empty<ORBMatch>();
            bool hasTransform = false;
            Similarity2D transform = Similarity2D.Identity;

            if (p.RansacIterations > 0)
            {
                if (EstimateSimilarity(matches, query.Keypoints, reference.Keypoints, p, out transform, out inliers))
                {
                    hasTransform = true;
                    verified = inliers;
                }
                else
                {
                    transform = Similarity2D.Identity;
                    inliers = Array.Empty<ORBMatch>();
                    verified = Array.Empty<ORBMatch>();
                }
            }

            // ---- 5. aggregate scores ----
            double meanDistance = 256.0;
            double matchRatio = 0.0;
            double similarity = 0.0;
            if (verified.Length > 0)
            {
                long sum = 0;
                foreach (ORBMatch m in verified)
                {
                    sum += m.Distance;
                }

                meanDistance = (double)sum / verified.Length;
                matchRatio = Math.Min(1.0, (double)verified.Length / Math.Min(nq, nr));
                similarity = matchRatio * (1.0 - meanDistance / 256.0);
            }

            return new ORBMatchResult
            {
                QueryCount = nq,
                ReferenceCount = nr,
                Matches = matches,
                Inliers = inliers,
                DominantRotationDegrees = dominantRotation,
                HasTransform = hasTransform,
                Transform = transform,
                VerifiedMatchCount = verified.Length,
                MeanDistance = meanDistance,
                MatchRatio = matchRatio,
                Similarity = similarity,
            };
        }

        /// <summary>Hamming distance between two 256-bit descriptors (4 ulongs each).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int HammingDistance(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
        {
            return BitOperations.PopCount(a[0] ^ b[0])
                 + BitOperations.PopCount(a[1] ^ b[1])
                 + BitOperations.PopCount(a[2] ^ b[2])
                 + BitOperations.PopCount(a[3] ^ b[3]);
        }

        // ----------------------------------------------------------------------------------
        //  Validation
        // ----------------------------------------------------------------------------------

        private static void ValidateExtractorInput(int length, int width, int height, ORBExtractorParameters p)
        {
            if (width < MinLevelSize || height < MinLevelSize)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"Image must be at least {MinLevelSize}x{MinLevelSize} pixels.");
            }

            if ((long)width * height > length)
            {
                throw new ArgumentException($"Buffer holds {length} bytes but {width}x{height} needs {(long)width * height}.");
            }

            if (p.FeatureCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(p), "FeatureCount must be >= 1.");
            }

            if (p.LevelCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(p), "LevelCount must be >= 1.");
            }

            if (!(p.ScaleFactor > 1f))
            {
                throw new ArgumentOutOfRangeException(nameof(p), "ScaleFactor must be > 1.");
            }

            if (p.MinFastThreshold < 1 || p.InitialFastThreshold > 255 || p.MinFastThreshold > p.InitialFastThreshold)
            {
                throw new ArgumentOutOfRangeException(nameof(p), "Require 1 <= MinFastThreshold <= InitialFastThreshold <= 255.");
            }

            if (p.CellSize < 4)
            {
                throw new ArgumentOutOfRangeException(nameof(p), "CellSize must be >= 4.");
            }
        }

        private static void ValidateMatchParameters(ORBMatchParameters p)
        {
            if (!(p.RatioThreshold > 0f) || p.MaxDistance < 0 || p.RansacIterations < 0 || !(p.MaxScaleChange >= 1.0) || !(p.InlierThresholdPixels > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(p), "Invalid ORBMatchParameters.");
            }
        }

        // ----------------------------------------------------------------------------------
        //  Static tables
        // ----------------------------------------------------------------------------------

        private static int[] BuildUMax()
        {
            var umax = new int[HalfPatchSize + 2];
            int vmax = (int)Math.Floor(HalfPatchSize * Math.Sqrt(2.0) / 2 + 1);
            int vmin = (int)Math.Ceiling(HalfPatchSize * Math.Sqrt(2.0) / 2);
            for (int v = 0; v <= vmax; v++)
            {
                umax[v] = (int)Math.Round(Math.Sqrt((double)HalfPatchSize * HalfPatchSize - v * v));
            }

            // Make the circle symmetric about the 45-degree diagonal.
            for (int v = HalfPatchSize, v0 = 0; v >= vmin; v--)
            {
                while (umax[v0] == umax[v0 + 1])
                {
                    v0++;
                }

                umax[v] = v0;
                v0++;
            }

            return umax;
        }

        private static int[] BuildGaussKernel()
        {
            const double sigma = 2.0;
            var g = new double[4];
            double sum = 0;
            for (int i = 0; i < 4; i++)
            {
                g[i] = Math.Exp(-(i * i) / (2 * sigma * sigma));
                sum += i == 0 ? g[i] : 2 * g[i];
            }

            var k = new int[4];
            for (int i = 1; i < 4; i++)
            {
                k[i] = (int)Math.Round(256.0 * g[i] / sum);
            }

            k[0] = 256 - 2 * (k[1] + k[2] + k[3]);
            return k;
        }

        // ----------------------------------------------------------------------------------
        //  Pyramid
        // ----------------------------------------------------------------------------------

        /// <summary>One pyramid level in a pooled buffer with a reflect-101 border of <see cref="EdgeThreshold"/> pixels on every side.</summary>
        private sealed class Plane
        {
            public readonly int Width;
            public readonly int Height;
            public readonly int Stride;
            public readonly int Origin; // index of pixel (0, 0)
            public readonly int Length; // bytes used, including padding
            public readonly byte[] Data;

            public Plane(int width, int height)
            {
                Width = width;
                Height = height;
                Stride = width + 2 * EdgeThreshold;
                Origin = EdgeThreshold * Stride + EdgeThreshold;
                Length = Stride * (height + 2 * EdgeThreshold);
                Data = ArrayPool<byte>.Shared.Rent(Length);
            }

            public void Release() => ArrayPool<byte>.Shared.Return(Data);
        }

        private static void FillBorder(Plane p)
        {
            int w = p.Width, h = p.Height, s = p.Stride;
            byte[] d = p.Data;

            for (int y = 0; y < h; y++)
            {
                int row = p.Origin + y * s;
                for (int k = 1; k <= EdgeThreshold; k++)
                {
                    d[row - k] = d[row + k];
                    d[row + w - 1 + k] = d[row + w - 1 - k];
                }
            }

            int firstRow = EdgeThreshold * s; // index of (x = -EdgeThreshold, y = 0)
            for (int k = 1; k <= EdgeThreshold; k++)
            {
                Array.Copy(d, firstRow + k * s, d, firstRow - k * s, s);
                Array.Copy(d, firstRow + (h - 1 - k) * s, d, firstRow + (h - 1 + k) * s, s);
            }
        }

        /// <summary>Bilinear resize with 11-bit fixed-point weights and centre-aligned sampling (same convention as cv::resize INTER_LINEAR).</summary>
        private static void ResizeBilinear(Plane src, Plane dst)
        {
            int sw = src.Width, sh = src.Height, dw = dst.Width, dh = dst.Height;
            double sx = (double)sw / dw, sy = (double)sh / dh;

            int[] xi = ArrayPool<int>.Shared.Rent(dw);
            int[] xw1 = ArrayPool<int>.Shared.Rent(dw);
            try
            {
                for (int dx = 0; dx < dw; dx++)
                {
                    SamplePosition((dx + 0.5) * sx - 0.5, sw, out xi[dx], out xw1[dx]);
                }

                byte[] s = src.Data;
                byte[] d = dst.Data;
                for (int dy = 0; dy < dh; dy++)
                {
                    SamplePosition((dy + 0.5) * sy - 0.5, sh, out int iy, out int wy1);
                    int wy0 = 2048 - wy1;
                    int row0 = src.Origin + iy * src.Stride;
                    int row1 = row0 + src.Stride;
                    int outRow = dst.Origin + dy * dst.Stride;

                    for (int dx = 0; dx < dw; dx++)
                    {
                        int ix = xi[dx];
                        int wx1 = xw1[dx];
                        int wx0 = 2048 - wx1;
                        int top = s[row0 + ix] * wx0 + s[row0 + ix + 1] * wx1;
                        int bot = s[row1 + ix] * wx0 + s[row1 + ix + 1] * wx1;
                        d[outRow + dx] = (byte)((top * wy0 + bot * wy1 + (1 << 21)) >> 22);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(xi);
                ArrayPool<int>.Shared.Return(xw1);
            }
        }

        private static void SamplePosition(double f, int size, out int index, out int weight1)
        {
            int i = (int)Math.Floor(f);
            double frac = f - i;
            if (i < 0)
            {
                i = 0;
                frac = 0;
            }
            else if (i >= size - 1)
            {
                i = size - 2;
                frac = 1;
            }

            index = i;
            weight1 = (int)(frac * 2048 + 0.5);
        }

        /// <summary>
        /// Budget per level: geometric series so finer levels get more features (ORB-SLAM's allocation).
        /// </summary>
        private static int[] ComputeFeatureBudget(int total, float scaleFactor, int levels)
        {
            var result = new int[levels];
            double factor = 1.0 / scaleFactor;
            double desired = total * (1.0 - factor) / (1.0 - Math.Pow(factor, levels));
            int sum = 0;
            for (int i = 0; i < levels - 1; i++)
            {
                result[i] = (int)Math.Round(desired);
                sum += result[i];
                desired *= factor;
            }

            result[levels - 1] = Math.Max(total - sum, 0);
            return result;
        }

        // ----------------------------------------------------------------------------------
        //  Per-level pipeline
        // ----------------------------------------------------------------------------------

        private sealed class LevelResult
        {
            public static readonly LevelResult Empty = new(Array.Empty<ORBKeypoint>(), Array.Empty<ulong>());

            public readonly ORBKeypoint[] Keypoints;
            public readonly ulong[] Descriptors;

            public LevelResult(ORBKeypoint[] keypoints, ulong[] descriptors)
            {
                Keypoints = keypoints;
                Descriptors = descriptors;
            }
        }

        private static LevelResult ProcessLevel(Plane plane, int level, float scale, int budget, ORBExtractorParameters p)
        {
            if (budget <= 0)
            {
                return LevelResult.Empty;
            }

            CornerList raw = CornerList.Create(2048);
            CornerList kept = CornerList.Create(1024);
            byte[] scoreMap = ArrayPool<byte>.Shared.Rent(plane.Length);
            Corner[]? selected = null;
            byte[]? blurred = null;
            ushort[]? blurTemp = null;

            try
            {
                Array.Clear(scoreMap, 0, plane.Length);
                DetectCorners(plane, scoreMap, p, ref raw, ref kept);
                if (kept.Count == 0)
                {
                    return LevelResult.Empty;
                }

                selected = ArrayPool<Corner>.Shared.Rent(kept.Count);
                int n = DistributeOctree(kept.Items, kept.Count, plane.Width, plane.Height, budget, selected);

                // Orientation uses the un-blurred level; descriptors use the blurred one (as in ORB-SLAM).
                var keypoints = new ORBKeypoint[n];
                var angles = new float[n];
                for (int i = 0; i < n; i++)
                {
                    Corner c = selected[i];
                    float angle = ComputeAngle(plane.Data, plane.Origin + c.Y * plane.Stride + c.X, plane.Stride);
                    angles[i] = angle;
                    keypoints[i] = new ORBKeypoint(c.X * scale, c.Y * scale, angle, c.Response, level);
                }

                blurred = ArrayPool<byte>.Shared.Rent(plane.Length);
                blurTemp = ArrayPool<ushort>.Shared.Rent(plane.Length);
                GaussianBlur7(plane, blurred, blurTemp);

                var descriptors = new ulong[n * ORBFeatureSet.DescriptorLongs];
                for (int i = 0; i < n; i++)
                {
                    Corner c = selected[i];
                    ComputeDescriptor(blurred, plane.Origin + c.Y * plane.Stride + c.X, plane.Stride, angles[i], descriptors, i * ORBFeatureSet.DescriptorLongs);
                }

                return new LevelResult(keypoints, descriptors);
            }
            finally
            {
                raw.Release();
                kept.Release();
                ArrayPool<byte>.Shared.Return(scoreMap);
                if (selected is not null)
                {
                    ArrayPool<Corner>.Shared.Return(selected);
                }

                if (blurred is not null)
                {
                    ArrayPool<byte>.Shared.Return(blurred);
                }

                if (blurTemp is not null)
                {
                    ArrayPool<ushort>.Shared.Return(blurTemp);
                }
            }
        }

        // ----------------------------------------------------------------------------------
        //  FAST-9 detection
        // ----------------------------------------------------------------------------------

        private struct Corner
        {
            public int X;
            public int Y;
            public float Response;
        }

        private struct CornerList
        {
            public Corner[] Items;
            public int Count;

            public static CornerList Create(int capacity) => new() { Items = ArrayPool<Corner>.Shared.Rent(capacity) };

            public void Add(int x, int y, float response)
            {
                if (Count == Items.Length)
                {
                    Corner[] bigger = ArrayPool<Corner>.Shared.Rent(Items.Length * 2);
                    Array.Copy(Items, bigger, Count);
                    ArrayPool<Corner>.Shared.Return(Items);
                    Items = bigger;
                }

                Items[Count++] = new Corner { X = x, Y = y, Response = response };
            }

            public void Release()
            {
                if (Items is not null)
                {
                    ArrayPool<Corner>.Shared.Return(Items);
                    Items = null!;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Px(byte[] a, int i)
        {
            // Every caller indexes inside the padded plane by construction (see EdgeThreshold).
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), i);
        }

        /// <summary>
        /// Grid-based FAST-9: each cell uses the initial threshold and falls back to the low one when empty, so
        /// low-texture areas still contribute. Scores go into a shared map so non-maximum suppression works across cell borders.
        /// </summary>
        private static void DetectCorners(Plane plane, byte[] scoreMap, ORBExtractorParameters p, ref CornerList raw, ref CornerList kept)
        {
            int w = plane.Width, h = plane.Height, stride = plane.Stride;

            Span<int> off = stackalloc int[16];
            Span<int> diff = stackalloc int[16];
            for (int k = 0; k < 16; k++)
            {
                off[k] = CircleDy[k] * stride + CircleDx[k];
            }

            int nCols = Math.Max(1, w / p.CellSize);
            int nRows = Math.Max(1, h / p.CellSize);
            int cellW = (w + nCols - 1) / nCols;
            int cellH = (h + nRows - 1) / nRows;

            for (int cy = 0; cy < nRows; cy++)
            {
                int y0 = cy * cellH;
                int y1 = Math.Min(y0 + cellH, h);
                if (y0 >= y1)
                {
                    break;
                }

                for (int cx = 0; cx < nCols; cx++)
                {
                    int x0 = cx * cellW;
                    int x1 = Math.Min(x0 + cellW, w);
                    if (x0 >= x1)
                    {
                        break;
                    }

                    int found = DetectFastCell(plane.Data, plane.Origin, stride, scoreMap, p.InitialFastThreshold, x0, y0, x1, y1, off, diff, ref raw);
                    if (found == 0 && p.MinFastThreshold < p.InitialFastThreshold)
                    {
                        DetectFastCell(plane.Data, plane.Origin, stride, scoreMap, p.MinFastThreshold, x0, y0, x1, y1, off, diff, ref raw);
                    }
                }
            }

            // Non-maximum suppression over the 8-neighbourhood. Equal scores: the neighbour that comes first in raster order wins.
            byte[] sm = scoreMap;
            for (int i = 0; i < raw.Count; i++)
            {
                Corner c = raw.Items[i];
                int idx = plane.Origin + c.Y * stride + c.X;
                int s = sm[idx];
                if (sm[idx - stride - 1] >= s || sm[idx - stride] >= s || sm[idx - stride + 1] >= s || sm[idx - 1] >= s)
                {
                    continue;
                }

                if (sm[idx + 1] > s || sm[idx + stride - 1] > s || sm[idx + stride] > s || sm[idx + stride + 1] > s)
                {
                    continue;
                }

                kept.Add(c.X, c.Y, s);
            }
        }

        private static int DetectFastCell(byte[] img, int origin, int stride, byte[] scoreMap, int t,
            int x0, int y0, int x1, int y1, Span<int> off, Span<int> diff, ref CornerList list)
        {
            int found = 0;
            int o0 = off[0], o4 = off[4], o8 = off[8], o12 = off[12];

            // off and diff both hold exactly 16 entries; the loop below indexes 0..15.
            ref int offRef = ref MemoryMarshal.GetReference(off);
            ref int diffRef = ref MemoryMarshal.GetReference(diff);

            for (int y = y0; y < y1; y++)
            {
                int idx = origin + y * stride + x0;
                for (int x = x0; x < x1; x++, idx++)
                {
                    int v = Px(img, idx);
                    int hi = v + t;
                    int lo = v - t;

                    // High-speed rejection: any 9-long arc contains at least two of the four compass pixels.
                    int p0 = Px(img, idx + o0), p4 = Px(img, idx + o4), p8 = Px(img, idx + o8), p12 = Px(img, idx + o12);
                    int nb = (p0 > hi ? 1 : 0) + (p4 > hi ? 1 : 0) + (p8 > hi ? 1 : 0) + (p12 > hi ? 1 : 0);
                    int nd = (p0 < lo ? 1 : 0) + (p4 < lo ? 1 : 0) + (p8 < lo ? 1 : 0) + (p12 < lo ? 1 : 0);
                    if (nb < 2 && nd < 2)
                    {
                        continue;
                    }

                    int maskBright = 0, maskDark = 0;
                    for (int k = 0; k < 16; k++)
                    {
                        int d = Px(img, idx + Unsafe.Add(ref offRef, k)) - v;
                        Unsafe.Add(ref diffRef, k) = d;
                        if (d > t)
                        {
                            maskBright |= 1 << k;
                        }
                        else if (d < -t)
                        {
                            maskDark |= 1 << k;
                        }
                    }

                    uint startsBright = RunStarts9(maskBright);
                    uint startsDark = RunStarts9(maskDark);
                    if ((startsBright | startsDark) == 0)
                    {
                        continue;
                    }

                    // Score = largest threshold at which the pixel would still be a corner.
                    int best = Math.Max(ArcScore(diff, startsBright, false), ArcScore(diff, startsDark, true));
                    int score = Math.Min(best - 1, 255);
                    scoreMap[idx] = (byte)score;
                    list.Add(x, y, score);
                    found++;
                }
            }

            return found;
        }

        /// <summary>Bit i of the result is set when circle positions i..i+8 (wrapping) are all set in the 16-bit mask.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RunStarts9(int mask)
        {
            uint m = (uint)mask;
            uint x = m | (m << 16);    // unroll the circle
            uint y = x & (x >> 1);     // runs >= 2
            y &= y >> 2;               // runs >= 4
            y &= y >> 4;               // runs >= 8
            y &= x >> 8;               // runs >= 9
            return y & 0xFFFFu;
        }

        /// <summary>Largest over the given arc starts of the smallest (signed) difference along the 9-pixel arc.</summary>
        private static int ArcScore(ReadOnlySpan<int> diff, uint starts, bool negate)
        {
            int best = 0;
            while (starts != 0)
            {
                int s = BitOperations.TrailingZeroCount(starts);
                starts &= starts - 1;
                int min = int.MaxValue;
                for (int j = 0; j < 9; j++)
                {
                    int d = diff[(s + j) & 15];
                    if (negate)
                    {
                        d = -d;
                    }

                    if (d < min)
                    {
                        min = d;
                    }
                }

                if (min > best)
                {
                    best = min;
                }
            }

            return best;
        }

        // ----------------------------------------------------------------------------------
        //  Octree keypoint distribution (ORB-SLAM's DistributeOctTree)
        // ----------------------------------------------------------------------------------

        private struct OctNode
        {
            public int X0, Y0, X1, Y1; // half-open bounds
            public int Start, Count;   // range inside the shared index array
        }

        /// <summary>
        /// Splits the image into quadtree cells until there are about <paramref name="target"/> of them, then keeps the
        /// strongest corner of each, giving an even spatial spread. Works in place on an index array: no per-node allocations.
        /// </summary>
        private static int DistributeOctree(Corner[] c, int n, int w, int h, int target, Corner[] output)
        {
            if (n == 0)
            {
                return 0;
            }

            int[] idx = ArrayPool<int>.Shared.Rent(n);
            int[] tmp = ArrayPool<int>.Shared.Rent(n);
            try
            {
                // Initial vertical strips so the starting cells are roughly square.
                int nIni = Math.Max(1, (int)Math.Round((double)w / h));
                int strip = (w + nIni - 1) / nIni;
                var start = new int[nIni + 1];
                for (int i = 0; i < n; i++)
                {
                    start[Math.Min(c[i].X / strip, nIni - 1) + 1]++;
                }

                for (int s = 0; s < nIni; s++)
                {
                    start[s + 1] += start[s];
                }

                var fill = new int[nIni];
                Array.Copy(start, fill, nIni);
                for (int i = 0; i < n; i++)
                {
                    idx[fill[Math.Min(c[i].X / strip, nIni - 1)]++] = i;
                }

                var nodes = new List<OctNode>(Math.Max(target, nIni) * 2);
                var next = new List<OctNode>(Math.Max(target, nIni) * 2);
                for (int s = 0; s < nIni; s++)
                {
                    int count = start[s + 1] - start[s];
                    if (count > 0)
                    {
                        nodes.Add(new OctNode { X0 = s * strip, X1 = Math.Min((s + 1) * strip, w), Y0 = 0, Y1 = h, Start = start[s], Count = count });
                    }
                }

                Span<OctNode> kids = stackalloc OctNode[4];
                bool finish = false;
                while (!finish)
                {
                    int prevSize = nodes.Count;
                    int nToExpand = 0;
                    next.Clear();
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        OctNode node = nodes[i];
                        if (node.Count <= 1)
                        {
                            next.Add(node);
                            continue;
                        }

                        int k = SplitNode(c, idx, tmp, node, kids);
                        for (int j = 0; j < k; j++)
                        {
                            next.Add(kids[j]);
                            if (kids[j].Count > 1)
                            {
                                nToExpand++;
                            }
                        }
                    }

                    (nodes, next) = (next, nodes);

                    if (nodes.Count >= target || nodes.Count == prevSize)
                    {
                        finish = true;
                    }
                    else if (nodes.Count + nToExpand * 3 > target)
                    {
                        // A full pass would overshoot the target: split the most crowded nodes first and stop exactly at it.
                        while (!finish)
                        {
                            prevSize = nodes.Count;
                            var order = new List<int>();
                            for (int i = 0; i < nodes.Count; i++)
                            {
                                if (nodes[i].Count > 1)
                                {
                                    order.Add(i);
                                }
                            }

                            order.Sort((a, b) =>
                            {
                                int byCount = nodes[b].Count.CompareTo(nodes[a].Count);
                                return byCount != 0 ? byCount : a.CompareTo(b);
                            });

                            int live = nodes.Count;
                            foreach (int ni in order)
                            {
                                OctNode node = nodes[ni];
                                int k = SplitNode(c, idx, tmp, node, kids);
                                node.Count = 0; // mark parent as removed
                                nodes[ni] = node;
                                for (int j = 0; j < k; j++)
                                {
                                    nodes.Add(kids[j]);
                                }

                                live += k - 1;
                                if (live >= target)
                                {
                                    break;
                                }
                            }

                            nodes.RemoveAll(static nd => nd.Count == 0);
                            if (nodes.Count >= target || nodes.Count == prevSize)
                            {
                                finish = true;
                            }
                        }
                    }
                }

                // Strongest corner of every cell.
                int outCount = 0;
                foreach (OctNode node in nodes)
                {
                    int best = idx[node.Start];
                    for (int i = 1; i < node.Count; i++)
                    {
                        int cand = idx[node.Start + i];
                        if (c[cand].Response > c[best].Response)
                        {
                            best = cand;
                        }
                    }

                    output[outCount++] = c[best];
                }

                return outCount;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(idx);
                ArrayPool<int>.Shared.Return(tmp);
            }
        }

        /// <summary>Partitions a node's index range into its (non-empty) quadrants in place and writes the children to <paramref name="kids"/>.</summary>
        private static int SplitNode(Corner[] c, int[] idx, int[] tmp, OctNode node, Span<OctNode> kids)
        {
            int midX = node.X0 + ((node.X1 - node.X0) >> 1);
            int midY = node.Y0 + ((node.Y1 - node.Y0) >> 1);
            int end = node.Start + node.Count;

            Span<int> cnt = stackalloc int[4];
            cnt.Clear();
            for (int i = node.Start; i < end; i++)
            {
                Corner k = c[idx[i]];
                cnt[(k.X >= midX ? 1 : 0) | (k.Y >= midY ? 2 : 0)]++;
            }

            Span<int> baseIdx = stackalloc int[4];
            Span<int> pos = stackalloc int[4];
            baseIdx[0] = node.Start;
            baseIdx[1] = baseIdx[0] + cnt[0];
            baseIdx[2] = baseIdx[1] + cnt[1];
            baseIdx[3] = baseIdx[2] + cnt[2];
            baseIdx.CopyTo(pos);

            for (int i = node.Start; i < end; i++)
            {
                int ci = idx[i];
                Corner k = c[ci];
                tmp[pos[(k.X >= midX ? 1 : 0) | (k.Y >= midY ? 2 : 0)]++] = ci;
            }

            Array.Copy(tmp, node.Start, idx, node.Start, node.Count);

            int written = 0;
            for (int q = 0; q < 4; q++)
            {
                if (cnt[q] == 0)
                {
                    continue;
                }

                kids[written++] = new OctNode
                {
                    X0 = (q & 1) == 0 ? node.X0 : midX,
                    X1 = (q & 1) == 0 ? midX : node.X1,
                    Y0 = (q & 2) == 0 ? node.Y0 : midY,
                    Y1 = (q & 2) == 0 ? midY : node.Y1,
                    Start = baseIdx[q],
                    Count = cnt[q],
                };
            }

            return written;
        }

        // ----------------------------------------------------------------------------------
        //  Orientation, blur, descriptor
        // ----------------------------------------------------------------------------------

        /// <summary>Intensity-centroid orientation in degrees [0, 360) over a circular patch of radius 15.</summary>
        private static float ComputeAngle(byte[] data, int center, int stride)
        {
            int m01 = 0, m10 = 0;
            for (int u = -HalfPatchSize; u <= HalfPatchSize; u++)
            {
                m10 += u * Px(data, center + u);
            }

            // Rows v and -v are processed together.
            for (int v = 1; v <= HalfPatchSize; v++)
            {
                int vSum = 0;
                int d = UMax[v];
                int up = center + v * stride;
                int down = center - v * stride;
                for (int u = -d; u <= d; u++)
                {
                    int plus = Px(data, up + u);
                    int minus = Px(data, down + u);
                    vSum += plus - minus;
                    m10 += u * (plus + minus);
                }

                m01 += v * vSum;
            }

            float deg = MathF.Atan2(m01, m10) * (180f / MathF.PI);
            return deg < 0f ? deg + 360f : deg;
        }

        /// <summary>Separable 7x7 Gaussian (sigma 2) over the whole padded plane; edges are clamped.</summary>
        private static void GaussianBlur7(Plane src, byte[] dst, ushort[] tmp)
        {
            int pw = src.Stride;
            int ph = src.Height + 2 * EdgeThreshold;
            byte[] s = src.Data;
            int k0 = GaussKernel[0], k1 = GaussKernel[1], k2 = GaussKernel[2], k3 = GaussKernel[3];

            // src, tmp and dst all hold at least pw * ph elements; every index below stays inside that range.
            ref byte sRef = ref MemoryMarshal.GetArrayDataReference(s);
            ref ushort tRef = ref MemoryMarshal.GetArrayDataReference(tmp);
            ref byte dRef = ref MemoryMarshal.GetArrayDataReference(dst);

            for (int y = 0; y < ph; y++)
            {
                int row = y * pw;
                for (int x = 0; x < 3; x++)
                {
                    tmp[row + x] = HorizontalClamped(s, row, x, pw, k0, k1, k2, k3);
                    tmp[row + pw - 1 - x] = HorizontalClamped(s, row, pw - 1 - x, pw, k0, k1, k2, k3);
                }

                for (int x = 3; x < pw - 3; x++)
                {
                    int i = row + x;
                    Unsafe.Add(ref tRef, i) = (ushort)(k0 * Unsafe.Add(ref sRef, i)
                        + k1 * (Unsafe.Add(ref sRef, i - 1) + Unsafe.Add(ref sRef, i + 1))
                        + k2 * (Unsafe.Add(ref sRef, i - 2) + Unsafe.Add(ref sRef, i + 2))
                        + k3 * (Unsafe.Add(ref sRef, i - 3) + Unsafe.Add(ref sRef, i + 3)));
                }
            }

            for (int y = 0; y < ph; y++)
            {
                int row = y * pw;
                int rm3 = Math.Max(y - 3, 0) * pw, rm2 = Math.Max(y - 2, 0) * pw, rm1 = Math.Max(y - 1, 0) * pw;
                int rp1 = Math.Min(y + 1, ph - 1) * pw, rp2 = Math.Min(y + 2, ph - 1) * pw, rp3 = Math.Min(y + 3, ph - 1) * pw;
                for (int x = 0; x < pw; x++)
                {
                    int sum = k0 * Unsafe.Add(ref tRef, row + x)
                            + k1 * (Unsafe.Add(ref tRef, rm1 + x) + Unsafe.Add(ref tRef, rp1 + x))
                            + k2 * (Unsafe.Add(ref tRef, rm2 + x) + Unsafe.Add(ref tRef, rp2 + x))
                            + k3 * (Unsafe.Add(ref tRef, rm3 + x) + Unsafe.Add(ref tRef, rp3 + x));
                    Unsafe.Add(ref dRef, row + x) = (byte)((sum + 32768) >> 16);
                }
            }
        }

        private static ushort HorizontalClamped(byte[] s, int row, int x, int pw, int k0, int k1, int k2, int k3)
        {
            static int At(byte[] s, int row, int x, int pw) => s[row + Math.Clamp(x, 0, pw - 1)];
            return (ushort)(k0 * At(s, row, x, pw)
                          + k1 * (At(s, row, x - 1, pw) + At(s, row, x + 1, pw))
                          + k2 * (At(s, row, x - 2, pw) + At(s, row, x + 2, pw))
                          + k3 * (At(s, row, x - 3, pw) + At(s, row, x + 3, pw)));
        }

        /// <summary>Steered BRIEF: the 256 test pairs are rotated by the keypoint angle, then compared on the blurred level.</summary>
        private static void ComputeDescriptor(byte[] blurred, int center, int stride, float angleDeg, ulong[] dst, int dstOffset)
        {
            float rad = angleDeg * (MathF.PI / 180f);
            float a = MathF.Cos(rad);
            float b = MathF.Sin(rad);
            sbyte[] pat = Pattern;

            for (int word = 0; word < 4; word++)
            {
                ulong bits = 0;
                int pi = word * 64 * 4;
                for (int bit = 0; bit < 64; bit++, pi += 4)
                {
                    float x0 = pat[pi], y0 = pat[pi + 1], x1 = pat[pi + 2], y1 = pat[pi + 3];
                    int t0 = Px(blurred, center + (int)MathF.Round(x0 * b + y0 * a) * stride + (int)MathF.Round(x0 * a - y0 * b));
                    int t1 = Px(blurred, center + (int)MathF.Round(x1 * b + y1 * a) * stride + (int)MathF.Round(x1 * a - y1 * b));
                    if (t0 < t1)
                    {
                        bits |= 1UL << bit;
                    }
                }

                dst[dstOffset + word] = bits;
            }
        }

        // ----------------------------------------------------------------------------------
        //  Matching
        // ----------------------------------------------------------------------------------

        /// <summary>Linear scan for the two closest reference descriptors. Returns the best index (or -1).</summary>
        private static int FindBestMatch(ulong[] qd, int qi, ulong[] rd, int nr, out int bestDist, out int secondDist)
        {
            ulong a0 = qd[qi * 4], a1 = qd[qi * 4 + 1], a2 = qd[qi * 4 + 2], a3 = qd[qi * 4 + 3];
            ref ulong baseRef = ref MemoryMarshal.GetArrayDataReference(rd); // rd.Length == nr * 4 (enforced by ORBFeatureSet)

            int best = int.MaxValue, second = int.MaxValue, bestIdx = -1;
            for (int r = 0; r < nr; r++)
            {
                ref ulong p = ref Unsafe.Add(ref baseRef, r * 4);
                int d = BitOperations.PopCount(a0 ^ p)
                      + BitOperations.PopCount(a1 ^ Unsafe.Add(ref p, 1))
                      + BitOperations.PopCount(a2 ^ Unsafe.Add(ref p, 2))
                      + BitOperations.PopCount(a3 ^ Unsafe.Add(ref p, 3));
                if (d < best)
                {
                    second = best;
                    best = d;
                    bestIdx = r;
                }
                else if (d < second)
                {
                    second = d;
                }
            }

            bestDist = best;
            secondDist = second;
            return bestIdx;
        }

        /// <summary>
        /// Histogram of keypoint angle differences (30 bins of 12 degrees). True matches share one relative rotation, so
        /// only the three most populated bins are kept. Also returns the circular-mean rotation of the top bin.
        /// </summary>
        private static ORBMatch[] FilterByRotation(ORBMatch[] matches, ORBKeypoint[] qk, ORBKeypoint[] rk, bool filter, out double dominantDegrees)
        {
            int n = matches.Length;
            var bins = new int[n];
            var diffs = new float[n];
            Span<int> hist = stackalloc int[HistogramBins];
            hist.Clear();

            for (int i = 0; i < n; i++)
            {
                float d = (qk[matches[i].QueryIndex].Angle - rk[matches[i].ReferenceIndex].Angle) % 360f;
                if (d < 0f)
                {
                    d += 360f;
                }

                diffs[i] = d;
                int bin = (int)MathF.Round(d * (HistogramBins / 360f));
                if (bin >= HistogramBins)
                {
                    bin = 0;
                }

                bins[i] = bin;
                hist[bin]++;
            }

            int max1 = 0, max2 = 0, max3 = 0, i1 = -1, i2 = -1, i3 = -1;
            for (int b = 0; b < HistogramBins; b++)
            {
                int s = hist[b];
                if (s > max1)
                {
                    max3 = max2; max2 = max1; max1 = s;
                    i3 = i2; i2 = i1; i1 = b;
                }
                else if (s > max2)
                {
                    max3 = max2; max2 = s;
                    i3 = i2; i2 = b;
                }
                else if (s > max3)
                {
                    max3 = s;
                    i3 = b;
                }
            }

            if (max2 < 0.1f * max1)
            {
                i2 = -1;
                i3 = -1;
            }
            else if (max3 < 0.1f * max1)
            {
                i3 = -1;
            }

            double sumSin = 0, sumCos = 0;
            for (int i = 0; i < n; i++)
            {
                if (bins[i] == i1)
                {
                    double r = diffs[i] * (Math.PI / 180.0);
                    sumSin += Math.Sin(r);
                    sumCos += Math.Cos(r);
                }
            }

            double mean = Math.Atan2(sumSin, sumCos) * (180.0 / Math.PI);
            dominantDegrees = mean <= -180.0 ? mean + 360.0 : mean;

            if (!filter)
            {
                return matches;
            }

            var kept = new List<ORBMatch>(n);
            for (int i = 0; i < n; i++)
            {
                int b = bins[i];
                if (b == i1 || b == i2 || b == i3)
                {
                    kept.Add(matches[i]);
                }
            }

            return kept.ToArray();
        }

        // ----------------------------------------------------------------------------------
        //  RANSAC 2D similarity (reference -> query), complex-number formulation q = a * p + t
        // ----------------------------------------------------------------------------------

        private static bool EstimateSimilarity(ORBMatch[] matches, ORBKeypoint[] qk, ORBKeypoint[] rk, ORBMatchParameters prm,
            out Similarity2D model, out ORBMatch[] inliers)
        {
            model = Similarity2D.Identity;
            inliers = Array.Empty<ORBMatch>();

            int n = matches.Length;
            int minInliers = Math.Max(prm.MinInliers, 2);
            if (n < minInliers)
            {
                return false;
            }

            var px = new double[n];
            var py = new double[n];
            var qx = new double[n];
            var qy = new double[n];
            for (int i = 0; i < n; i++)
            {
                ORBKeypoint r = rk[matches[i].ReferenceIndex];
                ORBKeypoint q = qk[matches[i].QueryIndex];
                px[i] = r.X;
                py[i] = r.Y;
                qx[i] = q.X;
                qy[i] = q.Y;
            }

            double thr2 = prm.InlierThresholdPixels * prm.InlierThresholdPixels;
            double minScale2 = 1.0 / (prm.MaxScaleChange * prm.MaxScaleChange);
            double maxScale2 = prm.MaxScaleChange * prm.MaxScaleChange;

            // Deterministic xorshift32 so identical inputs always give identical results.
            uint rng = 0x9E3779B9u;
            int NextIndex(int bound)
            {
                rng ^= rng << 13;
                rng ^= rng >> 17;
                rng ^= rng << 5;
                return (int)(((ulong)rng * (ulong)bound) >> 32);
            }

            int CountInliers(double ar, double ai, double tr, double ti)
            {
                int count = 0;
                for (int k = 0; k < n; k++)
                {
                    double ex = ar * px[k] - ai * py[k] + tr - qx[k];
                    double ey = ai * px[k] + ar * py[k] + ti - qy[k];
                    if (ex * ex + ey * ey <= thr2)
                    {
                        count++;
                    }
                }

                return count;
            }

            int bestCount = 0;
            double bAr = 1, bAi = 0, bTr = 0, bTi = 0;
            for (int it = 0; it < prm.RansacIterations; it++)
            {
                int i = NextIndex(n);
                int j = NextIndex(n - 1);
                if (j >= i)
                {
                    j++;
                }

                double dx = px[j] - px[i], dy = py[j] - py[i];
                double d2 = dx * dx + dy * dy;
                if (d2 < 1.0)
                {
                    continue;
                }

                double ex = qx[j] - qx[i], ey = qy[j] - qy[i];
                double ar = (ex * dx + ey * dy) / d2;
                double ai = (ey * dx - ex * dy) / d2;
                double s2 = ar * ar + ai * ai;
                if (s2 < minScale2 || s2 > maxScale2)
                {
                    continue;
                }

                double tr = qx[i] - (ar * px[i] - ai * py[i]);
                double ti = qy[i] - (ai * px[i] + ar * py[i]);
                int count = CountInliers(ar, ai, tr, ti);
                if (count > bestCount)
                {
                    bestCount = count;
                    bAr = ar; bAi = ai; bTr = tr; bTi = ti;
                }
            }

            if (bestCount < minInliers)
            {
                return false;
            }

            // Refine: least-squares fit on the current inliers, then re-select inliers (twice).
            var mask = new bool[n];
            for (int pass = 0; pass < 2; pass++)
            {
                double mpx = 0, mpy = 0, mqx = 0, mqy = 0;
                int m = 0;
                for (int k = 0; k < n; k++)
                {
                    double ex = bAr * px[k] - bAi * py[k] + bTr - qx[k];
                    double ey = bAi * px[k] + bAr * py[k] + bTi - qy[k];
                    mask[k] = ex * ex + ey * ey <= thr2;
                    if (mask[k])
                    {
                        mpx += px[k]; mpy += py[k]; mqx += qx[k]; mqy += qy[k];
                        m++;
                    }
                }

                if (m < minInliers)
                {
                    return false;
                }

                mpx /= m; mpy /= m; mqx /= m; mqy /= m;
                double num = 0, numI = 0, den = 0;
                for (int k = 0; k < n; k++)
                {
                    if (!mask[k])
                    {
                        continue;
                    }

                    double ux = px[k] - mpx, uy = py[k] - mpy, vx = qx[k] - mqx, vy = qy[k] - mqy;
                    num += ux * vx + uy * vy;
                    numI += ux * vy - uy * vx;
                    den += ux * ux + uy * uy;
                }

                if (den < 1e-9)
                {
                    return false;
                }

                bAr = num / den;
                bAi = numI / den;
                bTr = mqx - (bAr * mpx - bAi * mpy);
                bTi = mqy - (bAi * mpx + bAr * mpy);
            }

            var list = new List<ORBMatch>(n);
            for (int k = 0; k < n; k++)
            {
                double ex = bAr * px[k] - bAi * py[k] + bTr - qx[k];
                double ey = bAi * px[k] + bAr * py[k] + bTi - qy[k];
                if (ex * ex + ey * ey <= thr2)
                {
                    list.Add(matches[k]);
                }
            }

            if (list.Count < minInliers)
            {
                return false;
            }

            inliers = list.ToArray();
            model = new Similarity2D(Math.Sqrt(bAr * bAr + bAi * bAi), Math.Atan2(bAi, bAr), bTr, bTi);
            return true;
        }
    }
}
