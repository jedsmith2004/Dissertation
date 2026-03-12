#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MotionGen
{
    public static class BvhImporter
    {
        public sealed class BvhJoint
        {
            public string Name;
            public Vector3 Offset;
            public readonly List<string> Channels = new();
            public int ChannelOffset;
            public readonly List<BvhJoint> Children = new();
            public BvhJoint Parent;
            public bool IsEndSite;
        }

        public sealed class BvhClip
        {
            public BvhJoint Root;
            public List<BvhJoint> JointsDfsOrder = new();
            public int TotalChannels;
            public int FrameCount;
            public float FrameTime;
            public float[][] Frames;
        }

        private static readonly (string[] bvhAliases, string human)[] BoneMapping =
        {
            (new[] { "Pelvis", "Hips" }, "Hips"),
            (new[] { "L_Hip", "LeftUpLeg" }, "LeftUpperLeg"),
            (new[] { "R_Hip", "RightUpLeg" }, "RightUpperLeg"),
            (new[] { "Spine1", "Spine" }, "Spine"),
            (new[] { "L_Knee", "LeftLeg" }, "LeftLowerLeg"),
            (new[] { "R_Knee", "RightLeg" }, "RightLowerLeg"),
            (new[] { "Spine2", "Spine1" }, "Chest"),
            (new[] { "L_Ankle", "LeftFoot" }, "LeftFoot"),
            (new[] { "R_Ankle", "RightFoot" }, "RightFoot"),
            (new[] { "Spine3", "Spine2" }, "UpperChest"),
            (new[] { "L_Foot", "LeftToe", "LeftToes" }, "LeftToes"),
            (new[] { "R_Foot", "RightToe", "RightToes" }, "RightToes"),
            (new[] { "Neck" }, "Neck"),
            (new[] { "L_Collar", "LeftShoulder" }, "LeftShoulder"),
            (new[] { "R_Collar", "RightShoulder" }, "RightShoulder"),
            (new[] { "Head" }, "Head"),
            (new[] { "L_Shoulder", "LeftArm" }, "LeftUpperArm"),
            (new[] { "R_Shoulder", "RightArm" }, "RightUpperArm"),
            (new[] { "L_Elbow", "LeftForeArm" }, "LeftLowerArm"),
            (new[] { "R_Elbow", "RightForeArm" }, "RightLowerArm"),
            (new[] { "L_Wrist", "LeftHand" }, "LeftHand"),
            (new[] { "R_Wrist", "RightHand" }, "RightHand"),
        };

        public static (AnimationClip clip, Avatar avatar) ImportAsHumanoid(string bvhText)
        {
            BvhClip bvh;
            try
            {
                bvh = Parse(bvhText);
                bvh.JointsDfsOrder = RebuildDfsOrder(bvh.Root);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BvhImporter] Parse failed: {ex.Message}");
                return (null, null);
            }

            var skeletonRoot = BuildSkeleton(bvh, out var boneMap);
            var avatar = BuildAvatar(bvh, skeletonRoot);
            if (avatar == null)
            {
                UnityEngine.Object.DestroyImmediate(skeletonRoot);
                return (null, null);
            }

            var clip = BuildAnimationClip(bvh, avatar, skeletonRoot.transform, boneMap);
            if (clip == null)
            {
                UnityEngine.Object.DestroyImmediate(avatar);
                UnityEngine.Object.DestroyImmediate(skeletonRoot);
                return (null, null);
            }

            UnityEngine.Object.DestroyImmediate(skeletonRoot);
            return (clip, avatar);
        }

        public static BvhClip Parse(string bvhText)
        {
            var lines = bvhText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var clip = new BvhClip();
            int index = 0;

            while (index < lines.Length && !lines[index].Trim().StartsWith("HIERARCHY", StringComparison.OrdinalIgnoreCase))
                index++;
            index++;

            int channelOffset = 0;
            clip.Root = ParseJoint(lines, ref index, ref channelOffset, null);
            clip.TotalChannels = channelOffset;

            while (index < lines.Length && !lines[index].Trim().StartsWith("MOTION", StringComparison.OrdinalIgnoreCase))
                index++;
            index++;

            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("Frames:", StringComparison.OrdinalIgnoreCase))
                {
                    clip.FrameCount = int.Parse(line.Substring("Frames:".Length).Trim(), CultureInfo.InvariantCulture);
                    index++;
                    break;
                }

                index++;
            }

            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("Frame Time:", StringComparison.OrdinalIgnoreCase))
                {
                    clip.FrameTime = float.Parse(line.Substring("Frame Time:".Length).Trim(), CultureInfo.InvariantCulture);
                    index++;
                    break;
                }

                index++;
            }

            clip.Frames = new float[clip.FrameCount][];
            int frameIndex = 0;
            while (index < lines.Length && frameIndex < clip.FrameCount)
            {
                var line = lines[index].Trim();
                index++;
                if (string.IsNullOrEmpty(line))
                    continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var values = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    values[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);

                clip.Frames[frameIndex++] = values;
            }

            return clip;
        }

        private static BvhJoint ParseJoint(string[] lines, ref int index, ref int channelOffset, BvhJoint parent)
        {
            var header = lines[index].Trim();
            index++;

            var joint = new BvhJoint { Parent = parent };

            if (header.StartsWith("End Site", StringComparison.OrdinalIgnoreCase))
            {
                joint.IsEndSite = true;
                joint.Name = $"{parent?.Name ?? "Joint"}_End";
                SkipToOpeningBrace(lines, ref index);
                index++;
                ReadOffsetBlock(lines, ref index, joint);
                return joint;
            }

            if (header.StartsWith("ROOT", StringComparison.OrdinalIgnoreCase))
                joint.Name = header.Substring("ROOT".Length).Trim();
            else if (header.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase))
                joint.Name = header.Substring("JOINT".Length).Trim();
            else
                throw new FormatException($"Unexpected BVH joint header: {header}");

            SkipToOpeningBrace(lines, ref index);
            index++;

            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Substring("OFFSET".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    joint.Offset = new Vector3(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture));
                    index++;
                }
                else if (line.StartsWith("CHANNELS", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Substring("CHANNELS".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    int channelCount = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    joint.ChannelOffset = channelOffset;
                    for (int i = 0; i < channelCount; i++)
                        joint.Channels.Add(parts[i + 1]);
                    channelOffset += channelCount;
                    index++;
                }
                else if (line.StartsWith("JOINT", StringComparison.OrdinalIgnoreCase) || line.StartsWith("End Site", StringComparison.OrdinalIgnoreCase))
                {
                    joint.Children.Add(ParseJoint(lines, ref index, ref channelOffset, joint));
                }
                else if (line.StartsWith("}"))
                {
                    index++;
                    break;
                }
                else
                {
                    index++;
                }
            }

            return joint;
        }

        private static void SkipToOpeningBrace(string[] lines, ref int index)
        {
            while (index < lines.Length && !lines[index].Trim().StartsWith("{", StringComparison.OrdinalIgnoreCase))
                index++;
        }

        private static void ReadOffsetBlock(string[] lines, ref int index, BvhJoint joint)
        {
            while (index < lines.Length)
            {
                var line = lines[index].Trim();
                index++;
                if (line.StartsWith("OFFSET", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Substring("OFFSET".Length).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    joint.Offset = new Vector3(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture));
                }

                if (line.StartsWith("}"))
                    break;
            }
        }

        private static List<BvhJoint> RebuildDfsOrder(BvhJoint root)
        {
            var ordered = new List<BvhJoint>();

            void Visit(BvhJoint joint)
            {
                if (joint == null || joint.IsEndSite)
                    return;

                ordered.Add(joint);
                foreach (var child in joint.Children)
                    Visit(child);
            }

            Visit(root);
            return ordered;
        }

        private static Vector3 BvhPosToUnity(Vector3 value)
        {
            return new Vector3(-value.x, value.y, value.z);
        }

        private static Quaternion BvhEulerToUnityQuat(float zDeg, float yDeg, float xDeg)
        {
            var qx = Quaternion.AngleAxis(xDeg, Vector3.right);
            var qy = Quaternion.AngleAxis(-yDeg, Vector3.up);
            var qz = Quaternion.AngleAxis(-zDeg, Vector3.forward);
            return qz * qy * qx;
        }

        private static GameObject BuildSkeleton(BvhClip clip, out Dictionary<string, Transform> boneMap)
        {
            var map = new Dictionary<string, Transform>();
            var rootObject = new GameObject("BVH_Skeleton");

            void Create(BvhJoint joint, Transform parent)
            {
                if (joint == null || joint.IsEndSite)
                    return;

                var node = new GameObject(joint.Name);
                node.transform.SetParent(parent, false);
                node.transform.localPosition = BvhPosToUnity(joint.Offset);
                node.transform.localRotation = Quaternion.identity;
                node.transform.localScale = Vector3.one;
                map[joint.Name] = node.transform;

                foreach (var child in joint.Children)
                    Create(child, node.transform);
            }

            Create(clip.Root, rootObject.transform);
            boneMap = map;
            return rootObject;
        }

        private static Avatar BuildAvatar(BvhClip bvh, GameObject skeletonRoot)
        {
            var description = new HumanDescription
            {
                human = MakeHumanBones(bvh),
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

            var avatar = AvatarBuilder.BuildHumanAvatar(skeletonRoot, description);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogError("[BvhImporter] Failed to build a valid humanoid avatar from BVH.");
                return null;
            }

            avatar.name = "BVH_Avatar";
            return avatar;
        }

        private static HumanBone[] MakeHumanBones(BvhClip bvh)
        {
            var availableNames = new HashSet<string>(bvh.JointsDfsOrder.Select(joint => joint.Name));
            bool usesMixamoStyleNames = availableNames.Contains("Spine") || availableNames.Contains("LeftUpLeg");

            return BoneMapping
                .Select(mapping => new
                {
                    mapping.human,
                    aliases = usesMixamoStyleNames
                        ? mapping.human switch
                        {
                            "Spine" => new[] { "Spine", "Spine1" },
                            "Chest" => new[] { "Spine1", "Spine2" },
                            "UpperChest" => new[] { "Spine2", "Spine3" },
                            _ => mapping.bvhAliases,
                        }
                        : mapping.bvhAliases
                })
                .Select(mapping => new
                {
                    mapping.human,
                    boneName = mapping.aliases.FirstOrDefault(availableNames.Contains)
                })
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.boneName))
                .GroupBy(mapping => mapping.boneName)
                .Select(group => group.First())
                .Select(mapping => new HumanBone
                {
                    boneName = mapping.boneName,
                    humanName = mapping.human,
                    limit = new HumanLimit { useDefaultValues = true },
                })
                .ToArray();
        }

        private static SkeletonBone[] MakeSkeletonBones(GameObject skeletonRoot)
        {
            var transforms = skeletonRoot.GetComponentsInChildren<Transform>(true);
            return transforms.Select(transform => new SkeletonBone
            {
                name = transform.name,
                position = transform.localPosition,
                rotation = transform.localRotation,
                scale = transform.localScale,
            }).ToArray();
        }

        private static AnimationClip BuildAnimationClip(BvhClip bvh, Avatar avatar, Transform skeletonRoot, Dictionary<string, Transform> boneMap)
        {
            var handler = new HumanPoseHandler(avatar, skeletonRoot);
            var pose = new HumanPose();

            try
            {
                var muscleData = new float[bvh.FrameCount][];
                var bodyPositions = new Vector3[bvh.FrameCount];
                var bodyRotations = new Quaternion[bvh.FrameCount];

                for (int frame = 0; frame < bvh.FrameCount; frame++)
                {
                    ResetToRestPose(bvh, boneMap);
                    ApplyFrame(bvh, frame, boneMap);
                    handler.GetHumanPose(ref pose);

                    muscleData[frame] = (float[])pose.muscles.Clone();
                    bodyPositions[frame] = pose.bodyPosition;
                    bodyRotations[frame] = pose.bodyRotation;
                }

                var clip = new AnimationClip
                {
                    frameRate = 1f / Mathf.Max(0.0001f, bvh.FrameTime),
                    legacy = false,
                };

                for (int muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                {
                    var curve = new AnimationCurve();
                    for (int frame = 0; frame < bvh.FrameCount; frame++)
                        curve.AddKey(new Keyframe(frame * bvh.FrameTime, muscleData[frame][muscle]));

                    var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), HumanTrait.MuscleName[muscle]);
                    AnimationUtility.SetEditorCurve(clip, binding, curve);
                }

                SetAnimatorCurve(clip, "RootT.x", bvh, bodyPositions, value => value.x);
                SetAnimatorCurve(clip, "RootT.y", bvh, bodyPositions, value => value.y);
                SetAnimatorCurve(clip, "RootT.z", bvh, bodyPositions, value => value.z);
                SetAnimatorCurve(clip, "RootQ.x", bvh, bodyRotations, value => value.x);
                SetAnimatorCurve(clip, "RootQ.y", bvh, bodyRotations, value => value.y);
                SetAnimatorCurve(clip, "RootQ.z", bvh, bodyRotations, value => value.z);
                SetAnimatorCurve(clip, "RootQ.w", bvh, bodyRotations, value => value.w);
                clip.EnsureQuaternionContinuity();

                return clip;
            }
            finally
            {
                handler.Dispose();
            }
        }

        private static void ResetToRestPose(BvhClip bvh, Dictionary<string, Transform> boneMap)
        {
            foreach (var joint in bvh.JointsDfsOrder)
            {
                if (!boneMap.TryGetValue(joint.Name, out var transform))
                    continue;

                transform.localPosition = BvhPosToUnity(joint.Offset);
                transform.localRotation = Quaternion.identity;
            }
        }

        private static void ApplyFrame(BvhClip clip, int frameIndex, Dictionary<string, Transform> boneMap)
        {
            var values = clip.Frames[frameIndex];

            foreach (var joint in clip.JointsDfsOrder)
            {
                if (!boneMap.TryGetValue(joint.Name, out var transform))
                    continue;

                int offset = joint.ChannelOffset;
                if (joint.Channels.Count == 6)
                {
                    transform.localPosition = BvhPosToUnity(new Vector3(values[offset], values[offset + 1], values[offset + 2]));
                    transform.localRotation = BvhEulerToUnityQuat(values[offset + 3], values[offset + 4], values[offset + 5]);
                }
                else if (joint.Channels.Count == 3)
                {
                    transform.localRotation = BvhEulerToUnityQuat(values[offset], values[offset + 1], values[offset + 2]);
                }
            }
        }

        private static void SetAnimatorCurve<T>(AnimationClip clip, string propertyName, BvhClip bvh, T[] values, Func<T, float> selector)
        {
            var curve = new AnimationCurve();
            for (int frame = 0; frame < bvh.FrameCount; frame++)
                curve.AddKey(new Keyframe(frame * bvh.FrameTime, selector(values[frame])));

            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }
}
#endif
