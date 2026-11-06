using Server.Manager.SpatialSync;
using System.Drawing.Drawing2D;
using System.Numerics;

namespace Server.Utilities
{
    /// <summary>
    /// A 3D view of one sub-map of scene anchors that can be moved around with the mouse: walls, doors and windows as outlined rectangles with a tick
    /// along their normal, floor and ceiling as flat rectangles, furniture as boxes, and the headsets as a dot with a line where they look. Each player has
    /// a colour. Anchors drawn solid were matched with another player's (or are the reference player's own), dashed ones only this player has so far.
    /// The view (angle, position, zoom) belongs to the control and is not touched when the data is replaced.
    /// Left mouse button: orbit. Right or middle button: pan. Wheel: zoom. Double click: fit the view. L: labels on/off. F: floor and ceiling on/off.
    /// </summary>
    internal sealed class SceneMapView : Control
    {
        private static readonly Color[] PlayerColors =
        {
            Color.FromArgb(255, 99, 99), Color.FromArgb(99, 190, 255), Color.FromArgb(120, 230, 120), Color.FromArgb(255, 210, 90),
            Color.FromArgb(220, 130, 255), Color.FromArgb(90, 230, 220), Color.FromArgb(255, 160, 90), Color.FromArgb(200, 200, 200),
        };

        private SceneSubMap? _data;
        private float _yaw = 0.6f;
        private float _pitch = 0.45f;
        private float _distance = 6f;
        private Vector3 _target;
        private bool _fitted;
        private bool _showLabels = true;
        private bool _showHorizontal = true;
        private Point _lastMouse;

        public SceneMapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            DoubleBuffered = true;
            TabStop = true;
            BackColor = Color.FromArgb(20, 20, 24);
        }

        /// <summary>Replace what is shown. The first data with anchors also fits the view to them; later data keeps the view the user chose.</summary>
        public void SetData(SceneSubMap data)
        {
            _data = data;
            if (!_fitted && data.Anchors.Length > 0)
                FitToData();
            Invalidate();
        }

        private void FitToData()
        {
            if (_data == null || _data.Anchors.Length == 0)
                return;

            Vector3 sum = Vector3.Zero;
            foreach (SceneViewAnchor a in _data.Anchors)
                sum += a.Position;
            _target = sum / _data.Anchors.Length;
            float radius = _data.Anchors.Max(a => Vector3.Distance(a.Position, _target));
            _distance = Math.Max(2f, radius * 2.4f);
            _fitted = true;
        }

        #region Camera

        private void GetBasis(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up)
        {
            float cp = MathF.Cos(_pitch);
            eye = _target + _distance * new Vector3(cp * MathF.Sin(_yaw), MathF.Sin(_pitch), cp * MathF.Cos(_yaw));
            forward = Vector3.Normalize(_target - eye);
            // Unity's axes are left-handed (looking along +Z, +X is to the right), so right is up x forward.
            right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            up = Vector3.Cross(forward, right);
        }

        #endregion Camera

        #region Mouse and keyboard

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus(); // so the wheel and keys reach the view
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;

            if (e.Button == MouseButtons.Left)
            {
                _yaw += dx * 0.01f;
                _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.5f, 1.5f);
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                GetBasis(out _, out _, out Vector3 right, out Vector3 up);
                float scale = _distance * 0.0015f;
                _target += (-dx * right + dy * up) * scale;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, e.Delta / 120f), 0.3f, 500f);
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            FitToData();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.L)
            {
                _showLabels = !_showLabels;
                Invalidate();
            }
            else if (e.KeyCode == Keys.F)
            {
                _showHorizontal = !_showHorizontal;
                Invalidate();
            }
        }

        #endregion Mouse and keyboard

        #region Drawing

        private delegate bool ProjectFunc(Vector3 p, out float sx, out float sy, out float depth);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_data == null || Width < 10 || Height < 10)
                return;

            GetBasis(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up);
            float focal = 0.866f * Height; // 60 degree vertical field of view
            float cx = Width / 2f, cy = Height / 2f;

            bool Project(Vector3 p, out float sx, out float sy, out float depth)
            {
                Vector3 v = p - eye;
                depth = Vector3.Dot(v, forward);
                sx = sy = 0f;
                if (depth < 0.05f)
                    return false;
                sx = cx + focal * Vector3.Dot(v, right) / depth;
                sy = cy - focal * Vector3.Dot(v, up) / depth;
                return true;
            }

            DrawGrid(g, Project);

            using var font = new Font(FontFamily.GenericSansSerif, 11f, FontStyle.Regular, GraphicsUnit.Pixel);
            foreach (SceneViewAnchor anchor in _data.Anchors)
                DrawAnchor(g, Project, font, anchor);

            foreach (SceneViewPlayer player in _data.Players)
            {
                if (!Project(player.Position, out float sx, out float sy, out _))
                    continue;
                Color color = PlayerColor(player.PlayerID);
                using var pen = new Pen(color, 2f);
                if (Project(player.Position + player.Forward * 0.4f, out float fx, out float fy, out _))
                    g.DrawLine(pen, sx, sy, fx, fy);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, sx - 5, sy - 5, 10, 10);
                using var bold = new Font(FontFamily.GenericSansSerif, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString($"P{player.PlayerID}", bold, brush, sx + 7, sy - 8);
            }

            DrawOverlay(g);
        }

        private static Color PlayerColor(int playerID) => PlayerColors[Math.Abs(playerID) % PlayerColors.Length];

        /// <summary>A 1 m grid at the height of the floor (the lowest anchor), to judge distances.</summary>
        private void DrawGrid(Graphics g, ProjectFunc project)
        {
            if (_data == null || _data.Anchors.Length == 0)
                return;

            float floorY = _data.Anchors.Where(a => a.Kind == "Floor").Select(a => a.Position.Y).DefaultIfEmpty(_data.Anchors.Min(a => a.Position.Y)).Min();
            float radius = Math.Clamp(_distance, 2f, 20f);
            int cellsAround = (int)MathF.Ceiling(radius);
            float centerX = MathF.Round(_target.X), centerZ = MathF.Round(_target.Z);
            using var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
            for (int i = -cellsAround; i <= cellsAround; i++)
            {
                DrawSegment(g, project, pen, new Vector3(centerX + i, floorY, centerZ - cellsAround), new Vector3(centerX + i, floorY, centerZ + cellsAround));
                DrawSegment(g, project, pen, new Vector3(centerX - cellsAround, floorY, centerZ + i), new Vector3(centerX + cellsAround, floorY, centerZ + i));
            }
        }

        private void DrawAnchor(Graphics g, ProjectFunc project, Font font, SceneViewAnchor anchor)
        {
            if (!_showHorizontal && (anchor.Kind == "Floor" || anchor.Kind == "Ceiling"))
                return;

            Color color = PlayerColor(anchor.PlayerID);
            using var pen = new Pen(color, anchor.Matched ? 2f : 1.5f) { DashStyle = anchor.Matched ? DashStyle.Solid : DashStyle.Dash };

            Vector3 x = Vector3.Transform(Vector3.UnitX, anchor.Rotation);
            Vector3 y = Vector3.Transform(Vector3.UnitY, anchor.Rotation);
            Vector3 z = Vector3.Transform(Vector3.UnitZ, anchor.Rotation);
            Vector3 c = anchor.Position;

            switch (anchor.Kind)
            {
                case "Wall":
                    {
                        Vector3 a = x * (anchor.Size.X / 2f), b = y * (anchor.Size.Y / 2f);
                        DrawLoop(g, project, pen, c - a - b, c + a - b, c + a + b, c - a + b);
                        DrawSegment(g, project, pen, c, c + z * 0.3f); // which way the surface faces
                        break;
                    }
                case "Floor":
                case "Ceiling":
                    {
                        // Drawn flat: along the anchor's own horizontal axis (or the other one if that one points up).
                        Vector3 along = new Vector3(x.X, 0f, x.Z);
                        if (along.LengthSquared() < 0.05f)
                            along = new Vector3(y.X, 0f, y.Z);
                        along = along.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(along);
                        Vector3 across = new Vector3(-along.Z, 0f, along.X);
                        Vector3 a = along * (anchor.Size.X / 2f), b = across * (anchor.Size.Y / 2f);
                        DrawLoop(g, project, pen, c - a - b, c + a - b, c + a + b, c - a + b);
                        break;
                    }
                default:
                    {
                        Vector3 a = x * (anchor.Size.X / 2f), b = y * (anchor.Size.Y / 2f), d = z * (anchor.Size.Z / 2f);
                        Vector3[] lower = { c - a - b - d, c + a - b - d, c + a - b + d, c - a - b + d };
                        Vector3[] upper = { c - a + b - d, c + a + b - d, c + a + b + d, c - a + b + d };
                        DrawLoop(g, project, pen, lower);
                        DrawLoop(g, project, pen, upper);
                        for (int i = 0; i < 4; i++)
                            DrawSegment(g, project, pen, lower[i], upper[i]);
                        break;
                    }
            }

            if (_showLabels && anchor.Kind != "Floor" && anchor.Kind != "Ceiling" && project(c, out float lx, out float ly, out _))
            {
                using var brush = new SolidBrush(color);
                g.DrawString(ShortLabel(anchor.Label), font, brush, lx - 12, ly - 6);
            }
        }

        private static string ShortLabel(string label) => label switch
        {
            "WALL_FACE" => "wall",
            "INVISIBLE_WALL_FACE" => "wall (inferred)",
            "DOOR_FRAME" => "door",
            "WINDOW_FRAME" => "window",
            "WALL_ART" => "art",
            _ => label.ToLowerInvariant(),
        };

        private static void DrawLoop(Graphics g, ProjectFunc project, Pen pen, params Vector3[] corners)
        {
            for (int i = 0; i < corners.Length; i++)
                DrawSegment(g, project, pen, corners[i], corners[(i + 1) % corners.Length]);
        }

        private static void DrawSegment(Graphics g, ProjectFunc project, Pen pen, Vector3 a, Vector3 b)
        {
            if (project(a, out float ax, out float ay, out _) && project(b, out float bx, out float by, out _))
                g.DrawLine(pen, ax, ay, bx, by);
        }

        private void DrawOverlay(Graphics g)
        {
            if (_data == null)
                return;

            int matched = _data.Anchors.Count(a => a.Matched);
            string title = $"{_data.Title} - {_data.Anchors.Length} anchors" + (_data.IsGlobal ? $" ({matched} matched)" : "") + $", {_data.Players.Length} player(s)";
            string players = string.Join("   ", _data.PlayerIDs.Select(id => $"player {id}"));
            string help = "drag: orbit   right-drag: pan   wheel: zoom   double-click: fit   L: labels " + (_showLabels ? "on" : "off") +
                          "   F: floor/ceiling " + (_showHorizontal ? "on" : "off") + "   solid = matched with another player, dashed = only this player   grid = 1 m";
            using var font = new Font(FontFamily.GenericSansSerif, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString(title, font, Brushes.White, 8, 6);
            g.DrawString(_data.Note, font, Brushes.Orange, 8, 6 + font.Height + 2);

            float legendX = 8;
            int legendY = 6 + (font.Height + 2) * 2;
            foreach (int id in _data.PlayerIDs)
            {
                using var brush = new SolidBrush(PlayerColor(id));
                string text = $"player {id}";
                g.DrawString(text, font, brush, legendX, legendY);
                legendX += g.MeasureString(text, font).Width + 14;
            }
            g.DrawString(help, font, Brushes.Gray, 8, Height - font.Height - 6);
        }

        #endregion Drawing
    }
}
