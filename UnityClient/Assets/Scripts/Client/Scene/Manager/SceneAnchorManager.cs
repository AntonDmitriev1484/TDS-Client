using Client.Utilities;
using Assets.Scripts.Utilities;
using Meta.XR.MRUtilityKit;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

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

        [Tooltip("MRUK's world locking: when on, MRUK moves the OVRCameraRig's TrackingSpace every frame so the room anchors stay lined up with the real room " +
                 "as the headset refines its map. That shifts everything virtual (and the poses sent for the spatial sync) whenever the correction changes. " +
                 "Off by default, so tracking is left as the headset reports it. Applied to the MRUK in the scene too, whoever created it.")]
        public bool EnableWorldLock = false;

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
        private float _nextVisualCheck;

        // The room's anchors as they are described to the server, worked out once per room (see BuildKeyFrame).
        private readonly List<CachedAnchor> _cachedAnchors = new List<CachedAnchor>();
        private MRUKRoom _cachedRoom;
        private int _cachedRoomAnchorCount = -1;
        private bool _cacheStale;

        /// <summary>
        /// What never changes about one anchor once the room is loaded: which anchor it is, its label, its id, and the middle and size of its
        /// bounds in its own space. Only its pose in the world is read again for each key frame.
        /// </summary>
        private sealed class CachedAnchor
        {
            public MRUKAnchor Anchor;
            public string Uuid;
            public string Label;
            public Vector3 LocalCentre;
            public SharedLibrary.Game.Vector3 Size;
        }

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
                mruk.EnableWorldLock = EnableWorldLock;
                mruk.SceneSettings = new MRUK.MRUKSettings
                {
                    DataSource = MRUK.SceneDataSource.Device,
                    LoadSceneOnStartup = false, // loaded once by LoadRoomAsync (Start), so two loads never run at once
                    RoomPrefabs = new GameObject[0],
                    SceneJsons = new TextAsset[0],
                };
                host.SetActive(true);
                DebugLog.Log($"[{nameof(SceneAnchorManager)}] No MRUK in the scene: added one to load the room from the device.");
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

            // Also for an MRUK that the scene has (its own setting is overridden here, so there is one switch for it).
            MRUK.Instance.EnableWorldLock = EnableWorldLock;
            DebugLog.Log($"[{nameof(SceneAnchorManager)}] MRUK world lock is {(EnableWorldLock ? "on" : "off")}.");

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
            DebugLog.Log($"[{nameof(SceneAnchorManager)}] {reason}: starting Space Setup, scan the room.");
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
                    DebugLog.Log($"[{nameof(SceneAnchorManager)}] Room loaded from the device ({MRUK.Instance.Rooms.Count} room(s)).");
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
                RefreshAnchorCache(room);

                // Only the poses are read again (the anchors are tracked by the headset, so their world poses can still shift): that is a
                // transform each, with no bounds, labels or ids to work out.
                foreach (CachedAnchor cached in _cachedAnchors)
                {
                    if (cached.Anchor == null)
                    {
                        _cacheStale = true; // the anchor was destroyed: describe the room again next time
                        continue;
                    }

                    Transform t = cached.Anchor.transform;
                    keyFrame.Anchors.Add(new SceneAnchorDTO(cached.Uuid, cached.Label,
                        Conversions.ToSharedPose(t.TransformPoint(cached.LocalCentre), t.rotation), cached.Size));
                }
            }

            AnchorCount = keyFrame.Anchors.Count;
            if (Time.unscaledTime >= _nextLogTime)
            {
                _nextLogTime = Time.unscaledTime + 10f;
                DebugLog.Log(room == null
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
        /// Describes the room's anchors once (label, id, and the middle and size of their bounds) and keeps that for every later key frame. It is
        /// done again only if the room is a different one, its number of anchors changes, an anchor was destroyed, or nothing could be described
        /// yet (the room is still loading).
        /// </summary>
        private void RefreshAnchorCache(MRUKRoom room)
        {
            int anchorCount = room.Anchors.Count;
            if (!_cacheStale && room == _cachedRoom && anchorCount == _cachedRoomAnchorCount && _cachedAnchors.Count > 0)
                return;

            _cachedAnchors.Clear();
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor == null)
                    continue;

                string label = anchor.Label.ToString();
                if (label == "GLOBAL_MESH" || !TryLocalBounds(anchor, label, out Vector3 localCentre, out Vector3 size, out _))
                    continue;

                _cachedAnchors.Add(new CachedAnchor
                {
                    Anchor = anchor,
                    Uuid = anchor.Anchor.Uuid != Guid.Empty ? anchor.Anchor.Uuid.ToString("D") : anchor.GetInstanceID().ToString(),
                    Label = label,
                    LocalCentre = localCentre,
                    Size = new SharedLibrary.Game.Vector3(size.x, size.y, size.z),
                });
            }

            _cachedRoom = room;
            _cachedRoomAnchorCount = anchorCount;
            _cacheStale = false;
            DebugLog.Log($"[{nameof(SceneAnchorManager)}] Described {_cachedAnchors.Count} of the room's {anchorCount} scene anchors; the key frames reuse this.");
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
            // Which room is current (and how many anchors it has) hardly ever changes, and MRUK works it out by testing the head against the
            // room outlines, so it is checked twice a second and not every frame.
            if (Time.unscaledTime < _nextVisualCheck)
                return;
            _nextVisualCheck = Time.unscaledTime + 0.5f;

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
            DebugLog.Log($"[{nameof(SceneAnchorManager)}] Drawing {_visuals.Count} scene anchors in the headset.");
        }

        private void OnEnable()
        {
            // The names are turned to face the headset right before a camera culls and renders (not in LateUpdate): the headset pose is
            // updated again after LateUpdate, so turning them earlier faces them at a slightly old head pose and the text swims when you turn.
            Camera.onPreCull += FaceLabelsToCamera;
            RenderPipelineManager.beginCameraRendering += FaceLabelsToCamera;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= FaceLabelsToCamera;
            RenderPipelineManager.beginCameraRendering -= FaceLabelsToCamera;
        }

        private void FaceLabelsToCamera(ScriptableRenderContext context, Camera cam) => FaceLabelsToCamera(cam);

        private void FaceLabelsToCamera(Camera cam)
        {
            if (_labels.Count == 0 || cam == null || cam.cameraType != CameraType.Game)
                return;

            Transform head = Head();
            if (head == null || (cam.transform != head && !cam.CompareTag("MainCamera")))
                return; // only the headset's camera

            Vector3 eye = cam.transform.position;
            foreach (Transform label in _labels)
            {
                if (label == null)
                    continue;
                Vector3 away = label.position - eye;
                if (away.sqrMagnitude > 1e-6f)
                    label.rotation = Quaternion.LookRotation(away);
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

            // Each anchor is one LineRenderer (one draw call): the outline is a single path, retracing an edge where it has to.
            Color color = LabelColor(label);
            Vector3 h = size * 0.5f;
            if (isPlane)
            {
                Vector3 r0 = new Vector3(c.x - h.x, c.y - h.y, c.z), r1 = new Vector3(c.x + h.x, c.y - h.y, c.z);
                Vector3 r2 = new Vector3(c.x + h.x, c.y + h.y, c.z), r3 = new Vector3(c.x - h.x, c.y + h.y, c.z);
                // The tick at the first corner shows which way the surface faces.
                AddLine(anchor.transform, color, r0 + new Vector3(0f, 0f, 0.3f), r0, r1, r2, r3, r0);
            }
            else
            {
                Vector3 l0 = c + new Vector3(-h.x, -h.y, -h.z), l1 = c + new Vector3(h.x, -h.y, -h.z), l2 = c + new Vector3(h.x, -h.y, h.z), l3 = c + new Vector3(-h.x, -h.y, h.z);
                Vector3 u0 = c + new Vector3(-h.x, h.y, -h.z), u1 = c + new Vector3(h.x, h.y, -h.z), u2 = c + new Vector3(h.x, h.y, h.z), u3 = c + new Vector3(-h.x, h.y, h.z);
                AddLine(anchor.transform, color, l0, l1, l2, l3, l0, u0, u1, u2, u3, u0, u3, l3, l2, u2, u1, l1);
            }
            AddLabel(anchor.transform, c, label.ToLowerInvariant().Replace('_', ' '), color);
        }

        private void AddLine(Transform parent, Color color, params Vector3[] points)
        {
            var go = new GameObject("SceneAnchorOutline");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = false;
            line.numCornerVertices = 0;
            line.positionCount = points.Length;
            line.SetPositions(points);
            line.startWidth = line.endWidth = 0.012f;
            line.startColor = line.endColor = color;
            line.sharedMaterial = LineMaterial();
            ConfigureRenderer(line);
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
            var labelRenderer = go.GetComponent<MeshRenderer>();
            labelRenderer.sharedMaterial = font.material;
            ConfigureRenderer(labelRenderer);
            _visuals.Add(go);
            _labels.Add(go.transform);
        }

        /// <summary>The outlines and names are only drawn: no shadows (the shadow pass would draw all of them again), probes or motion vectors.</summary>
        private static void ConfigureRenderer(Renderer renderer)
        {
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
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
