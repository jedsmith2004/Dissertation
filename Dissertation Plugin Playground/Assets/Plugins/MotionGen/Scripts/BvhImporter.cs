// ──────────────────────────────────────────────────────────────────────
// BvhImporter.cs — Parse a BVH file and create a Humanoid AnimationClip
// ──────────────────────────────────────────────────────────────────────
// Replaces the broken ModelImporter pipeline with a direct C# approach:
//   1.  Parse BVH text  → joint hierarchy + per-frame Euler channels
//   2.  Build temporary skeleton (GameObjects)
//   3.  AvatarBuilder.BuildHumanAvatar  → Humanoid Avatar
//   4.  HumanPoseHandler  → sample muscle values each frame
//   5.  Bake muscle + root curves into an AnimationClip
//
// This works with the 22-joint SMPL/HumanML3D BVH produced by bvh_writer.py.
// The BVH is assumed to be right-handed Y-up; we convert to Unity's
// left-handed Y-up on import.
// ──────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MotionGen
{
    /// <summary>
    /// Parses a BVH file and converts it into a Humanoid AnimationClip
    /// that can be retargeted onto any Unity Humanoid character.
    /// </summary>
    public static class BvhImporter
    {
        // ─────────────────────────── Data model ───────────────────────────

        public class BvhJoint
        {
            public string Name;
            public Vector3 Offset;                 // OFFSET in BVH coords (right-handed)
            public List<string> Channels = new();  // e.g. ["Zrotation","Yrotation","Xrotation"]
            public int ChannelOffset;              // index into the per-frame float array
            public List<BvhJoint> Children = new();
            public BvhJoint Parent;
            public bool IsEndSite;
        }

        public class BvhClip
        {
            public BvhJoint Root;
            public List<BvhJoint> JointsDfsOrder = new(); // every JOINT in DFS order (excludes End Sites)
            public int TotalChannels;
            public int FrameCount;
            public float FrameTime;                // seconds per frame
            public float[][] Frames;               // [frame][channel]
        }

        // ─────────────────────────── Parsing ──────────────────────────────

        /// <summary>Parse a BVH text string into a <see cref="BvhClip"/>.</summary>
        public static BvhClip Parse(string bvhText)
        {
            var lines = bvhText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            int idx = 0;

            // Skip to HIERARCHY
            while (idx < lines.Length && !lines[idx].Trim().StartsWith("HIERARCHY", StringComparison.OrdinalIgnoreCase))
                idx++;
            idx++; // skip HIERARCHY line

            int channelOffset = 0;
            var clip = new BvhClip();
            clip.Root = ParseJoint(lines, ref idx, ref channelOffset, null, clip.JointsDfsOrder);
            clip.TotalChannels = channelOffset;

            // MOTION section
            while (idx < lines.Length && !lines[idx].Trim().StartsWith("MOTION", StringComparison.OrdinalIgnoreCase))
                idx++;
            idx++; // skip MOTION

            // Frames:
            while (idx < lines.Length)
            {
                var trimmed = lines[idx].Trim();
                if (trimmed.StartsWith("Frames:", StringComparison.OrdinalIgnoreCase))
                {
                    clip.FrameCount = int.Parse(trimmed.Substring("Frames:".Length).Trim(), CultureInfo.InvariantCulture);
                    idx++;
                    break;
                }
                idx++;
            }

            // Frame Time:
            while (idx < lines.Length)
            {
                var trimmed = lines[idx].Trim();
                if (trimmed.StartsWith("Frame Time:", StringComparison.OrdinalIgnoreCase))
                {
                    clip.FrameTime = float.Parse(trimmed.Substring("Frame Time:".Length).Trim(), CultureInfo.InvariantCulture);
                    idx++;
                    break;
                }
                idx++;
            }

            // Frame data
            clip.Frames = new float[clip.FrameCount][];
            int f = 0;
            while (idx < lines.Length && f < clip.FrameCount)
            {
                var trimmed = lines[idx].Trim();
                if (string.IsNullOrEmpty(trimmed)) { idx++; continue; }

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var vals = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    vals[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);

                clip.Frames[f] = vals;
                f++;
                idx++;
            }

            return clip;
        }

        private static BvhJoint ParseJoint(string[] lines, ref int idx, ref int channelOffset, BvhJoint parent, List<BvhJoint> dfsOrder)
        {
            // Current line should be ROOT/JOINT/End Site
            var trimmed = lines[idx].Trim();
            idx++;

            var joint = new BvhJoint { Parent = parent };

            if (trimmed.StartsWith("End Site", StringComparison.OrdinalIgnoreCase))
            {
                joint.IsEndSite = true;
                joint.Name = parent.Name + "_End";
                // Skip { ... }
                while (idx < lines.Length && !lines[idx].Trim().StartsWith("{")) idx++;
                idx++; // skip {
                // Read OFFSET
                while (idx < lines.Length)
                {
                    var line = lines[idx].Trim();
                    idx++;
                    if (line.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = line.Substring("OFFSET".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        joint.Offset = new Vector3(
                            float.Parse(p[0], CultureInfo.InvariantCulture),
                            float.Parse(p[1], CultureInfo.InvariantCulture),
                            float.Parse(p[2], CultureInfo.InvariantCulture));
                    }
                    if (line.StartsWith("}")) break;
                }
                return joint;
            }

            // ROOT or JOINT
            if (trimmed.StartsWith("ROOT", StringComparison.OrdinalIgnoreCase))
                joint.Name = trimmed.Substring("ROOT".Length).Trim();
            else if (trimmed.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase))
                joint.Name = trimmed.Substring("JOINT".Length).Trim();

            // Skip {
            while (idx < lines.Length && !lines[idx].Trim().StartsWith("{")) idx++;
            idx++;

            // Read contents until matching }
            while (idx < lines.Length)
            {
                var line = lines[idx].Trim();

                if (line.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                {
                    var p = line.Substring("OFFSET".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    joint.Offset = new Vector3(
                        float.Parse(p[0], CultureInfo.InvariantCulture),
                        float.Parse(p[1], CultureInfo.InvariantCulture),
                        float.Parse(p[2], CultureInfo.InvariantCulture));
                    idx++;
                }
                else if (line.StartsWith("CHANNELS", StringComparison.OrdinalIgnoreCase))
                {
                    var p = line.Substring("CHANNELS".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int numCh = int.Parse(p[0], CultureInfo.InvariantCulture);
                    joint.ChannelOffset = channelOffset;
                    for (int c = 0; c < numCh; c++)
                        joint.Channels.Add(p[1 + c]);
                    channelOffset += numCh;
                    idx++;
                }
                else if (line.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("End Site", StringComparison.OrdinalIgnoreCase))
                {
                    var child = ParseJoint(lines, ref idx, ref channelOffset, joint, dfsOrder);
                    joint.Children.Add(child);
                }
                else if (line.StartsWith("ROOT", StringComparison.OrdinalIgnoreCase))
                {
                    // Shouldn't happen inside another joint, but handle gracefully.
                    var child = ParseJoint(lines, ref idx, ref channelOffset, joint, dfsOrder);
                    joint.Children.Add(child);
                }
                else if (line.StartsWith("}"))
                {
                    idx++;
                    break;
                }
                else
                {
                    idx++;
                }
            }

            dfsOrder.Add(joint);
            // Re-order: we added the joint after its children (post-order). We need pre-order (DFS).
            // Fix: move the joint from the end to just before where its children were added.
            // Actually let's fix this by inserting at the right position.
            // Simpler: we'll rebuild DFS order after parsing.
            return joint;
        }

        /// <summary>Rebuild the DFS-order list (pre-order) from the parsed tree.</summary>
        private static List<BvhJoint> RebuildDfsOrder(BvhJoint root)
        {
            var list = new List<BvhJoint>();
            void Visit(BvhJoint j)
            {
                if (j.IsEndSite) return;
                list.Add(j);
                foreach (var c in j.Children)
                    Visit(c);
            }
            Visit(root);
            return list;
        }

        // ──────────────────── Coordinate conversion helpers ───────────────

        /// <summary>
        /// Convert a BVH position (right-handed Y-up) to Unity (left-handed Y-up).
        /// We mirror the X axis.  This keeps the forward direction (+Z) intact
        /// and correctly swaps left/right so that the character faces Unity's +Z.
        /// </summary>
        private static Vector3 BvhPosToUnity(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        /// <summary>
        /// Convert BVH ZYX-intrinsic Euler angles (degrees) from a right-handed system
        /// to a Unity Quaternion (left-handed) using X-axis mirroring.
        ///
        /// Derivation:
        ///   Let M = diag(-1,1,1).  For a RH rotation R = Rz(α)·Ry(β)·Rx(γ),
        ///   the LH equivalent is  M·R·M = Rz(-α)·Ry(-β)·Rx(γ).
        /// </summary>
        private static Quaternion BvhEulerToUnityQuat(float zDeg, float yDeg, float xDeg)
        {
            Quaternion qx = Quaternion.AngleAxis(xDeg, Vector3.right);    // X unchanged
            Quaternion qy = Quaternion.AngleAxis(-yDeg, Vector3.up);      // Y negated
            Quaternion qz = Quaternion.AngleAxis(-zDeg, Vector3.forward); // Z negated
            return qz * qy * qx;
        }

        // ──────────────── Build skeleton GameObjects ──────────────────────

        /// <summary>
        /// Create a temporary hierarchy of GameObjects matching the BVH skeleton.
        /// Each joint becomes a child Transform positioned at its OFFSET (converted to Unity coords).
        /// Returns a map from joint name → Transform.
        /// </summary>
        private static GameObject BuildSkeleton(BvhClip clip, out Dictionary<string, Transform> boneMap)
        {
            var map = new Dictionary<string, Transform>();

            // Create outermost container (this is the "model root" that AvatarBuilder expects).
            var container = new GameObject("BVH_Skeleton");

            // Recursively create children.
            void Create(BvhJoint joint, Transform parent)
            {
                if (joint.IsEndSite) return;

                var go = new GameObject(joint.Name);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = BvhPosToUnity(joint.Offset);
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                map[joint.Name] = go.transform;

                foreach (var child in joint.Children)
                    Create(child, go.transform);
            }

            Create(clip.Root, container.transform);
            boneMap = map;
            return container;
        }

        // ──────────────── HumanBone mapping (BVH ↔ Humanoid) ─────────────

        private static readonly (string bvh, string human)[] BoneMapping = new[]
        {
            ("Pelvis",      "Hips"),
            ("L_Hip",       "LeftUpperLeg"),
            ("R_Hip",       "RightUpperLeg"),
            ("Spine1",      "Spine"),
            ("L_Knee",      "LeftLowerLeg"),
            ("R_Knee",      "RightLowerLeg"),
            ("Spine2",      "Chest"),
            ("L_Ankle",     "LeftFoot"),
            ("R_Ankle",     "RightFoot"),
            ("Spine3",      "UpperChest"),
            ("L_Foot",      "LeftToes"),
            ("R_Foot",      "RightToes"),
            ("Neck",        "Neck"),
            ("L_Collar",    "LeftShoulder"),
            ("R_Collar",    "RightShoulder"),
            ("Head",        "Head"),
            ("L_Shoulder",  "LeftUpperArm"),
            ("R_Shoulder",  "RightUpperArm"),
            ("L_Elbow",     "LeftLowerArm"),
            ("R_Elbow",     "RightLowerArm"),
            ("L_Wrist",     "LeftHand"),
            ("R_Wrist",     "RightHand"),
        };

        private static HumanBone[] MakeHumanBones()
        {
            var bones = new HumanBone[BoneMapping.Length];
            for (int i = 0; i < BoneMapping.Length; i++)
            {
                bones[i] = new HumanBone
                {
                    boneName = BoneMapping[i].bvh,
                    humanName = BoneMapping[i].human,
                    limit = new HumanLimit { useDefaultValues = true },
                };
            }
            return bones;
        }

        private static SkeletonBone[] MakeSkeletonBones(GameObject skeletonRoot)
        {
            var transforms = skeletonRoot.GetComponentsInChildren<Transform>(true);
            var bones = new SkeletonBone[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                bones[i] = new SkeletonBone
                {
                    name = transforms[i].name,
                    position = transforms[i].localPosition,
                    rotation = transforms[i].localRotation,
                    scale = transforms[i].localScale,
                };
            }
            return bones;
        }

        // ──────────────── Apply one BVH frame to skeleton ─────────────────

        /// <summary>
        /// Set each bone's localPosition/localRotation from the given BVH frame.
        /// Root gets its position channels; all joints get rotation channels.
        /// Coordinates are converted from right-handed to Unity left-handed.
        /// </summary>
        private static void ApplyFrame(BvhClip clip, int frame, Dictionary<string, Transform> boneMap)
        {
            var data = clip.Frames[frame];

            foreach (var joint in clip.JointsDfsOrder)
            {
                if (!boneMap.TryGetValue(joint.Name, out var t))
                    continue;

                int off = joint.ChannelOffset;

                if (joint.Channels.Count == 6)
                {
                    // Root joint: Xposition Yposition Zposition Zrotation Yrotation Xrotation
                    float px = data[off + 0];
                    float py = data[off + 1];
                    float pz = data[off + 2];
                    t.localPosition = BvhPosToUnity(new Vector3(px, py, pz));

                    float zr = data[off + 3];
                    float yr = data[off + 4];
                    float xr = data[off + 5];
                    t.localRotation = BvhEulerToUnityQuat(zr, yr, xr);
                }
                else if (joint.Channels.Count == 3)
                {
                    // Regular joint: Zrotation Yrotation Xrotation
                    float zr = data[off + 0];
                    float yr = data[off + 1];
                    float xr = data[off + 2];
                    t.localRotation = BvhEulerToUnityQuat(zr, yr, xr);
                }
            }
        }

        // ──────────────────── Main public entry point ─────────────────────

        /// <summary>
        /// Import a BVH text string as a Humanoid AnimationClip.
        ///
        /// The clip stores muscle curves extracted via <see cref="HumanPoseHandler"/>,
        /// so it can be retargeted to any Unity Humanoid character.
        ///
        /// Returns (clip, avatar).  The caller should save both as assets if needed.
        /// Returns (null, null) on failure.
        /// </summary>
        public static (AnimationClip clip, Avatar avatar) ImportAsHumanoid(string bvhText)
        {
            // ── 1.  Parse ──
            BvhClip bvh;
            try
            {
                bvh = Parse(bvhText);
                // Fix DFS order (the parser adds in post-order, we need pre-order).
                bvh.JointsDfsOrder = RebuildDfsOrder(bvh.Root);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BvhImporter] Parse failed: {ex.Message}\n{ex.StackTrace}");
                return (null, null);
            }

            Debug.Log($"[BvhImporter] Parsed BVH: {bvh.JointsDfsOrder.Count} joints, {bvh.FrameCount} frames, {bvh.FrameTime:F6}s/frame ({1f / bvh.FrameTime:F1} fps), {bvh.TotalChannels} channels");

            // ── 2.  Build temporary skeleton ──
            var skeletonRoot = BuildSkeleton(bvh, out var boneMap);

            // ── 3.  Build Humanoid Avatar ──
            var desc = new HumanDescription
            {
                human = MakeHumanBones(),
                skeleton = MakeSkeletonBones(skeletonRoot),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(skeletonRoot, desc);
            if (avatar == null || !avatar.isValid)
            {
                Debug.LogError("[BvhImporter] AvatarBuilder.BuildHumanAvatar failed — avatar is null or invalid.");
                UnityEngine.Object.DestroyImmediate(skeletonRoot);
                return (null, null);
            }
            if (!avatar.isHuman)
            {
                Debug.LogError("[BvhImporter] Avatar built but is not Humanoid. Check bone mapping.");
                UnityEngine.Object.DestroyImmediate(skeletonRoot);
                return (null, null);
            }

            avatar.name = "BVH_Avatar";
            Debug.Log("[BvhImporter] Humanoid Avatar built successfully.");

            // ── 4.  Sample muscle values per frame via HumanPoseHandler ──
            var handler = new HumanPoseHandler(avatar, skeletonRoot.transform);
            var pose = new HumanPose();

            int numMuscles = HumanTrait.MuscleCount;
            float fps = 1f / Mathf.Max(0.0001f, bvh.FrameTime);

            // Pre-allocate storage.
            var muscleData = new float[bvh.FrameCount][];
            var bodyPositions = new Vector3[bvh.FrameCount];
            var bodyRotations = new Quaternion[bvh.FrameCount];

            for (int f = 0; f < bvh.FrameCount; f++)
            {
                // Reset skeleton to rest pose before applying frame.
                ResetToRestPose(bvh, boneMap);

                // Apply the BVH frame data.
                ApplyFrame(bvh, f, boneMap);

                // Read back humanoid pose.
                handler.GetHumanPose(ref pose);

                bodyPositions[f] = pose.bodyPosition;
                bodyRotations[f] = pose.bodyRotation;
                muscleData[f] = (float[])pose.muscles.Clone();
            }

            Debug.Log($"[BvhImporter] Sampled {bvh.FrameCount} frames × {numMuscles} muscles.");

            // ── 5.  Build the AnimationClip ──
            var clip = new AnimationClip
            {
                frameRate = fps,
                legacy = false,
            };

            // --- Muscle curves ---
            for (int m = 0; m < numMuscles; m++)
            {
                var curve = new AnimationCurve();
                for (int f = 0; f < bvh.FrameCount; f++)
                    curve.AddKey(new Keyframe(f * bvh.FrameTime, muscleData[f][m]));

                // The property name for humanoid muscle curves uses the muscle name directly.
                var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), GetMusclePropertyName(m));
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            // --- Root body position ---
            SetAnimatorCurve(clip, "RootT.x", bvh, bodyPositions, p => p.x);
            SetAnimatorCurve(clip, "RootT.y", bvh, bodyPositions, p => p.y);
            SetAnimatorCurve(clip, "RootT.z", bvh, bodyPositions, p => p.z);

            // --- Root body rotation ---
            SetAnimatorCurve(clip, "RootQ.x", bvh, bodyRotations, q => q.x);
            SetAnimatorCurve(clip, "RootQ.y", bvh, bodyRotations, q => q.y);
            SetAnimatorCurve(clip, "RootQ.z", bvh, bodyRotations, q => q.z);
            SetAnimatorCurve(clip, "RootQ.w", bvh, bodyRotations, q => q.w);

            clip.EnsureQuaternionContinuity();

            // ── 6.  Cleanup ──
            handler.Dispose();
            UnityEngine.Object.DestroyImmediate(skeletonRoot);

            Debug.Log($"[BvhImporter] AnimationClip created: {clip.length:F2}s, {fps:F1} fps, {numMuscles} muscle channels.");
            return (clip, avatar);
        }

        // ──────────────── Helpers ─────────────────────────────────────────

        /// <summary>Reset all bones to their rest-pose (offset position, identity rotation).</summary>
        private static void ResetToRestPose(BvhClip bvh, Dictionary<string, Transform> boneMap)
        {
            foreach (var joint in bvh.JointsDfsOrder)
            {
                if (!boneMap.TryGetValue(joint.Name, out var t)) continue;
                t.localPosition = BvhPosToUnity(joint.Offset);
                t.localRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// Map muscle index → the property name Unity uses inside Humanoid AnimationClips.
        /// Unity expects the exact string from <see cref="HumanTrait.MuscleName"/>.
        /// </summary>
        private static string GetMusclePropertyName(int muscleIndex)
        {
            return HumanTrait.MuscleName[muscleIndex];
        }

        private static void SetAnimatorCurve<T>(AnimationClip clip, string property, BvhClip bvh, T[] values, Func<T, float> selector)
        {
            var curve = new AnimationCurve();
            for (int f = 0; f < bvh.FrameCount; f++)
                curve.AddKey(new Keyframe(f * bvh.FrameTime, selector(values[f])));

            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), property);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }
}
