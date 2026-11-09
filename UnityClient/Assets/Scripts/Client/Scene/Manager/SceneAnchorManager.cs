using Assets.Scripts.Utilities;
using Meta.XR.MRUtilityKit;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Client.Scene.Manager
{
    /// <summary>
    /// Collects this headset's scene anchors (the walls, floor, ceiling, doors, windows and furniture of its Space Setup room scan) for the spatial
    /// synchronization. They are read through the Meta XR MR Utility Kit (<see cref="MRUK"/>), which loads the room from the device. Every anchor goes to
    /// the server as a label, a size and a pose in this headset's world space (<see cref="BuildKeyFrame"/>); the server matches the anchors of the headsets
    /// with each other by label and size and works out where each headset is in relation to the others. Nothing here is shared between headsets.
    ///
    /// Needs the room set up on the headset (Settings > Physical Space > Space Setup, in the room all the players are in), scene permission granted
    /// (OVRManager: Scene Support "Required" or "Supported", which adds the USE_SCENE permission), and the Meta XR MR Utility Kit package. An MRUK
    /// component is added at runtime if the scene does not have one.
    /// </summary>
    public class SceneAnchorManager : MonoBehaviour
    {
        [Tooltip("Scan the room (Space Setup) only when it is needed: when the headset has no saved room, or you are not inside any saved room. " +
                 "Otherwise the room saved on the headset is reused, so the scan is done once per room. " +
                 "Only works in a build running on the headset: scene capture does not work over Quest Link.")]
        public bool ScanIfNoRoomHere = true;

        [Tooltip("Draw the scene anchors in the headset (an outline and a name on each wall, door, window, floor, ceiling and piece of furniture) " +
                 "so you can see what was loaded and whether it matches the room.")]
        public bool ShowAnchors = true;

        public static SceneAnchorManager Instance { get; private set; }

        /// <summary>How many scene anchors the headset's current room has (0 until the room is loaded).</summary>
        public int AnchorCount { get; private set; }

        private OVRCameraRig _rig;
        private readonly List<GameObject> _visuals = new List<GameObject>();
        private readonly List<Transform> _labels = new List<Transform>();
        private MRUKRoom _visualRoom;
        private int _visualAnchorCount = -1;
        private Material _lineMaterial;
        private Font _labelFont;
        private float _nextLogTime;

        private void Awake()
        {
            Instance = this;

            // MRUK loads the room scan from the device when it starts.
            if (FindFirstObjectByType<MRUK>() == null)
            {
                // MRUK's SceneSettings is a plain field that the editor fills in; on a component added from code it is null, and MRUK throws in
                // Awake/Update. So the component is made on an inactive object, given default settings (not loading the room itself: Start does, once),
                // and only then activated, which runs its Awake with the settings in place.
                var host = new GameObject("MRUK");
                host.SetActive(false);
                var mruk = host.AddComponent<MRUK>();
                mruk.SceneSettings = new MRUK.MRUKSettings
                {
                    DataSource = MRUK.SceneDataSource.Device,
                    LoadSceneOnStartup = false, // loaded once by LoadRoomAsync (Start), so two loads never run at once
                    RoomPrefabs = new GameObject[0],
                    SceneJsons = new TextAsset[0],
                };
                host.SetActive(true);
                Debug.Log($"[{nameof(SceneAnchorManager)}] No MRUK in the scene: added one to load the room from the device.");
            }
        }

        /// <summary>
        /// Load the room that is saved on the headset (Space Setup keeps it there between runs, and anchors it to the headset's own tracking, so it
        /// lines up with the real room every run). Only if there is none, or the headset is not inside any saved room (a different room), is a new
        /// scan asked for. (MRUK's own load in its Awake can be too early over Quest Link, so the MRUK this class creates does not load on its own.)
        /// If the scene has its own MRUK that loads on start, that one is left to it.
        /// </summary>
        private async void Start()
        {
            while (MRUK.Instance == null)
                await Task.Yield();

            if (MRUK.Instance.SceneSettings != null && MRUK.Instance.SceneSettings.LoadSceneOnStartup)
                return; // the scene's MRUK loads the room itself

            MRUK.LoadDeviceResult result = await LoadRoomAsync();
            if (!ScanIfNoRoomHere || result == MRUK.LoadDeviceResult.NoScenePermission || result == MRUK.LoadDeviceResult.DiscoveryOngoing)
                return;

            string reason = null;
            if (result != MRUK.LoadDeviceResult.Success)
                reason = "the headset has no saved room";
            else
            {
                await Task.Delay(1500); // let the headset's tracking settle before asking where it is
                if (!HeadIsInASavedRoom())
                    reason = "you are not inside any room saved on the headset (a different room?)";
            }
            if (reason == null)
                return; // the saved room is the room you are in: nothing to scan

#if !UNITY_EDITOR && UNITY_ANDROID
            // Space Setup pauses the app and prompts the user to scan the room; the app resumes when they finish or cancel, and the new scan is
            // what the load below reads.
            Debug.Log($"[{nameof(SceneAnchorManager)}] {reason}: starting Space Setup, scan the room.");
            if (!await OVRScene.RequestSpaceSetup())
            {
                Debug.LogWarning($"[{nameof(SceneAnchorManager)}] Space Setup could not be started.");
                return;
            }
            await LoadRoomAsync();
#else
            Debug.LogWarning($"[{nameof(SceneAnchorManager)}] {reason}, and Space Setup cannot be started over Quest Link or in the editor. " +
                             "Scan the room on the headset (Settings > Physical Space > Space Setup) or run a build on it.");
#endif
        }

        /// <summary>
        /// Whether the headset is inside the floor outline of any loaded room. (MRUK's GetCurrentRoom always answers with some room, the last one or
        /// the first, so it cannot tell that you are somewhere else.)
        /// </summary>
        private bool HeadIsInASavedRoom()
        {
            Transform head = Head();
            if (head == null || MRUK.Instance == null || MRUK.Instance.Rooms == null)
                return true; // cannot tell: keep what was loaded
            foreach (MRUKRoom room in MRUK.Instance.Rooms)
            {
                if (room != null && room.IsPositionInRoom(head.position))
                    return true;
            }
            return false;
        }

        private async Task<MRUK.LoadDeviceResult> LoadRoomAsync()
        {
            try
            {
                MRUK.LoadDeviceResult result = await MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound: false);
                if (result == MRUK.LoadDeviceResult.Success)
                    Debug.Log($"[{nameof(SceneAnchorManager)}] Room loaded from the device ({MRUK.Instance.Rooms.Count} room(s)).");
                else
                    Debug.LogWarning($"[{nameof(SceneAnchorManager)}] Loading the room from the device failed: {result}. " + result switch
                    {
                        MRUK.LoadDeviceResult.NoScenePermission => "The scene permission (USE_SCENE) was not granted: accept the prompt on the headset, or enable it in the app's permissions.",
                        MRUK.LoadDeviceResult.NoRoomsFound => "The headset has no saved room.",
                        MRUK.LoadDeviceResult.NotInitialized => "Not initialized: in the editor this needs Meta Quest Link running with spatial data over Link enabled.",
                        MRUK.LoadDeviceResult.DiscoveryOngoing => "Another room load is still running.",
                        _ => "",
                    });
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{nameof(SceneAnchorManager)}] Loading the room from the device threw: {ex.Message}");
                return MRUK.LoadDeviceResult.Failure;
            }
        }

        /// <summary>
        /// The headset's pose and its scene anchors, all in world space (the space the headset pose is in), for the server. Sent even with no anchors
        /// (the room is still loading), so the server knows the headset is alive.
        /// </summary>
        public KeyFrameAnchorDTO BuildKeyFrame(long id)
        {
            Transform head = Head();
            if (head == null)
                return null;

            var keyFrame = new KeyFrameAnchorDTO
            {
                ID = id,
                Timestamp = DateTime.UtcNow,
                CameraLocalPose = Conversions.ToSharedPose(head.position, head.rotation),
            };

            MRUKRoom room = CurrentRoom();
            if (room != null)
            {
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor != null && TryDescribe(anchor, out SceneAnchorDTO dto))
                        keyFrame.Anchors.Add(dto);
                }
            }

            AnchorCount = keyFrame.Anchors.Count;
            if (Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 10f;
                Debug.Log(room == null
                    ? $"[{nameof(SceneAnchorManager)}] No room loaded yet (is Space Setup done and scene permission granted?)."
                    : $"[{nameof(SceneAnchorManager)}] Sending {AnchorCount} scene anchors.");
            }

            return keyFrame;
        }

        private static MRUKRoom CurrentRoom()
        {
            MRUK mruk = MRUK.Instance;
            if (mruk == null)
                return null;
            MRUKRoom room = mruk.GetCurrentRoom();
            if (room == null && mruk.Rooms != null && mruk.Rooms.Count > 0)
                room = mruk.Rooms[0];
            return room;
        }

        /// <summary>
        /// One scene anchor as a label, the pose of the middle of its bounds and the size of its bounds. Flat anchors (walls, doors, windows, floor,
        /// ceiling) use their plane; the rest (furniture) use their volume, falling back to the plane.
        /// </summary>
        private static bool TryDescribe(MRUKAnchor anchor, out SceneAnchorDTO dto)
        {
            dto = null;
            string label = anchor.Label.ToString();
            if (label == "GLOBAL_MESH")
                return false;

            Transform t = anchor.transform;
            if (!TryLocalBounds(anchor, label, out Vector3 localCentre, out Vector3 size, out _))
                return false;
            Vector3 centre = t.TransformPoint(localCentre);

            string uuid = anchor.Anchor.Uuid != Guid.Empty ? anchor.Anchor.Uuid.ToString("D") : anchor.GetInstanceID().ToString();
            dto = new SceneAnchorDTO(uuid, label, Conversions.ToSharedPose(centre, t.rotation),
                new SharedLibrary.Game.Vector3(size.x, size.y, size.z));
            return true;
        }

        /// <summary>
        /// The middle and the size of an anchor's bounds in the anchor's own space, and whether it is flat. Flat anchors (walls, doors, windows, floor,
        /// ceiling) use their plane; the rest (furniture) use their volume, falling back to the plane. What is sent to the server and what is drawn.
        /// </summary>
        private static bool TryLocalBounds(MRUKAnchor anchor, string label, out Vector3 localCentre, out Vector3 size, out bool isPlane)
        {
            bool flat = label == "WALL_FACE" || label == "INVISIBLE_WALL_FACE" || label == "DOOR_FRAME" || label == "WINDOW_FRAME" ||
                        label == "WALL_ART" || label == "FLOOR" || label == "CEILING";

            if ((flat || !anchor.VolumeBounds.HasValue) && anchor.PlaneRect.HasValue)
            {
                Rect r = anchor.PlaneRect.Value;
                localCentre = new Vector3(r.center.x, r.center.y, 0f);
                size = new Vector3(r.width, r.height, 0f);
                isPlane = true;
                return true;
            }
            if (anchor.VolumeBounds.HasValue)
            {
                Bounds b = anchor.VolumeBounds.Value;
                localCentre = b.center;
                size = b.size;
                isPlane = false;
                return true;
            }
            localCentre = Vector3.zero;
            size = Vector3.zero;
            isPlane = false;
            return false;
        }

        #region Visualization

        private void Update()
        {
            MRUKRoom room = ShowAnchors ? CurrentRoom() : null;
            int count = room != null ? room.Anchors.Count : -1;
            if (room == _visualRoom && count == _visualAnchorCount)
                return;

            ClearVisuals();
            _visualRoom = room;
            _visualAnchorCount = count;
            if (room == null)
                return;

            foreach (MRUKAnchor anchor in room.Anchors)
            {
                try
                {
                    if (anchor != null)
                        DrawAnchor(anchor);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{nameof(SceneAnchorManager)}] Could not draw an anchor: {ex.Message}");
                }
            }
            Debug.Log($"[{nameof(SceneAnchorManager)}] Drawing {_visuals.Count} scene anchors in the headset.");
        }

        /// <summary>The names face the headset.</summary>
        private void LateUpdate()
        {
            Transform head = Head();
            if (head == null)
                return;
            foreach (Transform label in _labels)
            {
                if (label != null)
                    label.rotation = Quaternion.LookRotation(label.position - head.position);
            }
        }

        private void ClearVisuals()
        {
            foreach (GameObject visual in _visuals)
            {
                if (visual != null)
                    Destroy(visual);
            }
            _visuals.Clear();
            _labels.Clear();
        }

        /// <summary>An outline of the anchor's bounds (a rectangle and a tick along its normal for flat anchors, a box for furniture) and its name, as children of the anchor.</summary>
        private void DrawAnchor(MRUKAnchor anchor)
        {
            string label = anchor.Label.ToString();
            if (label == "GLOBAL_MESH" || !TryLocalBounds(anchor, label, out Vector3 c, out Vector3 size, out bool isPlane))
                return;

            Color color = LabelColor(label);
            Vector3 h = size * 0.5f;
            if (isPlane)
            {
                AddLine(anchor.transform, true, color, new Vector3(c.x - h.x, c.y - h.y, c.z), new Vector3(c.x + h.x, c.y - h.y, c.z),
                    new Vector3(c.x + h.x, c.y + h.y, c.z), new Vector3(c.x - h.x, c.y + h.y, c.z));
                AddLine(anchor.transform, false, color, c, c + new Vector3(0f, 0f, 0.3f)); // which way the surface faces
            }
            else
            {
                Vector3[] lower = { c + new Vector3(-h.x, -h.y, -h.z), c + new Vector3(h.x, -h.y, -h.z), c + new Vector3(h.x, -h.y, h.z), c + new Vector3(-h.x, -h.y, h.z) };
                Vector3[] upper = { c + new Vector3(-h.x, h.y, -h.z), c + new Vector3(h.x, h.y, -h.z), c + new Vector3(h.x, h.y, h.z), c + new Vector3(-h.x, h.y, h.z) };
                AddLine(anchor.transform, true, color, lower);
                AddLine(anchor.transform, true, color, upper);
                for (int i = 0; i < 4; i++)
                    AddLine(anchor.transform, false, color, lower[i], upper[i]);
            }
            AddLabel(anchor.transform, c, label.ToLowerInvariant().Replace('_', ' '), color);
        }

        private void AddLine(Transform parent, bool loop, Color color, params Vector3[] points)
        {
            var go = new GameObject("SceneAnchorOutline");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = loop;
            line.positionCount = points.Length;
            line.SetPositions(points);
            line.startWidth = line.endWidth = 0.012f;
            line.startColor = line.endColor = color;
            line.sharedMaterial = LineMaterial();
            _visuals.Add(go);
        }

        private void AddLabel(Transform parent, Vector3 localPosition, string text, Color color)
        {
            Font font = LabelFont();
            if (font == null)
                return; // no built-in font in this Unity version: the outlines still show

            var go = new GameObject("SceneAnchorLabel");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = font;
            mesh.fontSize = 64;
            mesh.characterSize = 0.012f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.color = color;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            _visuals.Add(go);
            _labels.Add(go.transform);
        }

        private Material LineMaterial()
        {
            if (_lineMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _lineMaterial = new Material(shader);
            }
            return _lineMaterial;
        }

        private Font LabelFont()
        {
            if (_labelFont == null)
            {
                try { _labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch (Exception) { _labelFont = null; }
                if (_labelFont == null)
                {
                    try { _labelFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                    catch (Exception) { _labelFont = null; }
                }
            }
            return _labelFont;
        }

        private static Color LabelColor(string label)
        {
            switch (label)
            {
                case "WALL_FACE":
                case "INVISIBLE_WALL_FACE": return new Color(0.3f, 0.6f, 1f);
                case "FLOOR": return new Color(0.3f, 1f, 0.4f);
                case "CEILING": return new Color(0.3f, 1f, 1f);
                case "DOOR_FRAME": return new Color(1f, 0.9f, 0.2f);
                case "WINDOW_FRAME": return new Color(1f, 0.3f, 0.9f);
                default: return new Color(1f, 0.55f, 0.15f);
            }
        }

        private void OnDestroy()
        {
            ClearVisuals();
        }

        #endregion Visualization

        private Transform Head()
        {
            if (_rig == null)
                _rig = FindFirstObjectByType<OVRCameraRig>();
            if (_rig != null && _rig.centerEyeAnchor != null)
                return _rig.centerEyeAnchor;
            return Camera.main != null ? Camera.main.transform : null;
        }
    }
}
