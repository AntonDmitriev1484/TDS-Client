using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns and tracks <see cref="GrabbableCube"/>s by id, and watches both tracked hands for
/// pinches on them. It only reports hand input through <see cref="PinchStarted"/> and
/// <see cref="PinchEnded"/>; deciding whether a cube is actually grabbed is left to
/// <see cref="CubeGameStateManager"/>, which checks it against the shared game state.
///
/// Uses only OVRHand / OVRSkeleton from the Meta XR Core SDK (no Interaction SDK needed).
///
/// The runtime's own pinch flag (OVRHand.GetFingerIsPinching) is tuned for palm-down pinches
/// and drops out when the back of the hand hides the fingers from the headset cameras, which
/// is exactly the pose used to pinch a side or bottom face. So pinches are also detected from
/// the skeleton (thumb-to-index tip distance), and proximity is tested at several points on
/// the hand rather than only at the pinch point.
/// </summary>
public class GrabbableCubeManager : MonoBehaviour
{
    [SerializeField] float _cubeSize = 0.25f;
    [SerializeField] Color _cubeColor = new Color(0.2f, 0.6f, 1f);

    [Tooltip("How far outside a cube's surface a hand point still counts as touching it (meters).")]
    [SerializeField] float _grabMargin = 0.04f;

    [Tooltip("Thumb-to-index tip distance that starts a pinch (meters).")]
    [SerializeField] float _pinchStartDistance = 0.02f;

    [Tooltip("Thumb-to-index tip distance that ends a pinch (meters). Larger than start, to avoid flicker.")]
    [SerializeField] float _pinchEndDistance = 0.045f;

    [Tooltip("A pinch that starts slightly before the hand reaches a cube still counts within this time (seconds).")]
    [SerializeField] float _grabGraceTime = 0.25f;

    [Tooltip("How long a pinch on a cube survives while the hand is untracked (seconds).")]
    [SerializeField] float _trackingLossTolerance = 0.5f;

    /// <summary>A hand started pinching on a cube.</summary>
    public event Action<GrabbableCube, OVRHand> PinchStarted;

    /// <summary>A hand stopped pinching a cube it had started pinching (released, or tracking lost).</summary>
    public event Action<GrabbableCube, OVRHand> PinchEnded;

    class HandState
    {
        public OVRHand Hand;
        public OVRSkeleton Skeleton;
        public bool Pinching;
        public float PinchStartTime = float.NegativeInfinity;
        public float LastTrackedTime;
        public GrabbableCube PinchedCube;
    }

    readonly Dictionary<string, GrabbableCube> _cubes = new Dictionary<string, GrabbableCube>();
    readonly List<HandState> _hands = new List<HandState>();
    readonly List<Vector3> _contactPoints = new List<Vector3>(5);
    int _nextId;

    public IReadOnlyDictionary<string, GrabbableCube> Cubes => _cubes;

    public float CubeSize => _cubeSize;

    void Awake()
    {
        foreach (var hand in FindObjectsByType<OVRHand>(FindObjectsSortMode.None))
            _hands.Add(new HandState { Hand = hand, Skeleton = hand.GetComponent<OVRSkeleton>() });

        if (_hands.Count == 0)
            Debug.LogError($"{nameof(GrabbableCubeManager)}: no OVRHand found in the scene.");
    }

    /// <summary>
    /// Creates a cube at <paramref name="pose"/>. Pass an <paramref name="id"/> to use one
    /// assigned elsewhere (e.g. by a server); otherwise one is generated.
    /// </summary>
    public GrabbableCube SpawnCube(Pose pose, string id = null)
    {
        id ??= NextFreeId();
        if (_cubes.ContainsKey(id))
            throw new ArgumentException($"A cube with id '{id}' already exists.", nameof(id));

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"GrabbableCube ({id})";
        go.transform.SetPositionAndRotation(pose.position, pose.rotation);
        go.transform.localScale = Vector3.one * _cubeSize;

        // Kinematic so it stays put when released: there is no physical floor in passthrough.
        var body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        var cube = go.AddComponent<GrabbableCube>();
        cube.Initialize(id, _cubeColor);
        _cubes.Add(id, cube);
        return cube;
    }

    public bool TryGetCube(string id, out GrabbableCube cube) => _cubes.TryGetValue(id, out cube);

    public bool DestroyCube(string id)
    {
        if (!_cubes.TryGetValue(id, out var cube))
            return false;

        foreach (var state in _hands)
        {
            if (state.PinchedCube == cube)
                EndPinch(state);
        }

        _cubes.Remove(id);
        Destroy(cube.gameObject);
        return true;
    }

    string NextFreeId()
    {
        string id;
        do id = $"cube-{_nextId++}";
        while (_cubes.ContainsKey(id));
        return id;
    }

    void Update()
    {
        foreach (var state in _hands)
        {
            bool tracked = state.Hand.isActiveAndEnabled && state.Hand.IsTracked;
            if (!tracked)
            {
                // Brief tracking dropouts are common when the hand turns away from the cameras;
                // keep the pinch through them, but end it if the hand stays lost.
                if (state.PinchedCube != null && Time.time - state.LastTrackedTime > _trackingLossTolerance)
                    EndPinch(state);
                state.Pinching = false;
                continue;
            }

            state.LastTrackedTime = Time.time;
            UpdatePinch(state);

            if (!state.Pinching)
            {
                if (state.PinchedCube != null)
                    EndPinch(state);
                continue;
            }

            // A fresh pinch that is (or, within the grace time, becomes) close to a cube.
            if (state.PinchedCube == null && Time.time - state.PinchStartTime <= _grabGraceTime)
            {
                var cube = FindNearestCube(state);
                if (cube != null)
                {
                    state.PinchedCube = cube;
                    PinchStarted?.Invoke(cube, state.Hand);
                }
            }
        }
    }

    void EndPinch(HandState state)
    {
        var cube = state.PinchedCube;
        state.PinchedCube = null;
        PinchEnded?.Invoke(cube, state.Hand);
    }

    /// <summary>
    /// Combines the runtime pinch flag with a skeleton-based thumb/index distance test, with
    /// hysteresis so a pinch doesn't flicker on and off at the threshold.
    /// </summary>
    void UpdatePinch(HandState state)
    {
        bool systemPinch = state.Hand.GetFingerIsPinching(OVRHand.HandFinger.Index);

        bool geometricPinch = false;
        if (TryGetBone(state.Skeleton, Bone.ThumbTip, out var thumb) &&
            TryGetBone(state.Skeleton, Bone.IndexTip, out var index))
        {
            float threshold = state.Pinching ? _pinchEndDistance : _pinchStartDistance;
            geometricPinch = Vector3.Distance(thumb.position, index.position) <= threshold;
        }

        bool pinching = systemPinch || geometricPinch;
        if (pinching && !state.Pinching)
            state.PinchStartTime = Time.time;

        state.Pinching = pinching;
    }

    /// <summary>The cube closest to any point on the hand, if one is within the grab margin.</summary>
    GrabbableCube FindNearestCube(HandState state)
    {
        _contactPoints.Clear();
        if (TryGetBone(state.Skeleton, Bone.ThumbTip, out var thumb)) _contactPoints.Add(thumb.position);
        if (TryGetBone(state.Skeleton, Bone.IndexTip, out var index)) _contactPoints.Add(index.position);
        if (TryGetBone(state.Skeleton, Bone.MiddleTip, out var middle)) _contactPoints.Add(middle.position);
        if (thumb != null && index != null) _contactPoints.Add((thumb.position + index.position) * 0.5f);
        if (TryGetPalmCenter(state.Skeleton, out var palm)) _contactPoints.Add(palm);
        if (_contactPoints.Count == 0)
            _contactPoints.Add(state.Hand.transform.position);

        GrabbableCube nearest = null;
        float nearestSqr = _grabMargin * _grabMargin;
        foreach (var cube in _cubes.Values)
        {
            foreach (var point in _contactPoints)
            {
                // ClosestPoint returns the point itself when it is inside the cube, so that counts too.
                float sqr = (cube.Collider.ClosestPoint(point) - point).sqrMagnitude;
                if (sqr <= nearestSqr)
                {
                    nearestSqr = sqr;
                    nearest = cube;
                }
            }
        }

        return nearest;
    }

    enum Bone { ThumbTip, IndexTip, MiddleTip, Wrist, MiddleKnuckle, Palm }

    /// <summary>Maps to the bone ids of whichever hand skeleton (OVR or OpenXR) is in use.</summary>
    static OVRSkeleton.BoneId ToBoneId(Bone bone, bool openXR)
    {
        switch (bone)
        {
            case Bone.ThumbTip: return openXR ? OVRSkeleton.BoneId.XRHand_ThumbTip : OVRSkeleton.BoneId.Hand_ThumbTip;
            case Bone.IndexTip: return openXR ? OVRSkeleton.BoneId.XRHand_IndexTip : OVRSkeleton.BoneId.Hand_IndexTip;
            case Bone.MiddleTip: return openXR ? OVRSkeleton.BoneId.XRHand_MiddleTip : OVRSkeleton.BoneId.Hand_MiddleTip;
            case Bone.Wrist: return openXR ? OVRSkeleton.BoneId.XRHand_Wrist : OVRSkeleton.BoneId.Hand_WristRoot;
            case Bone.MiddleKnuckle: return openXR ? OVRSkeleton.BoneId.XRHand_MiddleProximal : OVRSkeleton.BoneId.Hand_Middle1;
            default: return openXR ? OVRSkeleton.BoneId.XRHand_Palm : OVRSkeleton.BoneId.Invalid;
        }
    }

    static bool TryGetBone(OVRSkeleton skeleton, Bone bone, out Transform result)
    {
        result = null;
        if (skeleton == null || !skeleton.IsInitialized || !skeleton.IsDataValid)
            return false;

        var id = ToBoneId(bone, skeleton.GetSkeletonType().IsOpenXRHandSkeleton());
        if (id == OVRSkeleton.BoneId.Invalid)
            return false;

        foreach (var b in skeleton.Bones)
        {
            if (b.Id == id)
            {
                result = b.Transform;
                return result != null;
            }
        }

        return false;
    }

    static bool TryGetPalmCenter(OVRSkeleton skeleton, out Vector3 palm)
    {
        if (TryGetBone(skeleton, Bone.Palm, out var palmBone))
        {
            palm = palmBone.position;
            return true;
        }

        // The OVR skeleton has no palm bone: approximate it between the wrist and middle knuckle.
        if (TryGetBone(skeleton, Bone.Wrist, out var wrist) && TryGetBone(skeleton, Bone.MiddleKnuckle, out var knuckle))
        {
            palm = (wrist.position + knuckle.position) * 0.5f;
            return true;
        }

        palm = default;
        return false;
    }
}
