using System.Buffers;
using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>Settings of the semi-global matcher.</summary>
    public sealed class SemiGlobalMatcherParameters
    {
        /// <summary>Disparities 0 to MaxDisparity - 1 are searched.</summary>
        public int MaxDisparity { get; init; } = 96;

        /// <summary>
        /// The other image is searched to the right of the reference pixel (its match is at x + d) if true, to the left (x - d) if false.
        /// For a rectified pair this is true when the reference is the right image.
        /// </summary>
        public bool SearchRight { get; init; }

        /// <summary>Penalty for a disparity that differs by one from the neighbour's, and for a larger jump (the usual SGM P1 and P2, in census bit-differences, 0 to 24 per pixel).</summary>
        public int P1 { get; init; } = 4;
        public int P2 { get; init; } = 20;

        /// <summary>The best aggregated cost must be this many percent lower than the best one at a disparity two or more steps away, or the pixel is left out.</summary>
        public int UniquenessPercent { get; init; } = 8;

        /// <summary>The disparity found from the other image's side must agree to within this many pixels (left-right check).</summary>
        public float LeftRightTolerance { get; init; } = 1.5f;

        /// <summary>Regions of the disparity map (neighbours differing by at most <see cref="SpeckleDifference"/>) smaller than this many pixels are removed.</summary>
        public int SpeckleSize { get; init; } = 120;
        public float SpeckleDifference { get; init; } = 1f;

        /// <summary>Number of aggregation directions: 4 (left-right, up-down) or 8 (also the diagonals).</summary>
        public int Paths { get; init; } = 4;
    }

    /// <summary>
    /// Semi-global matching (Hirschmuller 2008), the method behind OpenCV's StereoSGBM, for a rectified pair of 8-bit grayscale images.
    /// The matching cost is the Hamming distance of 5x5 census transforms (robust to the two cameras' different brightness); the costs are
    /// aggregated along several image directions with a small penalty for a disparity change of one pixel and a larger one for bigger jumps,
    /// which smooths the result where the image is flat but keeps depth edges where there is a strong image edge. The result is checked for
    /// uniqueness and left-right consistency, and small isolated regions are removed.
    /// </summary>
    public static class SemiGlobalMatcher
    {
        private const int CensusRadius = 2;
        private const byte MaxCost = 24;

        /// <summary>
        /// The disparity of every pixel of <paramref name="reference"/> against <paramref name="other"/>, row by row, with sub-pixel precision;
        /// NaN where no reliable disparity was found.
        /// </summary>
        public static float[] Compute(byte[] reference, byte[] other, int width, int height, SemiGlobalMatcherParameters p)
        {
            int disparities = Math.Max(8, p.MaxDisparity);
            int pixels = width * height;
            var result = new float[pixels];
            Array.Fill(result, float.NaN);
            if (width < 2 * CensusRadius + 8 || height < 2 * CensusRadius + 8)
                return result;

            int sign = p.SearchRight ? 1 : -1;
            uint[] censusReference = Census(reference, width, height);
            uint[] censusOther = Census(other, width, height);

            long cells = (long)pixels * disparities;
            byte[] cost = ArrayPool<byte>.Shared.Rent((int)cells);
            ushort[] sum = ArrayPool<ushort>.Shared.Rent((int)cells);
            try
            {
                Array.Clear(sum, 0, (int)cells);

                // Matching cost of every pixel at every disparity.
                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int baseIndex = (y * width + x) * disparities;
                        bool inside = x >= CensusRadius && x < width - CensusRadius && y >= CensusRadius && y < height - CensusRadius;
                        uint c = censusReference[y * width + x];
                        for (int d = 0; d < disparities; d++)
                        {
                            int xo = x + sign * d;
                            if (!inside || xo < CensusRadius || xo >= width - CensusRadius)
                                cost[baseIndex + d] = MaxCost;
                            else
                                cost[baseIndex + d] = (byte)BitOperations.PopCount(c ^ censusOther[y * width + xo]);
                        }
                    }
                });

                // Aggregate along the paths.
                (int dx, int dy)[] directions = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) };
                int pathCount = p.Paths >= 8 ? 8 : 4;
                for (int r = 0; r < pathCount; r++)
                {
                    (int dx, int dy) = directions[r];
                    var starts = new List<int>();
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int px = x - dx, py = y - dy;
                            if (px < 0 || py < 0 || px >= width || py >= height)
                                starts.Add(y * width + x);
                        }
                    }

                    Parallel.ForEach(starts, start =>
                    {
                        var previous = new ushort[disparities];
                        var current = new ushort[disparities];
                        int x = start % width, y = start / width;
                        int previousPixel = -1;
                        while (x >= 0 && y >= 0 && x < width && y < height)
                        {
                            int pixel = y * width + x;
                            int index = pixel * disparities;
                            if (previousPixel < 0)
                            {
                                for (int d = 0; d < disparities; d++)
                                    current[d] = cost[index + d];
                            }
                            else
                            {
                                int minPrevious = ushort.MaxValue;
                                for (int d = 0; d < disparities; d++)
                                    if (previous[d] < minPrevious)
                                        minPrevious = previous[d];

                                // A strong image edge makes a depth jump likely, so it is penalised less.
                                int change = Math.Abs(reference[pixel] - reference[previousPixel]);
                                int p2 = change > 10 ? Math.Max(p.P1 + 1, p.P2 / 3) : p.P2;
                                int jump = minPrevious + p2;
                                for (int d = 0; d < disparities; d++)
                                {
                                    int best = Math.Min(previous[d], jump);
                                    if (d > 0)
                                        best = Math.Min(best, previous[d - 1] + p.P1);
                                    if (d < disparities - 1)
                                        best = Math.Min(best, previous[d + 1] + p.P1);
                                    current[d] = (ushort)(cost[index + d] + best - minPrevious);
                                }
                            }

                            for (int d = 0; d < disparities; d++)
                                sum[index + d] += current[d];

                            (previous, current) = (current, previous);
                            previousPixel = pixel;
                            x += dx;
                            y += dy;
                        }
                    });
                }

                // The best disparity of each reference pixel, and (for the left-right check) of each pixel of the other image.
                var integerDisparity = new short[pixels];
                Array.Fill(integerDisparity, (short)-1);
                var otherDisparity = new short[pixels];
                Array.Fill(otherDisparity, (short)-1);

                Parallel.For(0, height, y =>
                {
                    var otherBest = new int[width];
                    Array.Fill(otherBest, int.MaxValue);
                    for (int x = CensusRadius; x < width - CensusRadius; x++)
                    {
                        int index = (y * width + x) * disparities;
                        int best = int.MaxValue, bestD = -1;
                        for (int d = 0; d < disparities; d++)
                        {
                            int s = sum[index + d];
                            if (s < best)
                            {
                                best = s;
                                bestD = d;
                            }

                            int xo = x + sign * d;
                            if (xo >= CensusRadius && xo < width - CensusRadius && s < otherBest[xo])
                            {
                                otherBest[xo] = s;
                                otherDisparity[y * width + xo] = (short)d;
                            }
                        }

                        if (bestD < 1 || bestD >= disparities - 1)
                            continue;

                        // Unique: nothing nearly as good two or more steps away.
                        int second = int.MaxValue;
                        for (int d = 0; d < disparities; d++)
                            if (Math.Abs(d - bestD) > 1 && sum[index + d] < second)
                                second = sum[index + d];
                        if (second != int.MaxValue && best * (100 + p.UniquenessPercent) > second * 100)
                            continue;

                        // Between the integer disparities: the top of the parabola through the cost at the best one and its neighbours.
                        float s0 = sum[index + bestD - 1], s1 = sum[index + bestD], s2 = sum[index + bestD + 1];
                        float curvature = s0 - 2f * s1 + s2;
                        float refined = bestD + (curvature > 1e-6f ? Math.Clamp(0.5f * (s0 - s2) / curvature, -0.5f, 0.5f) : 0f);
                        integerDisparity[y * width + x] = (short)bestD;
                        result[y * width + x] = refined;
                    }
                });

                // Left-right consistency.
                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        if (integerDisparity[i] < 0)
                            continue;
                        int xo = x + sign * integerDisparity[i];
                        if (xo < 0 || xo >= width || otherDisparity[y * width + xo] < 0 || Math.Abs(otherDisparity[y * width + xo] - integerDisparity[i]) > p.LeftRightTolerance)
                            result[i] = float.NaN;
                    }
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(cost);
                ArrayPool<ushort>.Shared.Return(sum);
            }

            RemoveSpeckles(result, width, height, p.SpeckleSize, p.SpeckleDifference);
            return result;
        }

        /// <summary>5x5 census transform of the (slightly smoothed) image: bit i says whether the i-th neighbour is darker than the centre pixel.</summary>
        private static uint[] Census(byte[] image, int width, int height)
        {
            var smooth = new byte[image.Length];
            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    int sumValues = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= height)
                            continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= width)
                                continue;
                            sumValues += image[yy * width + xx];
                            count++;
                        }
                    }
                    smooth[y * width + x] = (byte)(sumValues / count);
                }
            });

            var census = new uint[image.Length];
            Parallel.For(CensusRadius, height - CensusRadius, y =>
            {
                for (int x = CensusRadius; x < width - CensusRadius; x++)
                {
                    byte center = smooth[y * width + x];
                    uint bits = 0;
                    for (int dy = -CensusRadius; dy <= CensusRadius; dy++)
                    {
                        for (int dx = -CensusRadius; dx <= CensusRadius; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;
                            bits = (bits << 1) | (smooth[(y + dy) * width + x + dx] < center ? 1u : 0u);
                        }
                    }
                    census[y * width + x] = bits;
                }
            });
            return census;
        }

        /// <summary>Remove regions of similar disparity (neighbours within <paramref name="difference"/>) with fewer than <paramref name="minSize"/> pixels.</summary>
        private static void RemoveSpeckles(float[] disparity, int width, int height, int minSize, float difference)
        {
            if (minSize <= 1)
                return;

            var visited = new bool[disparity.Length];
            var stack = new Stack<int>();
            var region = new List<int>();
            for (int start = 0; start < disparity.Length; start++)
            {
                if (visited[start] || float.IsNaN(disparity[start]))
                    continue;

                region.Clear();
                stack.Push(start);
                visited[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    region.Add(i);
                    int x = i % width, y = i / width;
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + (n == 0 ? 1 : n == 1 ? -1 : 0), ny = y + (n == 2 ? 1 : n == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            continue;
                        int j = ny * width + nx;
                        if (!visited[j] && !float.IsNaN(disparity[j]) && MathF.Abs(disparity[j] - disparity[i]) <= difference)
                        {
                            visited[j] = true;
                            stack.Push(j);
                        }
                    }
                }

                if (region.Count < minSize)
                    foreach (int i in region)
                        disparity[i] = float.NaN;
            }
        }
    }
}
