using Server.Archived.Manager.SpatialSync;
using System.Numerics;

namespace Server.Archived.Utilities
{
    /// <summary>
    /// A 3D view of the map points and key frame cameras of one sub-map that can be moved around with the mouse. The view (angle,
    /// position, zoom) belongs to the control and is not touched when the data is replaced, so the points can be updated while the
    /// user looks around. Left mouse button: orbit. Right or middle button: pan. Wheel: zoom. Double click: fit the view to the points.
    /// C: switch between colouring the points by player and by height.
    /// </summary>
    internal sealed class PointCloudView : Control
    {
        private static readonly Color[] PlayerColors =
        {
            Color.FromArgb(255, 99, 99), Color.FromArgb(99, 190, 255), Color.FromArgb(120, 230, 120), Color.FromArgb(255, 210, 90),
            Color.FromArgb(220, 130, 255), Color.FromArgb(90, 230, 220), Color.FromArgb(255, 160, 90), Color.FromArgb(200, 200, 200),
        };

        private MapSnapshotSubMap? _data;
        private float _yaw = 0.6f;
        private float _pitch = 0.45f;
        private float _distance = 5f;
        private Vector3 _target;
        private bool _fitted;
        private bool _colorByHeight;
        private bool _showSparse = true;
        private bool _showDense = false;
        private bool _showBoxes = true;
        private bool _showAnchors = true;
        private bool _shadeByBrightness;
        private Point _lastMouse;

        public PointCloudView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            DoubleBuffered = true;
            TabStop = true;
            BackColor = Color.FromArgb(20, 20, 24);
        }

        /// <summary>Replace the points and key frames shown. The first data with points also fits the view to them; later data keeps the view the user chose.</summary>
        public void SetData(MapSnapshotSubMap data)
        {
            _data = data;
            if (!_fitted && FitPoints().Length > 0)
                FitToData();
            Invalidate();
        }

        /// <summary>The points the view is fitted to: the sparse map points, or the dense ones if there are none.</summary>
        private MapSnapshotPoint[] FitPoints()
        {
            if (_data == null)
                return Array.Empty<MapSnapshotPoint>();
            return _data.Points.Length > 0 ? _data.Points : _data.DensePoints ?? Array.Empty<MapSnapshotPoint>();
        }

        private void FitToData()
        {
            MapSnapshotPoint[] fitPoints = FitPoints();
            if (fitPoints.Length == 0)
                return;

            Vector3 sum = Vector3.Zero;
            foreach (MapSnapshotPoint p in fitPoints)
                sum += p.Position;
            _target = sum / fitPoints.Length;

            float[] distances = fitPoints.Select(p => Vector3.Distance(p.Position, _target)).OrderBy(d => d).ToArray();
            float radius = distances[(int)(0.9f * (distances.Length - 1))];
            _distance = Math.Max(1.5f, radius * 2.5f);
            _fitted = true;
        }

        #region Camera

        private void GetBasis(out Vector3 eye, out Vector3 forward, out Vector3 right, out Vector3 up)
        {
            float cp = MathF.Cos(_pitch);
            eye = _target + _distance * new Vector3(cp * MathF.Sin(_yaw), MathF.Sin(_pitch), cp * MathF.Cos(_yaw));
            forward = Vector3.Normalize(_target - eye);
            // Unity's axes are left-handed (looking along +Z, +X is to the right), so right is up x forward, not forward x up; the other way round
            // the whole view is a mirror image of the room.
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
                _yaw += dx * 0.01f; // (the view is not mirrored any more, so this keeps the drag turning the scene the same way)
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
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, e.Delta / 120f), 0.2f, 500f);
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
            if (e.KeyCode == Keys.C)
            {
                _colorByHeight = !_colorByHeight;
                Invalidate();
            }
            else if (e.KeyCode == Keys.D)
            {
                _showDense = !_showDense;
                Invalidate();
            }
            else if (e.KeyCode == Keys.S)
            {
                _showSparse = !_showSparse;
                Invalidate();
            }
            else if (e.KeyCode == Keys.G)
            {
                // Shade the points by their brightness, keeping the colour of the player (or height).
                _shadeByBrightness = !_shadeByBrightness;
                Invalidate();
            }
            else if (e.KeyCode == Keys.A)
            {
                _showAnchors = !_showAnchors;
                Invalidate();
            }
            else if (e.KeyCode == Keys.B)
            {
                _showBoxes = !_showBoxes;
                Invalidate();
            }
        }

        #endregion Mouse and keyboard

        #region Drawing

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

            float minY = float.MaxValue, maxY = float.MinValue;
            MapSnapshotPoint[] fitPoints = FitPoints();
            foreach (MapSnapshotPoint p in fitPoints)
            {
                minY = Math.Min(minY, p.Position.Y);
                maxY = Math.Max(maxY, p.Position.Y);
            }
            if (fitPoints.Length == 0)
            {
                minY = maxY = 0f;
            }

            DrawGrid(g, Project, minY);

            // Points: shaded by distance from the viewer so near points stand out. The dense points are drawn first and small, the sparse
            // map points on top of them and larger.
            using var brush = new SolidBrush(Color.White);

            void DrawPoints(MapSnapshotPoint[] points, float minSize, float maxSize, float sizeScale)
            {
                foreach (MapSnapshotPoint p in points)
                {
                    if (!Project(p.Position, out float sx, out float sy, out float depth))
                        continue;
                    if (sx < -10 || sy < -10 || sx > Width + 10 || sy > Height + 10)
                        continue;

                    Color baseColor = _colorByHeight
                        ? HueColor(maxY > minY ? (p.Position.Y - minY) / (maxY - minY) : 0.5f)
                        : PlayerColors[Math.Abs(p.PlayerID) % PlayerColors.Length];
                    // Shaded by distance, or (G) by the point's brightness, so the colour of the player (or height) and the brightness show together.
                    float shade = _shadeByBrightness && p.Gray >= 0f
                        ? 0.12f + 0.88f * Math.Clamp(p.Gray / 255f, 0f, 1f)
                        : Math.Clamp(1.25f - depth / (_distance * 2.5f), 0.35f, 1f);
                    brush.Color = Color.FromArgb((int)(baseColor.R * shade), (int)(baseColor.G * shade), (int)(baseColor.B * shade));
                    float size = Math.Clamp(focal * sizeScale / depth, minSize, maxSize);
                    g.FillRectangle(brush, sx - size / 2f, sy - size / 2f, size, size);
                }
            }

            if (_showDense && _data.DensePoints != null)
                DrawPoints(_data.DensePoints, 1.5f, 3f, 0.006f);
            if (_showSparse)
                DrawPoints(_data.Points, 2f, 6f, 0.012f);

            // The shared anchors: the voxels each one holds in its own colour (the part its uniqueness was judged over bright, the rest dim), a ring on
            // the floor at the radius it covers (in the line style of the player who saw most of it) and its number. The other map's
            // anchors from its last comparison with this one are dashed: green where an anchor was found and it agrees with the best placement the
            // anchors agree on, red where it was found but disagrees, magenta where the best whole-map alignment puts them.
            if (_showAnchors && _data.Anchors != null)
                foreach (MapSnapshotAnchor anchor in _data.Anchors)
                    DrawAnchor(g, Project, focal, anchor, AnchorColors[Math.Abs(anchor.ID) % AnchorColors.Length], PlayerDashStyle(anchor.PlayerID));
            if (_showBoxes && _data.OverlayAnchors != null)
                foreach (MapSnapshotAnchor anchor in _data.OverlayAnchors)
                    DrawAnchor(g, Project, focal, anchor, anchor.Style == 2 ? Color.FromArgb(80, 255, 120) : anchor.Style == 3 ? Color.FromArgb(255, 70, 70) : Color.FromArgb(255, 60, 220), System.Drawing.Drawing2D.DashStyle.Dash);

            // Camera pose indicator: a dot and a short line where the camera looks, only for the most recent key frame of each player.
            foreach (MapSnapshotKeyFrame keyFrame in _data.KeyFrames
                .GroupBy(k => k.PlayerID)
                .Select(group => group.OrderByDescending(k => k.Sequence).ThenByDescending(k => k.ID).First()))
            {
                if (!Project(keyFrame.Position, out float sx, out float sy, out _))
                    continue;

                Color color = PlayerColors[Math.Abs(keyFrame.PlayerID) % PlayerColors.Length];
                using var pen = new Pen(color, 1.5f);
                if (Project(keyFrame.Position + keyFrame.Forward * 0.25f, out float fx, out float fy, out _))
                    g.DrawLine(pen, sx, sy, fx, fy);
                g.DrawEllipse(pen, sx - 3, sy - 3, 6, 6);
            }

            DrawOverlay(g);
        }

        /// <summary>A 1 m grid under the points (at the height of the lowest of them), to judge distances.</summary>
        private void DrawGrid(Graphics g, ProjectFunc project, float floorY)
        {
            if (_data == null || FitPoints().Length == 0)
                return;

            float radius = Math.Clamp(_distance, 2f, 20f);
            int cellsAround = (int)MathF.Ceiling(radius);
            float centerX = MathF.Round(_target.X), centerZ = MathF.Round(_target.Z);
            using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            for (int i = -cellsAround; i <= cellsAround; i++)
            {
                DrawSegment(g, project, pen, new Vector3(centerX + i, floorY, centerZ - cellsAround), new Vector3(centerX + i, floorY, centerZ + cellsAround));
                DrawSegment(g, project, pen, new Vector3(centerX - cellsAround, floorY, centerZ + i), new Vector3(centerX + cellsAround, floorY, centerZ + i));
            }
        }

        private static readonly System.Drawing.Drawing2D.DashStyle[] PlayerDashStyles =
        {
            System.Drawing.Drawing2D.DashStyle.Solid, System.Drawing.Drawing2D.DashStyle.Dash, System.Drawing.Drawing2D.DashStyle.Dot, System.Drawing.Drawing2D.DashStyle.DashDot,
        };

        private static readonly string[] PlayerDashNames = { "solid", "dashed", "dotted", "dash-dot" };

        /// <summary>The line style of a player's anchors: by the player's place in this map's list of players.</summary>
        private System.Drawing.Drawing2D.DashStyle PlayerDashStyle(int playerID)
        {
            int index = _data != null ? Array.IndexOf(_data.PlayerIDs, playerID) : 0;
            return PlayerDashStyles[Math.Max(0, index) % PlayerDashStyles.Length];
        }

        private static readonly Color[] AnchorColors =
        {
            Color.FromArgb(255, 235, 60), Color.FromArgb(60, 255, 200), Color.FromArgb(255, 140, 40), Color.FromArgb(120, 255, 80),
            Color.FromArgb(80, 200, 255), Color.FromArgb(255, 100, 140), Color.FromArgb(200, 140, 255), Color.FromArgb(255, 255, 255),
        };

        private void DrawAnchor(Graphics g, ProjectFunc project, float focal, MapSnapshotAnchor anchor, Color color, System.Drawing.Drawing2D.DashStyle dash)
        {
            void Points(Vector3[] points, float shade)
            {
                using var brush = new SolidBrush(Color.FromArgb((int)(color.R * shade), (int)(color.G * shade), (int)(color.B * shade)));
                foreach (Vector3 position in points)
                {
                    if (!project(position, out float sx, out float sy, out float depth))
                        continue;
                    if (sx < -10 || sy < -10 || sx > Width + 10 || sy > Height + 10)
                        continue;
                    float size = Math.Clamp(focal * 0.012f / depth, 2f, 6f);
                    g.FillRectangle(brush, sx - size / 2f, sy - size / 2f, size, size);
                }
            }

            Points(anchor.Context, 0.45f);
            Points(anchor.Core, 1f);

            using var pen = new Pen(color, 2f) { DashStyle = dash };
            const int segments = 48;
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.PI * 2f / segments, a1 = (i + 1) * MathF.PI * 2f / segments;
                DrawSegment(g, project, pen, anchor.Position + new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0)) * anchor.Radius, anchor.Position + new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1)) * anchor.Radius);
            }
            if (project(anchor.Position + new Vector3(0f, 0.05f, 0f), out float lx, out float ly, out _))
            {
                using var font = new Font(FontFamily.GenericSansSerif, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(color);
                g.DrawString("#" + anchor.ID, font, textBrush, lx + 3, ly - 14);
                g.FillEllipse(textBrush, lx - 3, ly - 3, 6, 6);
            }
        }

        private delegate bool ProjectFunc(Vector3 p, out float sx, out float sy, out float depth);

        private static void DrawSegment(Graphics g, ProjectFunc project, Pen pen, Vector3 a, Vector3 b)
        {
            if (project(a, out float ax, out float ay, out _) && project(b, out float bx, out float by, out _))
                g.DrawLine(pen, ax, ay, bx, by);
        }

        private void DrawOverlay(Graphics g)
        {
            if (_data == null)
                return;

            string players = string.Join(", ", _data.PlayerIDs);
            string title = $"Map {_data.ID}" + (players.Length > 0 ? $" (player{(_data.PlayerIDs.Length > 1 ? "s" : "")} {players})" : " (no player)") +
                           $" - {_data.Points.Length} map points, {_data.DensePoints?.Length ?? 0} dense points, {_data.KeyFrames.Length} key frames";
            int anchors = _data.Anchors?.Length ?? 0;
            string legend = $"coloured points + ring = {anchors} anchors, bright = judged area (A: " + (_showAnchors ? "on" : "off") + ")   dashed: green = anchor found, agrees; red = found, disagrees; magenta = whole-map result: " + (_data.OverlayNote ?? "no comparison with another map yet") + "   B: comparison " + (_showBoxes ? "on" : "off");
            string lines = "anchor lines: " + string.Join(", ", _data.PlayerIDs.Select((id, i) => $"player {id} {PlayerDashNames[i % PlayerDashNames.Length]}"));
            string help = "drag: orbit   right-drag: pan   wheel: zoom   double-click: fit   C: colour by " + (_colorByHeight ? "player" : "height") + "   D: dense " + (_showDense ? "on" : "off") + "   G: brightness " + (_shadeByBrightness ? "on" : "off") + "   S: sparse " + (_showSparse ? "on" : "off") + "   grid = 1 m";
            using var font = new Font(FontFamily.GenericSansSerif, 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString(title, font, Brushes.White, 8, 6);
            g.DrawString(legend, font, Brushes.Orange, 8, 6 + font.Height + 2);
            if (_data.PlayerIDs.Length > 1)
                g.DrawString(lines, font, Brushes.Orange, 8, 6 + (font.Height + 2) * 2);
            g.DrawString(help, font, Brushes.Gray, 8, Height - font.Height - 6);
        }

        /// <summary>Hue from red (0) through green to blue (1).</summary>
        private static Color HueColor(float t)
        {
            float h = Math.Clamp(t, 0f, 1f) * 4f;
            int sector = Math.Min(3, (int)h);
            float f = h - sector;
            (float r, float gr, float b) = sector switch
            {
                0 => (1f, f, 0f),
                1 => (1f - f, 1f, 0f),
                2 => (0f, 1f, f),
                _ => (0f, 1f - f, 1f),
            };
            return Color.FromArgb((int)(r * 255), (int)(gr * 255), (int)(b * 255));
        }

        #endregion Drawing
    }
}
