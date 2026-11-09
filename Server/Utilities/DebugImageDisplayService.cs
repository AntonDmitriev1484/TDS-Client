using Server.Archived.Manager.SpatialSync;
using Server.Archived.Utilities;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Runtime.InteropServices;

namespace Server.Utilities
{
    internal sealed class DebugImageDisplayService : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _uiThread;
        private readonly Dictionary<int, Form> _windows = new();
        private Form? _dispatcher;
        private bool _disposed;

        /// <summary>
        /// Flip the decoded key frame upside-down before showing it. Set to false if the debug window shows the image upside-down.
        /// </summary>
        private const bool FlipVertically = false;

        // Key in _windows for the global map window (player IDs are positive, so this never clashes).
        private const int MapWindowKey = int.MinValue;

        // The 3D point cloud windows, one per sub-map (keyed by sub-map ID), refreshed from the latest map snapshot every few seconds.
        private const int PointCloudIntervalMilliseconds = 3000;
        private readonly Dictionary<int, Form> _pointCloudWindows = new();
        private System.Windows.Forms.Timer? _pointCloudTimer;
        private GlobalMapSnapshot? _pointCloudSnapshot;
        private long _drawnPointCloudVersion = -1;

        public DebugImageDisplayService()
        {
            _uiThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "Spatial Sync Debug Image Windows"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            _ready.Wait();
        }

        public void ShowFrame(int playerID, KeyFrameImageDTO keyFrame)
        {
            if (keyFrame.RightCamera?.ImageData is null || keyFrame.RightCamera.ImageData.Length == 0)
                return;

            // Decode here (the caller's thread) so the UI thread only has to swap the picture.
            Bitmap? bitmap = DecodeImage(keyFrame.RightCamera.ImageData);
            if (bitmap is null)
                return;

            if (_disposed || _dispatcher is null || _dispatcher.IsDisposed)
            {
                bitmap.Dispose();
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(() => UpdatePlayerWindow(playerID, bitmap));
            }
            catch (InvalidOperationException)
            {
                // The UI thread shut down between the check and the post.
                bitmap.Dispose();
            }
        }

        /// <summary>
        /// Show a key frame with its stereo depth: a dot at each measured point, coloured by how far in front of the camera it is
        /// (red = near, through yellow and green, to blue = far) and labelled with the depth in meters when there are not too many.
        /// </summary>
        public void ShowFrameWithDepthPoints(int playerID, KeyFrameImageDTO keyFrame, int imageWidth, int imageHeight, IReadOnlyList<DepthPoint> points, string note, IReadOnlyList<RejectedPoint>? rejected = null)
        {
            if (keyFrame.RightCamera?.ImageData is null || keyFrame.RightCamera.ImageData.Length == 0)
                return;

            // Decode and draw here (the caller's thread) so the UI thread only has to swap the picture.
            Bitmap? bitmap = DecodeImage(keyFrame.RightCamera.ImageData);
            if (bitmap is null)
                return;

            try
            {
                DrawDepthPoints(bitmap, imageWidth, imageHeight, points, note, rejected ?? Array.Empty<RejectedPoint>());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is ExternalException)
            {
                // Show the plain frame if drawing failed.
                Console.Error.WriteLine($"Could not draw the depth points on the frame of player {playerID}: {ex.Message}");
            }

            if (_disposed || _dispatcher is null || _dispatcher.IsDisposed)
            {
                bitmap.Dispose();
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(() => UpdatePlayerWindow(playerID, bitmap));
            }
            catch (InvalidOperationException)
            {
                // The UI thread shut down between the check and the post.
                bitmap.Dispose();
            }
        }

        /// <summary>
        /// Show the global map (top-down view of every player's key frames and map points) in its own window. Call it every
        /// time the map has been updated; the window is created on first use and then refreshed in place.
        /// </summary>
        public void ShowMap(GlobalMapSnapshot snapshot)
        {
            if (_disposed || _dispatcher is null || _dispatcher.IsDisposed)
                return;

            // Draw here (the caller's thread) so the UI thread only has to swap the picture.
            Bitmap bitmap = GlobalMapRenderer.Render(snapshot);

            try
            {
                _dispatcher.BeginInvoke(() => UpdateMapWindow(bitmap));
            }
            catch (InvalidOperationException)
            {
                // The UI thread shut down between the check and the post.
                bitmap.Dispose();
            }
        }

        /// <summary>
        /// Hand over the latest copy of the map. Every few seconds each sub-map's points and key frames are shown in a window of its own
        /// that can be moved around (see <see cref="PointCloudView"/>); a sub-map that was merged into another has its window closed.
        /// The first call shows the windows right away.
        /// </summary>
        public void ShowPointClouds(GlobalMapSnapshot snapshot)
        {
            if (_disposed || _dispatcher is null || _dispatcher.IsDisposed)
                return;

            Volatile.Write(ref _pointCloudSnapshot, snapshot);

            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (_pointCloudTimer != null && !_pointCloudTimer.Enabled)
                    {
                        _pointCloudTimer.Start();
                        UpdatePointCloudWindows();
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // The UI thread shut down between the check and the post.
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            PostToUi(Application.ExitThread);
            _ready.Dispose();
        }

        private void RunMessageLoop()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _dispatcher = new Form
            {
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                FormBorderStyle = FormBorderStyle.FixedToolWindow
            };
            _dispatcher.Load += (_, _) =>
            {
                _dispatcher!.Hide();
                _ready.Set();
            };

            _pointCloudTimer = new System.Windows.Forms.Timer { Interval = PointCloudIntervalMilliseconds };
            _pointCloudTimer.Tick += (_, _) => UpdatePointCloudWindows();

            Application.Run(_dispatcher);

            _pointCloudTimer.Dispose();
            foreach (Form window in _pointCloudWindows.Values.ToArray())
                window.Dispose();
            _pointCloudWindows.Clear();
            foreach (Form window in _windows.Values.ToArray())
                window.Dispose();
            _windows.Clear();
            _dispatcher.Dispose();
        }

        private void PostToUi(Action action)
        {
            if (_disposed || _dispatcher is null || _dispatcher.IsDisposed)
                return;

            _dispatcher.BeginInvoke(action);
        }

        private void UpdatePlayerWindow(int playerID, Bitmap bitmap)
        {
            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            if (!_windows.TryGetValue(playerID, out Form? window) || window.IsDisposed)
            {
                window = CreatePlayerWindow(playerID);
                _windows[playerID] = window;
                window.Show();
            }

            var pictureBox = (PictureBox)window.Controls[0];
            Image? previousImage = pictureBox.Image;
            pictureBox.Image = bitmap;
            previousImage?.Dispose();
            window.Text = $"Player {playerID} - Key frame";
        }

        /// <summary>Give every sub-map of the latest snapshot its window and its current points (on the UI thread). The windows keep the view the user chose.</summary>
        private void UpdatePointCloudWindows()
        {
            GlobalMapSnapshot? snapshot = Volatile.Read(ref _pointCloudSnapshot);
            if (_disposed || snapshot == null || snapshot.Version == _drawnPointCloudVersion)
                return;
            _drawnPointCloudVersion = snapshot.Version;

            foreach (MapSnapshotSubMap subMap in snapshot.SubMaps)
            {
                if (!_pointCloudWindows.TryGetValue(subMap.ID, out Form? window) || window.IsDisposed)
                {
                    int id = subMap.ID;
                    var view = new PointCloudView { Dock = DockStyle.Fill };
                    window = new Form { Width = 1000, Height = 750, StartPosition = FormStartPosition.Manual, Location = new Point(40 + 40 * ((id - 1) % 6), 40 + 40 * ((id - 1) % 6)) };
                    window.Controls.Add(view);
                    window.FormClosed += (_, _) => _pointCloudWindows.Remove(id);
                    _pointCloudWindows[id] = window;
                    window.Show();
                }

                window.Text = $"Map {subMap.ID} - 3D points";
                ((PointCloudView)window.Controls[0]).SetData(subMap);
            }

            // A sub-map that no longer exists (merged into another) has nothing left to show.
            var existing = snapshot.SubMaps.Select(s => s.ID).ToHashSet();
            foreach (int id in _pointCloudWindows.Keys.Where(id => !existing.Contains(id)).ToList())
                _pointCloudWindows[id].Close();
        }

        private void UpdateMapWindow(Bitmap bitmap)
        {
            if (_disposed)
            {
                bitmap.Dispose();
                return;
            }

            if (!_windows.TryGetValue(MapWindowKey, out Form? window) || window.IsDisposed)
            {
                window = CreateWindow(MapWindowKey, "Global map", 1020, 860);
                _windows[MapWindowKey] = window;
                window.Show();
            }

            var pictureBox = (PictureBox)window.Controls[0];
            Image? previousImage = pictureBox.Image;
            pictureBox.Image = bitmap;
            previousImage?.Dispose();
        }

        private Form CreatePlayerWindow(int playerID)
        {
            return CreateWindow(playerID, $"Player {playerID} - Key frame", 800, 600);
        }

        private Form CreateWindow(int key, string title, int width, int height)
        {
            var pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };

            var window = new Form
            {
                Text = title,
                Width = width,
                Height = height,
                StartPosition = FormStartPosition.CenterScreen
            };
            window.Controls.Add(pictureBox);
            window.FormClosed += (_, _) => _windows.Remove(key);
            return window;
        }

        // Depths from DepthNear to DepthFar (meters) are spread over the colour scale, on a log scale so nearby points keep their detail.
        private const float DepthNear = 0.3f;
        private const float DepthFar = 8f;

        /// <summary>Red for near through yellow and green to blue for far.</summary>
        private static Color DepthColor(float depth)
        {
            float t = (MathF.Log(Math.Clamp(depth, DepthNear, DepthFar)) - MathF.Log(DepthNear)) / (MathF.Log(DepthFar) - MathF.Log(DepthNear));
            float hue = 240f * t; // 0 = red ... 240 = blue
            float h = hue / 60f;
            int sector = (int)MathF.Floor(h);
            float f = h - sector;
            float up = f, down = 1f - f;
            (float r, float g, float b) = sector switch
            {
                0 => (1f, up, 0f),
                1 => (down, 1f, 0f),
                2 => (0f, 1f, up),
                3 => (0f, down, 1f),
                _ => (0f, 0f, 1f),
            };
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static void DrawDepthPoints(Bitmap bitmap, int imageWidth, int imageHeight, IReadOnlyList<DepthPoint> points, string note, IReadOnlyList<RejectedPoint> rejected)
        {
            float sx = imageWidth > 0 ? bitmap.Width / (float)imageWidth : 1f;
            float sy = imageHeight > 0 ? bitmap.Height / (float)imageHeight : 1f;

            // The dots are sized by the spacing of the grid (the points and the rejected positions together are the whole grid), so neighbours do
            // not cover each other and every grid position can be seen.
            int gridCells = Math.Max(1, points.Count + rejected.Count);
            float spacing = MathF.Sqrt(bitmap.Width * (float)bitmap.Height / gridCells);
            float radius = Math.Clamp(0.4f * spacing, 1.5f, 8f);

            using Graphics g = Graphics.FromImage(bitmap);

            // Grid positions without a depth first (small squares, by reason), so the depth dots lie on top.
            int[] rejectedCounts = new int[5];
            Color[] rejectedColors = { Color.Transparent, Color.Gray, Color.Orange, Color.Magenta, Color.DimGray };
            var rejectedBrushes = rejectedColors.Select(c => new SolidBrush(c)).ToArray();
            float half = Math.Max(1f, radius * 0.4f);
            foreach (RejectedPoint point in rejected)
            {
                int reason = Math.Clamp(point.Reason, 1, 4);
                rejectedCounts[reason]++;
                g.FillRectangle(rejectedBrushes[reason], point.X * sx - half, point.Y * sy - half, 2 * half, 2 * half);
            }
            foreach (SolidBrush brush in rejectedBrushes)
                brush.Dispose();

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var labelFont = new Font(FontFamily.GenericSansSerif, Math.Max(10f, bitmap.Height / 55f), FontStyle.Bold, GraphicsUnit.Pixel);
            bool label = points.Count <= 60;
            using var outline = new Pen(Color.Black, radius >= 4f ? 2f : 1f);
            foreach (DepthPoint point in points)
            {
                float x = point.X * sx, y = point.Y * sy;
                using var brush = new SolidBrush(DepthColor(point.Depth));
                g.FillEllipse(brush, x - radius, y - radius, 2 * radius, 2 * radius);
                g.DrawEllipse(outline, x - radius, y - radius, 2 * radius, 2 * radius);
                if (label)
                {
                    string text = point.Depth.ToString("0.0");
                    g.DrawString(text, labelFont, Brushes.Black, x + radius + 1, y - radius + 1);
                    g.DrawString(text, labelFont, Brushes.White, x + radius, y - radius);
                }
            }

            string summary = $"{points.Count} depth points";
            if (points.Count > 0)
            {
                float[] depths = points.Select(p => p.Depth).OrderBy(v => v).ToArray();
                summary += $"   depth {depths[0]:0.0}-{depths[^1]:0.0} m, median {depths[depths.Length / 2]:0.0} m";
            }
            if (note.Length > 0)
                summary += $"   ({note})";
            string second = rejected.Count > 0
                ? $"no depth: {rejectedCounts[1]} unmatched (grey), {rejectedCounts[2]} out of range (orange), {rejectedCounts[3]} edge/disagree (magenta), {rejectedCounts[4]} outside"
                : "";

            using var font = new Font(FontFamily.GenericSansSerif, Math.Max(12f, bitmap.Height / 40f), FontStyle.Bold, GraphicsUnit.Pixel);
            SizeF size = g.MeasureString(summary, font);
            g.FillRectangle(Brushes.Black, 4, 4, size.Width + 8, font.Height + 4);
            g.DrawString(summary, font, Brushes.White, 8, 6);
            if (second.Length > 0)
            {
                SizeF secondSize = g.MeasureString(second, font);
                g.FillRectangle(Brushes.Black, 4, font.Height + 10, secondSize.Width + 8, font.Height + 4);
                g.DrawString(second, font, Brushes.White, 8, font.Height + 12);
            }

            // Colour scale along the bottom edge: near ... far.
            int barWidth = Math.Min(bitmap.Width - 16, 300), barHeight = Math.Max(8, bitmap.Height / 60);
            int barTop = bitmap.Height - barHeight - 8;
            for (int i = 0; i < barWidth; i++)
            {
                float depth = MathF.Exp(MathF.Log(DepthNear) + (MathF.Log(DepthFar) - MathF.Log(DepthNear)) * i / Math.Max(1, barWidth - 1));
                using var pen = new Pen(DepthColor(depth));
                g.DrawLine(pen, 8 + i, barTop, 8 + i, barTop + barHeight);
            }
            g.DrawString($"{DepthNear:0.#} m", labelFont, Brushes.White, 8, barTop - labelFont.Height - 2);
            g.DrawString($"{DepthFar:0.#} m", labelFont, Brushes.White, 8 + barWidth - 30, barTop - labelFont.Height - 2);
        }

        /// <summary>
        /// Decode a JPEG/PNG key frame image into a standalone bitmap (not tied to the source stream), optionally flipped.
        /// Returns null if the bytes are not a valid image.
        /// </summary>
        private static Bitmap? DecodeImage(byte[] imageData)
        {
            try
            {
                using var stream = new MemoryStream(imageData);
                using var decoded = new Bitmap(stream);

                // Copying into a new 32bpp bitmap detaches it from the stream and gives it a plain pixel format.
                var bitmap = new Bitmap(decoded);
                if (FlipVertically)
                    bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);

                return bitmap;
            }
            catch (ArgumentException)
            {
                return null; // Not a decodable image
            }
        }
    }
}
