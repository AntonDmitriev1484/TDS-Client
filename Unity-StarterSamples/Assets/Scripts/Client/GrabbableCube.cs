using UnityEngine;

/// <summary>
/// A cube whose grabbed state is set from outside (by <see cref="GameStateManager"/>, from
/// the shared game state). It turns orange while grabbed.
///
/// Invariant: the cube follows a hand only while <see cref="Grabbed"/> is true. Releasing
/// (setting <see cref="Grabbed"/> to false) always forgets the hand, so a later grab can
/// never resume following a stale one. <see cref="Grab"/> sets the state and the hand
/// together. Setting <see cref="Grabbed"/> to true without a hand (e.g. a server reporting
/// that someone else holds it) marks the cube grabbed but leaves its pose alone.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class GrabbableCube : MonoBehaviour
{
    [SerializeField] Color _defaultColor = new Color(0.2f, 0.6f, 1f);
    [SerializeField] Color _grabbedColor = new Color(1f, 0.5f, 0f);

    [SerializeField] bool grabbed;

    Material _material;
    Transform _followTarget;
    Vector3 _followLocalPosition;
    Quaternion _followLocalRotation;

    public string Id { get; private set; }

    public Collider Collider { get; private set; }

    public bool Grabbed
    {
        get => grabbed;
        set
        {
            grabbed = value;
            if (!grabbed)
                _followTarget = null;
            ApplyColor();
        }
    }

    /// <summary>The hand this cube is following, or null.</summary>
    public Transform FollowTarget => _followTarget;

    public void Initialize(string id, Color defaultColor)
    {
        Id = id;
        _defaultColor = defaultColor;
        ApplyColor();
    }

    /// <summary>
    /// Marks the cube grabbed by <paramref name="hand"/> and starts following it, keeping the
    /// current pose relative to the hand. Grabbing with another hand hands the cube over.
    /// </summary>
    public void Grab(Transform hand)
    {
        Grabbed = true;
        _followTarget = hand;
        if (hand == null)
            return;

        _followLocalPosition = hand.InverseTransformPoint(transform.position);
        _followLocalRotation = Quaternion.Inverse(hand.rotation) * transform.rotation;
    }

    void Awake()
    {
        _material = GetComponent<Renderer>().material;
        Collider = GetComponent<Collider>();
        ApplyColor();
    }

    void OnValidate()
    {
        // Lets the checkbox in the Inspector drive the color during Play mode.
        if (_material != null)
            ApplyColor();
    }

    // LateUpdate so the hand anchors have already been updated for this frame.
    void LateUpdate()
    {
        if (!grabbed)
        {
            // Also covers the Inspector checkbox, which writes the field without the setter.
            _followTarget = null;
            return;
        }

        if (_followTarget == null)
            return;

        transform.SetPositionAndRotation(_followTarget.TransformPoint(_followLocalPosition),
            _followTarget.rotation * _followLocalRotation);
    }

    void ApplyColor()
    {
        if (_material != null)
            _material.color = grabbed ? _grabbedColor : _defaultColor;
    }
}
