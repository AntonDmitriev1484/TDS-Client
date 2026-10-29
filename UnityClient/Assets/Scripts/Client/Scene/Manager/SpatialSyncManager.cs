using Client.Communication.Manager;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEngine;
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

        /// <summary>World-space camera pose of every key frame extracted so far, by key frame ID.</summary>
        public Dictionary<long, Pose> KeyFramePose { get; private set; }

        /// <summary>True once the passthrough camera is delivering images.</summary>
        public bool IsCameraReady => _cameraAccess != null && _cameraAccess.IsPlaying;

        private long _keyFrameID = 0;

        private const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
        private const float PermissionTimeoutSeconds = 60f;
        private const float CameraStartTimeoutSeconds = 10f;

        [Header("Passthrough Camera")]
        [Tooltip("Leave empty to find one in the scene or create one automatically.")]
        [SerializeField] private PassthroughCameraAccessBlockData _cameraAccess;
        [SerializeField] private Vector2Int _requestedResolution = new Vector2Int(1280, 960);

        [Header("Debug")]
        [Tooltip("Write every extracted key frame to Application.persistentDataPath as a PNG, to check image orientation.")]
        [SerializeField] private bool _debugSaveKeyFramePng = false;

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
            KeyFramePose = new Dictionary<long, Pose>();

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
                Debug.LogError($"[SpatialSync] Passthrough camera did not start (IsSupported = {_cameraAccess.IsSupported}). " +
                               "Check OVRManager > 'Enabled Passthrough Camera Access' and that this is a Quest 3/3S.");
            }
        }

        private PassthroughCameraAccess CreateCameraAccess()
        {
            // Configure the component while its GameObject is inactive so the settings are in place before it starts.
            var host = new GameObject("[SpatialSync] PassthroughCameraAccess");
            host.SetActive(false);
            host.transform.SetParent(transform, false);

            var access = host.AddComponent<PassthroughCameraAccess>();
            access.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            access.RequestedResolution = _requestedResolution;

            host.SetActive(true);
            return access;
        }

        #endregion Initialization

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
                Debug.LogWarning("[SpatialSync] Passthrough camera is not ready; no key frame extracted.");
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
                Debug.LogWarning($"[SpatialSync] Unexpected camera buffer ({colors.Length} pixels for {size.x}x{size.y}); no key frame extracted.");
                return null;
            }

            byte[] gray = ToGrayscaleTopDown(colors, size.x, size.y);

            if (camera.Timestamp != timestamp)
            {
                Debug.LogWarning("[SpatialSync] Camera image changed while capturing; no key frame extracted.");
                return null;
            }

            _keyFrameID++;
            CameraIntrinsics intrinsics = camera.Intrinsics;

            var keyFrame = new KeyFrameDTO
            {
                Id = _keyFrameID,
                CameraPose = cameraPose,
                Timestamp = timestamp,
                Width = size.x,
                Height = size.y,
                Gray = gray,
            };

            KeyFramePose[keyFrame.Id] = cameraPose;

            if (_debugSaveKeyFramePng)
            {
                DebugSavePng(keyFrame);
            }

            return keyFrame;
        }

        /// <summary>
        /// ORB only needs intensity, so convert to 8-bit luma now (4x smaller than RGBA). Unity images store their bottom
        /// row first; flip so row 0 is the top row, which is what image libraries and the server expect.
        /// </summary>
        private static byte[] ToGrayscaleTopDown(NativeArray<Color32> colors, int width, int height)
        {
            var gray = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int src = (height - 1 - y) * width;
                int dst = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = colors[src + x];
                    gray[dst + x] = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
                }
            }

            return gray;
        }

        private void DebugSavePng(KeyFrameDTO keyFrame)
        {
            // Texture2D rows are bottom-first, so undo the flip before writing: the PNG then shows Gray as the server sees it.
            int w = keyFrame.Width, h = keyFrame.Height;
            var bottomFirst = new byte[keyFrame.Gray.Length];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(keyFrame.Gray, y * w, bottomFirst, (h - 1 - y) * w, w);
            }

            var texture = new Texture2D(w, h, TextureFormat.R8, false);
            texture.LoadRawTextureData(bottomFirst);
            texture.Apply(false);

            string path = Path.Combine(Application.persistentDataPath, $"keyframe_{keyFrame.Id}.png");
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Destroy(texture);
            Debug.Log($"[SpatialSync] Saved key frame {keyFrame.Id} to {path}");
        }

        #endregion Key Frame Methods

    }
}
