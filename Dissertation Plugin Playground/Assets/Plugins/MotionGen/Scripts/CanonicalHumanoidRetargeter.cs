#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MotionGen
{
    [Serializable]
    public class CanonicalMotionJson
    {
        public string schema;
        public int fps = 20;
        public string[] jointNames;
        public int[] parents;
        public CanonicalMotionVec3[] restOffsets;
        public CanonicalMotionFrame[] frames;
    }

    [Serializable]
    public class CanonicalMotionFrame
    {
        public CanonicalMotionVec3 position;
        public CanonicalMotionVec3 rotationEuler;
        public CanonicalMotionQuat rootRotation;
        public CanonicalMotionVec3[] joints;
        public CanonicalMotionVec3[] worldJoints;
        public CanonicalMotionQuat[] localRotations;
    }

    [Serializable]
    public class CanonicalMotionVec3
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3() => new Vector3(x, y, z);
    }

    [Serializable]
    public class CanonicalMotionQuat
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
    }

    public static class CanonicalHumanoidRetargeter
    {
        public const string CanonicalSchema = "motiongen-canonical-v1";

        private static readonly int[] SourceParents =
        {
            -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19,
        };

        private static readonly string[] DefaultJointNames =
        {
            "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee",
            "spine2", "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot",
            "neck", "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
            "left_elbow", "right_elbow", "left_wrist", "right_wrist",
        };

        private static readonly (string source, string human)[] CanonicalHumanMapping =
        {
            ("pelvis", "Hips"),
            ("left_hip", "LeftUpperLeg"),
            ("right_hip", "RightUpperLeg"),
            ("spine1", "Spine"),
            ("left_knee", "LeftLowerLeg"),
            ("right_knee", "RightLowerLeg"),
            ("spine2", "Chest"),
            ("left_ankle", "LeftFoot"),
            ("right_ankle", "RightFoot"),
            ("spine3", "UpperChest"),
            ("left_foot", "LeftToes"),
            ("right_foot", "RightToes"),
            ("neck", "Neck"),
            ("left_collar", "LeftShoulder"),
            ("right_collar", "RightShoulder"),
            ("head", "Head"),
            ("left_shoulder", "LeftUpperArm"),
            ("right_shoulder", "RightUpperArm"),
            ("left_elbow", "LeftLowerArm"),
            ("right_elbow", "RightLowerArm"),
            ("left_wrist", "LeftHand"),
            ("right_wrist", "RightHand"),
        };

        private sealed class BoneTrack
        {
            public int sourceIndex;
            public Transform transform;
            public Quaternion bindWorldRotation;
            public Quaternion sourceBindRotation;
            public Quaternion previousSourceRotation;
            public bool hasPreviousSourceRotation;
        }

        private sealed class CarrierSourceSkeleton
        {
            public GameObject rootObject;
            public Transform[] jointNodes;
            public Transform[] rotationNodes;
        }

        private sealed class CanonicalSourceSkeletonRig
        {
            public GameObject rootObject;
            public Transform[] boneTransforms;
            public Transform[] jointAnchors;
            public Quaternion[] bindLocalRotations;
        }

        private sealed class AlignedSourceAvatarRig
        {
            public GameObject rootObject;
            public Transform[] boneTransforms;
            public Quaternion[] targetBindLocalRotations;
            public Quaternion[] sourceBindLocalRotations;
            public Quaternion[] capturedCalibrationLocalRotations;
            public List<BoneTrack> tracks;
        }
        

        private readonly struct DiagnosticBoneMapping
        {
            public readonly int sourceIndex;
            public readonly int childSourceIndex;
            public readonly HumanBodyBones bone;
            public readonly HumanBodyBones childBone;

            public DiagnosticBoneMapping(int sourceIndex, int childSourceIndex, HumanBodyBones bone, HumanBodyBones childBone)
            {
                this.sourceIndex = sourceIndex;
                this.childSourceIndex = childSourceIndex;
                this.bone = bone;
                this.childBone = childBone;
            }
        }

        private static readonly DiagnosticBoneMapping[] DiagnosticBoneMappings =
        {
            new DiagnosticBoneMapping(0, 3, HumanBodyBones.Hips, HumanBodyBones.Spine),
            new DiagnosticBoneMapping(1, 4, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg),
            new DiagnosticBoneMapping(2, 5, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
            new DiagnosticBoneMapping(3, 6, HumanBodyBones.Spine, HumanBodyBones.Chest),
            new DiagnosticBoneMapping(4, 7, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
            new DiagnosticBoneMapping(5, 8, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
            new DiagnosticBoneMapping(6, 9, HumanBodyBones.Chest, HumanBodyBones.UpperChest),
            new DiagnosticBoneMapping(7, 10, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes),
            new DiagnosticBoneMapping(8, 11, HumanBodyBones.RightFoot, HumanBodyBones.RightToes),
            new DiagnosticBoneMapping(9, 12, HumanBodyBones.UpperChest, HumanBodyBones.Neck),
            new DiagnosticBoneMapping(12, 15, HumanBodyBones.Neck, HumanBodyBones.Head),
            new DiagnosticBoneMapping(13, 16, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm),
            new DiagnosticBoneMapping(14, 17, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm),
            new DiagnosticBoneMapping(16, 18, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm),
            new DiagnosticBoneMapping(17, 19, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
            new DiagnosticBoneMapping(18, 20, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
            new DiagnosticBoneMapping(19, 21, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
        };

        private static readonly int[] SelectiveBasisExperimentBoneIndices =
        {
            7, 8,       // ankles / feet
            10, 11,     // toes
            12, 15,     // neck / head
            13, 14,     // shoulders
            16, 17,     // upper arms
            18, 19,     // lower arms
        };

        public static bool TryCreateClipFromJson(string jsonText, Animator animator, AnimationClip clip, global::MotionGenEditorSettings settings, int fallbackFps)
        {
            if (string.IsNullOrWhiteSpace(jsonText) || animator == null || clip == null)
                return false;

            var motion = JsonUtility.FromJson<CanonicalMotionJson>(jsonText);
            if (!IsCanonical(motion))
                return false;

            return TryCreateClip(animator, clip, motion, fallbackFps, settings);
        }

        public static bool TryBuildRetargetDiagnosisFromJson(string jsonText, Animator animator, global::MotionGenEditorSettings settings, out string diagnosisText)
        {
            diagnosisText = null;

            if (string.IsNullOrWhiteSpace(jsonText) || animator == null)
                return false;

            var motion = JsonUtility.FromJson<CanonicalMotionJson>(jsonText);
            if (!IsCanonical(motion))
                return false;

            return TryBuildRetargetDiagnosis(motion, animator, settings, out diagnosisText);
        }

        public static bool IsCanonical(CanonicalMotionJson motion)
        {
            if (motion == null || motion.frames == null || motion.frames.Length == 0)
                return false;

            var first = motion.frames[0];
            return string.Equals(motion.schema, CanonicalSchema, StringComparison.OrdinalIgnoreCase)
                && first != null
                && first.localRotations != null
                && first.localRotations.Length >= 22;
        }

        private static bool TryBuildRetargetDiagnosis(CanonicalMotionJson motion, Animator animator, global::MotionGenEditorSettings settings, out string diagnosisText)
        {
            diagnosisText = null;

            if (motion == null || animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
                return false;

            var sb = new StringBuilder(4096);
            sb.AppendLine("# MotionGen Retarget Diagnostics");
            sb.AppendLine();
            sb.AppendLine($"- Animator: {animator.name}");
            sb.AppendLine($"- Avatar: {animator.avatar.name}");
            sb.AppendLine($"- Frames: {motion.frames.Length}");
            sb.AppendLine($"- Source FPS: {(motion.fps > 0 ? motion.fps : 20)}");
            sb.AppendLine($"- Calibration enabled: {(settings != null && settings.useRetargetCalibration ? "yes" : "no")}");
            sb.AppendLine();

            AppendRootContractDiagnosis(sb, motion);
            AppendSourceFidelityDiagnosis(sb, motion, animator);
            AppendBoneBasisDiagnosis(sb, motion, animator, settings);

            diagnosisText = sb.ToString();
            return true;
        }

        private static bool TryCreateClipFromExactSourceAvatar(Animator targetAnimator, AnimationClip clip, CanonicalMotionJson motion, int fallbackFps, global::MotionGenEditorSettings settings)
        {
            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
                return false;

            if (motion?.frames == null || motion.frames.Length == 0)
                return false;

            if (!HasCanonicalSkeletonData(motion))
                return false;

            var sourceRig = BuildCanonicalSourceSkeletonRig(motion);
            var sourceSkeletonRoot = sourceRig?.rootObject;
            if (sourceSkeletonRoot == null || sourceRig.boneTransforms == null || sourceRig.boneTransforms.Length < 22 || sourceRig.boneTransforms[0] == null)
                return false;

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;

            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();

            var root = targetAnimator.transform;
            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();

            foreach (var transform in targetAnimator.GetComponentsInChildren<Transform>(true))
            {
                if (!originalLocalPositions.ContainsKey(transform))
                    originalLocalPositions[transform] = transform.localPosition;
                if (!originalLocalRotations.ContainsKey(transform))
                    originalLocalRotations[transform] = transform.localRotation;
            }

            var rootPosX = new AnimationCurve();
            var rootPosY = new AnimationCurve();
            var rootPosZ = new AnimationCurve();
            var rootRotX = new AnimationCurve();
            var rootRotY = new AnimationCurve();
            var rootRotZ = new AnimationCurve();
            var rootRotW = new AnimationCurve();

            var muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                muscleCurves[i] = new AnimationCurve();

            int fps = motion.fps > 0 ? motion.fps : Mathf.Max(1, fallbackFps);
            float frameTime = 1f / Mathf.Max(1, fps);

            var firstFrame = motion.frames[0];
            var firstPos = firstFrame.position != null ? firstFrame.position.ToVector3() : Vector3.zero;
            var firstRootRotation = GetSourceRootRotationUnity(firstFrame);
            var basisCalibration = BuildAutomaticBasisCalibration(motion, targetAnimator, settings);

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(sourceSkeletonRoot);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                    return false;

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, sourceSkeletonRoot.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                clip.ClearCurves();
                clip.frameRate = fps;

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        return false;

                    float time = frameIndex * frameTime;

                    ApplyFrameToCanonicalSourceSkeleton(sourceRig, frame, firstPos, firstRootRotation, basisCalibration, settings);

                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    rootPosX.AddKey(time, targetPose.bodyPosition.x);
                    rootPosY.AddKey(time, targetPose.bodyPosition.y);
                    rootPosZ.AddKey(time, targetPose.bodyPosition.z);
                    rootRotX.AddKey(time, targetPose.bodyRotation.x);
                    rootRotY.AddKey(time, targetPose.bodyRotation.y);
                    rootRotZ.AddKey(time, targetPose.bodyRotation.z);
                    rootRotW.AddKey(time, targetPose.bodyRotation.w);

                    for (int m = 0; m < HumanTrait.MuscleCount; m++)
                        muscleCurves[m].AddKey(time, targetPose.muscles[m]);
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;

                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();

                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (sourceSkeletonRoot != null)
                    UnityEngine.Object.DestroyImmediate(sourceSkeletonRoot);
            }

            SetAnimatorCurve(clip, "RootT.x", rootPosX);
            SetAnimatorCurve(clip, "RootT.y", rootPosY);
            SetAnimatorCurve(clip, "RootT.z", rootPosZ);
            SetAnimatorCurve(clip, "RootQ.x", rootRotX);
            SetAnimatorCurve(clip, "RootQ.y", rootRotY);
            SetAnimatorCurve(clip, "RootQ.z", rootRotZ);
            SetAnimatorCurve(clip, "RootQ.w", rootRotW);

            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                SetAnimatorCurve(clip, HumanTrait.MuscleName[i], muscleCurves[i]);

            clip.EnsureQuaternionContinuity();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool TryCreateClipFromAlignedSourceAvatar(Animator targetAnimator, AnimationClip clip, CanonicalMotionJson motion, int fallbackFps, global::MotionGenEditorSettings settings)
        {
            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
                return false;

            if (motion?.frames == null || motion.frames.Length == 0)
                return false;

            if (!HasCanonicalSkeletonData(motion))
                return false;

            var sourceRig = BuildAlignedSourceAvatarRig(motion, targetAnimator, settings);
            var sourceSkeletonRoot = sourceRig?.rootObject;
            if (sourceSkeletonRoot == null || sourceRig.boneTransforms == null || sourceRig.boneTransforms.Length < 22 || sourceRig.boneTransforms[0] == null)
                return false;

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;

            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();

            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();

            foreach (var transform in targetAnimator.GetComponentsInChildren<Transform>(true))
            {
                if (!originalLocalPositions.ContainsKey(transform))
                    originalLocalPositions[transform] = transform.localPosition;
                if (!originalLocalRotations.ContainsKey(transform))
                    originalLocalRotations[transform] = transform.localRotation;
            }

            var rootPosX = new AnimationCurve();
            var rootPosY = new AnimationCurve();
            var rootPosZ = new AnimationCurve();
            var rootRotX = new AnimationCurve();
            var rootRotY = new AnimationCurve();
            var rootRotZ = new AnimationCurve();
            var rootRotW = new AnimationCurve();
            var muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                muscleCurves[i] = new AnimationCurve();
            var targetPoses = new List<HumanPose>(motion.frames.Length);
            var sourceRootDeltas = new List<Vector3>(motion.frames.Length);

            int fps = motion.fps > 0 ? motion.fps : Mathf.Max(1, fallbackFps);
            float frameTime = 1f / Mathf.Max(1, fps);

            var firstFrame = motion.frames[0];
            var firstPos = firstFrame.position != null ? firstFrame.position.ToVector3() : Vector3.zero;
            var firstRootRotation = GetSourceRootRotationUnity(firstFrame);

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(sourceSkeletonRoot);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                    return false;

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, sourceSkeletonRoot.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                clip.ClearCurves();
                clip.frameRate = fps;

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        return false;

                    var deltaSource = frame.position != null ? frame.position.ToVector3() - firstPos : Vector3.zero;
                    sourceRig.boneTransforms[0].localPosition = SourceVectorToUnity(deltaSource);

                    var currentRootRotation = GetSourceRootRotationUnity(frame);
                    var rootDeltaRotation = currentRootRotation * Quaternion.Inverse(firstRootRotation);
                    if (GetExperimentMode(settings) == global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.RootInverse)
                        rootDeltaRotation = Quaternion.Inverse(rootDeltaRotation);
                    sourceRig.boneTransforms[0].localRotation = GetCapturedCalibrationLocalRotation(sourceRig.capturedCalibrationLocalRotations, 0)
                        * rootDeltaRotation
                        * GetBindLocalRotation(sourceRig.targetBindLocalRotations, 0);

                    for (int i = 1; i < 22; i++)
                    {
                        if (sourceRig.boneTransforms[i] == null)
                            continue;

                        var localRotation = frame.localRotations != null && frame.localRotations.Length > i && frame.localRotations[i] != null
                            ? SourceQuaternionToUnity(frame.localRotations[i].ToQuaternion())
                            : Quaternion.identity;

                        var convertedLocalRotation = ConvertLocalRotationBetweenBindBases(
                            localRotation,
                            GetBindLocalRotation(sourceRig.sourceBindLocalRotations, i),
                            GetBindLocalRotation(sourceRig.targetBindLocalRotations, i)
                        );

                        sourceRig.boneTransforms[i].localRotation = GetCapturedCalibrationLocalRotation(sourceRig.capturedCalibrationLocalRotations, i)
                            * convertedLocalRotation
                            * GetBindLocalRotation(sourceRig.targetBindLocalRotations, i);
                    }

                    var sourceWorldPositions = BuildSourceWorldPositionsUnity(frame);
                    foreach (var track in sourceRig.tracks)
                    {
                        var sourceRotationReference = track.hasPreviousSourceRotation
                            ? track.previousSourceRotation
                            : track.sourceBindRotation;

                        if (!TryGetSourceBoneWorldRotation(sourceWorldPositions, track.sourceIndex, sourceRotationReference, out var currentSourceRotation))
                            continue;

                        var deltaBoneRotation = currentSourceRotation * Quaternion.Inverse(track.sourceBindRotation);
                        track.transform.rotation = deltaBoneRotation * track.bindWorldRotation;
                        track.previousSourceRotation = currentSourceRotation;
                        track.hasPreviousSourceRotation = true;
                    }

                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    targetPoses.Add(CloneHumanPose(targetPose));
                    sourceRootDeltas.Add(SourceVectorToUnity(deltaSource));
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;

                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();

                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (sourceSkeletonRoot != null)
                    UnityEngine.Object.DestroyImmediate(sourceSkeletonRoot);
            }

            if (targetPoses.Count == 0)
                return false;

            var firstBodyPosition = targetPoses[0].bodyPosition;
            const float rootScale = 1f;
            for (int frameIndex = 0; frameIndex < targetPoses.Count; frameIndex++)
            {
                float time = frameIndex * frameTime;
                var pose = targetPoses[frameIndex];
                var sourceDelta = frameIndex < sourceRootDeltas.Count ? sourceRootDeltas[frameIndex] : Vector3.zero;
                var scaledPosition = firstBodyPosition + sourceDelta * rootScale;

                rootPosX.AddKey(time, scaledPosition.x);
                rootPosY.AddKey(time, scaledPosition.y);
                rootPosZ.AddKey(time, scaledPosition.z);
                rootRotX.AddKey(time, pose.bodyRotation.x);
                rootRotY.AddKey(time, pose.bodyRotation.y);
                rootRotZ.AddKey(time, pose.bodyRotation.z);
                rootRotW.AddKey(time, pose.bodyRotation.w);

                for (int m = 0; m < HumanTrait.MuscleCount; m++)
                    muscleCurves[m].AddKey(time, pose.muscles[m]);
            }

            SetAnimatorCurve(clip, "RootT.x", rootPosX);
            SetAnimatorCurve(clip, "RootT.y", rootPosY);
            SetAnimatorCurve(clip, "RootT.z", rootPosZ);
            SetAnimatorCurve(clip, "RootQ.x", rootRotX);
            SetAnimatorCurve(clip, "RootQ.y", rootRotY);
            SetAnimatorCurve(clip, "RootQ.z", rootRotZ);
            SetAnimatorCurve(clip, "RootQ.w", rootRotW);

            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                SetAnimatorCurve(clip, HumanTrait.MuscleName[i], muscleCurves[i]);

            clip.EnsureQuaternionContinuity();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool TryCreateClip(Animator animator, AnimationClip clip, CanonicalMotionJson motion, int fallbackFps, global::MotionGenEditorSettings settings)
        {
            if (TryCreateClipFromAlignedSourceAvatar(animator, clip, motion, fallbackFps, settings))
                return true;

            if (TryCreateClipFromExactSourceAvatar(animator, clip, motion, fallbackFps, settings))
                return true;

            var sourceBindPositions = BuildSourceBindPositionsUnity(motion);
            var tracks = BuildTracks(animator, sourceBindPositions);
            if (tracks.Count == 0)
                return false;

            clip.ClearCurves();

            int fps = motion.fps > 0 ? motion.fps : Mathf.Max(1, fallbackFps);
            clip.frameRate = fps;
            float frameTime = 1f / Mathf.Max(1, fps);

            var root = animator.transform;
            var rootStartPos = root.localPosition;
            var rootStartRot = root.localRotation;

            var rootPosX = new AnimationCurve();
            var rootPosY = new AnimationCurve();
            var rootPosZ = new AnimationCurve();
            var rootRotX = new AnimationCurve();
            var rootRotY = new AnimationCurve();
            var rootRotZ = new AnimationCurve();
            var rootRotW = new AnimationCurve();

            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            var humanPose = new HumanPose();
            var muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                muscleCurves[i] = new AnimationCurve();

            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();
            originalLocalPositions[root] = root.localPosition;
            originalLocalRotations[root] = root.localRotation;
            foreach (var track in tracks)
            {
                if (!originalLocalRotations.ContainsKey(track.transform))
                    originalLocalRotations[track.transform] = track.transform.localRotation;
                if (!originalLocalPositions.ContainsKey(track.transform))
                    originalLocalPositions[track.transform] = track.transform.localPosition;
            }

            float rootScale = ComputeRootScale(animator, motion);
            var firstPos = motion.frames[0].position != null ? motion.frames[0].position.ToVector3() : Vector3.zero;
            var firstRootRotation = motion.frames[0].rootRotation != null
                ? SourceQuaternionToUnity(motion.frames[0].rootRotation.ToQuaternion())
                : Quaternion.identity;
            Quaternion previousRootRotation = Quaternion.identity;
            bool hasPreviousRootRotation = false;

            try
            {
                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        return false;

                    float time = frameIndex * frameTime;

                    var deltaSource = frame.position != null ? frame.position.ToVector3() - firstPos : Vector3.zero;
                    var deltaUnity = SourceVectorToUnity(deltaSource) * rootScale;
                    var rootPosition = rootStartPos + deltaUnity;

                    var currentRootRotation = frame.rootRotation != null
                        ? SourceQuaternionToUnity(frame.rootRotation.ToQuaternion())
                        : firstRootRotation;
                    var deltaRootRotation = currentRootRotation * Quaternion.Inverse(firstRootRotation);
                    var rootRotation = deltaRootRotation * rootStartRot;

                    if (hasPreviousRootRotation && Quaternion.Dot(previousRootRotation, rootRotation) < 0f)
                        rootRotation = new Quaternion(-rootRotation.x, -rootRotation.y, -rootRotation.z, -rootRotation.w);

                    previousRootRotation = rootRotation;
                    hasPreviousRootRotation = true;

                    root.localPosition = rootPosition;
                    root.localRotation = rootRotation;

                    var sourceWorldPositions = BuildSourceWorldPositionsUnity(frame);

                    foreach (var track in tracks)
                    {
                        var sourceRotationReference = track.hasPreviousSourceRotation
                            ? track.previousSourceRotation
                            : track.sourceBindRotation;

                        if (!TryGetSourceBoneWorldRotation(sourceWorldPositions, track.sourceIndex, sourceRotationReference, out var currentSourceRotation))
                            continue;

                        var deltaBoneRotation = currentSourceRotation * Quaternion.Inverse(track.sourceBindRotation);
                        track.transform.rotation = deltaBoneRotation * track.bindWorldRotation;
                        track.previousSourceRotation = currentSourceRotation;
                        track.hasPreviousSourceRotation = true;
                    }

                    poseHandler.GetHumanPose(ref humanPose);

                    rootPosX.AddKey(time, humanPose.bodyPosition.x);
                    rootPosY.AddKey(time, humanPose.bodyPosition.y);
                    rootPosZ.AddKey(time, humanPose.bodyPosition.z);
                    rootRotX.AddKey(time, humanPose.bodyRotation.x);
                    rootRotY.AddKey(time, humanPose.bodyRotation.y);
                    rootRotZ.AddKey(time, humanPose.bodyRotation.z);
                    rootRotW.AddKey(time, humanPose.bodyRotation.w);

                    for (int m = 0; m < HumanTrait.MuscleCount; m++)
                        muscleCurves[m].AddKey(time, humanPose.muscles[m]);
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;
                poseHandler.Dispose();
            }

            SetAnimatorCurve(clip, "RootT.x", rootPosX);
            SetAnimatorCurve(clip, "RootT.y", rootPosY);
            SetAnimatorCurve(clip, "RootT.z", rootPosZ);
            SetAnimatorCurve(clip, "RootQ.x", rootRotX);
            SetAnimatorCurve(clip, "RootQ.y", rootRotY);
            SetAnimatorCurve(clip, "RootQ.z", rootRotZ);
            SetAnimatorCurve(clip, "RootQ.w", rootRotW);

            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                SetAnimatorCurve(clip, HumanTrait.MuscleName[i], muscleCurves[i]);

            clip.EnsureQuaternionContinuity();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static List<BoneTrack> BuildTracks(Animator animator, Vector3[] sourceBindPositions)
        {
            var tracks = new List<BoneTrack>();

            foreach (var sourceIndex in GetRetargetBoneIndices())
            {
                if (!TryResolveBone(animator, sourceIndex, out var transform) || transform == null)
                    continue;

                if (!TryGetSourceBoneWorldRotation(sourceBindPositions, sourceIndex, Quaternion.identity, out var sourceBindRotation))
                    continue;

                tracks.Add(new BoneTrack
                {
                    sourceIndex = sourceIndex,
                    transform = transform,
                    bindWorldRotation = transform.rotation,
                    sourceBindRotation = sourceBindRotation,
                    previousSourceRotation = sourceBindRotation,
                    hasPreviousSourceRotation = false,
                });
            }

            tracks.Sort((a, b) => a.sourceIndex.CompareTo(b.sourceIndex));
            return tracks;
        }

        private static bool HasCanonicalSkeletonData(CanonicalMotionJson motion)
        {
            return motion != null
                && GetCanonicalJointNames(motion).Length >= 22
                && GetCanonicalParents(motion).Length >= 22
                && motion.restOffsets != null
                && motion.restOffsets.Length >= 22;
        }

        private static string[] GetCanonicalJointNames(CanonicalMotionJson motion)
        {
            return DefaultJointNames;
        }

        private static int[] GetCanonicalParents(CanonicalMotionJson motion)
        {
            return motion?.parents != null && motion.parents.Length >= 22
                ? motion.parents
                : SourceParents;
        }

        private static GameObject BuildCanonicalSourceSkeleton(CanonicalMotionJson motion, out Transform[] sourceBones)
        {
            var rig = BuildCanonicalSourceSkeletonRig(motion);
            sourceBones = rig?.boneTransforms;
            return rig?.rootObject;
        }

        private static CanonicalSourceSkeletonRig BuildCanonicalSourceSkeletonRig(CanonicalMotionJson motion)
        {
            if (!HasCanonicalSkeletonData(motion))
                return null;

            var jointNames = GetCanonicalJointNames(motion);
            var parents = GetCanonicalParents(motion);
            var bindPositions = BuildSourceBindPositionsUnity(motion);
            var bindLocalRotations = BuildCanonicalSourceBindLocalRotations(bindPositions, parents);

            var container = new GameObject("Canonical_SourceSkeleton")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;

            var sourceBones = new Transform[22];

            var jointAnchors = new Transform[22];

            var rootGo = new GameObject(jointNames[0])
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var rootTransform = rootGo.transform;
            rootTransform.SetParent(container.transform, false);
            rootTransform.localPosition = Vector3.zero;
            rootTransform.localRotation = bindLocalRotations[0];
            rootTransform.localScale = Vector3.one;

            var rootAnchorGo = new GameObject(jointNames[0] + "_anchor")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var rootAnchorTransform = rootAnchorGo.transform;
            rootAnchorTransform.SetParent(rootTransform, false);
            rootAnchorTransform.localPosition = Vector3.zero;
            rootAnchorTransform.localRotation = Quaternion.Inverse(bindLocalRotations[0]);
            rootAnchorTransform.localScale = Vector3.one;

            sourceBones[0] = rootTransform;
            jointAnchors[0] = rootAnchorTransform;

            for (int i = 1; i < 22; i++)
            {
                int parentIndex = parents[i];
                var parentAnchor = parentIndex >= 0 && parentIndex < i ? jointAnchors[parentIndex] : container.transform;
                var bindRotation = bindLocalRotations[i];
                var bindRotationInverse = Quaternion.Inverse(bindRotation);

                var boneGo = new GameObject(jointNames[i])
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var boneTransform = boneGo.transform;
                boneTransform.SetParent(parentAnchor, false);
                boneTransform.localPosition = Vector3.zero;
                boneTransform.localRotation = bindRotation;
                boneTransform.localScale = Vector3.one;

                var endGo = new GameObject(jointNames[i] + "_end")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var endTransform = endGo.transform;
                endTransform.SetParent(boneTransform, false);
                endTransform.localPosition = bindRotationInverse * SourceVectorToUnity(motion.restOffsets[i].ToVector3());
                endTransform.localRotation = bindRotationInverse;
                endTransform.localScale = Vector3.one;

                sourceBones[i] = boneTransform;
                jointAnchors[i] = endTransform;
            }

            return new CanonicalSourceSkeletonRig
            {
                rootObject = container,
                boneTransforms = sourceBones,
                jointAnchors = jointAnchors,
                bindLocalRotations = bindLocalRotations,
            };
        }

        private static AlignedSourceAvatarRig BuildAlignedSourceAvatarRig(CanonicalMotionJson motion, Animator targetAnimator, global::MotionGenEditorSettings settings)
        {
            if (!HasCanonicalSkeletonData(motion) || targetAnimator == null)
                return null;

            if (!TryBuildTargetNeutralPositions(targetAnimator, out var targetNeutralPositions, out _))
                return null;

            var jointNames = GetCanonicalJointNames(motion);
            var parents = GetCanonicalParents(motion);
            var sourceBindPositions = BuildSourceBindPositionsUnity(motion);
            var targetBindLocalRotations = BuildBindLocalRotationsFromReferencePositions(targetNeutralPositions, parents);
            var sourceBindLocalRotations = BuildCanonicalSourceBindLocalRotations(sourceBindPositions, parents);
            var capturedCalibrationLocalRotations = BuildCapturedCalibrationLocalRotations(settings);

            var container = new GameObject("Canonical_AlignedSourceAvatar")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;

            var sourceBones = new Transform[22];
            var jointAnchors = new Transform[22];

            var rootGo = new GameObject(jointNames[0])
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var rootTransform = rootGo.transform;
            rootTransform.SetParent(container.transform, false);
            rootTransform.localPosition = Vector3.zero;
            rootTransform.localRotation = targetBindLocalRotations[0];
            rootTransform.localScale = Vector3.one;

            var rootAnchorGo = new GameObject(jointNames[0] + "_anchor")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var rootAnchorTransform = rootAnchorGo.transform;
            rootAnchorTransform.SetParent(rootTransform, false);
            rootAnchorTransform.localPosition = Vector3.zero;
            rootAnchorTransform.localRotation = Quaternion.Inverse(targetBindLocalRotations[0]);
            rootAnchorTransform.localScale = Vector3.one;

            sourceBones[0] = rootTransform;
            jointAnchors[0] = rootAnchorTransform;

            for (int i = 1; i < 22; i++)
            {
                int parentIndex = parents[i];
                var parentAnchor = parentIndex >= 0 && parentIndex < i ? jointAnchors[parentIndex] : container.transform;
                var bindRotation = targetBindLocalRotations[i];
                var bindRotationInverse = Quaternion.Inverse(bindRotation);

                Vector3 targetOffset = parentIndex >= 0
                    ? targetNeutralPositions[i] - targetNeutralPositions[parentIndex]
                    : Vector3.zero;
                Vector3 targetDirection = SafeNormalize(targetOffset, SourceVectorToUnity(motion.restOffsets[i].ToVector3()));
                float sourceLength = SourceVectorToUnity(motion.restOffsets[i].ToVector3()).magnitude;
                Vector3 alignedOffset = targetDirection * sourceLength;

                var boneGo = new GameObject(jointNames[i])
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var boneTransform = boneGo.transform;
                boneTransform.SetParent(parentAnchor, false);
                boneTransform.localPosition = Vector3.zero;
                boneTransform.localRotation = bindRotation;
                boneTransform.localScale = Vector3.one;

                var endGo = new GameObject(jointNames[i] + "_end")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                var endTransform = endGo.transform;
                endTransform.SetParent(boneTransform, false);
                endTransform.localPosition = bindRotationInverse * alignedOffset;
                endTransform.localRotation = bindRotationInverse;
                endTransform.localScale = Vector3.one;

                sourceBones[i] = boneTransform;
                jointAnchors[i] = endTransform;
            }

            var tracks = new List<BoneTrack>();
            foreach (var sourceIndex in GetDirectionalCorrectionBoneIndices())
            {
                if (sourceIndex <= 0 || sourceIndex >= sourceBones.Length || sourceBones[sourceIndex] == null)
                    continue;

                if (!TryGetSourceBoneWorldRotation(sourceBindPositions, sourceIndex, Quaternion.identity, out var sourceBindRotation))
                    continue;

                tracks.Add(new BoneTrack
                {
                    sourceIndex = sourceIndex,
                    transform = sourceBones[sourceIndex],
                    bindWorldRotation = sourceBones[sourceIndex].rotation,
                    sourceBindRotation = sourceBindRotation,
                    previousSourceRotation = sourceBindRotation,
                    hasPreviousSourceRotation = false,
                });
            }

            tracks.Sort((a, b) => a.sourceIndex.CompareTo(b.sourceIndex));

            return new AlignedSourceAvatarRig
            {
                rootObject = container,
                boneTransforms = sourceBones,
                targetBindLocalRotations = targetBindLocalRotations,
                sourceBindLocalRotations = sourceBindLocalRotations,
                capturedCalibrationLocalRotations = capturedCalibrationLocalRotations,
                tracks = tracks,
            };
        }

        private static CarrierSourceSkeleton BuildCanonicalCarrierSourceSkeleton(CanonicalMotionJson motion)
        {
            if (!HasCanonicalSkeletonData(motion))
                return null;

            var jointNames = GetCanonicalJointNames(motion);
            var parents = GetCanonicalParents(motion);

            var container = new GameObject("Canonical_CarrierSourceSkeleton")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            container.transform.localPosition = Vector3.zero;
            container.transform.localRotation = Quaternion.identity;
            container.transform.localScale = Vector3.one;

            var jointNodes = new Transform[22];
            var rotationNodes = new Transform[22];

            var rootGo = new GameObject(jointNames[0])
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            rootGo.transform.SetParent(container.transform, false);
            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.identity;
            rootGo.transform.localScale = Vector3.one;
            jointNodes[0] = rootGo.transform;
            rotationNodes[0] = rootGo.transform;

            for (int i = 1; i < 22; i++)
            {
                int parentIndex = parents[i];
                if (parentIndex < 0 || parentIndex >= i || jointNodes[parentIndex] == null)
                    continue;

                var rotGo = new GameObject(jointNames[i] + "_rot")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                rotGo.transform.SetParent(jointNodes[parentIndex], false);
                rotGo.transform.localPosition = Vector3.zero;
                rotGo.transform.localRotation = Quaternion.identity;
                rotGo.transform.localScale = Vector3.one;

                var jointGo = new GameObject(jointNames[i])
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                jointGo.transform.SetParent(rotGo.transform, false);
                jointGo.transform.localPosition = SourceVectorToUnity(motion.restOffsets[i].ToVector3());
                jointGo.transform.localRotation = Quaternion.identity;
                jointGo.transform.localScale = Vector3.one;

                rotationNodes[i] = rotGo.transform;
                jointNodes[i] = jointGo.transform;
            }

            return new CarrierSourceSkeleton
            {
                rootObject = container,
                jointNodes = jointNodes,
                rotationNodes = rotationNodes,
            };
        }

        private static Avatar BuildCanonicalSourceAvatar(GameObject sourceSkeletonRoot)
        {
            if (sourceSkeletonRoot == null)
                return null;

            var desc = new HumanDescription
            {
                human = MakeCanonicalHumanBones(),
                skeleton = MakeSkeletonBones(sourceSkeletonRoot),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(sourceSkeletonRoot, desc);
            if (avatar != null)
                avatar.hideFlags = HideFlags.HideAndDontSave;
            return avatar;
        }

        private static HumanBone[] MakeCanonicalHumanBones()
        {
            var bones = new HumanBone[CanonicalHumanMapping.Length];
            for (int i = 0; i < CanonicalHumanMapping.Length; i++)
            {
                bones[i] = new HumanBone
                {
                    boneName = CanonicalHumanMapping[i].source,
                    humanName = CanonicalHumanMapping[i].human,
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

        private static void ApplyFrameToCanonicalSourceSkeleton(CanonicalSourceSkeletonRig rig, CanonicalMotionFrame frame, Vector3 firstPos, Quaternion firstRootRotation, Quaternion[] basisCalibration, global::MotionGenEditorSettings settings)
        {
            if (rig?.boneTransforms == null || rig.boneTransforms.Length < 22 || rig.boneTransforms[0] == null || frame == null)
                return;

            var sourceBones = rig.boneTransforms;
            var bindLocalRotations = rig.bindLocalRotations;

            var deltaSource = frame.position != null ? frame.position.ToVector3() - firstPos : Vector3.zero;
            sourceBones[0].localPosition = SourceVectorToUnity(deltaSource);

            var currentRootRotation = GetSourceRootRotationUnity(frame);
            var rootDeltaRotation = currentRootRotation * Quaternion.Inverse(firstRootRotation);
            if (GetExperimentMode(settings) == global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.RootInverse)
                rootDeltaRotation = Quaternion.Inverse(rootDeltaRotation);

            sourceBones[0].localRotation = rootDeltaRotation * GetBindLocalRotation(bindLocalRotations, 0);

            for (int i = 1; i < 22; i++)
            {
                if (sourceBones[i] == null)
                    continue;

                var localRotation = frame.localRotations != null && frame.localRotations.Length > i && frame.localRotations[i] != null
                    ? SourceQuaternionToUnity(frame.localRotations[i].ToQuaternion())
                    : Quaternion.identity;

                localRotation = ApplyExperimentalBasisCorrection(localRotation, i, basisCalibration, settings);

                sourceBones[i].localRotation = localRotation * GetBindLocalRotation(bindLocalRotations, i);
            }
        }

        private static Quaternion GetSourceRootRotationUnity(CanonicalMotionFrame frame)
        {
            return GetSourceRootRotationUnity(frame, SourceQuaternionToUnity);
        }

        private static Quaternion GetSourceRootRotationUnity(CanonicalMotionFrame frame, Func<Quaternion, Quaternion> quaternionConverter)
        {
            if (quaternionConverter == null)
                quaternionConverter = SourceQuaternionToUnity;

            if (frame?.rootRotation != null)
                return quaternionConverter(frame.rootRotation.ToQuaternion());

            if (frame?.localRotations != null && frame.localRotations.Length > 0 && frame.localRotations[0] != null)
                return quaternionConverter(frame.localRotations[0].ToQuaternion());

            return Quaternion.identity;
        }

        private static void AppendRootContractDiagnosis(StringBuilder sb, CanonicalMotionJson motion)
        {
            sb.AppendLine("## Root contract");

            int frameCount = motion.frames != null ? motion.frames.Length : 0;
            if (frameCount < 2)
            {
                sb.AppendLine("Not enough frames to analyze root motion.");
                sb.AppendLine();
                return;
            }

            float forwardVsBody = 0f;
            float inverseVsBody = 0f;
            float forwardVsTravel = 0f;
            float inverseVsTravel = 0f;
            int bodySamples = 0;
            int travelSamples = 0;

            int sampleCount = Mathf.Min(frameCount, 30);
            for (int i = 0; i < sampleCount; i++)
            {
                var frame = motion.frames[i];
                var positions = BuildSourceWorldPositionsUnity(frame);
                var bodyForward = FlattenHorizontal(ComputeCharacterForward(positions));
                if (bodyForward.sqrMagnitude < 1e-8f)
                    continue;

                var rootRotation = GetSourceRootRotationUnity(frame);
                var rootForward = FlattenHorizontal(rootRotation * Vector3.forward);
                var inverseRootForward = FlattenHorizontal(Quaternion.Inverse(rootRotation) * Vector3.forward);

                if (rootForward.sqrMagnitude > 1e-8f && inverseRootForward.sqrMagnitude > 1e-8f)
                {
                    forwardVsBody += Vector3.Dot(rootForward, bodyForward);
                    inverseVsBody += Vector3.Dot(inverseRootForward, bodyForward);
                    bodySamples++;
                }

                if (i + 1 >= sampleCount)
                    continue;

                var nextFrame = motion.frames[i + 1];
                var currentPos = frame.position != null ? SourceVectorToUnity(frame.position.ToVector3()) : Vector3.zero;
                var nextPos = nextFrame.position != null ? SourceVectorToUnity(nextFrame.position.ToVector3()) : currentPos;
                var travel = FlattenHorizontal(nextPos - currentPos);
                if (travel.sqrMagnitude < 1e-8f || rootForward.sqrMagnitude < 1e-8f || inverseRootForward.sqrMagnitude < 1e-8f)
                    continue;

                forwardVsTravel += Vector3.Dot(rootForward, travel);
                inverseVsTravel += Vector3.Dot(inverseRootForward, travel);
                travelSamples++;
            }

            float avgForwardVsBody = bodySamples > 0 ? forwardVsBody / bodySamples : 0f;
            float avgInverseVsBody = bodySamples > 0 ? inverseVsBody / bodySamples : 0f;
            float avgForwardVsTravel = travelSamples > 0 ? forwardVsTravel / travelSamples : 0f;
            float avgInverseVsTravel = travelSamples > 0 ? inverseVsTravel / travelSamples : 0f;

            sb.AppendLine($"- Avg dot(root forward, body forward): {avgForwardVsBody:F3}");
            sb.AppendLine($"- Avg dot(inverse root forward, body forward): {avgInverseVsBody:F3}");
            sb.AppendLine($"- Avg dot(root forward, travel dir): {avgForwardVsTravel:F3}");
            sb.AppendLine($"- Avg dot(inverse root forward, travel dir): {avgInverseVsTravel:F3}");

            string recommendation;
            if (avgInverseVsBody > avgForwardVsBody + 0.25f && avgInverseVsTravel > avgForwardVsTravel + 0.25f)
                recommendation = "Likely root quaternion inversion/sign contract mismatch. Inverse(rootRotation) matches body/travel better than rootRotation.";
            else if (avgForwardVsBody < -0.25f && avgForwardVsTravel < -0.25f)
                recommendation = "Likely 180° forward-axis mismatch after handedness conversion.";
            else
                recommendation = "Root contract is not obviously inverted from this sample. Bone-basis mismatch is likely dominating.";

            sb.AppendLine($"- Recommendation: {recommendation}");
            sb.AppendLine();
        }

        private static void AppendSourceFidelityDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator animator)
        {
            sb.AppendLine("## Source skeleton fidelity");

            if (motion?.frames == null || motion.frames.Length == 0 || !HasCanonicalSkeletonData(motion))
            {
                sb.AppendLine("Not enough canonical data to evaluate source fidelity.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            var sourceSkeletonRoot = exactRig?.rootObject;
            var sourceBones = exactRig?.boneTransforms;
            var sourceAnchors = exactRig?.jointAnchors;
            if (sourceSkeletonRoot == null || sourceBones == null || sourceBones.Length < 22 || sourceAnchors == null || sourceAnchors.Length < 22)
            {
                sb.AppendLine("Failed to build canonical source skeleton for fidelity test.");
                sb.AppendLine();
                return;
            }

            var carrierSkeleton = BuildCanonicalCarrierSourceSkeleton(motion);

            var sumError = 0f;
            var maxError = 0f;
            var sampleCount = 0;
            var boneErrorSum = new float[22];
            var boneErrorMax = new float[22];
            var carrierSumError = 0f;
            var carrierMaxError = 0f;
            var carrierSampleCount = 0;
            float rootVsLocal0AngleSum = 0f;
            float rootVsLocal0AngleMax = 0f;
            int rootVsLocal0Samples = 0;

            try
            {
                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    var expectedPositions = BuildSourceWorldPositionsUnity(frame);

                    for (int boneIndex = 0; boneIndex < 22; boneIndex++)
                    {
                        if (sourceAnchors[boneIndex] == null)
                            continue;

                        float error = Vector3.Distance(sourceAnchors[boneIndex].position, expectedPositions[boneIndex]);
                        sumError += error;
                        sampleCount++;
                        boneErrorSum[boneIndex] += error;
                        boneErrorMax[boneIndex] = Mathf.Max(boneErrorMax[boneIndex], error);
                        maxError = Mathf.Max(maxError, error);
                    }

                    if (carrierSkeleton?.jointNodes != null && carrierSkeleton.jointNodes.Length >= 22)
                    {
                        ApplyAbsoluteFrameToCarrierSourceSkeleton(carrierSkeleton, frame);

                        for (int boneIndex = 0; boneIndex < 22; boneIndex++)
                        {
                            if (carrierSkeleton.jointNodes[boneIndex] == null)
                                continue;

                            float carrierError = Vector3.Distance(carrierSkeleton.jointNodes[boneIndex].position, expectedPositions[boneIndex]);
                            carrierSumError += carrierError;
                            carrierSampleCount++;
                            carrierMaxError = Mathf.Max(carrierMaxError, carrierError);
                        }
                    }

                    if (frame.rootRotation != null && frame.localRotations[0] != null)
                    {
                        float angle = Quaternion.Angle(
                            SourceQuaternionToUnity(frame.rootRotation.ToQuaternion()),
                            SourceQuaternionToUnity(frame.localRotations[0].ToQuaternion()));
                        rootVsLocal0AngleSum += angle;
                        rootVsLocal0AngleMax = Mathf.Max(rootVsLocal0AngleMax, angle);
                        rootVsLocal0Samples++;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceSkeletonRoot);
                if (carrierSkeleton?.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(carrierSkeleton.rootObject);
            }

            if (sampleCount == 0)
            {
                sb.AppendLine("No valid fidelity samples found.");
                sb.AppendLine();
                return;
            }

            float meanError = sumError / sampleCount;
            float meanRootVsLocal0Angle = rootVsLocal0Samples > 0 ? rootVsLocal0AngleSum / rootVsLocal0Samples : 0f;

            sb.AppendLine($"- Mean joint position reconstruction error: {meanError:F5}");
            sb.AppendLine($"- Max joint position reconstruction error: {maxError:F5}");
            if (carrierSampleCount > 0)
            {
                sb.AppendLine($"- Mean FK-carrier reconstruction error: {carrierSumError / carrierSampleCount:F5}");
                sb.AppendLine($"- Max FK-carrier reconstruction error: {carrierMaxError:F5}");
            }
            sb.AppendLine($"- Mean angle between `rootRotation` and `localRotations[0]`: {meanRootVsLocal0Angle:F3}°");
            sb.AppendLine($"- Max angle between `rootRotation` and `localRotations[0]`: {rootVsLocal0AngleMax:F3}°");

            string recommendation;
            if (carrierSampleCount > 0 && (carrierSumError / carrierSampleCount) < 0.01f && carrierMaxError < 0.05f)
                recommendation = "Canonical local rotations are consistent with exported world joints only under an FK-carrier skeleton. The main problem is joint-rotation attachment semantics, not Humanoid retargeting.";
            else if (meanError < 0.01f && maxError < 0.05f)
                recommendation = "Source motion is self-consistent. The main problem is likely Humanoid interpretation, not backend export or canonical local-rotation application.";
            else if (meanError < 0.05f)
                recommendation = "Source motion is mostly self-consistent, with some contract drift. Humanoid interpretation is still the prime suspect.";
            else
                recommendation = "Canonical source local rotations/rest offsets/root motion do not reconstruct exported world joints well. Investigate backend/source contract before Humanoid retargeting.";

            sb.AppendLine($"- Recommendation: {recommendation}");
            sb.AppendLine();
            sb.AppendLine("| Bone | Mean error | Max error |\n|---|---:|---:|");

            for (int boneIndex = 0; boneIndex < 22; boneIndex++)
            {
                float boneMean = boneErrorSum[boneIndex] / Mathf.Max(1, motion.frames.Length);
                string boneName = boneIndex < DefaultJointNames.Length ? DefaultJointNames[boneIndex] : boneIndex.ToString();
                sb.AppendLine($"| {boneName} | {boneMean:F5} | {boneErrorMax[boneIndex]:F5} |");
            }

            sb.AppendLine();

            AppendQuaternionConversionCandidateDiagnosis(sb, motion);
            try
            {
                AppendSourceHumanoidAbstractionDiagnosis(sb, motion);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Source humanoid abstraction");
                sb.AppendLine($"- Failed to evaluate source humanoid abstraction: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendSourceHumanoidRoundtripDiagnosis(sb, motion);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Source humanoid roundtrip loss");
                sb.AppendLine($"- Failed to evaluate source humanoid roundtrip loss: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendTargetRetargetTransferDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Target retarget transfer");
                sb.AppendLine($"- Failed to evaluate target retarget transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendMuscleRangeTransferDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Muscle range transfer");
                sb.AppendLine($"- Failed to evaluate muscle range transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendTargetWorldSpaceMotionDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Target world-space motion transfer");
                sb.AppendLine($"- Failed to evaluate target world-space motion transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendTargetRelativePoseMotionDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Target relative-pose motion");
                sb.AppendLine($"- Failed to evaluate target relative-pose motion: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendGaitSemanticDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Gait semantic transfer");
                sb.AppendLine($"- Failed to evaluate gait semantic transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendFixedFrameGaitDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Fixed-frame gait semantic transfer");
                sb.AppendLine($"- Failed to evaluate fixed-frame gait semantic transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendEndEffectorDirectionDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## End-effector direction transfer");
                sb.AppendLine($"- Failed to evaluate end-effector direction transfer: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendRootLocomotionDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Root locomotion contract");
                sb.AppendLine($"- Failed to evaluate root locomotion contract: {ex.Message}");
                sb.AppendLine();
            }

            try
            {
                AppendBakedClipRoundtripDiagnosis(sb, motion, animator);
            }
            catch (Exception ex)
            {
                sb.AppendLine("## Baked clip roundtrip");
                sb.AppendLine($"- Failed to evaluate baked clip roundtrip: {ex.Message}");
                sb.AppendLine();
            }
        }

        private static void AppendQuaternionConversionCandidateDiagnosis(StringBuilder sb, CanonicalMotionJson motion)
        {
            sb.AppendLine("### Quaternion conversion candidates");

            var candidates = new (string name, Func<Quaternion, Quaternion> converter)[]
            {
                ("Current sign-flip", SourceQuaternionToUnity),
                ("Mirrored basis via LookRotation", SourceQuaternionToUnityViaMirroredBasis),
                ("Current sign-flip + inverse", q => Quaternion.Inverse(SourceQuaternionToUnity(q))),
                ("Mirrored basis + inverse", q => Quaternion.Inverse(SourceQuaternionToUnityViaMirroredBasis(q))),
            };

            sb.AppendLine("| Candidate | Mean error | Max error |\n|---|---:|---:|");

            foreach (var candidate in candidates)
            {
                EvaluateQuaternionConversionFidelity(motion, candidate.converter, out var meanError, out var maxError);
                sb.AppendLine($"| {candidate.name} | {meanError:F5} | {maxError:F5} |");
            }

            sb.AppendLine();
            sb.AppendLine("If one candidate is dramatically better, the main problem is the source quaternion handedness/basis conversion, not Humanoid retargeting.");
            sb.AppendLine();
        }

        private static void AppendSourceHumanoidAbstractionDiagnosis(StringBuilder sb, CanonicalMotionJson motion)
        {
            sb.AppendLine("## Source humanoid abstraction");

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("Could not build exact source rig for humanoid abstraction test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            var sourcePose = new HumanPose();
            float muscleDeltaSum = 0f;
            float muscleDeltaMax = 0f;
            int muscleDeltaSamples = 0;
            float bodyPosDeltaSum = 0f;
            float bodyRotDeltaSum = 0f;
            int frameDeltaSamples = 0;
            float[] previousMuscles = null;
            Vector3 previousBodyPosition = Vector3.zero;
            Quaternion previousBodyRotation = Quaternion.identity;
            bool hasPrevious = false;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);

                    if (sourcePose.muscles == null || sourcePose.muscles.Length != HumanTrait.MuscleCount)
                        continue;

                    if (hasPrevious && previousMuscles != null)
                    {
                        float frameMuscleDelta = 0f;
                        for (int i = 0; i < HumanTrait.MuscleCount; i++)
                        {
                            float delta = Mathf.Abs(sourcePose.muscles[i] - previousMuscles[i]);
                            frameMuscleDelta += delta;
                            muscleDeltaMax = Mathf.Max(muscleDeltaMax, delta);
                        }

                        muscleDeltaSum += frameMuscleDelta / HumanTrait.MuscleCount;
                        muscleDeltaSamples++;
                        bodyPosDeltaSum += Vector3.Distance(sourcePose.bodyPosition, previousBodyPosition);
                        bodyRotDeltaSum += Quaternion.Angle(sourcePose.bodyRotation, previousBodyRotation);
                        frameDeltaSamples++;
                    }

                    previousMuscles = (float[])sourcePose.muscles.Clone();
                    previousBodyPosition = sourcePose.bodyPosition;
                    previousBodyRotation = sourcePose.bodyRotation;
                    hasPrevious = true;
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (muscleDeltaSamples == 0)
            {
                sb.AppendLine("No valid humanoid abstraction samples found.");
                sb.AppendLine();
                return;
            }

            float meanMuscleDelta = muscleDeltaSum / muscleDeltaSamples;
            float meanBodyPosDelta = frameDeltaSamples > 0 ? bodyPosDeltaSum / frameDeltaSamples : 0f;
            float meanBodyRotDelta = frameDeltaSamples > 0 ? bodyRotDeltaSum / frameDeltaSamples : 0f;

            sb.AppendLine($"- Mean per-frame muscle delta: {meanMuscleDelta:F5}");
            sb.AppendLine($"- Max single-muscle frame delta: {muscleDeltaMax:F5}");
            sb.AppendLine($"- Mean per-frame bodyPosition delta: {meanBodyPosDelta:F5}");
            sb.AppendLine($"- Mean per-frame bodyRotation delta: {meanBodyRotDelta:F5}°");

            if (meanMuscleDelta < 0.0025f && meanBodyRotDelta < 0.5f)
                sb.AppendLine("- Interpretation: The exact source rig animates, but Unity Humanoid collapses it into an almost static human pose. The main problem is the source-humanoid abstraction layer.");
            else
                sb.AppendLine("- Interpretation: Unity Humanoid is registering meaningful motion from the source rig; the remaining issue is likely target retarget semantics or source avatar definition details.");

            sb.AppendLine();
        }

        private static void AppendSourceHumanoidRoundtripDiagnosis(StringBuilder sb, CanonicalMotionJson motion)
        {
            sb.AppendLine("## Source humanoid roundtrip loss");

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            var roundtripRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.jointAnchors == null ||
                roundtripRig?.rootObject == null || roundtripRig.boneTransforms == null || roundtripRig.jointAnchors == null)
            {
                sb.AppendLine("- Could not build exact source rigs for humanoid roundtrip test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            Avatar roundtripAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler roundtripPoseHandler = null;
            var sourcePose = new HumanPose();

            float jointErrorSum = 0f;
            float jointErrorMax = 0f;
            int jointSamples = 0;
            float exactFrameDeltaSum = 0f;
            float roundtripFrameDeltaSum = 0f;
            int deltaSamples = 0;
            Vector3[] previousExactPositions = null;
            Vector3[] previousRoundtripPositions = null;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                roundtripAvatar = BuildCanonicalSourceAvatar(roundtripRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman ||
                    roundtripAvatar == null || !roundtripAvatar.isValid || !roundtripAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build valid humanoid avatars from the exact source rigs.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                roundtripPoseHandler = new HumanPoseHandler(roundtripAvatar, roundtripRig.rootObject.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    roundtripPoseHandler.SetHumanPose(ref sourcePose);

                    var exactPositions = CaptureWorldPositions(exactRig.jointAnchors);
                    var roundtripPositions = CaptureWorldPositions(roundtripRig.jointAnchors);

                    for (int jointIndex = 0; jointIndex < Mathf.Min(exactPositions.Length, roundtripPositions.Length); jointIndex++)
                    {
                        float error = Vector3.Distance(exactPositions[jointIndex], roundtripPositions[jointIndex]);
                        jointErrorSum += error;
                        jointErrorMax = Mathf.Max(jointErrorMax, error);
                        jointSamples++;
                    }

                    if (previousExactPositions != null && previousRoundtripPositions != null)
                    {
                        float exactFrameDelta = 0f;
                        float roundtripFrameDelta = 0f;
                        int frameJointCount = Mathf.Min(exactPositions.Length, roundtripPositions.Length);
                        for (int jointIndex = 0; jointIndex < frameJointCount; jointIndex++)
                        {
                            exactFrameDelta += Vector3.Distance(exactPositions[jointIndex], previousExactPositions[jointIndex]);
                            roundtripFrameDelta += Vector3.Distance(roundtripPositions[jointIndex], previousRoundtripPositions[jointIndex]);
                        }

                        exactFrameDeltaSum += exactFrameDelta / Mathf.Max(1, frameJointCount);
                        roundtripFrameDeltaSum += roundtripFrameDelta / Mathf.Max(1, frameJointCount);
                        deltaSamples++;
                    }

                    previousExactPositions = exactPositions;
                    previousRoundtripPositions = roundtripPositions;
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                roundtripPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (roundtripAvatar != null)
                    UnityEngine.Object.DestroyImmediate(roundtripAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
                if (roundtripRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(roundtripRig.rootObject);
            }

            if (jointSamples == 0)
            {
                sb.AppendLine("- No valid source humanoid roundtrip samples found.");
                sb.AppendLine();
                return;
            }

            float meanJointError = jointErrorSum / jointSamples;
            float meanExactFrameDelta = deltaSamples > 0 ? exactFrameDeltaSum / deltaSamples : 0f;
            float meanRoundtripFrameDelta = deltaSamples > 0 ? roundtripFrameDeltaSum / deltaSamples : 0f;
            float roundtripDeltaRatio = meanExactFrameDelta > 1e-6f ? meanRoundtripFrameDelta / meanExactFrameDelta : 0f;

            sb.AppendLine($"- Mean source→humanoid→source joint error: {meanJointError:F5}");
            sb.AppendLine($"- Max source→humanoid→source joint error: {jointErrorMax:F5}");
            sb.AppendLine($"- Mean exact-rig per-frame joint delta: {meanExactFrameDelta:F5}");
            sb.AppendLine($"- Mean roundtripped per-frame joint delta: {meanRoundtripFrameDelta:F5}");
            sb.AppendLine($"- Roundtripped/exact joint delta ratio: {roundtripDeltaRatio:F3}");

            if (roundtripDeltaRatio < 0.35f)
                sb.AppendLine("- Interpretation: converting the exact source rig into Unity Humanoid is strongly damping spatial motion before target retargeting. The source humanoid abstraction is still the main suspect.");
            else if (meanJointError > 0.075f || jointErrorMax > 0.25f)
                sb.AppendLine("- Interpretation: the source Humanoid roundtrip keeps some motion amplitude, but it is materially changing the actual source pose. The remaining issue is still upstream of target retargeting and clip baking.");
            else
                sb.AppendLine("- Interpretation: the source Humanoid abstraction is preserving the exact source rig reasonably well in joint space. If live motion still looks frozen, the remaining issue is likely in editor/runtime playback conditions rather than retarget content.");

            sb.AppendLine();
        }

        private static void AppendTargetRetargetTransferDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Target retarget transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for transfer test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();

            float sourceFrameMuscleDeltaSum = 0f;
            float targetFrameMuscleDeltaSum = 0f;
            int deltaSamples = 0;
            float transferMuscleErrorSum = 0f;
            float transferMuscleErrorMax = 0f;
            int transferSamples = 0;
            float bodyPosTransferErrorSum = 0f;
            float bodyRotTransferErrorSum = 0f;
            int bodyTransferSamples = 0;
            float[] previousSourceMuscles = null;
            float[] previousTargetMuscles = null;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    if (sourcePose.muscles == null || targetPose.muscles == null || sourcePose.muscles.Length != HumanTrait.MuscleCount || targetPose.muscles.Length != HumanTrait.MuscleCount)
                        continue;

                    float frameTransferError = 0f;
                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        float error = Mathf.Abs(targetPose.muscles[i] - sourcePose.muscles[i]);
                        frameTransferError += error;
                        transferMuscleErrorMax = Mathf.Max(transferMuscleErrorMax, error);
                    }

                    transferMuscleErrorSum += frameTransferError / HumanTrait.MuscleCount;
                    transferSamples++;
                    bodyPosTransferErrorSum += Vector3.Distance(targetPose.bodyPosition, sourcePose.bodyPosition);
                    bodyRotTransferErrorSum += Quaternion.Angle(targetPose.bodyRotation, sourcePose.bodyRotation);
                    bodyTransferSamples++;

                    if (previousSourceMuscles != null && previousTargetMuscles != null)
                    {
                        float sourceDelta = 0f;
                        float targetDelta = 0f;
                        for (int i = 0; i < HumanTrait.MuscleCount; i++)
                        {
                            sourceDelta += Mathf.Abs(sourcePose.muscles[i] - previousSourceMuscles[i]);
                            targetDelta += Mathf.Abs(targetPose.muscles[i] - previousTargetMuscles[i]);
                        }

                        sourceFrameMuscleDeltaSum += sourceDelta / HumanTrait.MuscleCount;
                        targetFrameMuscleDeltaSum += targetDelta / HumanTrait.MuscleCount;
                        deltaSamples++;
                    }

                    previousSourceMuscles = (float[])sourcePose.muscles.Clone();
                    previousTargetMuscles = (float[])targetPose.muscles.Clone();
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (transferSamples == 0)
            {
                sb.AppendLine("- No valid target transfer samples found.");
                sb.AppendLine();
                return;
            }

            float meanTransferMuscleError = transferMuscleErrorSum / transferSamples;
            float meanSourceFrameDelta = deltaSamples > 0 ? sourceFrameMuscleDeltaSum / deltaSamples : 0f;
            float meanTargetFrameDelta = deltaSamples > 0 ? targetFrameMuscleDeltaSum / deltaSamples : 0f;
            float targetToSourceDeltaRatio = meanSourceFrameDelta > 1e-6f ? meanTargetFrameDelta / meanSourceFrameDelta : 0f;
            float meanBodyPosTransferError = bodyTransferSamples > 0 ? bodyPosTransferErrorSum / bodyTransferSamples : 0f;
            float meanBodyRotTransferError = bodyTransferSamples > 0 ? bodyRotTransferErrorSum / bodyTransferSamples : 0f;

            sb.AppendLine($"- Mean source per-frame muscle delta: {meanSourceFrameDelta:F5}");
            sb.AppendLine($"- Mean target per-frame muscle delta: {meanTargetFrameDelta:F5}");
            sb.AppendLine($"- Target/source muscle delta ratio: {targetToSourceDeltaRatio:F3}");
            sb.AppendLine($"- Mean source→target muscle error: {meanTransferMuscleError:F5}");
            sb.AppendLine($"- Max source→target single-muscle error: {transferMuscleErrorMax:F5}");
            sb.AppendLine($"- Mean source→target bodyPosition error: {meanBodyPosTransferError:F5}");
            sb.AppendLine($"- Mean source→target bodyRotation error: {meanBodyRotTransferError:F5}°");

            if (targetToSourceDeltaRatio < 0.35f)
                sb.AppendLine("- Interpretation: retargeting into the target humanoid is strongly compressing motion amplitude. The target-avatar retarget semantics are the primary remaining problem.");
            else if (meanTransferMuscleError > 0.08f || meanBodyRotTransferError > 10f)
                sb.AppendLine("- Interpretation: the target humanoid is receiving motion, but pose semantics differ substantially from the source human pose. Avatar definition details or target muscle interpretation are likely wrong.");
            else
                sb.AppendLine("- Interpretation: target retarget transfer is preserving most of the source human-pose motion. If the visible clip still looks wrong, investigate clip baking/application rather than source-to-target humanoid transfer.");

            sb.AppendLine();
        }

        private static void AppendMuscleRangeTransferDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Muscle range transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for muscle range test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();

            var sourceMin = new float[HumanTrait.MuscleCount];
            var sourceMax = new float[HumanTrait.MuscleCount];
            var targetMin = new float[HumanTrait.MuscleCount];
            var targetMax = new float[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                sourceMin[i] = float.PositiveInfinity;
                sourceMax[i] = float.NegativeInfinity;
                targetMin[i] = float.PositiveInfinity;
                targetMax[i] = float.NegativeInfinity;
            }

            int sampleCount = 0;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    if (sourcePose.muscles == null || targetPose.muscles == null || sourcePose.muscles.Length != HumanTrait.MuscleCount || targetPose.muscles.Length != HumanTrait.MuscleCount)
                        continue;

                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        sourceMin[i] = Mathf.Min(sourceMin[i], sourcePose.muscles[i]);
                        sourceMax[i] = Mathf.Max(sourceMax[i], sourcePose.muscles[i]);
                        targetMin[i] = Mathf.Min(targetMin[i], targetPose.muscles[i]);
                        targetMax[i] = Mathf.Max(targetMax[i], targetPose.muscles[i]);
                    }

                    sampleCount++;
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (sampleCount == 0)
            {
                sb.AppendLine("- No valid muscle range samples found.");
                sb.AppendLine();
                return;
            }

            var compressed = new List<(int index, float sourceRange, float targetRange, float ratio)>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                float sourceRange = float.IsInfinity(sourceMin[i]) || float.IsInfinity(sourceMax[i]) ? 0f : sourceMax[i] - sourceMin[i];
                float targetRange = float.IsInfinity(targetMin[i]) || float.IsInfinity(targetMax[i]) ? 0f : targetMax[i] - targetMin[i];
                float ratio = sourceRange > 1e-6f ? targetRange / sourceRange : 0f;
                compressed.Add((i, sourceRange, targetRange, ratio));
            }

            var ordered = compressed
                .Where(x => x.sourceRange > 0.01f)
                .OrderBy(x => x.ratio)
                .ThenByDescending(x => x.sourceRange)
                .Take(18)
                .ToList();

            sb.AppendLine("| Muscle | Source range | Target range | Ratio |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var item in ordered)
                sb.AppendLine($"| {HumanTrait.MuscleName[item.index]} | {item.sourceRange:F5} | {item.targetRange:F5} | {item.ratio:F3} |");

            sb.AppendLine();

            float meanRatio = ordered.Count > 0 ? ordered.Average(x => x.ratio) : 0f;
            if (meanRatio < 0.35f)
                sb.AppendLine("- Interpretation: a specific subset of humanoid muscles is being strongly flattened even though gross pose/world motion survives. The next fix should target those compressed muscle semantics rather than the whole pipeline.");
            else
                sb.AppendLine("- Interpretation: no large muscle-range collapse is visible among the dominant muscles. The remaining issue is more likely directional semantics than raw muscle amplitude.");

            sb.AppendLine();
        }

        private static void AppendTargetWorldSpaceMotionDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Target world-space motion transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length == 0)
            {
                sb.AppendLine("- No motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for world-space transfer test.");
                sb.AppendLine();
                return;
            }

            var trackedIndices = new[] { 0, 7, 8, 10, 11, 15, 20, 21 };
            var trackedTransforms = new Dictionary<int, Transform>();
            foreach (int sourceIndex in trackedIndices)
            {
                if (TryResolveBone(targetAnimator, sourceIndex, out var transform) && transform != null)
                    trackedTransforms[sourceIndex] = transform;
            }

            if (trackedTransforms.Count == 0)
            {
                sb.AppendLine("- Could not resolve any target bones for world-space transfer test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var tempClip = new AnimationClip { frameRate = Mathf.Max(1, motion.fps > 0 ? motion.fps : 20) };

            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();
            foreach (var transform in targetAnimator.GetComponentsInChildren<Transform>(true))
            {
                originalLocalPositions[transform] = transform.localPosition;
                originalLocalRotations[transform] = transform.localRotation;
            }

            float rootScale = ComputeRootScale(targetAnimator, motion);
            var sourceStart = new Dictionary<int, Vector3>();
            var directStart = new Dictionary<int, Vector3>();
            var bakedStart = new Dictionary<int, Vector3>();
            var previousSource = new Dictionary<int, Vector3>();
            var previousDirect = new Dictionary<int, Vector3>();
            var previousBaked = new Dictionary<int, Vector3>();
            var sourceExcursionMax = new Dictionary<int, float>();
            var directExcursionMax = new Dictionary<int, float>();
            var bakedExcursionMax = new Dictionary<int, float>();
            float sourceDeltaSum = 0f;
            float directDeltaSum = 0f;
            float bakedDeltaSum = 0f;
            int directDeltaSamples = 0;
            int bakedDeltaSamples = 0;
            bool startedAnimationModeHere = false;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                if (!TryCreateClipFromExactSourceAvatar(targetAnimator, tempClip, motion, motion.fps > 0 ? motion.fps : 20, null))
                {
                    sb.AppendLine("- Failed to build a temporary baked clip for world-space transfer test.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    float sourceFrameDelta = 0f;
                    float directFrameDelta = 0f;
                    int frameBoneCount = 0;

                    foreach (var kv in trackedTransforms)
                    {
                        int sourceIndex = kv.Key;
                        var targetTransform = kv.Value;
                        Vector3 sourcePosition = sourceWorld[sourceIndex] * rootScale;
                        Vector3 directPosition = targetTransform.position;

                        if (!sourceStart.ContainsKey(sourceIndex))
                        {
                            sourceStart[sourceIndex] = sourcePosition;
                            directStart[sourceIndex] = directPosition;
                            sourceExcursionMax[sourceIndex] = 0f;
                            directExcursionMax[sourceIndex] = 0f;
                        }

                        sourceExcursionMax[sourceIndex] = Mathf.Max(sourceExcursionMax[sourceIndex], Vector3.Distance(sourcePosition, sourceStart[sourceIndex]));
                        directExcursionMax[sourceIndex] = Mathf.Max(directExcursionMax[sourceIndex], Vector3.Distance(directPosition, directStart[sourceIndex]));

                        if (previousSource.TryGetValue(sourceIndex, out var previousSourcePosition) && previousDirect.TryGetValue(sourceIndex, out var previousDirectPosition))
                        {
                            sourceFrameDelta += Vector3.Distance(sourcePosition, previousSourcePosition);
                            directFrameDelta += Vector3.Distance(directPosition, previousDirectPosition);
                            frameBoneCount++;
                        }

                        previousSource[sourceIndex] = sourcePosition;
                        previousDirect[sourceIndex] = directPosition;
                    }

                    if (frameBoneCount > 0)
                    {
                        sourceDeltaSum += sourceFrameDelta / frameBoneCount;
                        directDeltaSum += directFrameDelta / frameBoneCount;
                        directDeltaSamples++;
                    }
                }

                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;

                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    startedAnimationModeHere = true;
                }

                float frameTime = 1f / Mathf.Max(1, motion.fps > 0 ? motion.fps : 20);
                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    float time = frameIndex * frameTime;
                    AnimationMode.SampleAnimationClip(targetAnimator.gameObject, tempClip, time);

                    float bakedFrameDelta = 0f;
                    int frameBoneCount = 0;
                    foreach (var kv in trackedTransforms)
                    {
                        int sourceIndex = kv.Key;
                        Vector3 bakedPosition = kv.Value.position;

                        if (!bakedStart.ContainsKey(sourceIndex))
                        {
                            bakedStart[sourceIndex] = bakedPosition;
                            bakedExcursionMax[sourceIndex] = 0f;
                        }

                        bakedExcursionMax[sourceIndex] = Mathf.Max(bakedExcursionMax[sourceIndex], Vector3.Distance(bakedPosition, bakedStart[sourceIndex]));

                        if (previousBaked.TryGetValue(sourceIndex, out var previousBakedPosition))
                        {
                            bakedFrameDelta += Vector3.Distance(bakedPosition, previousBakedPosition);
                            frameBoneCount++;
                        }

                        previousBaked[sourceIndex] = bakedPosition;
                    }

                    if (frameBoneCount > 0)
                    {
                        bakedDeltaSum += bakedFrameDelta / frameBoneCount;
                        bakedDeltaSamples++;
                    }
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (startedAnimationModeHere && AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            float meanDirectDelta = directDeltaSamples > 0 ? directDeltaSum / directDeltaSamples : 0f;
            float meanSourceDelta = directDeltaSamples > 0 ? sourceDeltaSum / directDeltaSamples : 0f;
            float meanBakedDelta = bakedDeltaSamples > 0 ? bakedDeltaSum / bakedDeltaSamples : 0f;

            sb.AppendLine($"- Mean source tracked-bone per-frame delta: {meanSourceDelta:F5}");
            sb.AppendLine($"- Mean direct-target tracked-bone per-frame delta: {meanDirectDelta:F5}");
            sb.AppendLine($"- Mean baked-target tracked-bone per-frame delta: {meanBakedDelta:F5}");
            sb.AppendLine($"- Direct/source tracked-bone delta ratio: {(meanSourceDelta > 1e-6f ? meanDirectDelta / meanSourceDelta : 0f):F3}");
            sb.AppendLine($"- Baked/direct tracked-bone delta ratio: {(meanDirectDelta > 1e-6f ? meanBakedDelta / meanDirectDelta : 0f):F3}");
            sb.AppendLine();
            sb.AppendLine("| Bone | Source excursion | Direct target excursion | Baked target excursion | Direct/source | Baked/direct |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");

            foreach (int sourceIndex in trackedIndices)
            {
                if (!trackedTransforms.ContainsKey(sourceIndex))
                    continue;

                float sourceExc = sourceExcursionMax.TryGetValue(sourceIndex, out var sourceValue) ? sourceValue : 0f;
                float directExc = directExcursionMax.TryGetValue(sourceIndex, out var directValue) ? directValue : 0f;
                float bakedExc = bakedExcursionMax.TryGetValue(sourceIndex, out var bakedValue) ? bakedValue : 0f;
                float directRatio = sourceExc > 1e-6f ? directExc / sourceExc : 0f;
                float bakedRatio = directExc > 1e-6f ? bakedExc / directExc : 0f;

                sb.AppendLine($"| {GetWorldSpaceDiagnosticLabel(sourceIndex)} | {sourceExc:F5} | {directExc:F5} | {bakedExc:F5} | {directRatio:F3} | {bakedRatio:F3} |");
            }

            sb.AppendLine();
            if (meanSourceDelta > 1e-6f && (meanDirectDelta / meanSourceDelta) < 0.35f)
                sb.AppendLine("- Interpretation: world-space motion is being strongly compressed before clip baking. The remaining problem is in source→target humanoid spatial transfer, not clip application.");
            else if (meanDirectDelta > 1e-6f && (meanBakedDelta / meanDirectDelta) < 0.35f)
                sb.AppendLine("- Interpretation: direct retargeted world-space motion is present, but baked clip world-space motion is being compressed. Investigate clip baking/evaluation rather than humanoid transfer.");
            else
                sb.AppendLine("- Interpretation: tracked target-bone world motion is broadly preserved through direct retarget and baking. If the result still looks wrong, inspect specific bone trajectories or scene playback conditions rather than overall motion amplitude.");

            sb.AppendLine();
        }

        private static void AppendTargetRelativePoseMotionDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Target relative-pose motion");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length == 0)
            {
                sb.AppendLine("- No motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for relative-pose test.");
                sb.AppendLine();
                return;
            }

            var trackedIndices = new[] { 7, 8, 10, 11, 15, 20, 21 };
            var trackedTransforms = new Dictionary<int, Transform>();
            foreach (int sourceIndex in trackedIndices)
            {
                if (TryResolveBone(targetAnimator, sourceIndex, out var transform) && transform != null)
                    trackedTransforms[sourceIndex] = transform;
            }

            var hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || trackedTransforms.Count == 0)
            {
                sb.AppendLine("- Could not resolve hips or tracked target bones.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var tempClip = new AnimationClip { frameRate = Mathf.Max(1, motion.fps > 0 ? motion.fps : 20) };

            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();
            foreach (var transform in targetAnimator.GetComponentsInChildren<Transform>(true))
            {
                originalLocalPositions[transform] = transform.localPosition;
                originalLocalRotations[transform] = transform.localRotation;
            }

            var sourceStart = new Dictionary<int, Vector3>();
            var directStart = new Dictionary<int, Vector3>();
            var bakedStart = new Dictionary<int, Vector3>();
            var previousSource = new Dictionary<int, Vector3>();
            var previousDirect = new Dictionary<int, Vector3>();
            var previousBaked = new Dictionary<int, Vector3>();
            var sourceExcursionMax = new Dictionary<int, float>();
            var directExcursionMax = new Dictionary<int, float>();
            var bakedExcursionMax = new Dictionary<int, float>();
            float sourceDeltaSum = 0f;
            float directDeltaSum = 0f;
            float bakedDeltaSum = 0f;
            int directDeltaSamples = 0;
            int bakedDeltaSamples = 0;
            bool startedAnimationModeHere = false;
            float rootScale = ComputeRootScale(targetAnimator, motion);

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                if (!TryCreateClipFromExactSourceAvatar(targetAnimator, tempClip, motion, motion.fps > 0 ? motion.fps : 20, null))
                {
                    sb.AppendLine("- Failed to build a temporary baked clip for relative-pose test.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    var directHipsPosition = hips.position;
                    var directHipsRotation = hips.rotation;
                    float sourceFrameDelta = 0f;
                    float directFrameDelta = 0f;
                    int frameBoneCount = 0;

                    foreach (var kv in trackedTransforms)
                    {
                        int sourceIndex = kv.Key;
                        var targetTransform = kv.Value;

                        Vector3 sourceRelative = sourceWorld[sourceIndex] - sourceWorld[0];
                        sourceRelative *= rootScale;
                        Vector3 directRelative = Quaternion.Inverse(directHipsRotation) * (targetTransform.position - directHipsPosition);

                        if (!sourceStart.ContainsKey(sourceIndex))
                        {
                            sourceStart[sourceIndex] = sourceRelative;
                            directStart[sourceIndex] = directRelative;
                            sourceExcursionMax[sourceIndex] = 0f;
                            directExcursionMax[sourceIndex] = 0f;
                        }

                        sourceExcursionMax[sourceIndex] = Mathf.Max(sourceExcursionMax[sourceIndex], Vector3.Distance(sourceRelative, sourceStart[sourceIndex]));
                        directExcursionMax[sourceIndex] = Mathf.Max(directExcursionMax[sourceIndex], Vector3.Distance(directRelative, directStart[sourceIndex]));

                        if (previousSource.TryGetValue(sourceIndex, out var previousSourceRelative) && previousDirect.TryGetValue(sourceIndex, out var previousDirectRelative))
                        {
                            sourceFrameDelta += Vector3.Distance(sourceRelative, previousSourceRelative);
                            directFrameDelta += Vector3.Distance(directRelative, previousDirectRelative);
                            frameBoneCount++;
                        }

                        previousSource[sourceIndex] = sourceRelative;
                        previousDirect[sourceIndex] = directRelative;
                    }

                    if (frameBoneCount > 0)
                    {
                        sourceDeltaSum += sourceFrameDelta / frameBoneCount;
                        directDeltaSum += directFrameDelta / frameBoneCount;
                        directDeltaSamples++;
                    }
                }

                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;

                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    startedAnimationModeHere = true;
                }

                float frameTime = 1f / Mathf.Max(1, motion.fps > 0 ? motion.fps : 20);
                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    float time = frameIndex * frameTime;
                    AnimationMode.SampleAnimationClip(targetAnimator.gameObject, tempClip, time);

                    var bakedHipsPosition = hips.position;
                    var bakedHipsRotation = hips.rotation;
                    float bakedFrameDelta = 0f;
                    int frameBoneCount = 0;

                    foreach (var kv in trackedTransforms)
                    {
                        int sourceIndex = kv.Key;
                        Vector3 bakedRelative = Quaternion.Inverse(bakedHipsRotation) * (kv.Value.position - bakedHipsPosition);

                        if (!bakedStart.ContainsKey(sourceIndex))
                        {
                            bakedStart[sourceIndex] = bakedRelative;
                            bakedExcursionMax[sourceIndex] = 0f;
                        }

                        bakedExcursionMax[sourceIndex] = Mathf.Max(bakedExcursionMax[sourceIndex], Vector3.Distance(bakedRelative, bakedStart[sourceIndex]));

                        if (previousBaked.TryGetValue(sourceIndex, out var previousBakedRelative))
                        {
                            bakedFrameDelta += Vector3.Distance(bakedRelative, previousBakedRelative);
                            frameBoneCount++;
                        }

                        previousBaked[sourceIndex] = bakedRelative;
                    }

                    if (frameBoneCount > 0)
                    {
                        bakedDeltaSum += bakedFrameDelta / frameBoneCount;
                        bakedDeltaSamples++;
                    }
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (startedAnimationModeHere && AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            float meanSourceDelta = directDeltaSamples > 0 ? sourceDeltaSum / directDeltaSamples : 0f;
            float meanDirectDelta = directDeltaSamples > 0 ? directDeltaSum / directDeltaSamples : 0f;
            float meanBakedDelta = bakedDeltaSamples > 0 ? bakedDeltaSum / bakedDeltaSamples : 0f;

            sb.AppendLine($"- Mean source relative-pose per-frame delta: {meanSourceDelta:F5}");
            sb.AppendLine($"- Mean direct-target relative-pose per-frame delta: {meanDirectDelta:F5}");
            sb.AppendLine($"- Mean baked-target relative-pose per-frame delta: {meanBakedDelta:F5}");
            sb.AppendLine($"- Direct/source relative-pose delta ratio: {(meanSourceDelta > 1e-6f ? meanDirectDelta / meanSourceDelta : 0f):F3}");
            sb.AppendLine($"- Baked/direct relative-pose delta ratio: {(meanDirectDelta > 1e-6f ? meanBakedDelta / meanDirectDelta : 0f):F3}");
            sb.AppendLine();
            sb.AppendLine("| Bone | Source relative excursion | Direct target relative excursion | Baked target relative excursion | Direct/source | Baked/direct |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");

            foreach (int sourceIndex in trackedIndices)
            {
                if (!trackedTransforms.ContainsKey(sourceIndex))
                    continue;

                float sourceExc = sourceExcursionMax.TryGetValue(sourceIndex, out var sourceValue) ? sourceValue : 0f;
                float directExc = directExcursionMax.TryGetValue(sourceIndex, out var directValue) ? directValue : 0f;
                float bakedExc = bakedExcursionMax.TryGetValue(sourceIndex, out var bakedValue) ? bakedValue : 0f;
                float directRatio = sourceExc > 1e-6f ? directExc / sourceExc : 0f;
                float bakedRatio = directExc > 1e-6f ? bakedExc / directExc : 0f;

                sb.AppendLine($"| {GetWorldSpaceDiagnosticLabel(sourceIndex)} | {sourceExc:F5} | {directExc:F5} | {bakedExc:F5} | {directRatio:F3} | {bakedRatio:F3} |");
            }

            sb.AppendLine();
            if (meanSourceDelta > 1e-6f && (meanDirectDelta / meanSourceDelta) < 0.35f)
                sb.AppendLine("- Interpretation: articulated pose motion relative to the hips is being strongly compressed during source→target humanoid transfer. This matches the visually static-pose symptom.");
            else if (meanDirectDelta > 1e-6f && (meanBakedDelta / meanDirectDelta) < 0.35f)
                sb.AppendLine("- Interpretation: direct retargeted relative-pose motion exists, but the baked clip is flattening it. Investigate curve baking/evaluation rather than humanoid transfer.");
            else
                sb.AppendLine("- Interpretation: root-relative pose motion is broadly preserved. If the pose still looks static, inspect specific bone semantics or avatar muscle mapping rather than overall articulated amplitude.");

            sb.AppendLine();
        }

        private static void AppendGaitSemanticDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Gait semantic transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length == 0)
            {
                sb.AppendLine("- No motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for gait semantic test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();

            var targetHips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
            var targetLeftFoot = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var targetRightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
            var targetLeftHand = targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            var targetRightHand = targetAnimator.GetBoneTransform(HumanBodyBones.RightHand);
            var targetHead = targetAnimator.GetBoneTransform(HumanBodyBones.Head);
            var targetLeftUpperLeg = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            var targetLeftLowerLeg = targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            var targetRightUpperLeg = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            var targetRightLowerLeg = targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            var targetLeftUpperArm = targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var targetLeftLowerArm = targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            var targetRightUpperArm = targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var targetRightLowerArm = targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm);

            if (targetHips == null || targetLeftFoot == null || targetRightFoot == null || targetLeftHand == null || targetRightHand == null || targetHead == null)
            {
                sb.AppendLine("- Target avatar is missing one or more required tracked bones.");
                sb.AppendLine();
                return;
            }

            var sourceStats = new GaitSemanticStats();
            var targetStats = new GaitSemanticStats();

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    var sourceFrame = BuildSourceBodyFrame(sourceWorld);

                    sourceStats.Accumulate(
                        InBodyFrame(sourceWorld[0], sourceFrame, sourceWorld[7]),
                        InBodyFrame(sourceWorld[0], sourceFrame, sourceWorld[8]),
                        InBodyFrame(sourceWorld[0], sourceFrame, sourceWorld[20]),
                        InBodyFrame(sourceWorld[0], sourceFrame, sourceWorld[21]),
                        InBodyFrame(sourceWorld[0], sourceFrame, sourceWorld[15]),
                        ComputeJointAngleDegrees(sourceWorld[1], sourceWorld[4], sourceWorld[7]),
                        ComputeJointAngleDegrees(sourceWorld[2], sourceWorld[5], sourceWorld[8]),
                        ComputeJointAngleDegrees(sourceWorld[16], sourceWorld[18], sourceWorld[20]),
                        ComputeJointAngleDegrees(sourceWorld[17], sourceWorld[19], sourceWorld[21]));

                    targetStats.Accumulate(
                        targetHips.InverseTransformPoint(targetLeftFoot.position),
                        targetHips.InverseTransformPoint(targetRightFoot.position),
                        targetHips.InverseTransformPoint(targetLeftHand.position),
                        targetHips.InverseTransformPoint(targetRightHand.position),
                        targetHips.InverseTransformPoint(targetHead.position),
                        ComputeJointAngleDegrees(targetLeftUpperLeg != null ? targetLeftUpperLeg.position : targetHips.position, targetLeftLowerLeg != null ? targetLeftLowerLeg.position : targetLeftFoot.position, targetLeftFoot.position),
                        ComputeJointAngleDegrees(targetRightUpperLeg != null ? targetRightUpperLeg.position : targetHips.position, targetRightLowerLeg != null ? targetRightLowerLeg.position : targetRightFoot.position, targetRightFoot.position),
                        ComputeJointAngleDegrees(targetLeftUpperArm != null ? targetLeftUpperArm.position : targetHips.position, targetLeftLowerArm != null ? targetLeftLowerArm.position : targetLeftHand.position, targetLeftHand.position),
                        ComputeJointAngleDegrees(targetRightUpperArm != null ? targetRightUpperArm.position : targetHips.position, targetRightLowerArm != null ? targetRightLowerArm.position : targetRightHand.position, targetRightHand.position));
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (sourceStats.sampleCount == 0 || targetStats.sampleCount == 0)
            {
                sb.AppendLine("- No valid gait semantic samples found.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("| Metric | Source | Target | Ratio |");
            sb.AppendLine("|---|---:|---:|---:|");
            AppendGaitMetricRow(sb, "Left foot forward range", sourceStats.LeftFootForwardRange, targetStats.LeftFootForwardRange);
            AppendGaitMetricRow(sb, "Right foot forward range", sourceStats.RightFootForwardRange, targetStats.RightFootForwardRange);
            AppendGaitMetricRow(sb, "Left foot height range", sourceStats.LeftFootHeightRange, targetStats.LeftFootHeightRange);
            AppendGaitMetricRow(sb, "Right foot height range", sourceStats.RightFootHeightRange, targetStats.RightFootHeightRange);
            AppendGaitMetricRow(sb, "Left foot lateral range", sourceStats.LeftFootLateralRange, targetStats.LeftFootLateralRange);
            AppendGaitMetricRow(sb, "Right foot lateral range", sourceStats.RightFootLateralRange, targetStats.RightFootLateralRange);
            AppendGaitMetricRow(sb, "Left hand forward range", sourceStats.LeftHandForwardRange, targetStats.LeftHandForwardRange);
            AppendGaitMetricRow(sb, "Right hand forward range", sourceStats.RightHandForwardRange, targetStats.RightHandForwardRange);
            AppendGaitMetricRow(sb, "Head vertical range", sourceStats.HeadHeightRange, targetStats.HeadHeightRange);
            AppendGaitMetricRow(sb, "Left knee bend range", sourceStats.LeftKneeRange, targetStats.LeftKneeRange);
            AppendGaitMetricRow(sb, "Right knee bend range", sourceStats.RightKneeRange, targetStats.RightKneeRange);
            AppendGaitMetricRow(sb, "Left elbow bend range", sourceStats.LeftElbowRange, targetStats.LeftElbowRange);
            AppendGaitMetricRow(sb, "Right elbow bend range", sourceStats.RightElbowRange, targetStats.RightElbowRange);
            sb.AppendLine();

            float footForwardRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootForwardRange, sourceStats.LeftFootForwardRange),
                SafeRatio(targetStats.RightFootForwardRange, sourceStats.RightFootForwardRange));
            float footHeightRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootHeightRange, sourceStats.LeftFootHeightRange),
                SafeRatio(targetStats.RightFootHeightRange, sourceStats.RightFootHeightRange));
            float footLateralRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootLateralRange, sourceStats.LeftFootLateralRange),
                SafeRatio(targetStats.RightFootLateralRange, sourceStats.RightFootLateralRange));
            float kneeRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftKneeRange, sourceStats.LeftKneeRange),
                SafeRatio(targetStats.RightKneeRange, sourceStats.RightKneeRange));

            if (footForwardRatio < 0.45f && footLateralRatio > 0.8f)
                sb.AppendLine("- Interpretation: sagittal foot swing is being flattened while side-to-side motion survives. This matches a character that wiggles in place instead of stepping.");
            else if (kneeRatio < 0.45f)
                sb.AppendLine("- Interpretation: knee flexion range is being strongly flattened. This matches a near-static gait pose even when root and world motion exist.");
            else if (footHeightRatio > 1.8f && footForwardRatio < 0.8f)
                sb.AppendLine("- Interpretation: vertical bounce is overrepresented relative to forward stepping. This matches a bouncing/drifting look rather than a walk.");
            else
                sb.AppendLine("- Interpretation: overall gait ranges are broadly preserved. The remaining issue is likely specific directional semantics or avatar muscle mapping, not gross amplitude loss.");

            sb.AppendLine();
        }

        private static void AppendFixedFrameGaitDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Fixed-frame gait semantic transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length == 0)
            {
                sb.AppendLine("- No motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for fixed-frame gait test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();

            var targetHips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
            var targetLeftFoot = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var targetRightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
            var targetLeftHand = targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            var targetRightHand = targetAnimator.GetBoneTransform(HumanBodyBones.RightHand);
            var targetHead = targetAnimator.GetBoneTransform(HumanBodyBones.Head);

            if (targetHips == null || targetLeftFoot == null || targetRightFoot == null || targetLeftHand == null || targetRightHand == null || targetHead == null)
            {
                sb.AppendLine("- Target avatar is missing one or more required tracked bones.");
                sb.AppendLine();
                return;
            }

            var sourceStats = new GaitSemanticStats();
            var targetStats = new GaitSemanticStats();
            bool haveFixedFrames = false;
            SourceBodyFrame sourceFixedFrame = default;
            SourceBodyFrame targetFixedFrame = default;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    if (!haveFixedFrames)
                    {
                        sourceFixedFrame = BuildSourceBodyFrame(sourceWorld);
                        targetFixedFrame = new SourceBodyFrame(targetHips.position, targetHips.right, targetHips.up, targetHips.forward);
                        haveFixedFrames = true;
                    }

                    sourceStats.Accumulate(
                        InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[7]),
                        InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[8]),
                        InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[20]),
                        InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[21]),
                        InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[15]),
                        0f,
                        0f,
                        0f,
                        0f);

                    targetStats.Accumulate(
                        InFixedFrame(targetHips.position, targetFixedFrame, targetLeftFoot.position),
                        InFixedFrame(targetHips.position, targetFixedFrame, targetRightFoot.position),
                        InFixedFrame(targetHips.position, targetFixedFrame, targetLeftHand.position),
                        InFixedFrame(targetHips.position, targetFixedFrame, targetRightHand.position),
                        InFixedFrame(targetHips.position, targetFixedFrame, targetHead.position),
                        0f,
                        0f,
                        0f,
                        0f);
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (!haveFixedFrames || sourceStats.sampleCount == 0 || targetStats.sampleCount == 0)
            {
                sb.AppendLine("- No valid fixed-frame gait samples found.");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("| Metric | Source | Target | Ratio |");
            sb.AppendLine("|---|---:|---:|---:|");
            AppendGaitMetricRow(sb, "Left foot forward range", sourceStats.LeftFootForwardRange, targetStats.LeftFootForwardRange);
            AppendGaitMetricRow(sb, "Right foot forward range", sourceStats.RightFootForwardRange, targetStats.RightFootForwardRange);
            AppendGaitMetricRow(sb, "Left foot height range", sourceStats.LeftFootHeightRange, targetStats.LeftFootHeightRange);
            AppendGaitMetricRow(sb, "Right foot height range", sourceStats.RightFootHeightRange, targetStats.RightFootHeightRange);
            AppendGaitMetricRow(sb, "Left foot lateral range", sourceStats.LeftFootLateralRange, targetStats.LeftFootLateralRange);
            AppendGaitMetricRow(sb, "Right foot lateral range", sourceStats.RightFootLateralRange, targetStats.RightFootLateralRange);
            AppendGaitMetricRow(sb, "Left hand forward range", sourceStats.LeftHandForwardRange, targetStats.LeftHandForwardRange);
            AppendGaitMetricRow(sb, "Right hand forward range", sourceStats.RightHandForwardRange, targetStats.RightHandForwardRange);
            AppendGaitMetricRow(sb, "Head vertical range", sourceStats.HeadHeightRange, targetStats.HeadHeightRange);
            sb.AppendLine();

            float footForwardRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootForwardRange, sourceStats.LeftFootForwardRange),
                SafeRatio(targetStats.RightFootForwardRange, sourceStats.RightFootForwardRange));
            float footHeightRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootHeightRange, sourceStats.LeftFootHeightRange),
                SafeRatio(targetStats.RightFootHeightRange, sourceStats.RightFootHeightRange));
            float footLateralRatio = AverageNonZeroRatios(
                SafeRatio(targetStats.LeftFootLateralRange, sourceStats.LeftFootLateralRange),
                SafeRatio(targetStats.RightFootLateralRange, sourceStats.RightFootLateralRange));

            if (footForwardRatio < 0.35f && footLateralRatio < 0.35f && footHeightRatio < 0.35f)
                sb.AppendLine("- Interpretation: even in a fixed body frame, target end-effectors are barely moving. The visually static pose is real, not a measurement artifact from using the rotating hips frame.");
            else
                sb.AppendLine("- Interpretation: much of the earlier collapse came from the rotating-frame measurement. The remaining issue is more likely directional mapping than total end-effector loss.");

            sb.AppendLine();
        }

        private static void AppendEndEffectorDirectionDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## End-effector direction transfer");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length < 2)
            {
                sb.AppendLine("- Not enough motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for direction test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();

            var targetHips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
            var targetMap = new Dictionary<int, Transform>
            {
                { 7, targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot) },
                { 8, targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot) },
                { 20, targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand) },
                { 21, targetAnimator.GetBoneTransform(HumanBodyBones.RightHand) },
            };

            if (targetHips == null || targetMap.Values.Any(t => t == null))
            {
                sb.AppendLine("- Target avatar is missing one or more required tracked bones.");
                sb.AppendLine();
                return;
            }

            var stats = new Dictionary<int, DirectionalTransferStats>
            {
                { 7, new DirectionalTransferStats() },
                { 8, new DirectionalTransferStats() },
                { 20, new DirectionalTransferStats() },
                { 21, new DirectionalTransferStats() },
            };

            bool haveFixedFrames = false;
            SourceBodyFrame sourceFixedFrame = default;
            SourceBodyFrame targetFixedFrame = default;
            var previousSourceRelative = new Dictionary<int, Vector3>();
            var previousTargetRelative = new Dictionary<int, Vector3>();

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    if (!haveFixedFrames)
                    {
                        sourceFixedFrame = BuildSourceBodyFrame(sourceWorld);
                        targetFixedFrame = new SourceBodyFrame(targetHips.position, targetHips.right, targetHips.up, targetHips.forward);
                        haveFixedFrames = true;
                    }

                    foreach (var kv in targetMap)
                    {
                        int sourceIndex = kv.Key;
                        Vector3 sourceRelative = InFixedFrame(sourceWorld[0], sourceFixedFrame, sourceWorld[sourceIndex]);
                        Vector3 targetRelative = InFixedFrame(targetHips.position, targetFixedFrame, kv.Value.position);

                        if (previousSourceRelative.TryGetValue(sourceIndex, out var previousSource) && previousTargetRelative.TryGetValue(sourceIndex, out var previousTarget))
                        {
                            stats[sourceIndex].Accumulate(sourceRelative - previousSource, targetRelative - previousTarget);
                        }

                        previousSourceRelative[sourceIndex] = sourceRelative;
                        previousTargetRelative[sourceIndex] = targetRelative;
                    }
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            sb.AppendLine("| Effector | Src Lat% | Src Up% | Src Fwd% | Tgt Lat% | Tgt Up% | Tgt Fwd% | Mean dir cosine |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            AppendDirectionRow(sb, "LeftFoot", stats[7]);
            AppendDirectionRow(sb, "RightFoot", stats[8]);
            AppendDirectionRow(sb, "LeftHand", stats[20]);
            AppendDirectionRow(sb, "RightHand", stats[21]);
            sb.AppendLine();

            if (stats[8].TargetForwardShare + 0.15f < stats[8].SourceForwardShare && stats[8].TargetLateralShare > stats[8].SourceLateralShare + 0.15f)
                sb.AppendLine("- Interpretation: right-foot swing is being rotated from forward stepping into lateral motion. This matches drifting/wiggling instead of a clean gait.");
            else if (stats[7].MeanDirectionCosine < 0.35f || stats[8].MeanDirectionCosine < 0.35f)
                sb.AppendLine("- Interpretation: foot swing directions are poorly aligned between source and target even though amplitude survives. The remaining issue is directional mapping, not motion loss.");
            else
                sb.AppendLine("- Interpretation: end-effector swing directions are broadly aligned. The remaining issue is likely a more specific limb semantic or avatar-axis problem.");

            sb.AppendLine();
        }

        private static void AppendRootLocomotionDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Root locomotion contract");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length < 2)
            {
                sb.AppendLine("- Not enough frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for locomotion test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();

            Vector3 sourceRootStart = Vector3.zero;
            Vector3 sourceHipsStart = Vector3.zero;
            Vector3 targetBodyStart = Vector3.zero;
            Vector3 targetHipsStart = Vector3.zero;
            Vector3 targetAnimatorStart = Vector3.zero;
            bool hasStart = false;

            float sourceRootTravel = 0f;
            float sourceHipsTravel = 0f;
            float targetBodyTravel = 0f;
            float targetHipsTravel = 0f;
            float targetAnimatorTravel = 0f;

            Vector3 previousSourceRoot = Vector3.zero;
            Vector3 previousSourceHips = Vector3.zero;
            Vector3 previousTargetBody = Vector3.zero;
            Vector3 previousTargetHips = Vector3.zero;
            Vector3 previousTargetAnimator = Vector3.zero;
            bool hasPrevious = false;

            float sourceRootNet = 0f;
            float sourceHipsNet = 0f;
            float targetBodyNet = 0f;
            float targetHipsNet = 0f;
            float targetAnimatorNet = 0f;

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                var hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips == null)
                {
                    sb.AppendLine("- Target avatar is missing hips transform.");
                    sb.AppendLine();
                    return;
                }

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    var sourceWorld = BuildSourceWorldPositionsUnity(frame);
                    Vector3 sourceRoot = sourceWorld[0];
                    Vector3 sourceHips = sourceWorld[0];
                    Vector3 targetBody = targetPose.bodyPosition;
                    Vector3 targetHips = hips.position;
                    Vector3 targetAnimatorPosition = targetAnimator.transform.position;

                    if (!hasStart)
                    {
                        sourceRootStart = sourceRoot;
                        sourceHipsStart = sourceHips;
                        targetBodyStart = targetBody;
                        targetHipsStart = targetHips;
                        targetAnimatorStart = targetAnimatorPosition;
                        hasStart = true;
                    }

                    if (hasPrevious)
                    {
                        sourceRootTravel += Vector3.Distance(sourceRoot, previousSourceRoot);
                        sourceHipsTravel += Vector3.Distance(sourceHips, previousSourceHips);
                        targetBodyTravel += Vector3.Distance(targetBody, previousTargetBody);
                        targetHipsTravel += Vector3.Distance(targetHips, previousTargetHips);
                        targetAnimatorTravel += Vector3.Distance(targetAnimatorPosition, previousTargetAnimator);
                    }

                    sourceRootNet = Vector3.Distance(sourceRoot, sourceRootStart);
                    sourceHipsNet = Vector3.Distance(sourceHips, sourceHipsStart);
                    targetBodyNet = Vector3.Distance(targetBody, targetBodyStart);
                    targetHipsNet = Vector3.Distance(targetHips, targetHipsStart);
                    targetAnimatorNet = Vector3.Distance(targetAnimatorPosition, targetAnimatorStart);

                    previousSourceRoot = sourceRoot;
                    previousSourceHips = sourceHips;
                    previousTargetBody = targetBody;
                    previousTargetHips = targetHips;
                    previousTargetAnimator = targetAnimatorPosition;
                    hasPrevious = true;
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            sb.AppendLine($"- Source root cumulative travel: {sourceRootTravel:F5}");
            sb.AppendLine($"- Target bodyPosition cumulative travel: {targetBodyTravel:F5}");
            sb.AppendLine($"- Target hips cumulative travel: {targetHipsTravel:F5}");
            sb.AppendLine($"- Target animator transform cumulative travel: {targetAnimatorTravel:F5}");
            sb.AppendLine($"- Source root net displacement: {sourceRootNet:F5}");
            sb.AppendLine($"- Target bodyPosition net displacement: {targetBodyNet:F5}");
            sb.AppendLine($"- Target hips net displacement: {targetHipsNet:F5}");
            sb.AppendLine($"- Target animator transform net displacement: {targetAnimatorNet:F5}");
            sb.AppendLine($"- bodyPosition/source cumulative ratio: {(sourceRootTravel > 1e-6f ? targetBodyTravel / sourceRootTravel : 0f):F3}");
            sb.AppendLine($"- hips/source cumulative ratio: {(sourceRootTravel > 1e-6f ? targetHipsTravel / sourceRootTravel : 0f):F3}");
            sb.AppendLine($"- animator/source cumulative ratio: {(sourceRootTravel > 1e-6f ? targetAnimatorTravel / sourceRootTravel : 0f):F3}");

            if (sourceRootTravel > 1e-6f && targetAnimatorTravel < sourceRootTravel * 0.1f && targetHipsTravel > sourceRootTravel * 0.5f)
                sb.AppendLine("- Interpretation: locomotion is staying inside the humanoid body/hips and is not transferring to the animator transform. The remaining issue is root-motion contract, not pose amplitude.");
            else if (sourceRootTravel > 1e-6f && targetBodyTravel > sourceRootTravel * 2f)
                sb.AppendLine("- Interpretation: Unity bodyPosition is amplifying locomotion relative to the source root. The remaining issue is root/bodyPosition scaling or contract mismatch.");
            else
                sb.AppendLine("- Interpretation: root/body locomotion transfer is not obviously collapsed. If the motion still does not read as walking, inspect gait semantics and footfall phasing rather than overall locomotion magnitude.");

            sb.AppendLine();
        }

        private static void AppendBakedClipRoundtripDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator targetAnimator)
        {
            sb.AppendLine("## Baked clip roundtrip");

            if (targetAnimator == null || !targetAnimator.isHuman || targetAnimator.avatar == null || !targetAnimator.avatar.isValid)
            {
                sb.AppendLine("- Target animator does not have a valid humanoid avatar.");
                sb.AppendLine();
                return;
            }

            if (motion?.frames == null || motion.frames.Length == 0)
            {
                sb.AppendLine("- No motion frames available.");
                sb.AppendLine();
                return;
            }

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            if (exactRig?.rootObject == null || exactRig.boneTransforms == null || exactRig.boneTransforms.Length < 22)
            {
                sb.AppendLine("- Could not build exact source rig for roundtrip test.");
                sb.AppendLine();
                return;
            }

            Avatar sourceAvatar = null;
            HumanPoseHandler sourcePoseHandler = null;
            HumanPoseHandler targetPoseHandler = null;
            var sourcePose = new HumanPose();
            var targetPose = new HumanPose();
            var targetPoseSequence = new List<HumanPose>(motion.frames.Length);

            try
            {
                sourceAvatar = BuildCanonicalSourceAvatar(exactRig.rootObject);
                if (sourceAvatar == null || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                {
                    sb.AppendLine("- Could not build a valid humanoid avatar from the exact source rig.");
                    sb.AppendLine();
                    return;
                }

                sourcePoseHandler = new HumanPoseHandler(sourceAvatar, exactRig.rootObject.transform);
                targetPoseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame);
                    sourcePoseHandler.GetHumanPose(ref sourcePose);
                    targetPoseHandler.SetHumanPose(ref sourcePose);
                    targetPoseHandler.GetHumanPose(ref targetPose);

                    if (targetPose.muscles == null || targetPose.muscles.Length != HumanTrait.MuscleCount)
                        continue;

                    targetPoseSequence.Add(CloneHumanPose(targetPose));
                }
            }
            finally
            {
                sourcePoseHandler?.Dispose();
                targetPoseHandler?.Dispose();
                if (sourceAvatar != null)
                    UnityEngine.Object.DestroyImmediate(sourceAvatar);
                if (exactRig.rootObject != null)
                    UnityEngine.Object.DestroyImmediate(exactRig.rootObject);
            }

            if (targetPoseSequence.Count == 0)
            {
                sb.AppendLine("- No valid target pose samples available for roundtrip test.");
                sb.AppendLine();
                return;
            }

            EvaluateBakedClipRoundtrip(targetAnimator, targetPoseSequence, motion.fps > 0 ? motion.fps : 20,
                out var meanMuscleError,
                out var maxMuscleError,
                out var meanBodyPosError,
                out var meanBodyRotError,
                out var meanFrameDeltaRatio);

            sb.AppendLine($"- Mean baked muscle error: {meanMuscleError:F5}");
            sb.AppendLine($"- Max baked single-muscle error: {maxMuscleError:F5}");
            sb.AppendLine($"- Mean baked bodyPosition error: {meanBodyPosError:F5}");
            sb.AppendLine($"- Mean baked bodyRotation error: {meanBodyRotError:F5}°");
            sb.AppendLine($"- Baked/source frame-delta ratio: {meanFrameDeltaRatio:F3}");

            if (meanFrameDeltaRatio < 0.35f)
                sb.AppendLine("- Interpretation: clip baking/sampling is strongly compressing motion amplitude. The remaining problem is in curve baking or clip evaluation.");
            else if (meanMuscleError > 0.05f || meanBodyRotError > 5f)
                sb.AppendLine("- Interpretation: the baked clip preserves motion magnitude but distorts pose semantics. Investigate how humanoid curves are written or sampled.");
            else
                sb.AppendLine("- Interpretation: the baked clip roundtrip is faithful. If the visible result is still wrong, investigate how the clip is being applied or previewed outside the diagnostic path.");

            sb.AppendLine();
        }

        private static void EvaluateBakedClipRoundtrip(
            Animator targetAnimator,
            List<HumanPose> targetPoseSequence,
            int fps,
            out float meanMuscleError,
            out float maxMuscleError,
            out float meanBodyPosError,
            out float meanBodyRotError,
            out float meanFrameDeltaRatio)
        {
            meanMuscleError = 0f;
            maxMuscleError = 0f;
            meanBodyPosError = 0f;
            meanBodyRotError = 0f;
            meanFrameDeltaRatio = 0f;

            if (targetAnimator == null || targetPoseSequence == null || targetPoseSequence.Count == 0)
                return;

            var clip = new AnimationClip { frameRate = Mathf.Max(1, fps) };
            var rootPosX = new AnimationCurve();
            var rootPosY = new AnimationCurve();
            var rootPosZ = new AnimationCurve();
            var rootRotX = new AnimationCurve();
            var rootRotY = new AnimationCurve();
            var rootRotZ = new AnimationCurve();
            var rootRotW = new AnimationCurve();
            var muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                muscleCurves[i] = new AnimationCurve();

            float frameTime = 1f / Mathf.Max(1, fps);
            for (int frameIndex = 0; frameIndex < targetPoseSequence.Count; frameIndex++)
            {
                float time = frameIndex * frameTime;
                var pose = targetPoseSequence[frameIndex];
                rootPosX.AddKey(time, pose.bodyPosition.x);
                rootPosY.AddKey(time, pose.bodyPosition.y);
                rootPosZ.AddKey(time, pose.bodyPosition.z);
                rootRotX.AddKey(time, pose.bodyRotation.x);
                rootRotY.AddKey(time, pose.bodyRotation.y);
                rootRotZ.AddKey(time, pose.bodyRotation.z);
                rootRotW.AddKey(time, pose.bodyRotation.w);

                for (int m = 0; m < HumanTrait.MuscleCount; m++)
                    muscleCurves[m].AddKey(time, pose.muscles[m]);
            }

            SetAnimatorCurve(clip, "RootT.x", rootPosX);
            SetAnimatorCurve(clip, "RootT.y", rootPosY);
            SetAnimatorCurve(clip, "RootT.z", rootPosZ);
            SetAnimatorCurve(clip, "RootQ.x", rootRotX);
            SetAnimatorCurve(clip, "RootQ.y", rootRotY);
            SetAnimatorCurve(clip, "RootQ.z", rootRotZ);
            SetAnimatorCurve(clip, "RootQ.w", rootRotW);
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                SetAnimatorCurve(clip, HumanTrait.MuscleName[i], muscleCurves[i]);
            clip.EnsureQuaternionContinuity();

            var poseHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);
            var sampledPose = new HumanPose();
            var originalLocalPositions = new Dictionary<Transform, Vector3>();
            var originalLocalRotations = new Dictionary<Transform, Quaternion>();
            foreach (var transform in targetAnimator.GetComponentsInChildren<Transform>(true))
            {
                originalLocalPositions[transform] = transform.localPosition;
                originalLocalRotations[transform] = transform.localRotation;
            }

            float muscleErrorSum = 0f;
            float bodyPosErrorSum = 0f;
            float bodyRotErrorSum = 0f;
            int poseSamples = 0;
            float bakedDeltaSum = 0f;
            float sourceDeltaSum = 0f;
            int deltaSamples = 0;
            float[] previousSampledMuscles = null;

            bool startedAnimationModeHere = false;

            try
            {
                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    startedAnimationModeHere = true;
                }

                for (int frameIndex = 0; frameIndex < targetPoseSequence.Count; frameIndex++)
                {
                    float time = frameIndex * frameTime;
                    AnimationMode.SampleAnimationClip(targetAnimator.gameObject, clip, time);
                    poseHandler.GetHumanPose(ref sampledPose);

                    var targetPose = targetPoseSequence[frameIndex];
                    float frameMuscleError = 0f;
                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        float error = Mathf.Abs(sampledPose.muscles[i] - targetPose.muscles[i]);
                        frameMuscleError += error;
                        maxMuscleError = Mathf.Max(maxMuscleError, error);
                    }

                    muscleErrorSum += frameMuscleError / HumanTrait.MuscleCount;
                    bodyPosErrorSum += Vector3.Distance(sampledPose.bodyPosition, targetPose.bodyPosition);
                    bodyRotErrorSum += Quaternion.Angle(sampledPose.bodyRotation, targetPose.bodyRotation);
                    poseSamples++;

                    if (frameIndex > 0 && previousSampledMuscles != null)
                    {
                        float sourceFrameDelta = 0f;
                        float bakedFrameDelta = 0f;
                        var previousTargetPose = targetPoseSequence[frameIndex - 1];
                        for (int i = 0; i < HumanTrait.MuscleCount; i++)
                        {
                            sourceFrameDelta += Mathf.Abs(targetPose.muscles[i] - previousTargetPose.muscles[i]);
                            bakedFrameDelta += Mathf.Abs(sampledPose.muscles[i] - previousSampledMuscles[i]);
                        }

                        sourceDeltaSum += sourceFrameDelta / HumanTrait.MuscleCount;
                        bakedDeltaSum += bakedFrameDelta / HumanTrait.MuscleCount;
                        deltaSamples++;
                    }

                    previousSampledMuscles = (float[])sampledPose.muscles.Clone();
                }
            }
            finally
            {
                foreach (var kv in originalLocalPositions)
                    kv.Key.localPosition = kv.Value;
                foreach (var kv in originalLocalRotations)
                    kv.Key.localRotation = kv.Value;
                poseHandler.Dispose();
                if (startedAnimationModeHere && AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
            }

            meanMuscleError = poseSamples > 0 ? muscleErrorSum / poseSamples : 0f;
            meanBodyPosError = poseSamples > 0 ? bodyPosErrorSum / poseSamples : 0f;
            meanBodyRotError = poseSamples > 0 ? bodyRotErrorSum / poseSamples : 0f;
            float meanSourceDelta = deltaSamples > 0 ? sourceDeltaSum / deltaSamples : 0f;
            float meanBakedDelta = deltaSamples > 0 ? bakedDeltaSum / deltaSamples : 0f;
            meanFrameDeltaRatio = meanSourceDelta > 1e-6f ? meanBakedDelta / meanSourceDelta : 0f;
        }

        private static HumanPose CloneHumanPose(HumanPose pose)
        {
            var clone = pose;
            if (pose.muscles != null)
                clone.muscles = (float[])pose.muscles.Clone();
            return clone;
        }

        private static string GetWorldSpaceDiagnosticLabel(int sourceIndex)
        {
            switch (sourceIndex)
            {
                case 0: return "Hips";
                case 7: return "LeftFoot";
                case 8: return "RightFoot";
                case 10: return "LeftToes";
                case 11: return "RightToes";
                case 15: return "Head";
                case 20: return "LeftHand";
                case 21: return "RightHand";
                default: return sourceIndex.ToString();
            }
        }

        private static void AppendGaitMetricRow(StringBuilder sb, string label, float sourceValue, float targetValue)
        {
            sb.AppendLine($"| {label} | {sourceValue:F5} | {targetValue:F5} | {SafeRatio(targetValue, sourceValue):F3} |");
        }

        private static float SafeRatio(float numerator, float denominator)
        {
            return Mathf.Abs(denominator) > 1e-6f ? numerator / denominator : 0f;
        }

        private static float AverageNonZeroRatios(params float[] values)
        {
            if (values == null || values.Length == 0)
                return 0f;

            float sum = 0f;
            int count = 0;
            foreach (float value in values)
            {
                if (value > 0f)
                {
                    sum += value;
                    count++;
                }
            }

            return count > 0 ? sum / count : 0f;
        }

        private static void AppendDirectionRow(StringBuilder sb, string label, DirectionalTransferStats stats)
        {
            sb.AppendLine($"| {label} | {stats.SourceLateralShare * 100f:F1} | {stats.SourceUpShare * 100f:F1} | {stats.SourceForwardShare * 100f:F1} | {stats.TargetLateralShare * 100f:F1} | {stats.TargetUpShare * 100f:F1} | {stats.TargetForwardShare * 100f:F1} | {stats.MeanDirectionCosine:F3} |");
        }

        private static SourceBodyFrame BuildSourceBodyFrame(Vector3[] positions)
        {
            var origin = positions != null && positions.Length > 0 ? positions[0] : Vector3.zero;
            var up = positions != null && positions.Length > 12
                ? SafeNormalize(positions[12] - positions[0], Vector3.up)
                : Vector3.up;
            var forward = positions != null ? ComputeCharacterForward(positions) : Vector3.forward;
            var right = SafeNormalize(Vector3.Cross(up, forward), Vector3.right);
            forward = SafeNormalize(Vector3.Cross(right, up), Vector3.forward);

            return new SourceBodyFrame(origin, right, up, forward);
        }

        private static Vector3 InBodyFrame(Vector3 origin, SourceBodyFrame frame, Vector3 point)
        {
            var delta = point - origin;
            return new Vector3(Vector3.Dot(delta, frame.right), Vector3.Dot(delta, frame.up), Vector3.Dot(delta, frame.forward));
        }

        private static Vector3 InFixedFrame(Vector3 currentOrigin, SourceBodyFrame fixedFrame, Vector3 point)
        {
            var delta = point - currentOrigin;
            return new Vector3(Vector3.Dot(delta, fixedFrame.right), Vector3.Dot(delta, fixedFrame.up), Vector3.Dot(delta, fixedFrame.forward));
        }

        private static float ComputeJointAngleDegrees(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = a - b;
            var cb = c - b;
            if (ab.sqrMagnitude < 1e-8f || cb.sqrMagnitude < 1e-8f)
                return 0f;
            return Vector3.Angle(ab, cb);
        }

        private readonly struct SourceBodyFrame
        {
            public readonly Vector3 origin;
            public readonly Vector3 right;
            public readonly Vector3 up;
            public readonly Vector3 forward;

            public SourceBodyFrame(Vector3 origin, Vector3 right, Vector3 up, Vector3 forward)
            {
                this.origin = origin;
                this.right = right;
                this.up = up;
                this.forward = forward;
            }
        }

        private sealed class GaitSemanticStats
        {
            public int sampleCount;

            private float leftFootForwardMin = float.PositiveInfinity;
            private float leftFootForwardMax = float.NegativeInfinity;
            private float rightFootForwardMin = float.PositiveInfinity;
            private float rightFootForwardMax = float.NegativeInfinity;
            private float leftFootHeightMin = float.PositiveInfinity;
            private float leftFootHeightMax = float.NegativeInfinity;
            private float rightFootHeightMin = float.PositiveInfinity;
            private float rightFootHeightMax = float.NegativeInfinity;
            private float leftFootLateralMin = float.PositiveInfinity;
            private float leftFootLateralMax = float.NegativeInfinity;
            private float rightFootLateralMin = float.PositiveInfinity;
            private float rightFootLateralMax = float.NegativeInfinity;
            private float leftHandForwardMin = float.PositiveInfinity;
            private float leftHandForwardMax = float.NegativeInfinity;
            private float rightHandForwardMin = float.PositiveInfinity;
            private float rightHandForwardMax = float.NegativeInfinity;
            private float headHeightMin = float.PositiveInfinity;
            private float headHeightMax = float.NegativeInfinity;
            private float leftKneeMin = float.PositiveInfinity;
            private float leftKneeMax = float.NegativeInfinity;
            private float rightKneeMin = float.PositiveInfinity;
            private float rightKneeMax = float.NegativeInfinity;
            private float leftElbowMin = float.PositiveInfinity;
            private float leftElbowMax = float.NegativeInfinity;
            private float rightElbowMin = float.PositiveInfinity;
            private float rightElbowMax = float.NegativeInfinity;

            public float LeftFootForwardRange => Range(leftFootForwardMin, leftFootForwardMax);
            public float RightFootForwardRange => Range(rightFootForwardMin, rightFootForwardMax);
            public float LeftFootHeightRange => Range(leftFootHeightMin, leftFootHeightMax);
            public float RightFootHeightRange => Range(rightFootHeightMin, rightFootHeightMax);
            public float LeftFootLateralRange => Range(leftFootLateralMin, leftFootLateralMax);
            public float RightFootLateralRange => Range(rightFootLateralMin, rightFootLateralMax);
            public float LeftHandForwardRange => Range(leftHandForwardMin, leftHandForwardMax);
            public float RightHandForwardRange => Range(rightHandForwardMin, rightHandForwardMax);
            public float HeadHeightRange => Range(headHeightMin, headHeightMax);
            public float LeftKneeRange => Range(leftKneeMin, leftKneeMax);
            public float RightKneeRange => Range(rightKneeMin, rightKneeMax);
            public float LeftElbowRange => Range(leftElbowMin, leftElbowMax);
            public float RightElbowRange => Range(rightElbowMin, rightElbowMax);

            public void Accumulate(Vector3 leftFoot, Vector3 rightFoot, Vector3 leftHand, Vector3 rightHand, Vector3 head, float leftKnee, float rightKnee, float leftElbow, float rightElbow)
            {
                sampleCount++;
                Accumulate(ref leftFootForwardMin, ref leftFootForwardMax, leftFoot.z);
                Accumulate(ref rightFootForwardMin, ref rightFootForwardMax, rightFoot.z);
                Accumulate(ref leftFootHeightMin, ref leftFootHeightMax, leftFoot.y);
                Accumulate(ref rightFootHeightMin, ref rightFootHeightMax, rightFoot.y);
                Accumulate(ref leftFootLateralMin, ref leftFootLateralMax, leftFoot.x);
                Accumulate(ref rightFootLateralMin, ref rightFootLateralMax, rightFoot.x);
                Accumulate(ref leftHandForwardMin, ref leftHandForwardMax, leftHand.z);
                Accumulate(ref rightHandForwardMin, ref rightHandForwardMax, rightHand.z);
                Accumulate(ref headHeightMin, ref headHeightMax, head.y);
                Accumulate(ref leftKneeMin, ref leftKneeMax, leftKnee);
                Accumulate(ref rightKneeMin, ref rightKneeMax, rightKnee);
                Accumulate(ref leftElbowMin, ref leftElbowMax, leftElbow);
                Accumulate(ref rightElbowMin, ref rightElbowMax, rightElbow);
            }

            private static void Accumulate(ref float min, ref float max, float value)
            {
                min = Mathf.Min(min, value);
                max = Mathf.Max(max, value);
            }

            private static float Range(float min, float max)
            {
                if (float.IsInfinity(min) || float.IsInfinity(max))
                    return 0f;
                return max - min;
            }
        }

        private sealed class DirectionalTransferStats
        {
            private float sourceAbsX;
            private float sourceAbsY;
            private float sourceAbsZ;
            private float targetAbsX;
            private float targetAbsY;
            private float targetAbsZ;
            private float cosineSum;
            private int cosineSamples;

            public float SourceLateralShare => NormalizedShare(sourceAbsX, sourceAbsX + sourceAbsY + sourceAbsZ);
            public float SourceUpShare => NormalizedShare(sourceAbsY, sourceAbsX + sourceAbsY + sourceAbsZ);
            public float SourceForwardShare => NormalizedShare(sourceAbsZ, sourceAbsX + sourceAbsY + sourceAbsZ);
            public float TargetLateralShare => NormalizedShare(targetAbsX, targetAbsX + targetAbsY + targetAbsZ);
            public float TargetUpShare => NormalizedShare(targetAbsY, targetAbsX + targetAbsY + targetAbsZ);
            public float TargetForwardShare => NormalizedShare(targetAbsZ, targetAbsX + targetAbsY + targetAbsZ);
            public float MeanDirectionCosine => cosineSamples > 0 ? cosineSum / cosineSamples : 0f;

            public void Accumulate(Vector3 sourceDelta, Vector3 targetDelta)
            {
                sourceAbsX += Mathf.Abs(sourceDelta.x);
                sourceAbsY += Mathf.Abs(sourceDelta.y);
                sourceAbsZ += Mathf.Abs(sourceDelta.z);
                targetAbsX += Mathf.Abs(targetDelta.x);
                targetAbsY += Mathf.Abs(targetDelta.y);
                targetAbsZ += Mathf.Abs(targetDelta.z);

                if (sourceDelta.sqrMagnitude > 1e-8f && targetDelta.sqrMagnitude > 1e-8f)
                {
                    cosineSum += Vector3.Dot(sourceDelta.normalized, targetDelta.normalized);
                    cosineSamples++;
                }
            }

            private static float NormalizedShare(float value, float total)
            {
                return total > 1e-6f ? value / total : 0f;
            }
        }

        private static Vector3[] CaptureWorldPositions(Transform[] transforms)
        {
            if (transforms == null)
                return Array.Empty<Vector3>();

            var positions = new Vector3[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
                positions[i] = transforms[i] != null ? transforms[i].position : Vector3.zero;
            return positions;
        }

        private static void EvaluateQuaternionConversionFidelity(CanonicalMotionJson motion, Func<Quaternion, Quaternion> quaternionConverter, out float meanError, out float maxError)
        {
            meanError = 0f;
            maxError = 0f;

            if (motion?.frames == null || motion.frames.Length == 0 || quaternionConverter == null)
                return;

            var exactRig = BuildCanonicalSourceSkeletonRig(motion);
            var sourceSkeletonRoot = exactRig?.rootObject;
            var sourceBones = exactRig?.boneTransforms;
            var sourceAnchors = exactRig?.jointAnchors;
            if (sourceSkeletonRoot == null || sourceBones == null || sourceBones.Length < 22 || sourceAnchors == null || sourceAnchors.Length < 22)
                return;

            float sumError = 0f;
            int sampleCount = 0;

            try
            {
                for (int frameIndex = 0; frameIndex < motion.frames.Length; frameIndex++)
                {
                    var frame = motion.frames[frameIndex];
                    if (frame == null || frame.localRotations == null || frame.localRotations.Length < 22)
                        continue;

                    ApplyAbsoluteFrameToCanonicalSourceSkeleton(exactRig, frame, quaternionConverter);
                    var expectedPositions = BuildSourceWorldPositionsUnity(frame);

                    for (int boneIndex = 0; boneIndex < 22; boneIndex++)
                    {
                        if (sourceAnchors[boneIndex] == null)
                            continue;

                        float error = Vector3.Distance(sourceAnchors[boneIndex].position, expectedPositions[boneIndex]);
                        sumError += error;
                        sampleCount++;
                        maxError = Mathf.Max(maxError, error);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceSkeletonRoot);
            }

            meanError = sampleCount > 0 ? sumError / sampleCount : 0f;
        }

        private static void AppendBoneBasisDiagnosis(StringBuilder sb, CanonicalMotionJson motion, Animator animator, global::MotionGenEditorSettings settings)
        {
            sb.AppendLine("## Bone basis mismatch");

            var sourceBindPositions = BuildSourceBindPositionsUnity(motion);
            var parents = GetCanonicalParents(motion);
            if (!TryBuildTargetNeutralPositions(animator, out var targetNeutralPositions, out var neutralPoseNote))
            {
                sb.AppendLine("Could not capture target neutral humanoid pose.");
                sb.AppendLine();
                return;
            }

            var sourceBindLocalRotations = BuildCanonicalSourceBindLocalRotations(sourceBindPositions, parents);
            var targetBindLocalRotations = BuildBindLocalRotationsFromReferencePositions(targetNeutralPositions, parents);

            sb.AppendLine($"- Target neutral capture: {neutralPoseNote}");
            sb.AppendLine();
            sb.AppendLine("| Bone | Source→target basis delta | Source aim | Target aim | Source pole | Target pole | Stored calibration | Notes |");
            sb.AppendLine("|---|---:|---|---|---|---|---:|---|");

            foreach (var mapping in DiagnosticBoneMappings)
            {
                if (!TryResolveBone(animator, mapping.sourceIndex, out var boneTransform) || boneTransform == null)
                    continue;

                if (!TryGetSourceBoneWorldRotation(sourceBindPositions, mapping.sourceIndex, Quaternion.identity, out var sourceBindRotation))
                    continue;

                if (!TryGetSourceBoneWorldRotation(targetNeutralPositions, mapping.sourceIndex, Quaternion.identity, out var targetBindRotation))
                    continue;

                var basisDelta = targetBindRotation * Quaternion.Inverse(sourceBindRotation);
                float basisAngle = Quaternion.Angle(Quaternion.identity, basisDelta);
                var sourceAim = sourceBindRotation * Vector3.up;
                var targetAim = targetBindRotation * Vector3.up;
                var sourcePole = sourceBindRotation * Vector3.forward;
                var targetPole = targetBindRotation * Vector3.forward;

                float calibrationAngle = 0f;
                if (settings != null && settings.TryGetCalibration((HumanBodyBones)mapping.bone, out var storedCalibration))
                    calibrationAngle = Quaternion.Angle(Quaternion.identity, storedCalibration);

                string note = basisAngle >= 75f
                    ? "strong mismatch"
                    : basisAngle >= 35f
                        ? "moderate mismatch"
                        : "small mismatch";

                sb.AppendLine(
                    $"| {mapping.bone} | {basisAngle:F1}° | {FormatVector(sourceAim)} | {FormatVector(targetAim)} | {FormatVector(sourcePole)} | {FormatVector(targetPole)} | {calibrationAngle:F1}° | {note} |"
                );
            }

            sb.AppendLine();
            AppendNeutralLocalAxisDiagnosis(sb, sourceBindLocalRotations, targetBindLocalRotations);
            sb.AppendLine();
            sb.AppendLine("Interpretation notes:");
            sb.AppendLine("- Large shoulder / upper-arm deltas indicate the canonical source basis is not a Unity-humanoid T-pose basis.");
            sb.AppendLine("- Large foot deltas indicate ankle/foot local-axis disagreement, which shows up as toes pitching upward.");
            sb.AppendLine("- Large hip / upper-leg deltas indicate abduction/twist mismatch, which shows up as legs splaying outward.");
            sb.AppendLine("- Large neutral local correction angles indicate a strong candidate for a per-bone static anatomical offset in the retarget path.");
            sb.AppendLine();
        }

        private static void AppendNeutralLocalAxisDiagnosis(StringBuilder sb, Quaternion[] sourceBindLocalRotations, Quaternion[] targetBindLocalRotations)
        {
            sb.AppendLine("### Neutral-frame local axis comparison");
            sb.AppendLine();
            sb.AppendLine("| Bone | Src local aim | Src local pole | Tgt local aim | Tgt local pole | Local correction | Notes |");
            sb.AppendLine("|---|---|---|---|---|---:|---|");

            foreach (var mapping in DiagnosticBoneMappings)
            {
                var sourceLocal = GetBindLocalRotation(sourceBindLocalRotations, mapping.sourceIndex);
                var targetLocal = GetBindLocalRotation(targetBindLocalRotations, mapping.sourceIndex);
                var localCorrection = targetLocal * Quaternion.Inverse(sourceLocal);
                float correctionAngle = Quaternion.Angle(Quaternion.identity, localCorrection);

                var sourceAim = sourceLocal * Vector3.up;
                var sourcePole = sourceLocal * Vector3.forward;
                var targetAim = targetLocal * Vector3.up;
                var targetPole = targetLocal * Vector3.forward;

                string note = correctionAngle >= 120f
                    ? "very strong static offset candidate"
                    : correctionAngle >= 75f
                        ? "strong static offset candidate"
                        : correctionAngle >= 35f
                            ? "moderate static offset candidate"
                            : "small local offset";

                sb.AppendLine(
                    $"| {mapping.bone} | {FormatVector(sourceAim)} | {FormatVector(sourcePole)} | {FormatVector(targetAim)} | {FormatVector(targetPole)} | {correctionAngle:F1}° | {note} |"
                );
            }
        }

        private static void ApplyAbsoluteFrameToCanonicalSourceSkeleton(CanonicalSourceSkeletonRig rig, CanonicalMotionFrame frame)
        {
            ApplyAbsoluteFrameToCanonicalSourceSkeleton(rig, frame, SourceQuaternionToUnity);
        }

        private static void ApplyAbsoluteFrameToCanonicalSourceSkeleton(CanonicalSourceSkeletonRig rig, CanonicalMotionFrame frame, Func<Quaternion, Quaternion> quaternionConverter)
        {
            if (rig?.boneTransforms == null || rig.boneTransforms.Length < 22 || rig.boneTransforms[0] == null || frame == null)
                return;

            var sourceBones = rig.boneTransforms;
            var bindLocalRotations = rig.bindLocalRotations;

            if (quaternionConverter == null)
                quaternionConverter = SourceQuaternionToUnity;

            sourceBones[0].localPosition = frame.position != null ? SourceVectorToUnity(frame.position.ToVector3()) : Vector3.zero;
            sourceBones[0].localRotation = GetSourceRootRotationUnity(frame, quaternionConverter) * GetBindLocalRotation(bindLocalRotations, 0);

            for (int i = 1; i < 22; i++)
            {
                if (sourceBones[i] == null)
                    continue;

                sourceBones[i].localRotation = (frame.localRotations != null && frame.localRotations.Length > i && frame.localRotations[i] != null
                    ? quaternionConverter(frame.localRotations[i].ToQuaternion())
                    : Quaternion.identity) * GetBindLocalRotation(bindLocalRotations, i);
            }
        }

        private static void ApplyAbsoluteFrameToCarrierSourceSkeleton(CarrierSourceSkeleton skeleton, CanonicalMotionFrame frame)
        {
            if (skeleton?.jointNodes == null || skeleton.rotationNodes == null || skeleton.jointNodes.Length < 22 || skeleton.rotationNodes.Length < 22)
                return;

            skeleton.jointNodes[0].localPosition = frame.position != null ? SourceVectorToUnity(frame.position.ToVector3()) : Vector3.zero;
            skeleton.jointNodes[0].localRotation = GetSourceRootRotationUnity(frame);

            for (int i = 1; i < 22; i++)
            {
                if (skeleton.rotationNodes[i] == null)
                    continue;

                skeleton.rotationNodes[i].localRotation = frame.localRotations != null && frame.localRotations.Length > i && frame.localRotations[i] != null
                    ? SourceQuaternionToUnity(frame.localRotations[i].ToQuaternion())
                    : Quaternion.identity;
            }
        }

        private static int[] GetRetargetBoneIndices()
        {
            return new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 13, 14, 15, 16, 17, 18, 19 };
        }

        private static int[] GetDirectionalCorrectionBoneIndices()
        {
            return Array.Empty<int>();
        }

        private static Vector3[] BuildDefaultSourceBindPositionsUnity()
        {
            var positions = new Vector3[22];
            positions[0] = Vector3.zero;
            for (int i = 1; i < 22; i++)
            {
                positions[i] = positions[SourceParents[i]] + SourceVectorToUnity(DefaultSourceRestOffset(i));
            }
            return positions;
        }

        private static Vector3[] BuildSourceBindPositionsUnity(CanonicalMotionJson motion)
        {
            if (motion != null && motion.restOffsets != null && motion.restOffsets.Length >= 22)
            {
                var positions = new Vector3[22];
                positions[0] = Vector3.zero;
                for (int i = 1; i < 22; i++)
                {
                    positions[i] = positions[SourceParents[i]] + SourceVectorToUnity(motion.restOffsets[i].ToVector3());
                }
                return positions;
            }

            return BuildDefaultSourceBindPositionsUnity();
        }

        private static Vector3 DefaultSourceRestOffset(int index)
        {
            switch (index)
            {
                case 1: return new Vector3(1, 0, 0);
                case 2: return new Vector3(-1, 0, 0);
                case 3: return new Vector3(0, 1, 0);
                case 4: return new Vector3(0, -1, 0);
                case 5: return new Vector3(0, -1, 0);
                case 6: return new Vector3(0, 1, 0);
                case 7: return new Vector3(0, -1, 0);
                case 8: return new Vector3(0, -1, 0);
                case 9: return new Vector3(0, 1, 0);
                case 10: return new Vector3(0, 0, 1);
                case 11: return new Vector3(0, 0, 1);
                case 12: return new Vector3(0, 1, 0);
                case 13: return new Vector3(1, 0, 0);
                case 14: return new Vector3(-1, 0, 0);
                case 15: return new Vector3(0, 0, 1);
                case 16: return new Vector3(0, -1, 0);
                case 17: return new Vector3(0, -1, 0);
                case 18: return new Vector3(0, -1, 0);
                case 19: return new Vector3(0, -1, 0);
                case 20: return new Vector3(0, -1, 0);
                case 21: return new Vector3(0, -1, 0);
                default: return Vector3.zero;
            }
        }

        private static Vector3[] BuildSourceWorldPositionsUnity(CanonicalMotionFrame frame)
        {
            var positions = new Vector3[22];

            if (frame.worldJoints != null && frame.worldJoints.Length >= 22)
            {
                for (int i = 0; i < 22; i++)
                    positions[i] = SourceVectorToUnity(frame.worldJoints[i].ToVector3());
                return positions;
            }

            if (frame.joints != null && frame.joints.Length >= 22)
            {
                var root = frame.position != null ? SourceVectorToUnity(frame.position.ToVector3()) : Vector3.zero;
                for (int i = 0; i < 22; i++)
                    positions[i] = root + SourceVectorToUnity(frame.joints[i].ToVector3());
            }

            return positions;
        }

        private static bool TryBuildTargetNeutralPositions(Animator animator, out Vector3[] positions, out string note)
        {
            positions = new Vector3[22];
            note = "captured from neutral humanoid muscles";

            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
                return false;

            var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            HumanPose originalPose = default;
            poseHandler.GetHumanPose(ref originalPose);

            try
            {
                var neutralPose = originalPose;
                if (neutralPose.muscles == null || neutralPose.muscles.Length != HumanTrait.MuscleCount)
                    neutralPose.muscles = new float[HumanTrait.MuscleCount];

                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    neutralPose.muscles[i] = 0f;

                poseHandler.SetHumanPose(ref neutralPose);

                for (int sourceIndex = 0; sourceIndex < positions.Length; sourceIndex++)
                {
                    if (!TryResolveBone(animator, sourceIndex, out var transform) || transform == null)
                    {
                        note = $"missing neutral target mapping for source joint {sourceIndex}";
                        return false;
                    }

                    positions[sourceIndex] = transform.position;
                }

                return true;
            }
            finally
            {
                poseHandler.SetHumanPose(ref originalPose);
                poseHandler.Dispose();
            }
        }

        private static Quaternion[] BuildAutomaticBasisCalibration(CanonicalMotionJson motion, Animator animator, global::MotionGenEditorSettings settings)
        {
            var calibration = new Quaternion[22];
            for (int i = 0; i < calibration.Length; i++)
                calibration[i] = Quaternion.identity;

            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid)
                return calibration;

            if (settings != null && !settings.useRetargetCalibration)
                return calibration;

            var experimentMode = GetExperimentMode(settings);
            if (experimentMode != global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.LocalPreMultiplyBasis
                && experimentMode != global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.LocalConjugateBasis
                && experimentMode != global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectivePreMultiplyBasis
                && experimentMode != global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectiveConjugateBasis)
            {
                return calibration;
            }

            var sourceBindPositions = BuildSourceBindPositionsUnity(motion);
            if (!TryBuildTargetNeutralPositions(animator, out var targetNeutralPositions, out _))
                return calibration;

            var sourceWorldBindRotations = new Quaternion[22];
            var targetWorldBindRotations = new Quaternion[22];
            var sourceLocalBindRotations = new Quaternion[22];
            var targetLocalBindRotations = new Quaternion[22];

            for (int i = 0; i < 22; i++)
            {
                sourceWorldBindRotations[i] = Quaternion.identity;
                targetWorldBindRotations[i] = Quaternion.identity;
                sourceLocalBindRotations[i] = Quaternion.identity;
                targetLocalBindRotations[i] = Quaternion.identity;

                if (!TryGetSourceBoneWorldRotation(sourceBindPositions, i, Quaternion.identity, out sourceWorldBindRotations[i]))
                    continue;

                if (!TryGetSourceBoneWorldRotation(targetNeutralPositions, i, Quaternion.identity, out targetWorldBindRotations[i]))
                    continue;

                int parent = SourceParents[i];
                if (parent >= 0)
                {
                    sourceLocalBindRotations[i] = Quaternion.Inverse(sourceWorldBindRotations[parent]) * sourceWorldBindRotations[i];
                    targetLocalBindRotations[i] = Quaternion.Inverse(targetWorldBindRotations[parent]) * targetWorldBindRotations[i];
                }
                else
                {
                    sourceLocalBindRotations[i] = sourceWorldBindRotations[i];
                    targetLocalBindRotations[i] = targetWorldBindRotations[i];
                }

                calibration[i] = targetLocalBindRotations[i] * Quaternion.Inverse(sourceLocalBindRotations[i]);
            }

            return calibration;
        }

        private static Quaternion ApplyExperimentalBasisCorrection(Quaternion localRotation, int index, Quaternion[] basisCalibration, global::MotionGenEditorSettings settings)
        {
            if (basisCalibration == null || index < 0 || index >= basisCalibration.Length)
                return localRotation;

            var correction = basisCalibration[index];
            var mode = GetExperimentMode(settings);

            if ((mode == global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectivePreMultiplyBasis
                || mode == global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectiveConjugateBasis)
                && !ShouldApplySelectiveBasisCorrection(index))
            {
                return localRotation;
            }

            switch (mode)
            {
                case global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.LocalPreMultiplyBasis:
                case global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectivePreMultiplyBasis:
                    return correction * localRotation;

                case global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.LocalConjugateBasis:
                case global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.SelectiveConjugateBasis:
                    return correction * localRotation * Quaternion.Inverse(correction);

                default:
                    return localRotation;
            }
        }

        private static bool ShouldApplySelectiveBasisCorrection(int sourceIndex)
        {
            for (int i = 0; i < SelectiveBasisExperimentBoneIndices.Length; i++)
            {
                if (SelectiveBasisExperimentBoneIndices[i] == sourceIndex)
                    return true;
            }

            return false;
        }

        private static global::MotionGenEditorSettings.CanonicalRetargetExperimentMode GetExperimentMode(global::MotionGenEditorSettings settings)
        {
            return settings != null
                ? settings.canonicalRetargetExperimentMode
                : global::MotionGenEditorSettings.CanonicalRetargetExperimentMode.Baseline;
        }

        private static bool TryGetSourceBoneWorldRotation(Vector3[] positions, int sourceIndex, Quaternion referenceRotation, out Quaternion rotation)
        {
            rotation = Quaternion.identity;

            if (!TryGetSourceBasis(positions, sourceIndex, out var aimVector, out var poleVector))
                return false;

            return TryBuildBasisRotation(aimVector, poleVector, referenceRotation, out rotation);
        }

        private static bool TryGetSourceBasis(Vector3[] p, int sourceIndex, out Vector3 aimVector, out Vector3 poleVector)
        {
            aimVector = Vector3.up;
            poleVector = Vector3.forward;

            var characterForward = ComputeCharacterForward(p);
            var chestForward = ComputeChestForward(p, characterForward);

            switch (sourceIndex)
            {
                case 0:
                    aimVector = p[3] - p[0];
                    if (aimVector.sqrMagnitude < 1e-8f)
                        aimVector = p[6] - p[0];
                    poleVector = chestForward;
                    return true;
                case 1:
                    aimVector = p[4] - p[1];
                    poleVector = ComputePoleFromBendPlane(p[1], p[4], p[7], characterForward);
                    return true;
                case 2:
                    aimVector = p[5] - p[2];
                    poleVector = ComputePoleFromBendPlane(p[2], p[5], p[8], characterForward);
                    return true;
                case 3:
                    aimVector = p[6] - p[3];
                    poleVector = chestForward;
                    return true;
                case 4:
                    aimVector = p[7] - p[4];
                    poleVector = ComputePoleFromBendPlane(p[1], p[4], p[7], characterForward);
                    return true;
                case 5:
                    aimVector = p[8] - p[5];
                    poleVector = ComputePoleFromBendPlane(p[2], p[5], p[8], characterForward);
                    return true;
                case 6:
                    aimVector = p[9] - p[6];
                    poleVector = chestForward;
                    return true;
                case 7:
                    aimVector = p[10] - p[7];
                    poleVector = ComputePoleFromBendPlane(p[4], p[7], p[10], characterForward);
                    return true;
                case 8:
                    aimVector = p[11] - p[8];
                    poleVector = ComputePoleFromBendPlane(p[5], p[8], p[11], characterForward);
                    return true;
                case 9:
                    aimVector = p[12] - p[9];
                    poleVector = chestForward;
                    return true;
                case 12:
                    aimVector = p[15] - p[12];
                    poleVector = chestForward;
                    return true;
                case 13:
                    aimVector = p[16] - p[13];
                    poleVector = chestForward;
                    return true;
                case 14:
                    aimVector = p[17] - p[14];
                    poleVector = chestForward;
                    return true;
                case 15:
                    aimVector = p[15] - p[12];
                    poleVector = chestForward;
                    return true;
                case 16:
                    aimVector = p[18] - p[16];
                    poleVector = ComputePoleFromBendPlane(p[16], p[18], p[20], chestForward);
                    return true;
                case 17:
                    aimVector = p[19] - p[17];
                    poleVector = ComputePoleFromBendPlane(p[17], p[19], p[21], chestForward);
                    return true;
                case 18:
                    aimVector = p[20] - p[18];
                    poleVector = ComputePoleFromBendPlane(p[16], p[18], p[20], chestForward);
                    return true;
                case 19:
                    aimVector = p[21] - p[19];
                    poleVector = ComputePoleFromBendPlane(p[17], p[19], p[21], chestForward);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryBuildBasisRotation(Vector3 aimVector, Vector3 poleVector, Quaternion referenceRotation, out Quaternion rotation)
        {
            rotation = Quaternion.identity;

            if (aimVector.sqrMagnitude < 1e-8f)
                return false;

            var aim = aimVector.normalized;
            var pole = Vector3.ProjectOnPlane(poleVector, aim);

            if (pole.sqrMagnitude < 1e-8f)
            {
                pole = Vector3.ProjectOnPlane(Vector3.forward, aim);
                if (pole.sqrMagnitude < 1e-8f)
                    pole = Vector3.ProjectOnPlane(Vector3.right, aim);
            }

            if (pole.sqrMagnitude < 1e-8f)
                return false;

            pole.Normalize();
            var primary = Quaternion.LookRotation(pole, aim);
            var flipped = Quaternion.LookRotation(-pole, aim);

            rotation = Quaternion.Angle(flipped, referenceRotation) < Quaternion.Angle(primary, referenceRotation)
                ? flipped
                : primary;

            return true;
        }

        private static Vector3 ComputeCharacterForward(Vector3[] positions)
        {
            var hipRight = SafeNormalize(positions[1] - positions[2], Vector3.right);
            var shoulderRight = SafeNormalize(positions[14] - positions[13], hipRight);
            var lateral = (hipRight + shoulderRight) * 0.5f;
            if (lateral.sqrMagnitude < 1e-8f)
                lateral = shoulderRight.sqrMagnitude > 1e-8f ? shoulderRight : hipRight;

            var up = positions[9] - positions[0];
            if (up.sqrMagnitude < 1e-8f)
                up = positions[12] - positions[0];
            up = SafeNormalize(up, Vector3.up);

            var forward = Vector3.Cross(lateral, up);
            if (forward.sqrMagnitude < 1e-8f)
                forward = Vector3.forward;

            return forward.normalized;
        }

        private static Vector3 ComputeChestForward(Vector3[] positions, Vector3 fallbackForward)
        {
            var shoulderRight = positions[14] - positions[13];
            var spineUp = positions[12] - positions[6];
            if (spineUp.sqrMagnitude < 1e-8f)
                spineUp = positions[9] - positions[3];

            if (shoulderRight.sqrMagnitude < 1e-8f || spineUp.sqrMagnitude < 1e-8f)
                return fallbackForward;

            var forward = Vector3.Cross(shoulderRight.normalized, spineUp.normalized);
            if (forward.sqrMagnitude < 1e-8f)
                return fallbackForward;

            return forward.normalized;
        }

        private static Vector3 ComputePoleFromBendPlane(Vector3 root, Vector3 mid, Vector3 end, Vector3 fallbackPole)
        {
            var upper = mid - root;
            var lower = end - mid;
            if (upper.sqrMagnitude < 1e-8f)
                return fallbackPole;

            var bendNormal = Vector3.Cross(upper, lower);
            if (bendNormal.sqrMagnitude < 1e-8f)
                return fallbackPole;

            var pole = Vector3.Cross(bendNormal.normalized, upper.normalized);
            if (pole.sqrMagnitude < 1e-8f)
                return fallbackPole;

            return pole.normalized;
        }

        private static Quaternion[] BuildCanonicalSourceBindLocalRotations(Vector3[] bindPositions, int[] parents)
        {
            var bindLocalRotations = new Quaternion[22];
            var worldBindRotations = new Quaternion[22];

            for (int i = 0; i < 22; i++)
            {
                bindLocalRotations[i] = Quaternion.identity;
                worldBindRotations[i] = Quaternion.identity;
            }

            if (bindPositions == null || bindPositions.Length < 22 || parents == null || parents.Length < 22)
                return bindLocalRotations;

            for (int i = 0; i < 22; i++)
            {
                Quaternion parentWorldRotation = Quaternion.identity;
                int parent = parents[i];
                if (parent >= 0 && parent < i)
                    parentWorldRotation = worldBindRotations[parent];

                if (!TryGetSourceBoneWorldRotation(bindPositions, i, parentWorldRotation, out worldBindRotations[i]))
                    worldBindRotations[i] = parentWorldRotation;

                bindLocalRotations[i] = parent >= 0 && parent < i
                    ? Quaternion.Inverse(parentWorldRotation) * worldBindRotations[i]
                    : worldBindRotations[i];
            }

            return bindLocalRotations;
        }

        private static Quaternion[] BuildBindLocalRotationsFromReferencePositions(Vector3[] referencePositions, int[] parents)
        {
            var bindLocalRotations = new Quaternion[22];
            var worldBindRotations = new Quaternion[22];

            for (int i = 0; i < 22; i++)
            {
                bindLocalRotations[i] = Quaternion.identity;
                worldBindRotations[i] = Quaternion.identity;
            }

            if (referencePositions == null || referencePositions.Length < 22 || parents == null || parents.Length < 22)
                return bindLocalRotations;

            for (int i = 0; i < 22; i++)
            {
                Quaternion parentWorldRotation = Quaternion.identity;
                int parent = parents[i];
                if (parent >= 0 && parent < i)
                    parentWorldRotation = worldBindRotations[parent];

                if (!TryGetSourceBoneWorldRotation(referencePositions, i, parentWorldRotation, out worldBindRotations[i]))
                    worldBindRotations[i] = parentWorldRotation;

                bindLocalRotations[i] = parent >= 0 && parent < i
                    ? Quaternion.Inverse(parentWorldRotation) * worldBindRotations[i]
                    : worldBindRotations[i];
            }

            return bindLocalRotations;
        }


        private static Quaternion GetBindLocalRotation(Quaternion[] bindLocalRotations, int index)
        {
            if (bindLocalRotations == null || index < 0 || index >= bindLocalRotations.Length)
                return Quaternion.identity;

            return bindLocalRotations[index];
        }

        private static Quaternion GetCapturedCalibrationLocalRotation(Quaternion[] capturedCalibration, int index)
        {
            if (capturedCalibration == null || index < 0 || index >= capturedCalibration.Length)
                return Quaternion.identity;

            return capturedCalibration[index];
        }

        private static Quaternion[] BuildCapturedCalibrationLocalRotations(global::MotionGenEditorSettings settings)
        {
            var calibration = new Quaternion[22];
            for (int i = 0; i < calibration.Length; i++)
                calibration[i] = Quaternion.identity;

            if (settings == null || !settings.useRetargetCalibration)
                return calibration;

            for (int i = 0; i < calibration.Length; i++)
            {
                if (!TryMapSourceIndexToHumanBodyBone(i, out var bone))
                    continue;

                if (!settings.TryGetCalibration(bone, out var correction))
                    continue;

                calibration[i] = correction;
            }

            return calibration;
        }

        private static bool TryMapSourceIndexToHumanBodyBone(int sourceIndex, out HumanBodyBones bone)
        {
            switch (sourceIndex)
            {
                case 0: bone = HumanBodyBones.Hips; return true;
                case 1: bone = HumanBodyBones.LeftUpperLeg; return true;
                case 2: bone = HumanBodyBones.RightUpperLeg; return true;
                case 3: bone = HumanBodyBones.Spine; return true;
                case 4: bone = HumanBodyBones.LeftLowerLeg; return true;
                case 5: bone = HumanBodyBones.RightLowerLeg; return true;
                case 6: bone = HumanBodyBones.Chest; return true;
                case 7: bone = HumanBodyBones.LeftFoot; return true;
                case 8: bone = HumanBodyBones.RightFoot; return true;
                case 9: bone = HumanBodyBones.UpperChest; return true;
                case 12: bone = HumanBodyBones.Neck; return true;
                case 13: bone = HumanBodyBones.LeftShoulder; return true;
                case 14: bone = HumanBodyBones.RightShoulder; return true;
                case 15: bone = HumanBodyBones.Head; return true;
                case 16: bone = HumanBodyBones.LeftUpperArm; return true;
                case 17: bone = HumanBodyBones.RightUpperArm; return true;
                case 18: bone = HumanBodyBones.LeftLowerArm; return true;
                case 19: bone = HumanBodyBones.RightLowerArm; return true;
                default:
                    bone = HumanBodyBones.LastBone;
                    return false;
            }
        }

        private static Quaternion ConvertLocalRotationBetweenBindBases(Quaternion localRotation, Quaternion sourceBindLocalRotation, Quaternion targetBindLocalRotation)
        {
            var basisDelta = targetBindLocalRotation * Quaternion.Inverse(sourceBindLocalRotation);
            return basisDelta * localRotation * Quaternion.Inverse(basisDelta);
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        {
            return value.sqrMagnitude < 1e-8f ? fallback : value.normalized;
        }

        private static Vector3 FlattenHorizontal(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-8f ? Vector3.zero : v.normalized;
        }

        private static string FormatVector(Vector3 v)
        {
            return $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
        }

        private static Vector3 SourceVectorToUnity(Vector3 v)
        {
            return new Vector3(v.x, v.y, -v.z);
        }

        private static Quaternion SourceQuaternionToUnity(Quaternion q)
        {
            return new Quaternion(-q.x, -q.y, q.z, q.w);
        }

        private static Quaternion SourceQuaternionToUnityViaMirroredBasis(Quaternion q)
        {
            var mirroredForward = SourceVectorToUnity(q * Vector3.forward);
            var mirroredUp = SourceVectorToUnity(q * Vector3.up);

            if (mirroredForward.sqrMagnitude < 1e-8f || mirroredUp.sqrMagnitude < 1e-8f)
                return Quaternion.identity;

            return Quaternion.LookRotation(mirroredForward.normalized, mirroredUp.normalized);
        }

        private static float ComputeRootScale(Animator animator, CanonicalMotionJson motion)
        {
            float source = ComputeSourceLegLength(motion);
            float target = ComputeTargetLegLength(animator);

            if (source < 1e-5f || target < 1e-5f)
                return 1f;

            return target / source;
        }

        private static float ComputeSourceLegLength(CanonicalMotionJson motion)
        {
            if (motion.restOffsets != null && motion.restOffsets.Length >= 22)
            {
                float right = motion.restOffsets[1].ToVector3().magnitude
                            + motion.restOffsets[4].ToVector3().magnitude
                            + motion.restOffsets[7].ToVector3().magnitude
                            + motion.restOffsets[10].ToVector3().magnitude;
                float left = motion.restOffsets[2].ToVector3().magnitude
                           + motion.restOffsets[5].ToVector3().magnitude
                           + motion.restOffsets[8].ToVector3().magnitude
                           + motion.restOffsets[11].ToVector3().magnitude;
                return 0.5f * (left + right);
            }

            if (motion.frames != null && motion.frames.Length > 0 && motion.frames[0].worldJoints != null && motion.frames[0].worldJoints.Length >= 12)
            {
                var w = motion.frames[0].worldJoints;
                float right = Vector3.Distance(w[0].ToVector3(), w[1].ToVector3())
                            + Vector3.Distance(w[1].ToVector3(), w[4].ToVector3())
                            + Vector3.Distance(w[4].ToVector3(), w[7].ToVector3())
                            + Vector3.Distance(w[7].ToVector3(), w[10].ToVector3());
                float left = Vector3.Distance(w[0].ToVector3(), w[2].ToVector3())
                           + Vector3.Distance(w[2].ToVector3(), w[5].ToVector3())
                           + Vector3.Distance(w[5].ToVector3(), w[8].ToVector3())
                           + Vector3.Distance(w[8].ToVector3(), w[11].ToVector3());
                return 0.5f * (left + right);
            }

            return 1f;
        }

        private static float ComputeTargetLegLength(Animator animator)
        {
            float left = ComputeTargetChainLength(animator,
                HumanBodyBones.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.LeftToes);

            float right = ComputeTargetChainLength(animator,
                HumanBodyBones.RightUpperLeg,
                HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot,
                HumanBodyBones.RightToes);

            if (left > 1e-5f && right > 1e-5f)
                return 0.5f * (left + right);

            return Mathf.Max(left, right);
        }

        private static float ComputeTargetChainLength(Animator animator, params HumanBodyBones[] bones)
        {
            float sum = 0f;
            Transform prev = null;
            foreach (var boneId in bones)
            {
                var bone = animator.GetBoneTransform(boneId);
                if (bone == null)
                    continue;

                if (prev != null)
                    sum += Vector3.Distance(prev.position, bone.position);

                prev = bone;
            }
            return sum;
        }

        private static bool TryResolveBone(Animator animator, int sourceIndex, out Transform transform)
        {
            transform = null;
            HumanBodyBones bone;

            switch (sourceIndex)
            {
                case 0: bone = HumanBodyBones.Hips; break;
                case 1: bone = HumanBodyBones.LeftUpperLeg; break;
                case 2: bone = HumanBodyBones.RightUpperLeg; break;
                case 3: bone = HumanBodyBones.Spine; break;
                case 4: bone = HumanBodyBones.LeftLowerLeg; break;
                case 5: bone = HumanBodyBones.RightLowerLeg; break;
                case 6: bone = HumanBodyBones.Chest; break;
                case 7: bone = HumanBodyBones.LeftFoot; break;
                case 8: bone = HumanBodyBones.RightFoot; break;
                case 9: bone = HumanBodyBones.UpperChest; break;
                case 10: bone = HumanBodyBones.LeftToes; break;
                case 11: bone = HumanBodyBones.RightToes; break;
                case 12: bone = HumanBodyBones.Neck; break;
                case 13: bone = HumanBodyBones.LeftShoulder; break;
                case 14: bone = HumanBodyBones.RightShoulder; break;
                case 15: bone = HumanBodyBones.Head; break;
                case 16: bone = HumanBodyBones.LeftUpperArm; break;
                case 17: bone = HumanBodyBones.RightUpperArm; break;
                case 18: bone = HumanBodyBones.LeftLowerArm; break;
                case 19: bone = HumanBodyBones.RightLowerArm; break;
                case 20: bone = HumanBodyBones.LeftHand; break;
                case 21: bone = HumanBodyBones.RightHand; break;
                default: return false;
            }

            transform = animator.GetBoneTransform(bone);
            if (sourceIndex == 9 && transform == null)
                transform = animator.GetBoneTransform(HumanBodyBones.Chest);

            return transform != null;
        }

        private static void SetAnimatorCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
        {
            var binding = new EditorCurveBinding
            {
                path = string.Empty,
                type = typeof(Animator),
                propertyName = propertyName,
            };
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

    }
}
#endif
