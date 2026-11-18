using SharedLibrary.Game;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace Client.Scene.Manager
{
    /// <summary>
    /// Shows the local player's study metrics (latency and the state and spatial inconsistencies, as measured by the server and sent in every
    /// game state) as text that stays fixed in the player's view: it is parented to the head camera, so it moves with the head and
    /// looks painted on the screen. Built in code, so nothing needs wiring in the scene.
    /// </summary>
    public class MetricsHud : MonoBehaviour
    {
        [Header("Placement (relative to the head)")]
        [Tooltip("Where the text sits in the head's space: x right, y up, z forward (meters).")]
        [SerializeField] Vector3 _localPosition = new Vector3(0.225f, -0.18f, 1.0f);

        [Header("Look")]
        [SerializeField] float _characterSize = 0.0035f;
        [SerializeField] int _fontSize = 100;
        [SerializeField] Color _color = new Color(0.55f, 1f, 0.55f, 1f);

        [Tooltip("The text is rewritten at most this often (seconds).")]
        [SerializeField] float _refreshInterval = 0.1f;

        const string VisiblePrefKey = "MetricsHudVisible";
        static int _visibleState = -1; // -1 = not read yet, 0 = hidden, 1 = shown

        /// <summary>
        /// Whether the metrics are shown at all. Set from the main menu's toggle button and remembered between runs (PlayerPrefs), so it
        /// holds across scenes. On by default.
        /// </summary>
        public static bool Visible
        {
            get
            {
                if (_visibleState < 0)
                    _visibleState = PlayerPrefs.GetInt(VisiblePrefKey, 1);
                return _visibleState == 1;
            }
            set
            {
                _visibleState = value ? 1 : 0;
                PlayerPrefs.SetInt(VisiblePrefKey, _visibleState);
                PlayerPrefs.Save();
            }
        }

        TextMesh _text;
        bool _wasVisible = true;
        Transform _anchor;
        Metrics _latest;
        bool _dirty;
        float _nextRefresh;

        /// <summary>Sets the metrics to show (from the latest game state). Null hides the display.</summary>
        public void SetMetrics(Metrics metrics)
        {
            _latest = metrics;
            _dirty = true;
        }

        void Awake()
        {
            var go = new GameObject("MetricsHud");
            go.transform.SetParent(transform, false);
            _anchor = go.transform;

            _text = go.AddComponent<TextMesh>();
            _text.anchor = TextAnchor.UpperLeft;
            _text.alignment = TextAlignment.Left;
            _text.fontSize = _fontSize;
            _text.characterSize = _characterSize;
            _text.color = _color;
            _text.text = "";
            go.SetActive(false);
        }

        void OnDestroy()
        {
            if (_anchor != null)
                Destroy(_anchor.gameObject);
        }

        void LateUpdate()
        {
            // Follow the head: parent to the main camera (found lazily, and again if it is replaced).
            Camera head = Camera.main;
            if (head == null)
                return;

            if (_anchor.parent != head.transform)
            {
                _anchor.SetParent(head.transform, false);
                _anchor.localPosition = _localPosition;
                _anchor.localRotation = Quaternion.identity;
            }

            if (!Visible)
            {
                if (_anchor.gameObject.activeSelf)
                    _anchor.gameObject.SetActive(false);
                _wasVisible = false;
                return;
            }
            if (!_wasVisible)
            {
                // Switched back on: show the latest metrics straight away.
                _wasVisible = true;
                _dirty = true;
                _nextRefresh = 0f;
            }

            if (!_dirty || Time.unscaledTime < _nextRefresh)
                return;
            _dirty = false;
            _nextRefresh = Time.unscaledTime + _refreshInterval;

            if (_latest == null)
            {
                _anchor.gameObject.SetActive(false);
                return;
            }

            _text.text = Format(_latest);
            _anchor.gameObject.SetActive(true);
        }

        static string Format(Metrics m)
        {
            return $"Metric 1: {m.LatencyMs:F0} ms\n"
                + $"Metric 2: {FormatInconsistency(m.PairwiseStateInconsistency, m.PairwiseStateMeanDistanceCm)}\n"
                + $"Metric 3: {FormatInconsistency(m.RetroactiveStateInconsistency, m.RetroactiveStateMeanDistanceCm)}\n"
                + $"Metric 4: {FormatInconsistency(m.PairwiseSpatialInconsistency, m.PairwiseSpatialMeanDistanceCm)}\n"
                + $"Metric 5: {FormatInconsistency(m.RetroactiveSpatialInconsistency, m.RetroactiveSpatialMeanDistanceCm)}";
        }

        static string FormatInconsistency(float inconsistency, float meanDistanceCm) =>
            inconsistency < 0f ? "--" : $"{inconsistency:F2} ({meanDistanceCm:F1} cm)";
    }
}
