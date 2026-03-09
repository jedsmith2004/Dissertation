using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime SMPL motion player. Loads a generated .smpl.json (bone rotations)
/// or raw generated .json (joint positions) and drives a Humanoid character
/// each frame. No FBX pipeline required.
///
/// Usage:
///   1. Add this component to a GameObject with a Humanoid Animator.
///   2. Drag any generated TextAsset (.smpl.json or .json) into "Motion Json".
///   3. Press Play.
/// </summary>
public class SMPLMotionPlayer : MonoBehaviour
{
    [Header("Inputs")]
    [Tooltip("Humanoid Animator on this character. Auto-detected if left empty.")]
    [SerializeField] private Animator animator;

    [Tooltip("Drag the generated .smpl.json or raw .json TextAsset here.")]
    [SerializeField] private TextAsset motionJson;

    [Header("Playback")]
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private bool loop = true;
    [Range(0.1f, 3f)]
    [SerializeField] private float playbackSpeed = 1f;
    [SerializeField] private bool applyRootTranslation = true;
    [SerializeField] private bool applyRootRotation = true;

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = true;

    // ── Runtime state ──
    private MotionData _data;
    private readonly Dictionary<string, Transform> _boneCache = new();
    private readonly Dictionary<string, Quaternion> _bindPose = new();
    private float _time;
    private bool _isPlaying;
    private int _lastAppliedFrame = -1;
    private bool _animatorWasEnabled;

    // ──────────────────── Unity lifecycle ────────────────────

    private void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator == null)
        {
            Debug.LogError($"[SMPLPlayer] No Animator found on '{name}' or its children. Add an Animator set to Humanoid.");
            enabled = false;
            return;
        }

        if (!animator.isHuman)
        {
            Debug.LogError($"[SMPLPlayer] Animator on '{animator.gameObject.name}' is NOT Humanoid. Set Rig → Animation Type = Humanoid on the FBX.");
            enabled = false;
            return;
        }

        BuildBoneCache();
        LoadMotion();
    }

    private void Start()
    {
        if (playOnStart && _data != null)
        {
            StartPlayback();
        }
    }

    private void OnDisable()
    {
        RestoreAnimator();
    }

    private void OnDestroy()
    {
        RestoreAnimator();
    }

    private void StartPlayback()
    {
        // CRITICAL: Disable the Animator so it stops overwriting bone rotations.
        _animatorWasEnabled = animator.enabled;
        animator.enabled = false;
        Log($"Disabled Animator on '{animator.gameObject.name}' to prevent it from fighting SMPL playback.");

        // Store bind pose so we can restore when done.
        CaptureBindPose();

        _time = 0f;
        _lastAppliedFrame = -1;
        _isPlaying = true;
        Log($"Playback started: {_data.frameCount} frames @ {_data.fps} fps, duration {_data.Duration:F2}s");
    }

    private void RestoreAnimator()
    {
        if (animator != null && _animatorWasEnabled)
        {
            // Restore bind pose.
            foreach (var kv in _bindPose)
            {
                if (_boneCache.TryGetValue(kv.Key, out var bone) && bone != null)
                    bone.localRotation = kv.Value;
            }

            animator.enabled = true;
            Log("Restored Animator.");
        }
    }

    private void CaptureBindPose()
    {
        _bindPose.Clear();
        foreach (var kv in _boneCache)
        {
            if (kv.Value != null)
                _bindPose[kv.Key] = kv.Value.localRotation;
        }
    }

    private void LateUpdate()
    {
        if (!_isPlaying || _data == null || _data.frameCount == 0)
            return;

        _time += Time.deltaTime * playbackSpeed;

        if (_time >= _data.Duration)
        {
            if (loop)
                _time %= _data.Duration;
            else
            {
                _time = _data.Duration;
                _isPlaying = false;
            }
        }

        var frameIndex = Mathf.Clamp(Mathf.FloorToInt(_time * _data.fps), 0, _data.frameCount - 1);
        ApplyFrame(frameIndex);
        _lastAppliedFrame = frameIndex;
    }

    // ──────────────────── Public API ────────────────────

    [ContextMenu("Reload Motion")]
    public void Reload()
    {
        BuildBoneCache();
        LoadMotion();
        _time = 0f;
        _lastAppliedFrame = -1;
        _isPlaying = playOnStart && _data != null;
    }

    [ContextMenu("Play")]
    public void Play()
    {
        if (_data != null)
            StartPlayback();
    }

    [ContextMenu("Stop")]
    public void Stop()
    {
        _isPlaying = false;
        RestoreAnimator();
    }

    public void ApplyFrame(int frameIndex)
    {
        if (_data == null) return;

        if (_data.hasBoneRotations)
            ApplyBoneRotationFrame(frameIndex);
        else if (_data.hasJointPositions)
            ApplyJointPositionFrame(frameIndex);
    }

    // ──────────────────── Bone cache ────────────────────

    private void BuildBoneCache()
    {
        _boneCache.Clear();

        var mapping = GetSmplToHumanoidMapping();
        int found = 0;

        foreach (var kv in mapping)
        {
            var t = animator.GetBoneTransform(kv.Value);
            if (t != null)
            {
                _boneCache[kv.Key] = t;
                found++;
            }
            else if (verboseLogging)
            {
                Debug.LogWarning($"[SMPLPlayer] Bone '{kv.Key}' → {kv.Value} not found on avatar.");
            }
        }

        Log($"Bone cache built: {found}/{mapping.Count} bones mapped on '{animator.gameObject.name}'.");
    }

    // ──────────────────── Motion loading ────────────────────

    private void LoadMotion()
    {
        _data = null;

        if (motionJson == null || string.IsNullOrWhiteSpace(motionJson.text))
        {
            Debug.LogError("[SMPLPlayer] No motion JSON assigned! Drag a .smpl.json or generated .json TextAsset into the 'Motion Json' slot.");
            return;
        }

        try
        {
            var text = motionJson.text;

            // Try SMPL sidecar format first (has "bones" array).
            if (text.Contains("\"bones\""))
            {
                var smpl = JsonUtility.FromJson<SmplJson>(text);
                if (smpl?.frames != null && smpl.frames.Length > 0 && smpl.frames[0].bones != null && smpl.frames[0].bones.Length > 0)
                {
                    _data = MotionData.FromSmpl(smpl);
                    Log($"Loaded SMPL sidecar: {_data.frameCount} frames @ {_data.fps} fps, {smpl.frames[0].bones.Length} bones/frame. hasBones=true");
                    return;
                }
            }

            // Fallback: raw generated JSON (has "joints" array with positions).
            if (text.Contains("\"joints\""))
            {
                var raw = JsonUtility.FromJson<RawGeneratedJson>(text);
                if (raw?.frames != null && raw.frames.Length > 0)
                {
                    _data = MotionData.FromRaw(raw);
                    Log($"Loaded raw generation JSON: {_data.frameCount} frames @ {_data.fps} fps, using joint positions → rotation retarget.");
                    return;
                }
            }

            Debug.LogError("[SMPLPlayer] Could not parse motion JSON. Expected either SMPL sidecar (bones[]) or raw generated (joints[]) format.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SMPLPlayer] JSON parse failed: {ex.Message}");
        }
    }

    // ──────────────────── Frame application: bone rotations ────────────────────

    private void ApplyBoneRotationFrame(int frameIndex)
    {
        var frame = _data.smplFrames[frameIndex];

        if (applyRootTranslation && frame.trans != null)
            transform.localPosition = new Vector3(frame.trans.x, frame.trans.y, frame.trans.z);

        if (applyRootRotation && frame.rootRotation != null)
        {
            var rq = frame.rootRotation;
            transform.localRotation = new Quaternion(rq.x, rq.y, rq.z, rq.w);
        }

        if (frame.bones == null) return;

        for (int i = 0; i < frame.bones.Length; i++)
        {
            var br = frame.bones[i];
            if (br?.rotation == null || string.IsNullOrEmpty(br.name))
                continue;

            if (!_boneCache.TryGetValue(br.name, out var bone) || bone == null)
                continue;

            bone.localRotation = new Quaternion(br.rotation.x, br.rotation.y, br.rotation.z, br.rotation.w);
        }
    }

    // ──────────────────── Frame application: joint positions (retarget) ────────────────────

    private void ApplyJointPositionFrame(int frameIndex)
    {
        var frame = _data.rawFrames[frameIndex];

        if (applyRootTranslation && frame.position != null)
            transform.localPosition = new Vector3(frame.position.x, frame.position.y, -frame.position.z);

        if (frame.joints == null || frame.joints.Length < 22)
            return;

        // Convert joint positions from T2M space (Z-forward) to Unity (Z-back).
        var joints = new Vector3[frame.joints.Length];
        for (int j = 0; j < joints.Length; j++)
        {
            var v = frame.joints[j];
            joints[j] = new Vector3(v.x, v.y, -v.z);
        }

        // Fix: T2M-GPT root joint (0) Y can be unreliable; use average of
        // hip joints (1=R_hip, 2=L_hip) which have correct absolute height.
        if (joints.Length >= 3)
            joints[0] = new Vector3(joints[0].x, (joints[1].y + joints[2].y) / 2f, joints[0].z);

        // Direction-based retarget: for each bone, compute rotation from bind→target direction.
        var limbMaps = GetT2MJointChainMap();
        foreach (var lm in limbMaps)
        {
            if (!_boneCache.TryGetValue(lm.smplName, out var bone) || bone == null)
                continue;

            if (lm.jointIdx < 0 || lm.childJointIdx < 0 ||
                lm.jointIdx >= joints.Length || lm.childJointIdx >= joints.Length)
                continue;

            var from = joints[lm.jointIdx];
            var to = joints[lm.childJointIdx];
            var targetDir = (to - from);
            if (targetDir.sqrMagnitude < 1e-8f)
                continue;

            var parentRot = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            var bindWorldDir = parentRot * lm.bindLocalDir;
            var delta = Quaternion.FromToRotation(bindWorldDir, targetDir.normalized);
            var worldRot = delta * parentRot * bone.localRotation;

            bone.rotation = Quaternion.Slerp(bone.rotation, worldRot, 0.6f);
        }
    }

    // ──────────────────── Mappings ────────────────────

    private static Dictionary<string, HumanBodyBones> GetSmplToHumanoidMapping()
    {
        return new Dictionary<string, HumanBodyBones>
        {
            { "pelvis",          HumanBodyBones.Hips },
            { "spine1",          HumanBodyBones.Spine },
            { "spine2",          HumanBodyBones.Chest },
            { "neck",            HumanBodyBones.Neck },
            { "head",            HumanBodyBones.Head },

            { "left_hip",        HumanBodyBones.LeftUpperLeg },
            { "left_knee",       HumanBodyBones.LeftLowerLeg },
            { "left_ankle",      HumanBodyBones.LeftFoot },

            { "right_hip",       HumanBodyBones.RightUpperLeg },
            { "right_knee",      HumanBodyBones.RightLowerLeg },
            { "right_ankle",     HumanBodyBones.RightFoot },

            { "left_shoulder",   HumanBodyBones.LeftUpperArm },
            { "left_elbow",      HumanBodyBones.LeftLowerArm },
            { "left_wrist",      HumanBodyBones.LeftHand },

            { "right_shoulder",  HumanBodyBones.RightUpperArm },
            { "right_elbow",     HumanBodyBones.RightLowerArm },
            { "right_wrist",     HumanBodyBones.RightHand },
        };
    }

    private struct JointChainEntry
    {
        public string smplName;
        public int jointIdx;
        public int childJointIdx;
        public Vector3 bindLocalDir;

        public JointChainEntry(string n, int j, int c, Vector3 d) { smplName = n; jointIdx = j; childJointIdx = c; bindLocalDir = d; }
    }

    private static JointChainEntry[] GetT2MJointChainMap()
    {
        // T2M 22 joint indices → SMPL bone name, with approximate bind-pose local direction.
        return new[]
        {
            new JointChainEntry("pelvis",         0,  3, Vector3.up),

            new JointChainEntry("right_hip",      1,  4, Vector3.down),
            new JointChainEntry("right_knee",     4,  7, Vector3.down),
            new JointChainEntry("right_ankle",    7, 10, Vector3.forward),

            new JointChainEntry("left_hip",       2,  5, Vector3.down),
            new JointChainEntry("left_knee",      5,  8, Vector3.down),
            new JointChainEntry("left_ankle",     8, 11, Vector3.forward),

            new JointChainEntry("spine1",         3,  6, Vector3.up),
            new JointChainEntry("spine2",         6,  9, Vector3.up),
            new JointChainEntry("neck",          12, 15, Vector3.up),

            new JointChainEntry("left_shoulder", 13, 16, Vector3.left),
            new JointChainEntry("left_elbow",    16, 18, Vector3.left),

            new JointChainEntry("right_shoulder",14, 17, Vector3.right),
            new JointChainEntry("right_elbow",   17, 19, Vector3.right),
        };
    }

    // ──────────────────── Logging ────────────────────

    private void Log(string msg)
    {
        if (verboseLogging)
            Debug.Log($"[SMPLPlayer] {msg}");
    }

    // ──────────────────── Data model ────────────────────

    private class MotionData
    {
        public int fps;
        public int frameCount;
        public bool hasBoneRotations;
        public bool hasJointPositions;

        public SmplFrame[] smplFrames;
        public RawFrame[] rawFrames;

        public float Duration => frameCount / Mathf.Max(1f, fps);

        public static MotionData FromSmpl(SmplJson smpl)
        {
            return new MotionData
            {
                fps = Mathf.Max(1, smpl.fps),
                frameCount = smpl.frames.Length,
                hasBoneRotations = true,
                hasJointPositions = false,
                smplFrames = smpl.frames,
            };
        }

        public static MotionData FromRaw(RawGeneratedJson raw)
        {
            return new MotionData
            {
                fps = Mathf.Max(1, raw.fps),
                frameCount = raw.frames.Length,
                hasBoneRotations = false,
                hasJointPositions = true,
                rawFrames = raw.frames,
            };
        }
    }

    // ── SMPL sidecar format ──

    [Serializable] private class SmplJson       { public int fps; public SmplFrame[] frames; }
    [Serializable] private class SmplFrame      { public Vec3 trans; public Quat rootRotation; public BoneRot[] bones; }
    [Serializable] private class BoneRot        { public string name; public Quat rotation; }

    // ── Raw generated JSON format ──

    [Serializable] private class RawGeneratedJson { public int fps; public RawFrame[] frames; }
    [Serializable] private class RawFrame         { public Vec3 position; public Vec3 rotationEuler; public Vec3[] joints; }

    // ── Shared primitives ──

    [Serializable] private class Vec3  { public float x; public float y; public float z; }
    [Serializable] private class Quat  { public float x; public float y; public float z; public float w; }
}
