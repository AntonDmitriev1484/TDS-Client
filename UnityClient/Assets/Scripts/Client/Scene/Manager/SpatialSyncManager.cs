using Assets.Scripts.Utilities;
using Client.Communication.Manager;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Communication.DataTransferObjects.SpatialSync;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace Client.Scene.Manager
{
    public class SpatialSyncManager : MonoBehaviour
    {

        #region Properties

        public static SpatialSyncManager Instance { get; set; } // Singleton Instance
        public bool IsInitialized { get; set; }


        private long _keyFrameID = 0;
        [Tooltip("Key frames per second while the global map is not ready for this device yet, so it is joined as soon as possible.")]
        public float SendRate = 5.0f;
        [Tooltip("Key frames per second once the server has given this device a global pose; it only needs to keep the alignment fresh.")]
        public float SyncedSendRate = 1.0f;
        private bool _isGloballyAligned = false; // True while the latest response from the server carried a global pose
        private float _tickInterval => 1.0f / (_isGloballyAligned ? SyncedSendRate : SendRate);
        private Task<bool> _pendingSend; // Key frame currently being serialized/sent, if any

        [Tooltip("Seconds the shared scene takes to move to a new alignment from the server. The very first alignment is applied immediately; " +
                 "every later one is blended in so corrections don't make the scene jump. 0 applies them immediately too.")]
        public float AlignmentBlendSeconds = 1.0f;
        private bool _hasAppliedGlobalPose = false; // True once the first global pose has been applied (that one is not blended)
        private bool _isBlending = false;
        private float _blendElapsed;
        private Vector3 _blendStartPosition;
        private Quaternion _blendStartRotation;
        private Vector3 _blendTargetPosition;
        private Quaternion _blendTargetRotation;

        // The shared scene's pose from the alignment alone (global frame -> this headset's world), before any drift.
        private Vector3 _alignedPosition = Vector3.zero;
        private Quaternion _alignedRotation = Quaternion.identity;

        // This player's view drift (GameState.DriftError): a turn about the vertical axis (degrees), then a shift along the
        // global frame's X axis (meters), both applied to the shared scene on top of the alignment.
        private float _driftRotationDegrees;          // As applied: the server's drift_rot with this headset's chosen sign
        private float _receivedDriftRotationDegrees;  // As the server sent it
        private float _driftTranslationMeters;

        private SceneAnchorManager _anchorManager;

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

                // The scene anchors of the headset's room scan (walls, floor, doors, furniture...) are what the server lines the headsets up with.
                // Made right away, so the room is scanned and loaded as soon as the app starts rather than after the connection is up.
                _anchorManager = GetComponent<SceneAnchorManager>();
                if (_anchorManager == null)
                    _anchorManager = gameObject.AddComponent<SceneAnchorManager>();

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
            else
            {
                // Until the server aligns this headset, the scene's own pose is the alignment.
                _alignedPosition = _sharedScene.transform.position;
                _alignedRotation = _sharedScene.transform.rotation;
            }

            if (ConnectionManager.Instance.InboundMessagesDict.TryGetValue(typeof(SyncResponseDTO), out var srEvent))
                ((UnityEvent<SyncResponseDTO>)srEvent).AddListener(OnSyncResponseDTOReceived);
            if (ConnectionManager.Instance.InboundMessagesDict.TryGetValue(typeof(BeginSpatialSyncDTO), out var bssEvent))
                ((UnityEvent<BeginSpatialSyncDTO>)bssEvent).AddListener(OnBeginSpatialSyncDTOReceived);

            IsInitialized = true;
            yield break;
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

            UpdateSharedSceneBlend();
            ApplySharedScenePose();
        }

        /// <summary>
        /// Sets this player's view drift: the shared scene is turned <paramref name="rotationDegrees"/> (in a randomly chosen direction, + or -) about the vertical axis through
        /// its origin, then shifted <paramref name="translationMeters"/> along the global frame's X axis. Only changes where this player
        /// sees things. (0, 0) removes the drift.
        /// </summary>
        public void SetDrift(float rotationDegrees, float translationMeters)
        {
            if (Mathf.Approximately(rotationDegrees, _receivedDriftRotationDegrees) && Mathf.Approximately(translationMeters, _driftTranslationMeters))
                return;

            // The server sends how far to turn; this headset picks which way, once per new drift, so it holds still in between.
            _receivedDriftRotationDegrees = rotationDegrees;
            _driftRotationDegrees = Random.value < 0.5f ? -rotationDegrees : rotationDegrees;
            _driftTranslationMeters = translationMeters;
            Debug.Log($"[{nameof(SpatialSyncManager)}] View drift set to {_driftRotationDegrees:+0.0;-0.0;0} deg, {translationMeters:0.00} m.");
        }

        /// <summary>
        /// Moves the aligned pose towards the latest alignment from the server: the position is interpolated and the rotation is
        /// slerped, eased in and out, over <see cref="AlignmentBlendSeconds"/>.
        /// </summary>
        private void UpdateSharedSceneBlend()
        {
            if (!_isBlending)
            {
                return;
            }

            _blendElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_blendElapsed / AlignmentBlendSeconds);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            _alignedPosition = Vector3.Lerp(_blendStartPosition, _blendTargetPosition, eased);
            _alignedRotation = Quaternion.Slerp(_blendStartRotation, _blendTargetRotation, eased);

            if (t >= 1f)
            {
                _isBlending = false;
            }
        }

        /// <summary>
        /// Puts the shared scene at the aligned pose with this player's drift on top. A point g in the global frame ends up at
        /// aligned * (driftRotation * g + driftTranslation): turned about the vertical axis, then shifted, then aligned.
        /// </summary>
        private void ApplySharedScenePose()
        {
            if (_sharedScene == null)
            {
                return;
            }

            Quaternion driftRotation = Quaternion.AngleAxis(_driftRotationDegrees, Vector3.up);
            Vector3 driftTranslation = new Vector3(_driftTranslationMeters, 0f, 0f);

            _sharedScene.transform.SetPositionAndRotation(
                _alignedPosition + _alignedRotation * driftTranslation,
                _alignedRotation * driftRotation);
        }

        /// <summary>
        /// Every <see cref="_tickInterval"/> seconds, send an anchor key frame to the server.
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
        /// Build the anchor key frame (the headset pose and its scene anchors) and send it, unless the connection isn't ready or the previous one is still in flight.
        /// </summary>
        private void TrySendKeyFrame()
        {
            // A backlog of large messages would only add latency, so skip this tick if the last send hasn't finished.
            if (_pendingSend != null && !_pendingSend.IsCompleted)
            {
                return;
            }

            if (_anchorManager == null || !ConnectionManager.Instance.IsConnected[ConnectionType.SpatialSync])
            {
                return;
            }

            KeyFrameAnchorDTO keyFrame = _anchorManager.BuildKeyFrame(_keyFrameID + 1);
            if (keyFrame == null)
            {
                return;
            }

            _keyFrameID++;

            // Serialize and send on a thread-pool thread so the JSON work doesn't stall the render loop.
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
            MainThreadDispatcher.Enqueue(() => StartCoroutine(BeginSpatialSync()));
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

            // L: where this headset was in its own tracking space when the key frame was taken.
            // G: where the server says that same pose is in the global frame.
            Pose localPose = Conversions.ToUnityPose(response.CameraLocalPose);
            Pose globalPose = Conversions.ToUnityPose(response.GlobalPose.Value);

            // The shared scene's content is authored in global coordinates, so the scene's transform must map global -> this headset's
            // world: world = rotation * global + position. Requiring that G maps to L gives:
            Quaternion rotation = localPose.rotation * Quaternion.Inverse(globalPose.rotation);
            Vector3 position = localPose.position - rotation * globalPose.position;

            // The first global pose puts the scene in place at once. Every later one is a correction of that alignment, so the
            // scene slerps to it over time instead of jumping. A response that arrives mid-blend starts a new blend from wherever
            // the scene is right now.
            // (Both work on the aligned pose; the drift is put on top of it every frame.)
            if (!_hasAppliedGlobalPose || AlignmentBlendSeconds <= 0f)
            {
                _alignedPosition = position;
                _alignedRotation = rotation;
                _hasAppliedGlobalPose = true;
                _isBlending = false;
                return;
            }

            _blendStartPosition = _alignedPosition;
            _blendStartRotation = _alignedRotation;
            _blendTargetPosition = position;
            _blendTargetRotation = rotation;
            _blendElapsed = 0f;
            _isBlending = true;
        }

        #endregion Handle Incoming Events


    }
}
