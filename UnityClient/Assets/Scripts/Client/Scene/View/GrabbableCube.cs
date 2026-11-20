using SharedLibrary.Game;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

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
    [SerializeField] Color _defaultColor = new Color(0.2f, 0.6f, 1f, 0.8f);
    [SerializeField] Color _ourGrabbedColor = new Color(1f, 0.5f, 0f, 0.8f);
    [SerializeField] Color _anotherGrabbedColor = new Color(1f, 0.1f, 0f, 0.8f);

    [SerializeField] GrabbedState grabbed;

    Material _material;
    Transform _followTarget;
    Vector3 _followLocalPosition;
    Quaternion _followLocalRotation;

    public string Id { get; private set; }

    public Collider Collider { get; private set; }

    public GrabbedState Grabbed
    {
        get => grabbed;
        set
        {
            grabbed = value;
            if (grabbed != GrabbedState.OurPlayer)
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
    public void Grab(Transform hand) => Grab(hand, hasFollowOffset: false, default, default);

    /// <summary>
    /// Like <see cref="Grab(Transform)"/>, but holds the cube at the given pose in the hand's space (taken from the grab action) instead
    /// of working the offset out from where the cube and hand are right now. So grabbing again after the cube was let go of
    /// and moved (the game state replaying the grab on top of a newer server state) keeps the same offset.
    /// </summary>
    public void Grab(Transform hand, Vector3 followLocalPosition, Quaternion followLocalRotation) =>
        Grab(hand, hasFollowOffset: true, followLocalPosition, followLocalRotation);

    void Grab(Transform hand, bool hasFollowOffset, Vector3 followLocalPosition, Quaternion followLocalRotation)
    {
        // If we have already followed the hand, then return early.
        if (_followTarget == hand)
        {
            return;
        }

        Grabbed = GrabbedState.OurPlayer;
        _followTarget = hand;
        if (hand == null)
            return;

        if (hasFollowOffset)
        {
            _followLocalPosition = followLocalPosition;
            _followLocalRotation = followLocalRotation;
        }
        else
        {
            _followLocalPosition = hand.InverseTransformPoint(transform.position);
            _followLocalRotation = Quaternion.Inverse(hand.rotation) * transform.rotation;
        }
    }

    void Awake()
    {
        _material = GetComponent<Renderer>().material;
        ConfigureTransparency();
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
        if (grabbed != GrabbedState.OurPlayer)
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
        {
            if (grabbed == GrabbedState.OurPlayer)
            {
                _material.color = _ourGrabbedColor;
            }
            else if (grabbed == GrabbedState.AnotherPlayer)
            {
                _material.color = _anotherGrabbedColor;
            }
            else
            {
                _material.color = _defaultColor;
            }
        }

    }

    void ConfigureTransparency()
    {
        if (_material == null)
            return;

        // Primitive materials are opaque by default, so their alpha channel is ignored.
        if (_material.HasProperty("_Mode"))
        {
            // Standard shader: Fade mode.
            _material.SetFloat("_Mode", 2f);
            _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_ZWrite", 0);
            _material.DisableKeyword("_ALPHATEST_ON");
            _material.EnableKeyword("_ALPHABLEND_ON");
            _material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        if (_material.HasProperty("_Surface"))
        {
            // URP Lit shader: transparent alpha blending.
            _material.SetFloat("_Surface", 1f);
            _material.SetFloat("_Blend", 0f);
            _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_ZWrite", 0);
            _material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        _material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}
