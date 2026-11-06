using System.Numerics;

namespace Server.Archived.Manager.SpatialSync
{
    /// <summary>
    /// A depth map of a key frame on a regular grid of its pixels. <see cref="Range"/> is the distance from the camera along the pixel's
    /// viewing ray, in meters; 0 where no depth could be estimated.
    /// </summary>
    public sealed class DepthMap
    {
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }

        /// <summary>Pixels between neighbouring grid cells.</summary>
        public int Stride { get; init; }

        /// <summary>Pixel position of the first grid cell.</summary>
        public int Origin { get; init; }

        public int GridWidth { get; init; }
        public int GridHeight { get; init; }
        public float[] Range { get; init; } = Array.Empty<float>();

        /// <summary>Why a grid cell has no depth (a <see cref="DepthStatus"/> value; 0 where it has one). Empty if not recorded.</summary>
        public byte[] Status { get; init; } = Array.Empty<byte>();

        public int ValidCount => Range.Count(r => r > 0f);

        /// <summary>The grid cells without a depth, with the pixel they are at and why (a <see cref="DepthStatus"/> value).</summary>
        public IEnumerable<(float X, float Y, byte Reason)> Rejected()
        {
            if (Status.Length != Range.Length)
                yield break;
            for (int gy = 0; gy < GridHeight; gy++)
                for (int gx = 0; gx < GridWidth; gx++)
                {
                    int i = gy * GridWidth + gx;
                    if (Range[i] <= 0f && Status[i] != DepthStatus.Valid)
                        yield return (PixelX(gx), PixelY(gy), Status[i]);
                }
        }

        public float PixelX(int gx) => Origin + gx * Stride;
        public float PixelY(int gy) => Origin + gy * Stride;

        /// <summary>The valid depths as 3D points (in the frame of <paramref name="frame"/>'s pose) with the pixel they belong to and their depth along the optical axis.</summary>
        public IEnumerable<(Vector3 Position, float X, float Y, float Depth)> Points(MapKeyFrame frame)
        {
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, frame.Rotation);
            for (int gy = 0; gy < GridHeight; gy++)
            {
                for (int gx = 0; gx < GridWidth; gx++)
                {
                    float r = Range[gy * GridWidth + gx];
                    if (r <= 0f)
                        continue;

                    float x = PixelX(gx), y = PixelY(gy);
                    Vector3 ray = frame.PixelToWorldRay(x, y);
                    yield return (frame.Position + ray * r, x, y, r * Vector3.Dot(ray, forward));
                }
            }
        }
    }

    /// <summary>Why a grid cell of a <see cref="DepthMap"/> has no depth.</summary>
    public static class DepthStatus
    {
        public const byte Valid = 0;
        /// <summary>The matcher found no (or too few) matches there.</summary>
        public const byte Unmatched = 1;
        /// <summary>The distance is outside the range that is used.</summary>
        public const byte OutOfRange = 2;
        /// <summary>On a depth edge, or the pixels of the block disagree.</summary>
        public const byte Edge = 3;
        /// <summary>Outside the rectified images (or the match would be).</summary>
        public const byte Outside = 4;
    }

    /// <summary>
    /// Depth of the right camera's image from the stereo pair of the headset's two cameras, with the cameras' poses as the headset reported them.
    /// The two images are first rectified: each is turned (about its own camera centre) to a common orientation with the baseline along the
    /// image's x axis, which is what a stereo matcher needs, even though the two cameras are slightly rotated against each other and the head
    /// may have moved a little between the two captures. Semi-global matching then finds the disparity of every pixel of the right image, and
    /// depth follows from the disparity and the distance between the two camera centres.
    /// </summary>
    public static class StereoDepthEstimator
    {
        public static DepthMap? Estimate(MapKeyFrame right, byte[] rightGray, MapKeyFrame left, byte[] leftGray,
                                         GlobalMappingParameters parameters, out string reason, out float verticalShift)
        {
            reason = "";
            verticalShift = 0f;
            int width = Math.Max(64, parameters.StereoWidth), height = Math.Max(64, parameters.StereoHeight);

            // The rectified cameras: at the real camera centres, with the x axis from the left camera to the right one.
            Vector3 baselineVector = right.Position - left.Position;
            float baseline = baselineVector.Length();
            if (!(baseline >= parameters.StereoMinBaselineMeters) || !(baseline <= parameters.StereoMaxBaselineMeters))
            {
                reason = $"the cameras are {baseline:0.000} m apart";
                return null;
            }

            Vector3 axisX = baselineVector / baseline;
            Vector3 forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, right.Rotation) + Vector3.Transform(Vector3.UnitZ, left.Rotation));
            Vector3 axisZ = forward - Vector3.Dot(forward, axisX) * axisX;
            if (axisZ.LengthSquared() < 1e-6f)
            {
                reason = "the cameras look along their baseline";
                return null;
            }
            axisZ = Vector3.Normalize(axisZ);
            Vector3 axisY = Vector3.Cross(axisZ, axisX);

            // Camera frame (x right, y up, z forward) to world: the rows of the matrix are the camera axes in world space.
            var basis = new Matrix4x4(axisX.X, axisX.Y, axisX.Z, 0f,
                                      axisY.X, axisY.Y, axisY.Z, 0f,
                                      axisZ.X, axisZ.Y, axisZ.Z, 0f,
                                      0f, 0f, 0f, 1f);
            Quaternion rectifiedRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));

            // The same field of view as the right camera, on the rectified image size.
            float focal = right.Fx * width / right.Width;
            MapKeyFrame rectRight = VirtualCamera(right.Position, rectifiedRotation, width, height, focal);
            MapKeyFrame rectLeft = VirtualCamera(left.Position, rectifiedRotation, width, height, focal);

            byte[] rectRightImage = Rectify(right, rightGray, rectRight, out bool[] rightValid);
            byte[] rectLeftImage = Rectify(left, leftGray, rectLeft, out bool[] leftValid);

            // The head can turn or tilt a little between the two captures. A vertical offset between the two images (which matching along rows
            // cannot cope with) shows in the profiles of the rows' brightness whatever the depths are, so measure it and take it out.
            if (parameters.StereoAlignVertical)
            {
                verticalShift = EstimateVerticalShift(rectRightImage, rectLeftImage, width, height, parameters.StereoMaxVerticalShiftPixels);
                if (verticalShift != 0f)
                    rectLeftImage = ShiftVertically(rectLeftImage, leftValid, width, height, verticalShift, out leftValid);
            }

            // The largest disparity that can occur: the nearest distance we care about.
            float minRange = MathF.Max(0.1f, parameters.StereoMinRangeMeters), maxRange = parameters.StereoMaxRangeMeters;
            int maxDisparity = (int)MathF.Ceiling(focal * baseline / minRange) + 2;
            maxDisparity = Math.Clamp((maxDisparity + 7) / 8 * 8, 16, Math.Max(16, parameters.StereoMaxDisparity));

            // The right image is the reference; its matches in the left image lie to the right (x_left = x_right + disparity).
            float[] disparity = SemiGlobalMatcher.Compute(rectRightImage, rectLeftImage, width, height, new SemiGlobalMatcherParameters
            {
                MaxDisparity = maxDisparity,
                SearchRight = true,
                P1 = parameters.SgmP1,
                P2 = parameters.SgmP2,
                UniquenessPercent = parameters.SgmUniquenessPercent,
                LeftRightTolerance = parameters.SgmLeftRightTolerancePixels,
                SpeckleSize = parameters.SgmSpeckleSize,
                SpeckleDifference = parameters.SgmSpeckleDifferencePixels,
                Paths = parameters.SgmPaths,
            });

            // Sample it on a grid of the right camera's own image, so it can be used like a depth map of the original image.
            int stride = Math.Max(1, parameters.StereoStride);
            int origin = stride;
            int gridWidth = (right.Width - 2 * origin) / stride, gridHeight = (right.Height - 2 * origin) / stride;
            if (gridWidth <= 0 || gridHeight <= 0)
            {
                reason = "the image is too small";
                return null;
            }

            var range = new float[gridWidth * gridHeight];
            var status = new byte[gridWidth * gridHeight];
            float focalBaseline = focal * baseline;

            // A block of pixels per depth value: the half size of the block in the rectified image (which may be smaller than the original).
            bool useBlocks = parameters.StereoBlockPixels > 1;
            int half = Math.Max(1, (int)MathF.Round(0.5f * parameters.StereoBlockPixels * width / right.Width));
            int blockArea = (2 * half + 1) * (2 * half + 1);

            Parallel.For(0, gridHeight, gy =>
            {
                Span<float> block = stackalloc float[blockArea];
                for (int gx = 0; gx < gridWidth; gx++)
                {
                    float px = origin + gx * stride, py = origin + gy * stride;
                    int cell = gy * gridWidth + gx;

                    // Where this pixel's viewing ray lands in the rectified right image, and how far along the rectified z axis a unit ray goes.
                    Vector3 ray = right.PixelToWorldRay(px, py);
                    if (!rectRight.TryProject(rectRight.Position + ray, out float u, out float v, out float rayZ) || rayZ < 0.2f)
                    {
                        status[cell] = DepthStatus.Outside;
                        continue;
                    }
                    int ui = (int)MathF.Round(u), vi = (int)MathF.Round(v);
                    if (ui < 2 || vi < 2 || ui >= width - 2 || vi >= height - 2 || !rightValid[vi * width + ui])
                    {
                        status[cell] = DepthStatus.Outside;
                        continue;
                    }

                    float d;
                    if (useBlocks)
                    {
                        // The median disparity of the block's matched pixels; the block must be mostly matched and its pixels must mostly agree.
                        int n = 0;
                        for (int yy = Math.Max(2, vi - half); yy <= Math.Min(height - 3, vi + half); yy++)
                            for (int xx = Math.Max(2, ui - half); xx <= Math.Min(width - 3, ui + half); xx++)
                            {
                                float value = disparity[yy * width + xx];
                                if (value > 0f && !float.IsNaN(value) && rightValid[yy * width + xx])
                                    block[n++] = value;
                            }
                        if (n < parameters.StereoBlockMinValidFraction * blockArea)
                        {
                            status[cell] = DepthStatus.Unmatched;
                            continue;
                        }
                        block.Slice(0, n).Sort();
                        d = block[n / 2];
                        float tolerance = MathF.Max(parameters.StereoEdgePixels, parameters.StereoEdgeFraction * d);
                        int agree = 0;
                        for (int i = 0; i < n; i++)
                            if (MathF.Abs(block[i] - d) <= tolerance)
                                agree++;
                        if (agree < parameters.StereoBlockAgreeFraction * n)
                        {
                            status[cell] = DepthStatus.Edge;
                            continue;
                        }
                    }
                    else
                    {
                        d = disparity[vi * width + ui];
                        if (!(d > 0f) || float.IsNaN(d))
                        {
                            status[cell] = DepthStatus.Unmatched;
                            continue;
                        }
                    }

                    float r = focalBaseline / (d * rayZ);
                    if (!(r >= minRange) || !(r <= maxRange))
                    {
                        status[cell] = DepthStatus.OutOfRange;
                        continue;
                    }

                    // The match must lie inside the rectified left image.
                    int li = (int)MathF.Round(u + d);
                    if (li < 0 || li >= width || !leftValid[vi * width + li])
                    {
                        status[cell] = DepthStatus.Outside;
                        continue;
                    }

                    // Not on a depth edge: the neighbours (a couple of pixels away) have about the same disparity.
                    float edgeTolerance = MathF.Max(parameters.StereoEdgePixels, parameters.StereoEdgeFraction * d);
                    if (!useBlocks &&
                        (Differs(disparity, width, ui - 2, vi, d, edgeTolerance) || Differs(disparity, width, ui + 2, vi, d, edgeTolerance) ||
                         Differs(disparity, width, ui, vi - 2, d, edgeTolerance) || Differs(disparity, width, ui, vi + 2, d, edgeTolerance)))
                    {
                        status[cell] = DepthStatus.Edge;
                        continue;
                    }

                    range[cell] = r;
                }
            });

            var map = new DepthMap
            {
                ImageWidth = right.Width,
                ImageHeight = right.Height,
                Stride = stride,
                Origin = origin,
                GridWidth = gridWidth,
                GridHeight = gridHeight,
                Range = range,
                Status = status,
            };
            if (map.ValidCount == 0)
            {
                reason = "no depth could be matched";
                return null;
            }
            return map;
        }

        private static bool Differs(float[] disparity, int width, int x, int y, float d, float tolerance)
        {
            float other = disparity[y * width + x];
            return !float.IsNaN(other) && MathF.Abs(other - d) > tolerance;
        }

        /// <summary>A pinhole camera of the given size, looking along <paramref name="rotation"/>, with its principal point in the middle.</summary>
        private static MapKeyFrame VirtualCamera(Vector3 position, Quaternion rotation, int width, int height, float focal)
        {
            var camera = new MapKeyFrame
            {
                Position = position,
                Rotation = rotation,
                Width = width,
                Height = height,
                Fx = focal,
                Fy = focal,
                Cx = (width - 1) / 2f,
                Cy = (height - 1) / 2f,
                FlipVertically = false,
            };
            camera.Prepare();
            return camera;
        }

        /// <summary>
        /// The image a rectified camera at the same centre as <paramref name="real"/> would take: for each of its pixels the viewing ray is followed
        /// into the real image (bilinear sampling). Pixels the real image does not cover are black and flagged invalid.
        /// </summary>
        private static byte[] Rectify(MapKeyFrame real, byte[] gray, MapKeyFrame rectified, out bool[] valid)
        {
            int width = rectified.Width, height = rectified.Height;
            int sourceWidth = real.Width, sourceHeight = real.Height;
            var image = new byte[width * height];
            var ok = new bool[width * height];

            Parallel.For(0, height, y =>
            {
                for (int x = 0; x < width; x++)
                {
                    Vector3 ray = rectified.PixelToWorldRay(x, y);
                    if (!real.TryProject(real.Position + ray, out float sx, out float sy, out _))
                        continue;
                    if (sx < 0f || sy < 0f || sx > sourceWidth - 1.001f || sy > sourceHeight - 1.001f)
                        continue;

                    int x0 = (int)sx, y0 = (int)sy;
                    float fx = sx - x0, fy = sy - y0;
                    int i = y0 * sourceWidth + x0;
                    float top = gray[i] * (1f - fx) + gray[i + 1] * fx;
                    float bottom = gray[i + sourceWidth] * (1f - fx) + gray[i + sourceWidth + 1] * fx;
                    image[y * width + x] = (byte)MathF.Round(top * (1f - fy) + bottom * fy);
                    ok[y * width + x] = true;
                }
            });

            valid = ok;
            return image;
        }

        /// <summary>
        /// How many rows lower the left image's content lies than the right image's (a vertical offset, in pixels, with sub-pixel precision), from the
        /// correlation of the two images' row profiles (the mean brightness of each row over the middle half of the width; their differences, so a
        /// brightness gradient does not matter). 0 if the profiles do not agree clearly on one offset.
        /// </summary>
        private static float EstimateVerticalShift(byte[] right, byte[] left, int width, int height, float maxShift)
        {
            int x0 = width / 4, x1 = width * 3 / 4;
            var profileRight = new float[height];
            var profileLeft = new float[height];
            for (int y = 0; y < height; y++)
            {
                float sumRight = 0f, sumLeft = 0f;
                for (int x = x0; x < x1; x++)
                {
                    sumRight += right[y * width + x];
                    sumLeft += left[y * width + x];
                }
                profileRight[y] = sumRight / (x1 - x0);
                profileLeft[y] = sumLeft / (x1 - x0);
            }

            var gradientRight = new float[height];
            var gradientLeft = new float[height];
            for (int y = 1; y < height - 1; y++)
            {
                gradientRight[y] = profileRight[y + 1] - profileRight[y - 1];
                gradientLeft[y] = profileLeft[y + 1] - profileLeft[y - 1];
            }

            int limit = Math.Max(1, (int)MathF.Ceiling(maxShift));
            var correlation = new float[2 * limit + 1];
            int best = -1;
            for (int s = -limit; s <= limit; s++)
            {
                float dot = 0f, normRight = 0f, normLeft = 0f;
                for (int y = limit + 1; y < height - limit - 1; y++)
                {
                    float a = gradientRight[y], b = gradientLeft[y + s];
                    dot += a * b;
                    normRight += a * a;
                    normLeft += b * b;
                }
                float c = dot / MathF.Sqrt(MathF.Max(1e-6f, normRight * normLeft));
                correlation[s + limit] = c;
                if (best < 0 || c > correlation[best])
                    best = s + limit;
            }

            // Clear enough: a strong peak, well above the correlation at an offset a few pixels away.
            if (correlation[best] < 0.5f)
                return 0f;
            for (int i = 0; i < correlation.Length; i++)
                if (Math.Abs(i - best) > 2 && correlation[i] > correlation[best] - 0.1f)
                    return 0f;

            float shift = best - limit;
            if (best > 0 && best < correlation.Length - 1)
            {
                float c0 = correlation[best - 1], c1 = correlation[best], c2 = correlation[best + 1];
                float curvature = c0 - 2f * c1 + c2;
                if (curvature < -1e-6f)
                    shift += Math.Clamp(0.5f * (c0 - c2) / curvature, -0.5f, 0.5f);
            }
            return MathF.Abs(shift) <= maxShift ? shift : 0f;
        }

        /// <summary>The image moved up by <paramref name="shift"/> rows: the result at row y is the input at row y + shift (bilinear).</summary>
        private static byte[] ShiftVertically(byte[] image, bool[] valid, int width, int height, float shift, out bool[] shiftedValid)
        {
            var result = new byte[image.Length];
            var ok = new bool[image.Length];
            for (int y = 0; y < height; y++)
            {
                float sy = y + shift;
                if (sy < 0f || sy > height - 1.001f)
                    continue;
                int y0 = (int)sy;
                float f = sy - y0;
                for (int x = 0; x < width; x++)
                {
                    int i = y0 * width + x;
                    if (!valid[i] || !valid[i + width])
                        continue;
                    result[y * width + x] = (byte)MathF.Round(image[i] * (1f - f) + image[i + width] * f);
                    ok[y * width + x] = true;
                }
            }
            shiftedValid = ok;
            return result;
        }
    }
}
