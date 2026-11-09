using Server.Manager.SpatialSync;

namespace Server.Utilities
{
    /// <summary>
    /// Shows the scene anchor sub-maps of a <see cref="GlobalMappingManager"/> in 3D windows (<see cref="SceneMapView"/>), on a UI thread of its own: a window
    /// for every player whose scan is not merged yet (in that player's own tracking space), and one "Global map" window that the players join as they are
    /// placed in it. When a player is merged, their own window closes and their anchors appear in the global window. A window the user closes stays closed.
    /// </summary>
    internal sealed class SceneMapViewer : IDisposable
    {
        private const int RefreshMilliseconds = 500;

        private readonly GlobalMappingManager _map;
        private readonly Thread _uiThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Dictionary<string, Form> _windows = new();
        private readonly HashSet<string> _dismissed = new();
        private Form? _dispatcher;
        private System.Windows.Forms.Timer? _timer;
        private long _drawnVersion = -1;
        private bool _disposed;

        public SceneMapViewer(GlobalMappingManager map)
        {
            _map = map;
            _uiThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "Scene map viewer",
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_dispatcher is { IsDisposed: false })
                _dispatcher.BeginInvoke(Application.ExitThread);
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
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
            };
            _dispatcher.Load += (_, _) =>
            {
                _dispatcher!.Hide();
                _ready.Set();
            };

            _timer = new System.Windows.Forms.Timer { Interval = RefreshMilliseconds };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();

            Application.Run(_dispatcher);

            _timer.Dispose();
            foreach (Form window in _windows.Values.ToArray())
                window.Dispose();
            _windows.Clear();
            _dispatcher.Dispose();
        }

        /// <summary>Bring the windows up to date with the map (on the UI thread).</summary>
        private void Refresh()
        {
            try
            {
                long version = _map.Version;
                if (_disposed || version == _drawnVersion)
                    return;
                _drawnVersion = version;

                SceneMapSnapshot snapshot = _map.GetSceneMapSnapshot();
                var keys = snapshot.SubMaps.Select(s => s.Key).ToHashSet();

                foreach (SceneSubMap subMap in snapshot.SubMaps)
                {
                    if (_dismissed.Contains(subMap.Key))
                        continue;

                    if (!_windows.TryGetValue(subMap.Key, out Form? window) || window.IsDisposed)
                    {
                        string key = subMap.Key;
                        var view = new SceneMapView { Dock = DockStyle.Fill };
                        int slot = _windows.Count;
                        window = new Form
                        {
                            Width = 1000,
                            Height = 750,
                            StartPosition = FormStartPosition.Manual,
                            Location = new Point(40 + 40 * (slot % 6), 40 + 40 * (slot % 6)),
                        };
                        window.Controls.Add(view);
                        window.FormClosed += (_, _) =>
                        {
                            _windows.Remove(key);
                            if (!_disposed)
                                _dismissed.Add(key); // closed by the user (or merged away: cleared below once the key is gone)
                        };
                        _windows[key] = window;
                        window.Show();
                    }

                    window.Text = subMap.Title;
                    ((SceneMapView)window.Controls[0]).SetData(subMap);
                }

                // A sub-map that no longer exists (the player was merged into the global map) has nothing left to show.
                foreach (string key in _windows.Keys.Where(k => !keys.Contains(k)).ToList())
                {
                    Form window = _windows[key];
                    window.Close();
                    _dismissed.Remove(key);
                }
                _dismissed.RemoveWhere(k => !keys.Contains(k));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SceneMapViewer] {ex.Message}");
            }
        }
    }
}
