using Client.Communication.Manager;
using SharedLibrary.Communication.Connection;
using SharedLibrary.Communication.DataTransferObjects;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Client.Scene.Manager
{
    /// <summary>
    /// Manages the Main Menu scene.
    ///
    /// Responsibilities:
    ///   1. Kicks off the Game WebSocket connection via <see cref="ConnectionManager"/>.
    ///   2. Gates the Play button — it is not interactable until the Game WebSocket is open.
    ///   3. On Play, sends a <see cref="ConnectDTO"/> to identify this player on the Game
    ///      socket, then transitions to the <see cref="SceneName.GameState"/> scene.
    ///
    /// The Play button is a standard Unity UI <see cref="Button"/>. Wire it to the pokeable
    /// XR pane (e.g. via Meta ISDK's PointableCanvas + InteractableUnityEventWrapper, or
    /// directly through the Button's OnClick list). Call <see cref="OnPlayPressed"/> from
    /// whatever XR interaction event fires on poke/click.
    /// </summary>
    public class MainMenuManager : MonoBehaviour
    {
        [Header("Menu Canvas")]
        [Tooltip("The world-space Canvas that contains the main menu pane. Positioned in front of the player on Start.")]
        [SerializeField] Canvas _mainMenuCanvas;

        [Tooltip("How far in front of the player's head the canvas is placed, in metres.")]
        [SerializeField] float _menuDistance = 0.5f;

        [Tooltip("Vertical offset applied after placing the canvas at eye level (negative = lower).")]
        [SerializeField] float _menuHeightOffset = 0f;

        [Tooltip("Time in seconds for the main menu to fade out after the first game state arrives.")]
        [SerializeField] float _menuFadeDuration = 0.5f;

        [Header("UI References")]
        [Tooltip("The Play button on the XR pokeable pane. Disabled until the Game WebSocket is ready.")]
        [SerializeField] Button _playButton;

        [Tooltip("Optional label shown below the button. Displays connection status.")]
        [SerializeField] TextMeshProUGUI _statusText;

        bool _isQueued;
        bool _mainMenuHidden;

        #region Unity Functions

        void Awake()
        {
            if (_playButton != null)
                _playButton.onClick.AddListener(QueueGame);

            PositionMenuInFrontOfPlayer();
            StartCoroutine(AutoMoveMenu());
        }

        void OnDestroy()
        {
            if (_playButton != null)
                _playButton.onClick.RemoveListener(QueueGame);
        }

        IEnumerator Start()
        {
            SetStatus("Connecting to server…", interactable: false, Color.white);

            // Wait for ConnectionManager to finish its HTTP initialization (login, etc.)
            while (ConnectionManager.Instance == null || !ConnectionManager.Instance.IsInitialized)
                yield return null;

            var connectionManager = ConnectionManager.Instance;
            if (connectionManager.InboundMessagesDict.TryGetValue(typeof(GameStateDTO), out var gameStateEvent))
                ((UnityEvent<GameStateDTO>)gameStateEvent).AddListener(OnGameStateDTOReceived);

            // Start the Game WebSocket. This is async; wrap it in a coroutine-friendly wait.
            SetStatus("Opening game connection…", interactable: false, Color.white);
            bool socketStarted = false;
            ConnectionManager.Instance.ConnectWebSocket(ConnectionType.Game)
                .ContinueWith(t =>
                {
                    socketStarted = true;
                    if (t.IsFaulted)
                        MainThreadDispatcher.Enqueue(() =>
                            Debug.LogError($"{nameof(MainMenuManager)}: ConnectWebSocket faulted — {t.Exception?.GetBaseException().Message}"));
                });

            // Wait until the WebSocket reports as connected (IsConnected is set inside the OnOpen
            // handler on the network thread, so it's safe to poll from the main thread).
            while (!ConnectionManager.Instance.IsConnected[ConnectionType.Game])
            {
                // Bail out if the task itself threw without ever setting IsConnected.
                if (socketStarted && !ConnectionManager.Instance.IsConnected[ConnectionType.Game]
                    && !ConnectionManager.Instance.NeedsConnection[ConnectionType.Game])
                {
                    SetStatus("Connection failed. Trying again...", interactable: false, Color.red);
                    yield break;
                }
                yield return null;
            }

            SetStatus("Ready!", interactable: true, Color.green);
        }

        void Update()
        {
            // Keep the button state in sync in case the socket drops after first connecting.
            if (_playButton != null)
            {
                bool connected = ConnectionManager.Instance != null
                    && ConnectionManager.Instance.IsInitialized
                    && ConnectionManager.Instance.IsConnected.ContainsKey(ConnectionType.Game)
                    && ConnectionManager.Instance.IsConnected[ConnectionType.Game];

                _playButton.interactable = connected;

                if (connected)
                {
                    SetStatus(_isQueued ? "Queued" : "Ready!", interactable: true, Color.green);
                }
                else
                {
                    SetStatus("Connection failed. Trying again...", interactable: false, Color.red);
                }
            }


        }

        #endregion Unity Functions

        void OnGameStateDTOReceived(GameStateDTO dto)
        {
            MainThreadDispatcher.Enqueue(HideMainMenu);
        }

        void HideMainMenu()
        {
            if (_mainMenuHidden)
                return;

            _mainMenuHidden = true;
            StartCoroutine(FadeOutMainMenu());
        }

        IEnumerator FadeOutMainMenu()
        {
            if (_mainMenuCanvas == null)
                yield break;

            CanvasGroup canvasGroup = _mainMenuCanvas.GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = _mainMenuCanvas.gameObject.AddComponent<CanvasGroup>();

            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            float startAlpha = canvasGroup.alpha;
            float elapsed = 0f;
            while (elapsed < _menuFadeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / _menuFadeDuration);
                canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, progress);
                yield return null;
            }

            canvasGroup.alpha = 0f;
            _mainMenuCanvas.gameObject.SetActive(false);
        }

        #region Private Helpers

        /// <summary>
        /// Snaps <see cref="_mainMenuCanvas"/> directly in front of the player's head.
        ///
        /// Uses <see cref="Camera.main"/> as the head reference — on Quest this is the centre-eye
        /// anchor. The canvas is placed <see cref="_menuDistance"/> metres along the camera's
        /// forward vector, then rotated to face the player so the pane is readable.
        ///
        /// Called from <see cref="Awake"/> so the canvas is in position before the first frame
        /// is rendered, avoiding a visible pop.
        /// </summary>
        void PositionMenuInFrontOfPlayer()
        {
            if (_mainMenuCanvas == null)
            {
                Debug.LogWarning($"{nameof(MainMenuManager)}: _mainMenuCanvas is not assigned; skipping placement.");
                return;
            }

            Camera head = Camera.main;
            if (head == null)
            {
                // Camera.main may not be ready yet — retry at the start of the first frame.
                StartCoroutine(PositionMenuNextFrame());
                return;
            }

            ApplyMenuPlacement(head);
        }

        /// <summary>
        /// Fallback: waits one frame so <see cref="Camera.main"/> has been initialised by the
        /// XR rig before positioning the canvas.
        /// </summary>
        IEnumerator PositionMenuNextFrame()
        {
            yield return null; // Wait one frame.

            Camera head = Camera.main;
            if (head == null)
            {
                Debug.LogError($"{nameof(MainMenuManager)}: Camera.main is still null after one frame; cannot place menu canvas.");
                yield break;
            }

            ApplyMenuPlacement(head);
        }

        IEnumerator AutoMoveMenu()
        {
            Camera head = Camera.main;
            if (head == null)
            {
                Debug.LogError($"{nameof(MainMenuManager)}: Camera.main is still null after one frame; cannot place menu canvas.");
                yield break;
            }

            while (true)
            {
                if ((_mainMenuCanvas.transform.position - head.transform.position).magnitude > _menuDistance * 2.0f)
                {
                    // Compute target placement.
                    Vector3 flatForward = head.transform.forward;
                    flatForward.y = 0f;
                    if (flatForward.sqrMagnitude < 0.001f)
                        flatForward = Vector3.forward;
                    flatForward.Normalize();

                    Vector3 targetPosition = head.transform.position
                        + flatForward * _menuDistance
                        + Vector3.up * _menuHeightOffset;
                    Quaternion targetRotation = Quaternion.LookRotation(flatForward);

                    float duration = 0.35f;
                    float elapsed = 0f;
                    Vector3 startPosition = _mainMenuCanvas.transform.position;
                    Quaternion startRotation = _mainMenuCanvas.transform.rotation;

                    while (elapsed < duration)
                    {
                        elapsed += Time.deltaTime;
                        float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                        _mainMenuCanvas.transform.SetPositionAndRotation(
                            Vector3.Lerp(startPosition, targetPosition, t),
                            Quaternion.Slerp(startRotation, targetRotation, t)
                        );
                        yield return null; // Each lerp step also yields a frame.
                    }

                    _mainMenuCanvas.transform.SetPositionAndRotation(targetPosition, targetRotation);
                }

                yield return null; // Wait one frame before checking again.
            }
        }

        /// <summary>
        /// Moves and orients the canvas relative to the given camera.
        /// </summary>
        void ApplyMenuPlacement(Camera head)
        {
            Transform camTransform = head.transform;

            // Project the camera's forward onto the horizontal plane so the menu doesn't tilt
            // with the player's head nod. Remove the Y component and renormalise.
            Vector3 flatForward = camTransform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.001f)
                flatForward = Vector3.forward; // Edge case: player looking straight up/down.
            flatForward.Normalize();

            // Place the canvas at eye level in front of the player.
            Vector3 position = camTransform.position
                + flatForward * _menuDistance
                + Vector3.up * _menuHeightOffset;

            // Rotate so the canvas faces back toward the player.
            Quaternion rotation = Quaternion.LookRotation(flatForward);

            _mainMenuCanvas.transform.SetPositionAndRotation(position, rotation);

            Debug.Log($"{nameof(MainMenuManager)}: menu canvas placed at {position} facing {flatForward}.");
        }



        /// <summary>
        /// Toggles the player's queue state on the Game socket.
        /// </summary>
        void SetStatus(string message, bool interactable, Color textColor)
        {
            if (_playButton != null)
                _playButton.interactable = interactable;

            if (_statusText != null)
                _statusText.text = message;
            _statusText.color = textColor;
        }

        public async void QueueGame()
        {
            var cm = ConnectionManager.Instance;
            if (cm == null)
                return;

            bool queue = !_isQueued;
            bool sent = queue
                ? await cm.SendDTO(ConnectionType.Game, new QueueDTO(cm.PlayerID))
                : await cm.SendDTO(ConnectionType.Game, new DequeueDTO(cm.PlayerID));

            if (sent)
            {
                _isQueued = queue;
                SetStatus(_isQueued ? "Queued" : "Ready!", interactable: true, Color.green);
            }
        }

        #endregion Private Helpers
    }
}
