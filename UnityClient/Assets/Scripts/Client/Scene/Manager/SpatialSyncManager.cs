using Assets.Scripts.Utilities;
using Client.Communication.Manager;
using Meta.XR;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Experimental.Rendering;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Client.Scene.Manager
{
    public class SpatialSyncManager : MonoBehaviour
    {

        #region Properties

        public static SpatialSyncManager Instance { get; set; } // Singleton Instance
        public bool IsInitialized { get; set; }


        /// <summary>True once the passthrough camera is delivering images.</summary>
        public bool IsCameraReady => _cameraAccess != null && _cameraAccess.IsPlaying;

        private long _keyFrameID = 0;
        [Tooltip("Key frames per second while the global map is not ready for this device yet, so it is joined as soon as possible.")]
        public float SendRate = 5.0f;
        [Tooltip("Key frames per second once the server has given this device a global pose; it only needs to keep the alignment fresh.")]
        public float SyncedSendRate = 1.0f;
        private bool _isGloballyAligned = false; // True while the latest response from the server carried a global pose
        private float _tickInterval => 1.0f / (_isGloballyAligned ? SyncedSendRate : SendRate);
        private Task<bool> _pendingSend; // Key frame currently being serialized/sent, if any

        private const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
        private const float PermissionTimeoutSeconds = 60f;
        private const float CameraStartTimeoutSeconds = 10f;

        [Header("Passthrough Camera")]
        [Tooltip("Leave empty to find one in the scene or create one automatically.")]
        [SerializeField] private PassthroughCameraAccess _cameraAccess;
        [SerializeField] private Vector2Int _requestedResolution = new Vector2Int(1280, 960);

        [Header("Key Frame Image")]
        [Tooltip("File format the key frame image is compressed to before sending. JPEG is much smaller; PNG is lossless but saves little on camera images.")]
        [SerializeField] private KeyFrameImageFormat _imageFormat = KeyFrameImageFormat.Jpeg;
        [Tooltip("JPEG quality, 1-100. Around 90 keeps ORB features essentially unchanged.")]
        [Range(1, 100)]
        [SerializeField] private int _jpegQuality = 90;

        [Header("Debug")]
        [Tooltip("Write every extracted key frame's compressed image to Application.persistentDataPath, to check image orientation.")]
        [SerializeField] private bool _debugSaveKeyFramePng = false;

        private SyncResponseDTO _latestSyncResponseDTO = null;
        readonly object _syncResponseLock = new object();

        private GameObject _sharedScene;

        #endregion Properties


        #region Initialization

        /// <summary>
        /// Singleton for Spatial Synchronization Manager.
        /// </summary>
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                // Set initial variables on spatial sync settings
                IsInitialized = false;

                // Wait for necessary objects before initializing spatial sync manager
                StartCoroutine(WaitToInitialize());
            }
            else
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Wait on necessary objects to initialize before initializing Spatial Sync Manager.
        /// </summary>
        private IEnumerator WaitToInitialize()
        {
            while (ConnectionManager.Instance == null || !ConnectionManager.Instance.IsInitialized)
            {
                yield return null;
            }

            // Passed wait checks on other needed objects, can now initialize server
            // Use a coroutine to wait for the async operation to complete
            yield return StartCoroutine(InitializeSpatialSyncCoroutine());
        }

        /// <summary>
        /// Initialize the spatial synchronization by getting initial global pose alignment from server.
        /// </summary>
        /// <returns></returns>
        private IEnumerator InitializeSpatialSyncCoroutine()
        {

            _sharedScene = GameObject.Find("SharedScene");
            if (_sharedScene == null)
                Debug.LogError($"{nameof(SpatialSyncManager)}: no GameObject named 'SharedScene' found in the scene.");

            if (ConnectionManager.Instance.InboundMessagesDict.TryGetValue(typeof(SyncResponseDTO), out var srEvent))
                ((UnityEvent<SyncResponseDTO>)srEvent).AddListener(OnSyncResponseDTOReceived);
            if (ConnectionManager.Instance.InboundMessagesDict.TryGetValue(typeof(BeginSpatialSyncDTO), out var bssEvent))
                ((UnityEvent<BeginSpatialSyncDTO>)bssEvent).AddListener(OnBeginSpatialSyncDTOReceived);

            yield return StartCoroutine(InitializeCameraCoroutine());

            IsInitialized = IsCameraReady;
        }

        /// <summary>
        /// Request the headset camera permission, make sure a PassthroughCameraAccess component exists and wait until it delivers images.
        /// </summary>
        private IEnumerator InitializeCameraCoroutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
            {
                Permission.RequestUserPermission(HeadsetCameraPermission);

                float permissionDeadline = Time.realtimeSinceStartup + PermissionTimeoutSeconds;
                while (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission)
                       && Time.realtimeSinceStartup < permissionDeadline)
                {
                    yield return null;
                }
            }

            if (!Permission.HasUserAuthorizedPermission(HeadsetCameraPermission))
            {
                Debug.LogError($"[SpatialSync] {HeadsetCameraPermission} was not granted; key frames cannot be extracted.");
                yield break;
            }
#endif

            if (_cameraAccess == null)
            {
                _cameraAccess = FindFirstObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            }

            if (_cameraAccess == null)
            {
                _cameraAccess = CreateCameraAccess();
            }

            float cameraDeadline = Time.realtimeSinceStartup + CameraStartTimeoutSeconds;
            while (!_cameraAccess.IsPlaying && Time.realtimeSinceStartup < cameraDeadline)
            {
                yield return null;
            }

            if (!_cameraAccess.IsPlaying)
            {
                Debug.LogError($"[{nameof(SpatialSyncManager)}] Passthrough camera did not start. " +
                               "Check OVRManager > 'Enabled Passthrough Camera Access' and that this is a Quest 3/3S.");
            }
        }

        private PassthroughCameraAccess CreateCameraAccess()
        {
            // Configure the component while its GameObject is inactive so the settings are in place before it starts.
            var host = new GameObject($"[{nameof(SpatialSyncManager)}] PassthroughCameraAccess");
            host.SetActive(false);
            host.transform.SetParent(transform, false);

            var access = host.AddComponent<PassthroughCameraAccess>();
            access.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            access.RequestedResolution = _requestedResolution;

            host.SetActive(true);
            return access;
        }

        #endregion Initialization

        #region Update Loop

        private void Update()
        {
            // Handle the latest sync response DTO from the server, if any.
            SyncResponseDTO syncResponseDTO = null;
            lock (_syncResponseLock)
            {
                if (_latestSyncResponseDTO != null)
                {
                    syncResponseDTO = _latestSyncResponseDTO;
                    _latestSyncResponseDTO = null;
                }
            }
            if (syncResponseDTO != null)
            {
                HandleIncomingMessage(syncResponseDTO);
            }
        }

        /// <summary>
        /// Every <see cref="_tickInterval"/> seconds, extract a key frame and send it to the server.
        /// </summary>
        private IEnumerator SendKeyFrameLoop()
        {
            float nextSendTime = Time.realtimeSinceStartup;

            while (true)
            {
                if (Time.realtimeSinceStartup >= nextSendTime)
                {
                    // Schedule from the previous deadline so the rate doesn't drift, but don't try to catch up
                    // with a burst of sends after a long stall (e.g. the app being paused).
                    nextSendTime += _tickInterval;
                    if (nextSendTime < Time.realtimeSinceStartup)
                    {
                        nextSendTime = Time.realtimeSinceStartup + _tickInterval;
                    }

                    // If the rate just went up (the alignment was lost), don't wait out a deadline set at the slow rate.
                    nextSendTime = Mathf.Min(nextSendTime, Time.realtimeSinceStartup + _tickInterval);

                    TrySendKeyFrame();
                }

                yield return null;
            }
        }

        /// <summary>
        /// Extract one key frame and send it, unless the camera/connection isn't ready or the previous one is still in flight.
        /// </summary>
        private void TrySendKeyFrame()
        {
            // A backlog of large messages would only add latency, so skip this tick if the last send hasn't finished.
            if (_pendingSend != null && !_pendingSend.IsCompleted)
            {
                return;
            }

            if (!IsCameraReady || !ConnectionManager.Instance.IsConnected[ConnectionType.SpatialSync])
            {
                return;
            }

            KeyFrameDTO keyFrame = ExtractKeyFrame(); // Must run on the main thread (camera access)
            if (keyFrame == null)
            {
                return;
            }

            // Serialize and send on a thread-pool thread so the JSON/base64 work doesn't stall the render loop.
            long keyFrameID = keyFrame.ID;
            _pendingSend = Task.Run(() => ConnectionManager.Instance.SendDTO(ConnectionType.SpatialSync, keyFrame));
            _pendingSend.ContinueWith(t =>
            {
                if (t.IsFaulted || (t.Status == TaskStatus.RanToCompletion && !t.Result))
                {
                    MainThreadDispatcher.Enqueue(() =>
                        Debug.LogWarning($"[{nameof(SpatialSyncManager)}] Key frame {keyFrameID} failed to send."));
                }
            });
        }

        #endregion Update Loop

        #region Key Frame Methods

        /// <summary>
        /// Capture the current passthrough camera image together with the pose of the camera that took it, and give it
        /// the next key frame ID. Must be called on the main thread. Returns null (and consumes no ID) if the camera is
        /// not ready or a new camera image arrived while capturing, in which case the pose might not match the pixels.
        /// </summary>
        public KeyFrameDTO ExtractKeyFrame()
        {
            if (!IsCameraReady)
            {
                Debug.LogWarning($"[{nameof(SpatialSyncManager)}] Passthrough camera is not ready; no key frame extracted.");
                return null;
            }

            PassthroughCameraAccess camera = _cameraAccess;

            // Read pose and pixels back to back on the main thread, then check the image did not change in between.
            DateTime timestamp = camera.Timestamp;
            Pose cameraPose = camera.GetCameraPose();
            NativeArray<Color32> colors = camera.GetColors(); // owned by the camera: copy it now, never cache or dispose it
            Vector2Int size = camera.CurrentResolution;

            if (!colors.IsCreated || colors.Length != size.x * size.y)
            {
                Debug.LogWarning($"[{nameof(SpatialSyncManager)}] Unexpected camera buffer ({colors.Length} pixels for {size.x}x{size.y}); no key frame extracted.");
                return null;
            }

            byte[] gray = ToGrayscale(colors);

            if (camera.Timestamp != timestamp)
            {
                Debug.LogWarning($"[{nameof(SpatialSyncManager)}] Camera image changed while capturing; no key frame extracted.");
                return null;
            }

            byte[] imageData = EncodeImage(gray, size.x, size.y);
            if (imageData == null || imageData.Length == 0)
            {
                Debug.LogWarning($"[{nameof(SpatialSyncManager)}] Failed to encode the key frame image as {_imageFormat}; no key frame extracted.");
                return null;
            }

            _keyFrameID++;
            PassthroughCameraAccess.CameraIntrinsics cameraIntrinsics = _cameraAccess.Intrinsics;

            var keyFrame = new KeyFrameDTO
            {
                ID = _keyFrameID,
                CameraLocalPose = Conversions.ToSharedPose(cameraPose),
                CameraIntrinsics = Conversions.ToSharedCameraIntrinsics(cameraIntrinsics),
                Timestamp = timestamp,
                Width = size.x,
                Height = size.y,
                ImageFormat = _imageFormat,
                ImageData = imageData,
            };

            if (_debugSaveKeyFramePng)
            {
                DebugSaveImage(keyFrame);
            }

            return keyFrame;
        }

        /// <summary>
        /// ORB only needs intensity, so convert to 8-bit luma (4x smaller than RGBA). Pixel order is left exactly as the
        /// camera delivers it (Unity image order, bottom row first), which is the order the image encoder expects.
        /// </summary>
        private static byte[] ToGrayscale(NativeArray<Color32> colors)
        {
            var gray = new byte[colors.Length];
            for (int i = 0; i < gray.Length; i++)
            {
                Color32 c = colors[i];
                gray[i] = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
            }

            return gray;
        }

        /// <summary>
        /// Compress the grayscale pixels to a JPEG or PNG file. Single-channel R8 data is written as an 8-bit grayscale
        /// image. The encoder reads rows bottom-first (Unity's convention) and writes the file upright, so the result
        /// decodes with row 0 at the top. Unity requires this to run on the main thread.
        /// </summary>
        private byte[] EncodeImage(byte[] gray, int width, int height)
        {
            if (_imageFormat == KeyFrameImageFormat.Png)
            {
                return ImageConversion.EncodeArrayToPNG(gray, GraphicsFormat.R8_UNorm, (uint)width, (uint)height);
            }

            return ImageConversion.EncodeArrayToJPG(gray, GraphicsFormat.R8_UNorm, (uint)width, (uint)height, 0, _jpegQuality);
        }

        /// <summary>
        /// Save the exact compressed image that is sent to the server, so you can open it and check it is upright.
        /// </summary>
        private void DebugSaveImage(KeyFrameDTO keyFrame)
        {
            string extension = keyFrame.ImageFormat == KeyFrameImageFormat.Png ? "png" : "jpg";
            string path = Path.Combine(Application.persistentDataPath, $"keyframe_{keyFrame.ID}.{extension}");
            File.WriteAllBytes(path, keyFrame.ImageData);
            Debug.Log($"[{nameof(SpatialSyncManager)}] Saved key frame {keyFrame.ID} ({keyFrame.ImageData.Length / 1024} KB) to {path}");
        }

        #endregion Key Frame Methods

        #region Handle Incoming Events

        /// <summary>
        /// This is called on the WebSocket thread when a SyncResponseDTO arrives from the server. It only sets a variable; the actual handling happens in Update.
        /// </summary>
        /// <param name="dto">The sync response DTO from the server.</param>
        void OnSyncResponseDTOReceived(SyncResponseDTO dto)
        {
            lock (_syncResponseLock)
            {
                _latestSyncResponseDTO = dto;
            }
        }

        /// <summary>
        /// Handles the BeginSpatialSyncDTO from the server, which indicates that the server is ready to start spatial synchronization (i.e., the client can start sending key frames to get the global pose).
        /// </summary>
        /// <param name="dto">The begin spatial sync DTO from the server.</param>
        void OnBeginSpatialSyncDTOReceived(BeginSpatialSyncDTO dto)
        {
            StartCoroutine(BeginSpatialSync());
        }

        IEnumerator BeginSpatialSync()
        {
            bool socketStarted = false;
            ConnectionManager.Instance.ConnectWebSocket(ConnectionType.SpatialSync)
                .ContinueWith(t =>
                {
                    socketStarted = true;
                    if (t.IsFaulted)
                        MainThreadDispatcher.Enqueue(() =>
                            Debug.LogError($"[{nameof(SpatialSyncManager)}]: ConnectWebSocket faulted — {t.Exception?.GetBaseException().Message}"));
                });

            // Wait until the WebSocket reports as connected (IsConnected is set inside the OnOpen
            // handler on the network thread, so it's safe to poll from the main thread).
            while (!ConnectionManager.Instance.IsConnected[ConnectionType.SpatialSync])
            {
                // Bail out if the task itself threw without ever setting IsConnected.
                if (socketStarted && !ConnectionManager.Instance.IsConnected[ConnectionType.SpatialSync]
                    && !ConnectionManager.Instance.NeedsConnection[ConnectionType.SpatialSync])
                {
                    Debug.LogError($"Connection failed to {ConnectionManager.Instance._serverUrl}. Trying again...");
                    yield break;
                }
                yield return null;
            }

            // Start loop to send keyframes to get spatial synchronization info.
            StartCoroutine(SendKeyFrameLoop());
        }

        /// <summary>
        /// This handles incoming spatial synchronization messages from the server.
        /// </summary>
        /// <param name="objDTO">The queued DTO.</param>
        void HandleIncomingMessage(object objDTO)
        {
            if (objDTO is SyncResponseDTO syncResponseDTO)
            {
                HandleSyncResponse(syncResponseDTO);
            }
            else
            {
                Debug.LogError($"{nameof(SpatialSyncManager)}: incoming message of type {objDTO.GetType().Name} was never handled!");
            }
        }

        /// <summary>
        /// Handle a response to a key frame. Without a global pose the global map is not ready for this device yet, so keep
        /// sending at the fast rate. With one, move the shared scene so the global frame lines up with this headset's tracking space.
        /// </summary>
        private void HandleSyncResponse(SyncResponseDTO response)
        {
            // Follows the latest response, so if the alignment is ever lost the send rate goes back up by itself.
            _isGloballyAligned = response.HasGlobalPose;
            if (!response.HasGlobalPose)
            {
                return;
            }

            if (_sharedScene == null)
            {
                Debug.LogError($"[{nameof(SpatialSyncManager)}] Received a global pose but there is no SharedScene to align.");
                return;
            }

            // L: where this headset's camera was in its own tracking space when the key frame was taken.
            // G: where the server says that same camera was in the global frame.
            Pose localPose = Conversions.ToUnityPose(response.CameraLocalPose);
            Pose globalPose = Conversions.ToUnityPose(response.GlobalPose.Value);

            // The shared scene's content is authored in global coordinates, so the scene's transform must map global -> this headset's
            // world: world = rotation * global + position. Requiring that G maps to L gives:
            Quaternion rotation = localPose.rotation * Quaternion.Inverse(globalPose.rotation);
            Vector3 position = localPose.position - rotation * globalPose.position;

            _sharedScene.transform.SetPositionAndRotation(position, rotation);
        }

        #endregion Handle Incoming Events


    }
}
