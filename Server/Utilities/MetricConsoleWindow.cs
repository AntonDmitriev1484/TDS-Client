using System.Text;

namespace Server.Utilities
{
    /// <summary>
    /// A console-style window (dark, monospaced, read-only, scrolling) on a UI thread of its own. A process only has one real console, so
    /// this is how a game gets a separate "console" of its own. <see cref="WriteLine"/> is safe to call from any thread and never blocks
    /// on the UI; if the window cannot be created, or the user closes it, the lines go to the main console instead.
    /// </summary>
    internal sealed class MetricConsoleWindow : IDisposable
    {
        private const int MaxChars = 200_000;   // the oldest lines are dropped beyond this, so a long session stays light
        private const int FlushMilliseconds = 100;

        private readonly object _lock = new();
        private readonly StringBuilder _pending = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly string _prefix;
        private Thread? _uiThread;
        private Form? _form;
        private TextBox? _box;
        private System.Windows.Forms.Timer? _timer;
        private string _title;
        private bool _failed;
        private bool _closedByUser;
        private bool _disposed;

        public MetricConsoleWindow(string title, string fallbackPrefix = "[Metrics]")
        {
            _title = title;
            _prefix = fallbackPrefix;
        }

        /// <summary>Changes the window title (for example once the game's ID is known).</summary>
        public void SetTitle(string title)
        {
            lock (_lock)
            {
                _title = title;
                if (_form is { IsDisposed: false } form)
                {
                    try { form.BeginInvoke(() => form.Text = title); } catch (Exception) { }
                }
            }
        }

        public void WriteLine(string line)
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                EnsureWindow();
                if (_failed || _closedByUser)
                {
                    // No window to show it in (or the user closed it): the main console is the fallback.
                    Console.WriteLine(line);
                    return;
                }
                _pending.Append(line).Append("\r\n");
            }
        }

        public void WriteError(string line) => WriteLine(line);

        public void Dispose()
        {
            Form? form;
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                form = _form;
            }

            try
            {
                if (form is { IsDisposed: false })
                    form.BeginInvoke(() => { Flush(); form.Close(); });
            }
            catch (Exception) { }
        }

        private void EnsureWindow()
        {
            if (_uiThread != null || _failed)
                return;

            try
            {
                _uiThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "Metric console" };
                _uiThread.SetApartmentState(ApartmentState.STA);
                _uiThread.Start();
                if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _form == null)
                    _failed = true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.Error.WriteLine($"{_prefix} Could not open the metrics window: {ex.Message}");
            }
        }

        private void RunMessageLoop()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                _box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(12, 12, 12),
                    ForeColor = Color.FromArgb(204, 204, 204),
                    Font = new Font("Consolas", 10f),
                    BorderStyle = BorderStyle.None,
                };

                var form = new Form
                {
                    Text = _title,
                    Width = 1100,
                    Height = 500,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(60 + 30 * (Environment.CurrentManagedThreadId % 5), 60 + 30 * (Environment.CurrentManagedThreadId % 5)),
                    ShowInTaskbar = true,
                };
                form.Controls.Add(_box);
                form.Load += (_, _) => _ready.Set();
                form.FormClosed += (_, _) =>
                {
                    lock (_lock) { _closedByUser = !_disposed; }
                    _timer?.Stop();
                };

                _timer = new System.Windows.Forms.Timer { Interval = FlushMilliseconds };
                _timer.Tick += (_, _) => Flush();
                _timer.Start();

                _form = form;
                Application.Run(form);

                _timer.Dispose();
                form.Dispose();
            }
            catch (Exception ex)
            {
                lock (_lock) { _failed = true; }
                Console.Error.WriteLine($"{_prefix} The metrics window stopped: {ex.Message}");
            }
            finally
            {
                _ready.Set();
            }
        }

        /// <summary>Moves the queued lines into the text box (on the UI thread) and keeps it scrolled to the end.</summary>
        private void Flush()
        {
            string text;
            lock (_lock)
            {
                if (_pending.Length == 0 || _box == null)
                    return;
                text = _pending.ToString();
                _pending.Clear();
            }

            TextBox box = _box!;
            if (box.TextLength + text.Length > MaxChars)
            {
                int drop = Math.Min(box.TextLength, box.TextLength + text.Length - MaxChars + MaxChars / 4);
                box.Select(0, drop);
                box.SelectedText = "";
            }
            box.AppendText(text);
        }
    }
}
