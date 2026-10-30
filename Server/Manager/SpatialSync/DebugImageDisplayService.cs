using SharedLibrary.Communication.DataTransferObjects.SpatialSync;

namespace Server.Manager.SpatialSync
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

        public void ShowFrame(int playerID, KeyFrameDTO keyFrame)
        {
            if (keyFrame.ImageData is null || keyFrame.ImageData.Length == 0)
                return;

            // Decode here (the caller's thread) so the UI thread only has to swap the picture.
            Bitmap? bitmap = DecodeImage(keyFrame.ImageData);
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

            Application.Run(_dispatcher);

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
