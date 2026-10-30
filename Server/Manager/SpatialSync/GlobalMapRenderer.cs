using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;

namespace Server.Manager.SpatialSync
{
    /// <summary>Draws a top-down view of the global map for the debug window.</summary>
    internal static class GlobalMapRenderer
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(76, 194, 255),  // blue
            Color.FromArgb(255, 159, 64),  // orange
            Color.FromArgb(96, 214, 128),  // green
            Color.FromArgb(240, 98, 182),  // pink
            Color.FromArgb(240, 220, 90),  // yellow
            Color.FromArgb(170, 130, 255), // purple
        };

        private static readonly double[] GridSteps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100 };

        /// <summary>Colour used for a player (also used by the legend).</summary>
        public static Color PlayerColor(int playerID) => Palette[((playerID % Palette.Length) + Palette.Length) % Palette.Length];

        /// <summary>
        /// Top-down view: world X to the right, world Z up the screen (Unity, looking down the -Y axis). Map points are small
        /// squares, key frames are dots joined into a trajectory with a short line showing where the camera looked.
        /// </summary>
        public static Bitmap Render(GlobalMapSnapshot snapshot, int width = 1000, int height = 800)
        {
            var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(24, 26, 30));

            using var font = new Font("Segoe UI", 9f);
            using var boldFont = new Font("Segoe UI", 10f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.FromArgb(214, 218, 224));
            using var dimBrush = new SolidBrush(Color.FromArgb(130, 136, 146));

            const int margin = 40;
            const int footer = 34;
            var plot = new RectangleF(margin, margin, width - 2 * margin, height - margin - footer);

            int totalKeyFrames = snapshot.SubMaps.Sum(s => s.KeyFrames.Length);
            int totalPoints = snapshot.SubMaps.Sum(s => s.Points.Length);
            if (totalKeyFrames == 0)
            {
                g.DrawString("Waiting for key frames...", boldFont, textBrush, plot.X + 8, plot.Y + 8);
                return bitmap;
            }

            // ----- View bounds: every key frame, plus the bulk of the points (clipped so stray points do not shrink the map) -----
            var xs = new List<float>();
            var zs = new List<float>();
            foreach (MapSnapshotSubMap subMap in snapshot.SubMaps)
                foreach (MapSnapshotKeyFrame kf in subMap.KeyFrames)
                {
                    xs.Add(kf.Position.X);
                    zs.Add(kf.Position.Z);
                }

            float minX = xs.Min(), maxX = xs.Max(), minZ = zs.Min(), maxZ = zs.Max();
            if (totalPoints >= 20)
            {
                float[] px = snapshot.SubMaps.SelectMany(s => s.Points).Select(p => p.X).OrderBy(v => v).ToArray();
                float[] pz = snapshot.SubMaps.SelectMany(s => s.Points).Select(p => p.Z).OrderBy(v => v).ToArray();
                minX = MathF.Min(minX, px[(int)(px.Length * 0.02)]);
                maxX = MathF.Max(maxX, px[(int)((px.Length - 1) * 0.98)]);
                minZ = MathF.Min(minZ, pz[(int)(pz.Length * 0.02)]);
                maxZ = MathF.Max(maxZ, pz[(int)((pz.Length - 1) * 0.98)]);
            }

            float rangeX = MathF.Max(maxX - minX, 2f);
            float rangeZ = MathF.Max(maxZ - minZ, 2f);
            float centerX = (minX + maxX) / 2f;
            float centerZ = (minZ + maxZ) / 2f;
            float scale = MathF.Min(plot.Width / (rangeX * 1.12f), plot.Height / (rangeZ * 1.12f)); // pixels per metre

            PointF ToScreen(float x, float z) => new(
                plot.X + plot.Width / 2f + (x - centerX) * scale,
                plot.Y + plot.Height / 2f - (z - centerZ) * scale);

            g.SetClip(plot);

            // ----- Grid -----
            double step = GridSteps.FirstOrDefault(s => s * scale >= 55, GridSteps[^1]);
            using var gridPen = new Pen(Color.FromArgb(44, 48, 56));
            using var axisPen = new Pen(Color.FromArgb(96, 104, 118));
            float left = centerX - plot.Width / 2f / scale, right = centerX + plot.Width / 2f / scale;
            float bottom = centerZ - plot.Height / 2f / scale, top = centerZ + plot.Height / 2f / scale;
            for (double x = Math.Ceiling(left / step) * step; x <= right; x += step)
            {
                PointF a = ToScreen((float)x, top), b = ToScreen((float)x, bottom);
                g.DrawLine(Math.Abs(x) < 1e-9 ? axisPen : gridPen, a, b);
                g.DrawString($"{x:0.##}", font, dimBrush, a.X + 2, plot.Bottom - 16);
            }
            for (double z = Math.Ceiling(bottom / step) * step; z <= top; z += step)
            {
                PointF a = ToScreen(left, (float)z), b = ToScreen(right, (float)z);
                g.DrawLine(Math.Abs(z) < 1e-9 ? axisPen : gridPen, a, b);
                g.DrawString($"{z:0.##}", font, dimBrush, plot.X + 3, a.Y - 15);
            }

            // ----- Map points, then key frames on top -----
            foreach (MapSnapshotSubMap subMap in snapshot.SubMaps)
            {
                using var pointBrush = new SolidBrush(Color.FromArgb(200, PlayerColor(subMap.PlayerID)));
                foreach (Vector3 point in subMap.Points)
                {
                    PointF s = ToScreen(point.X, point.Z);
                    g.FillRectangle(pointBrush, s.X - 1.5f, s.Y - 1.5f, 3f, 3f);
                }
            }

            foreach (MapSnapshotSubMap subMap in snapshot.SubMaps)
            {
                Color color = PlayerColor(subMap.PlayerID);
                using var trajectoryPen = new Pen(Color.FromArgb(140, color), 1.5f);
                using var headingPen = new Pen(color, 2f);
                using var keyFrameBrush = new SolidBrush(color);
                using var latestPen = new Pen(Color.White, 2f);

                PointF[] path = subMap.KeyFrames.Select(k => ToScreen(k.Position.X, k.Position.Z)).ToArray();
                if (path.Length >= 2)
                    g.DrawLines(trajectoryPen, path);

                for (int i = 0; i < subMap.KeyFrames.Length; i++)
                {
                    MapSnapshotKeyFrame kf = subMap.KeyFrames[i];
                    PointF s = path[i];
                    var look = new Vector2(kf.Forward.X, kf.Forward.Z);
                    if (look.LengthSquared() > 1e-6f)
                    {
                        look = Vector2.Normalize(look);
                        g.DrawLine(headingPen, s, new PointF(s.X + look.X * 16f, s.Y - look.Y * 16f));
                    }
                    g.FillEllipse(keyFrameBrush, s.X - 3.5f, s.Y - 3.5f, 7f, 7f);
                }

                PointF last = path[^1];
                g.DrawEllipse(latestPen, last.X - 7f, last.Y - 7f, 14f, 14f);
            }

            g.ResetClip();
            using var framePen = new Pen(Color.FromArgb(70, 76, 88));
            g.DrawRectangle(framePen, plot.X, plot.Y, plot.Width, plot.Height);

            // ----- Legend and footer -----
            float ly = 8;
            g.DrawString($"Global map v{snapshot.Version}  -  {totalKeyFrames} key frames, {totalPoints} map points", boldFont, textBrush, margin, ly);
            float legendY = plot.Y + 8;
            foreach (MapSnapshotSubMap subMap in snapshot.SubMaps)
            {
                using var swatch = new SolidBrush(PlayerColor(subMap.PlayerID));
                g.FillEllipse(swatch, plot.X + 10, legendY + 3, 9, 9);
                string aligned = subMap.IsAligned ? "aligned" : "own frame";
                g.DrawString($"Player {subMap.PlayerID}: {subMap.KeyFrames.Length} key frames, {subMap.Points.Length} points ({aligned})", font, textBrush, plot.X + 26, legendY);
                legendY += 18;
            }

            g.DrawString("Top-down view: X right, Z up, metres. White ring = latest key frame. Unaligned players are drawn in their own device frames, so they will not line up yet.",
                font, dimBrush, margin, height - footer + 10);
            return bitmap;
        }
    }
}
