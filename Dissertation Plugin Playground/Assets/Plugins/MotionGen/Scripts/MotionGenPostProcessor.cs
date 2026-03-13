#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class MotionGenPostProcessor
{
    private sealed class SampleFrame
    {
        public float time;
        public float[] muscles;
        public Vector3 rootCurvePosition;
        public Quaternion rootCurveRotation;
        public Vector3 sampledRootWorldPosition;
        public readonly Dictionary<MotionGenContactLimb, Vector3> effectors = new Dictionary<MotionGenContactLimb, Vector3>();
    }

    private static readonly MotionGenContactLimb[] AllLimbs =
    {
        MotionGenContactLimb.LeftFoot,
        MotionGenContactLimb.RightFoot,
        MotionGenContactLimb.LeftHand,
        MotionGenContactLimb.RightHand
    };

    public static bool TryDetectContactWindows(
        AnimationClip clip,
        Animator sourceAnimator,
        MotionGenPostProcessSettings settings,
        out List<MotionGenContactWindow> contactWindows,
        out string error)
    {
        contactWindows = new List<MotionGenContactWindow>();
        error = null;

        if (!TrySampleFrames(clip, sourceAnimator, out var frames, out error))
            return false;

        settings = settings?.Clone() ?? new MotionGenPostProcessSettings();
        settings.EnsureDefaults();

        foreach (var limb in AllLimbs)
        {
            if (!IsLimbEligible(limb, settings))
                continue;

            contactWindows.AddRange(DetectContactsForLimb(frames, limb, settings));
        }

        contactWindows = contactWindows
            .OrderBy(window => window.startTime)
            .ThenBy(window => window.limb)
            .ToList();
        RebuildAutoDetectedAnchors(frames, contactWindows);
        return true;
    }

    public static bool TryApplyPostProcessing(
        AnimationClip sourceClip,
        string sourceClipAssetPath,
        Animator sourceAnimator,
        MotionGenPostProcessSettings settings,
        List<MotionGenContactWindow> reviewedContactWindows,
        string referenceClipAssetPath,
        string processedClipAssetPath,
        out MotionGenPostProcessResult result,
        out string error)
    {
        result = null;
        error = null;

        if (sourceClip == null || sourceAnimator == null)
        {
            error = "A source clip and selected humanoid animator are required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceClipAssetPath))
        {
            error = "The selected source clip does not have a valid asset path.";
            return false;
        }

        settings = settings?.Clone() ?? new MotionGenPostProcessSettings();
        settings.EnsureDefaults();

        var sourceTicks = GetAssetLastWriteTicks(sourceClipAssetPath);
        referenceClipAssetPath = EnsureDerivedClipAssetPath(sourceClipAssetPath, referenceClipAssetPath, "_reference");
        processedClipAssetPath = EnsureDerivedClipAssetPath(sourceClipAssetPath, processedClipAssetPath, "_post");

        if (!EnsureReferenceClip(sourceClipAssetPath, referenceClipAssetPath, sourceTicks, out error))
            return false;

        if (!CopyClipAsset(referenceClipAssetPath, processedClipAssetPath, overwrite: true, out error))
            return false;

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var workingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(processedClipAssetPath);
        if (workingClip == null)
        {
            error = $"Unable to load processed clip at {processedClipAssetPath}.";
            return false;
        }

        if (!TrySampleFrames(workingClip, sourceAnimator, out var frames, out error))
            return false;

        if (frames.Count == 0)
        {
            error = "The selected clip did not produce any sample frames.";
            return false;
        }

        var smoothedRootPositions = frames.Select(frame => frame.rootCurvePosition).ToArray();
        if (settings.enableRootSmoothing)
            smoothedRootPositions = SmoothVectors(smoothedRootPositions, settings.rootSmoothingWindow, settings.rootSmoothingBlend);

        var smoothedMuscles = frames.Select(frame => (float[])frame.muscles.Clone()).ToArray();
        if (settings.enableMotionSmoothing)
            smoothedMuscles = SmoothMuscles(smoothedMuscles, settings.motionSmoothingWindow, settings.motionSmoothingBlend);

        var contactWindows = CloneContactWindows(reviewedContactWindows);
        if (settings.enableContactLocking && (contactWindows == null || contactWindows.Count == 0))
        {
            if (!TryDetectContactWindows(workingClip, sourceAnimator, settings, out contactWindows, out error))
                return false;
        }

        if (!TryResolveSupportContacts(
                workingClip,
                sourceAnimator,
                frames,
                smoothedRootPositions,
                smoothedMuscles,
                settings,
                contactWindows,
                out var finalRootPositions,
                out var finalRootRotations,
                out var finalMuscles,
                out error))
        {
            return false;
        }

        WriteHumanoidCurves(workingClip, frames, finalRootPositions, finalRootRotations, finalMuscles);

        EditorUtility.SetDirty(workingClip);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        result = new MotionGenPostProcessResult
        {
            sourceClipAssetPath = sourceClipAssetPath,
            referenceClipAssetPath = referenceClipAssetPath,
            processedClipAssetPath = processedClipAssetPath,
            sourceLastWriteTicks = sourceTicks,
            settings = settings.Clone(),
            contactWindows = CloneContactWindows(contactWindows)
        };
        return true;
    }

    private static List<MotionGenContactWindow> DetectContactsForLimb(
        List<SampleFrame> frames,
        MotionGenContactLimb limb,
        MotionGenPostProcessSettings settings)
    {
        var result = new List<MotionGenContactWindow>();
        if (frames.Count == 0)
            return result;

        var positions = frames.Select(frame => frame.effectors.TryGetValue(limb, out var position) ? position : Vector3.zero).ToArray();
        var deltaTime = GetDeltaTime(frames);
        var minFrameCount = Mathf.Max(2, Mathf.CeilToInt(settings.contactMinDuration / Mathf.Max(0.0001f, deltaTime)));
        var mergeGapFrameCount = Mathf.Max(1, Mathf.RoundToInt(minFrameCount * 0.5f));
        var horizontalSpeeds = CalculateHorizontalSpeeds(positions, deltaTime);
        var verticalSpeeds = CalculateVerticalSpeeds(positions, deltaTime);
        var localMinHeights = CalculateLocalMinimumHeights(positions, Mathf.Clamp(minFrameCount, 2, 8));
        var heightOverLocalMin = positions
            .Select((position, index) => Mathf.Max(0f, position.y - localMinHeights[index]))
            .ToArray();

        var adaptiveVelocityThreshold = Mathf.Max(settings.contactVelocityThreshold, CalculatePercentile(horizontalSpeeds, 0.4f) * 1.35f);
        var adaptiveVerticalThreshold = Mathf.Max(settings.contactVelocityThreshold, CalculatePercentile(verticalSpeeds, 0.45f) * 1.35f);
        var adaptiveHeightThreshold = Mathf.Max(settings.contactHeightThreshold, CalculatePercentile(heightOverLocalMin, 0.35f) * 1.5f);

        var startIndex = -1;
        var gapCount = 0;

        for (var index = 0; index < frames.Count; index++)
        {
            var isCandidate = horizontalSpeeds[index] <= adaptiveVelocityThreshold
                && verticalSpeeds[index] <= adaptiveVerticalThreshold
                && heightOverLocalMin[index] <= adaptiveHeightThreshold;

            if (isCandidate)
            {
                if (startIndex < 0)
                    startIndex = index;
                gapCount = 0;
            }
            else if (startIndex >= 0)
            {
                gapCount++;
                if (gapCount > mergeGapFrameCount)
                {
                    AddContactRange(result, frames, limb, positions, startIndex, index - gapCount, minFrameCount);
                    startIndex = -1;
                    gapCount = 0;
                }
            }
        }

        if (startIndex >= 0)
            AddContactRange(result, frames, limb, positions, startIndex, frames.Count - 1 - gapCount, minFrameCount);

        return result;
    }

    private static void AddContactRange(
        List<MotionGenContactWindow> windows,
        List<SampleFrame> frames,
        MotionGenContactLimb limb,
        Vector3[] positions,
        int startIndex,
        int endIndex,
        int minFrameCount)
    {
        if (endIndex - startIndex + 1 < minFrameCount)
            return;

        windows.Add(new MotionGenContactWindow
        {
            id = Guid.NewGuid().ToString("N"),
            limb = limb,
            startTime = frames[startIndex].time,
            endTime = frames[endIndex].time,
            enabled = true,
            autoDetected = true,
            // Lock to the support position at contact start rather than averaging the whole
            // window, which can otherwise drag the target forward during a planted step.
            anchorPosition = positions[startIndex]
        });
    }

    private static bool TryResolveSupportContacts(
        AnimationClip clip,
        Animator sourceAnimator,
        List<SampleFrame> frames,
        Vector3[] rootPositions,
        float[][] muscles,
        MotionGenPostProcessSettings settings,
        List<MotionGenContactWindow> contactWindows,
        out Vector3[] finalRootPositions,
        out Quaternion[] finalRootRotations,
        out float[][] finalMuscles,
        out string error)
    {
        error = null;
        var supportOffsets = BuildSupportOffsetCurve(frames, contactWindows);
        finalRootPositions = rootPositions
            .Select((position, index) => position + supportOffsets[index])
            .ToArray();
        finalRootRotations = frames.Select(frame => frame.rootCurveRotation).ToArray();
        finalMuscles = muscles.Select(values => (float[])values.Clone()).ToArray();

        if (!settings.enableContactLocking || contactWindows == null || contactWindows.Count == 0)
            return true;

        if (!TryCreateClone(sourceAnimator, out var cloneRoot, out var cloneAnimator, out error))
            return false;

        HumanPoseHandler handler = null;
        var usedAnimationMode = false;
        try
        {
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                usedAnimationMode = true;
            }

            handler = new HumanPoseHandler(cloneAnimator.avatar, cloneAnimator.transform);
            var pose = new HumanPose();

            for (var index = 0; index < frames.Count; index++)
            {
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(cloneRoot, clip, frames[index].time);
                AnimationMode.EndSampling();

                var targetRootPosition = finalRootPositions[index];
                var smoothedRootDelta = targetRootPosition - frames[index].rootCurvePosition;
                var baseSampledWorldRoot = cloneRoot.transform.position;

                var activeContacts = contactWindows
                    .Where(window => window != null && window.enabled && frames[index].time >= window.startTime && frames[index].time <= window.endTime)
                    .ToList();

                cloneRoot.transform.position = baseSampledWorldRoot + smoothedRootDelta;

                if (activeContacts.Count > 0)
                {
                    for (var iteration = 0; iteration < settings.ikIterations; iteration++)
                    {
                        var rootCorrection = CalculateAverageRootCorrection(cloneAnimator, activeContacts);
                        cloneRoot.transform.position += rootCorrection;

                        foreach (var window in activeContacts)
                        {
                            SolveLimbTowardsTarget(cloneAnimator, window.limb, window.anchorPosition);
                        }
                    }
                }

                handler.GetHumanPose(ref pose);
                finalRootPositions[index] = targetRootPosition;
                finalMuscles[index] = (float[])pose.muscles.Clone();
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Post-processing solve failed: {ex.Message}";
            return false;
        }
        finally
        {
            handler?.Dispose();
            if (usedAnimationMode && AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            if (cloneRoot != null)
                UnityEngine.Object.DestroyImmediate(cloneRoot);
        }
    }

    private static void SolveLimbTowardsTarget(Animator animator, MotionGenContactLimb limb, Vector3 target)
    {
        var chain = GetLimbChain(animator, limb);
        if (chain == null || chain.Length < 2 || chain.Any(transform => transform == null))
            return;

        var effector = chain[chain.Length - 1];
        for (var jointIndex = chain.Length - 2; jointIndex >= 0; jointIndex--)
        {
            var joint = chain[jointIndex];
            var toEffector = effector.position - joint.position;
            var toTarget = target - joint.position;
            if (toEffector.sqrMagnitude <= 0.000001f || toTarget.sqrMagnitude <= 0.000001f)
                continue;

            joint.rotation = Quaternion.FromToRotation(toEffector, toTarget) * joint.rotation;
        }
    }

    private static Transform[] GetLimbChain(Animator animator, MotionGenContactLimb limb)
    {
        return limb switch
        {
            MotionGenContactLimb.LeftFoot => new[]
            {
                animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),
                animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),
                animator.GetBoneTransform(HumanBodyBones.LeftFoot)
            },
            MotionGenContactLimb.RightFoot => new[]
            {
                animator.GetBoneTransform(HumanBodyBones.RightUpperLeg),
                animator.GetBoneTransform(HumanBodyBones.RightLowerLeg),
                animator.GetBoneTransform(HumanBodyBones.RightFoot)
            },
            MotionGenContactLimb.LeftHand => new[]
            {
                animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
                animator.GetBoneTransform(HumanBodyBones.LeftLowerArm),
                animator.GetBoneTransform(HumanBodyBones.LeftHand)
            },
            MotionGenContactLimb.RightHand => new[]
            {
                animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
                animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
                animator.GetBoneTransform(HumanBodyBones.RightHand)
            },
            _ => null
        };
    }

    private static bool TrySampleFrames(AnimationClip clip, Animator sourceAnimator, out List<SampleFrame> frames, out string error)
    {
        frames = new List<SampleFrame>();
        error = null;

        if (clip == null || sourceAnimator == null)
        {
            error = "A clip and selected humanoid animator are required for sampling.";
            return false;
        }

        if (!TryCreateClone(sourceAnimator, out var cloneRoot, out var cloneAnimator, out error))
            return false;

        var times = BuildSampleTimes(clip);
        HumanPoseHandler handler = null;
        var pose = new HumanPose();
        var usedAnimationMode = false;

        try
        {
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                usedAnimationMode = true;
            }

            handler = new HumanPoseHandler(cloneAnimator.avatar, cloneAnimator.transform);
            for (var index = 0; index < times.Count; index++)
            {
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(cloneRoot, clip, times[index]);
                AnimationMode.EndSampling();

                handler.GetHumanPose(ref pose);

                var frame = new SampleFrame
                {
                    time = times[index],
                    muscles = (float[])pose.muscles.Clone(),
                    rootCurvePosition = ReadRootCurvePosition(clip, times[index]),
                    rootCurveRotation = ReadRootCurveRotation(clip, times[index]),
                    sampledRootWorldPosition = cloneRoot.transform.position
                };

                foreach (var limb in AllLimbs)
                {
                    var effector = GetLimbEffector(cloneAnimator, limb);
                    if (effector != null)
                        frame.effectors[limb] = effector.position;
                }

                frames.Add(frame);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Frame sampling failed: {ex.Message}";
            return false;
        }
        finally
        {
            handler?.Dispose();
            if (usedAnimationMode && AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            if (cloneRoot != null)
                UnityEngine.Object.DestroyImmediate(cloneRoot);
        }
    }

    private static Transform GetLimbEffector(Animator animator, MotionGenContactLimb limb)
    {
        return limb switch
        {
            MotionGenContactLimb.LeftFoot => animator.GetBoneTransform(HumanBodyBones.LeftFoot),
            MotionGenContactLimb.RightFoot => animator.GetBoneTransform(HumanBodyBones.RightFoot),
            MotionGenContactLimb.LeftHand => animator.GetBoneTransform(HumanBodyBones.LeftHand),
            MotionGenContactLimb.RightHand => animator.GetBoneTransform(HumanBodyBones.RightHand),
            _ => null
        };
    }

    private static bool TryGetLimbEffectorPosition(Animator animator, MotionGenContactLimb limb, out Vector3 position)
    {
        var effector = GetLimbEffector(animator, limb);
        if (effector == null)
        {
            position = Vector3.zero;
            return false;
        }

        position = effector.position;
        return true;
    }

    private static Vector3 CalculateAverageRootCorrection(Animator animator, List<MotionGenContactWindow> activeContacts)
    {
        if (animator == null || activeContacts == null || activeContacts.Count == 0)
            return Vector3.zero;

        var totalCorrection = Vector3.zero;
        var count = 0;
        foreach (var window in activeContacts)
        {
            if (window == null || !window.enabled)
                continue;

            if (!TryGetLimbEffectorPosition(animator, window.limb, out var effectorPosition))
                continue;

            totalCorrection += window.anchorPosition - effectorPosition;
            count++;
        }

        return count > 0 ? totalCorrection / count : Vector3.zero;
    }

    private static Vector3[] BuildSupportOffsetCurve(List<SampleFrame> frames, List<MotionGenContactWindow> contactWindows)
    {
        var offsets = new Vector3[frames.Count];
        var hasKey = new bool[frames.Count];
        if (frames == null || frames.Count == 0 || contactWindows == null || contactWindows.Count == 0)
            return offsets;

        foreach (var window in contactWindows.Where(window => window != null && window.enabled).OrderBy(window => window.startTime))
        {
            var startIndex = FindClosestFrameIndex(frames, window.startTime);
            var endIndex = FindClosestFrameIndex(frames, window.endTime);
            if (startIndex < 0 || endIndex < 0)
                continue;

            if (!frames[startIndex].effectors.TryGetValue(window.limb, out var startEffectorPosition))
                continue;

            var offset = window.anchorPosition - startEffectorPosition;
            offsets[startIndex] = offset;
            hasKey[startIndex] = true;
            offsets[endIndex] = offset;
            hasKey[endIndex] = true;
        }

        var firstKey = Array.FindIndex(hasKey, value => value);
        if (firstKey < 0)
            return offsets;

        for (var index = 0; index < firstKey; index++)
            offsets[index] = offsets[firstKey];

        var previousKey = firstKey;
        for (var index = firstKey + 1; index < offsets.Length; index++)
        {
            if (!hasKey[index])
                continue;

            var nextKey = index;
            for (var gapIndex = previousKey + 1; gapIndex < nextKey; gapIndex++)
            {
                var t = (gapIndex - previousKey) / (float)(nextKey - previousKey);
                offsets[gapIndex] = Vector3.Lerp(offsets[previousKey], offsets[nextKey], t);
            }

            previousKey = nextKey;
        }

        for (var index = previousKey + 1; index < offsets.Length; index++)
            offsets[index] = offsets[previousKey];

        return offsets;
    }

    private static int FindClosestFrameIndex(List<SampleFrame> frames, float time)
    {
        if (frames == null || frames.Count == 0)
            return -1;

        var bestIndex = 0;
        var bestDistance = Mathf.Abs(frames[0].time - time);
        for (var index = 1; index < frames.Count; index++)
        {
            var distance = Mathf.Abs(frames[index].time - time);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestIndex = index;
        }

        return bestIndex;
    }

    private static bool TryCreateClone(Animator sourceAnimator, out GameObject cloneRoot, out Animator cloneAnimator, out string error)
    {
        cloneRoot = null;
        cloneAnimator = null;
        error = null;

        if (sourceAnimator == null || sourceAnimator.avatar == null || !sourceAnimator.isHuman)
        {
            error = "Select a valid humanoid animator before using post-processing.";
            return false;
        }

        cloneRoot = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
        cloneRoot.hideFlags = HideFlags.HideAndDontSave;
        cloneRoot.name = $"{sourceAnimator.gameObject.name}_MotionGenPostTemp";
        cloneAnimator = cloneRoot.GetComponent<Animator>();
        if (cloneAnimator == null || cloneAnimator.avatar == null || !cloneAnimator.isHuman)
        {
            if (cloneRoot != null)
                UnityEngine.Object.DestroyImmediate(cloneRoot);
            error = "The selected animator could not be cloned as a humanoid rig.";
            return false;
        }

        cloneAnimator.enabled = true;
        cloneAnimator.applyRootMotion = false;
        return true;
    }

    private static List<float> BuildSampleTimes(AnimationClip clip)
    {
        var result = new List<float>();
        if (clip == null)
            return result;

        var frameRate = Mathf.Max(1f, clip.frameRate);
        var clipLength = Mathf.Max(0.01f, clip.length);
        var sampleCount = Mathf.Max(2, Mathf.RoundToInt(clipLength * frameRate) + 1);
        for (var index = 0; index < sampleCount; index++)
        {
            var time = index == sampleCount - 1
                ? clipLength
                : Mathf.Min(clipLength, index / frameRate);
            result.Add(time);
        }

        return result;
    }

    private static Vector3[] SmoothVectors(Vector3[] values, int window, float blend)
    {
        var smoothed = new Vector3[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var average = Vector3.zero;
            var count = 0;
            for (var offset = -window; offset <= window; offset++)
            {
                var sampleIndex = Mathf.Clamp(index + offset, 0, values.Length - 1);
                average += values[sampleIndex];
                count++;
            }

            average /= Mathf.Max(1, count);
            smoothed[index] = Vector3.Lerp(values[index], average, blend);
        }

        if (smoothed.Length > 0)
        {
            smoothed[0] = values[0];
            smoothed[smoothed.Length - 1] = values[values.Length - 1];
        }

        return smoothed;
    }

    private static float[] CalculateHorizontalSpeeds(Vector3[] positions, float deltaTime)
    {
        var speeds = new float[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            var previous = positions[Mathf.Max(0, index - 1)];
            var next = positions[Mathf.Min(positions.Length - 1, index + 1)];
            var previousXZ = new Vector2(previous.x, previous.z);
            var nextXZ = new Vector2(next.x, next.z);
            var divisor = Mathf.Max(0.0001f, deltaTime * (index == 0 || index == positions.Length - 1 ? 1f : 2f));
            speeds[index] = Vector2.Distance(previousXZ, nextXZ) / divisor;
        }

        return speeds;
    }

    private static float[] CalculateVerticalSpeeds(Vector3[] positions, float deltaTime)
    {
        var speeds = new float[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            var previous = positions[Mathf.Max(0, index - 1)].y;
            var next = positions[Mathf.Min(positions.Length - 1, index + 1)].y;
            var divisor = Mathf.Max(0.0001f, deltaTime * (index == 0 || index == positions.Length - 1 ? 1f : 2f));
            speeds[index] = Mathf.Abs(next - previous) / divisor;
        }

        return speeds;
    }

    private static float[] CalculateLocalMinimumHeights(Vector3[] positions, int windowRadius)
    {
        var localMins = new float[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            var minHeight = float.PositiveInfinity;
            for (var offset = -windowRadius; offset <= windowRadius; offset++)
            {
                var sampleIndex = Mathf.Clamp(index + offset, 0, positions.Length - 1);
                minHeight = Mathf.Min(minHeight, positions[sampleIndex].y);
            }

            localMins[index] = float.IsPositiveInfinity(minHeight) ? positions[index].y : minHeight;
        }

        return localMins;
    }

    private static float CalculatePercentile(float[] values, float percentile)
    {
        if (values == null || values.Length == 0)
            return 0f;

        var ordered = values.OrderBy(value => value).ToArray();
        var rawIndex = Mathf.Clamp01(percentile) * (ordered.Length - 1);
        var lowerIndex = Mathf.FloorToInt(rawIndex);
        var upperIndex = Mathf.CeilToInt(rawIndex);
        if (lowerIndex == upperIndex)
            return ordered[lowerIndex];

        var t = rawIndex - lowerIndex;
        return Mathf.Lerp(ordered[lowerIndex], ordered[upperIndex], t);
    }

    private static float[][] SmoothMuscles(float[][] values, int window, float blend)
    {
        var smoothed = values.Select(frame => (float[])frame.Clone()).ToArray();
        if (values.Length == 0)
            return smoothed;

        for (var muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
        {
            for (var frameIndex = 0; frameIndex < values.Length; frameIndex++)
            {
                var average = 0f;
                var count = 0;
                for (var offset = -window; offset <= window; offset++)
                {
                    var sampleIndex = Mathf.Clamp(frameIndex + offset, 0, values.Length - 1);
                    average += values[sampleIndex][muscle];
                    count++;
                }

                average /= Mathf.Max(1, count);
                smoothed[frameIndex][muscle] = Mathf.Lerp(values[frameIndex][muscle], average, blend);
            }
        }

        return smoothed;
    }

    private static void WriteHumanoidCurves(
        AnimationClip clip,
        List<SampleFrame> frames,
        Vector3[] rootPositions,
        Quaternion[] rootRotations,
        float[][] muscles)
    {
        for (var muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
        {
            var curve = new AnimationCurve();
            for (var frame = 0; frame < frames.Count; frame++)
                curve.AddKey(new Keyframe(frames[frame].time, muscles[frame][muscle]));

            SetCurve(clip, HumanTrait.MuscleName[muscle], curve);
        }

        SetCurve(clip, "RootT.x", BuildCurve(frames, rootPositions.Select(value => value.x).ToArray()));
        SetCurve(clip, "RootT.y", BuildCurve(frames, rootPositions.Select(value => value.y).ToArray()));
        SetCurve(clip, "RootT.z", BuildCurve(frames, rootPositions.Select(value => value.z).ToArray()));
        SetCurve(clip, "RootQ.x", BuildCurve(frames, rootRotations.Select(value => value.x).ToArray()));
        SetCurve(clip, "RootQ.y", BuildCurve(frames, rootRotations.Select(value => value.y).ToArray()));
        SetCurve(clip, "RootQ.z", BuildCurve(frames, rootRotations.Select(value => value.z).ToArray()));
        SetCurve(clip, "RootQ.w", BuildCurve(frames, rootRotations.Select(value => value.w).ToArray()));
        clip.EnsureQuaternionContinuity();
    }

    private static AnimationCurve BuildCurve(List<SampleFrame> frames, float[] values)
    {
        var curve = new AnimationCurve();
        for (var frame = 0; frame < frames.Count; frame++)
            curve.AddKey(new Keyframe(frames[frame].time, values[frame]));
        return curve;
    }

    private static void SetCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
    {
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName), curve);
    }

    private static float EvaluateAnimatorFloatCurve(AnimationClip clip, string propertyName, float time)
    {
        var curve = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName));
        return curve != null ? curve.Evaluate(time) : 0f;
    }

    private static Vector3 ReadRootCurvePosition(AnimationClip clip, float time)
    {
        return new Vector3(
            EvaluateAnimatorFloatCurve(clip, "RootT.x", time),
            EvaluateAnimatorFloatCurve(clip, "RootT.y", time),
            EvaluateAnimatorFloatCurve(clip, "RootT.z", time));
    }

    private static Quaternion ReadRootCurveRotation(AnimationClip clip, float time)
    {
        var rotation = new Quaternion(
            EvaluateAnimatorFloatCurve(clip, "RootQ.x", time),
            EvaluateAnimatorFloatCurve(clip, "RootQ.y", time),
            EvaluateAnimatorFloatCurve(clip, "RootQ.z", time),
            EvaluateAnimatorFloatCurve(clip, "RootQ.w", time));

        var magnitudeSquared = rotation.x * rotation.x
            + rotation.y * rotation.y
            + rotation.z * rotation.z
            + rotation.w * rotation.w;
        if (magnitudeSquared <= 0.000001f)
            return Quaternion.identity;

        return Quaternion.Normalize(rotation);
    }

    private static bool IsLimbEligible(MotionGenContactLimb limb, MotionGenPostProcessSettings settings)
    {
        return limb switch
        {
            MotionGenContactLimb.LeftFoot => settings.contactLockFeet,
            MotionGenContactLimb.RightFoot => settings.contactLockFeet,
            MotionGenContactLimb.LeftHand => settings.contactLockHands,
            MotionGenContactLimb.RightHand => settings.contactLockHands,
            _ => false
        };
    }

    private static float GetDeltaTime(List<SampleFrame> frames)
    {
        if (frames.Count < 2)
            return 1f / 30f;
        return Mathf.Max(0.0001f, frames[1].time - frames[0].time);
    }

    private static string EnsureDerivedClipAssetPath(string sourceClipAssetPath, string existingPath, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(existingPath))
            return existingPath.Replace("\\", "/");

        var directory = Path.GetDirectoryName(sourceClipAssetPath)?.Replace("\\", "/") ?? "Assets";
        var fileName = Path.GetFileNameWithoutExtension(sourceClipAssetPath);
        return $"{directory}/{fileName}{suffix}.anim";
    }

    private static bool EnsureReferenceClip(string sourceClipAssetPath, string referenceClipAssetPath, long sourceTicks, out string error)
    {
        error = null;
        var sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(sourceClipAssetPath);
        if (sourceClip == null)
        {
            error = $"Unable to load source clip at {sourceClipAssetPath}.";
            return false;
        }

        var needsRefresh = AssetDatabase.LoadAssetAtPath<AnimationClip>(referenceClipAssetPath) == null;
        if (!needsRefresh)
        {
            var referenceTicks = GetAssetLastWriteTicks(referenceClipAssetPath);
            needsRefresh = referenceTicks < sourceTicks;
        }

        if (!needsRefresh)
            return true;

        return CopyClipAsset(sourceClipAssetPath, referenceClipAssetPath, overwrite: true, out error);
    }

    private static bool CopyClipAsset(string sourcePath, string targetPath, bool overwrite, out string error)
    {
        error = null;
        var sourceClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(sourcePath);
        if (sourceClip == null)
        {
            error = $"Unable to load clip at {sourcePath}.";
            return false;
        }

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
        var clone = UnityEngine.Object.Instantiate(sourceClip);
        clone.name = Path.GetFileNameWithoutExtension(targetPath);

        if (existing != null)
        {
            if (!overwrite)
                return true;

            EditorUtility.CopySerialized(clone, existing);
            UnityEngine.Object.DestroyImmediate(clone);
            EditorUtility.SetDirty(existing);
            return true;
        }

        var directory = Path.GetDirectoryName(targetPath)?.Replace("\\", "/");
        if (!string.IsNullOrWhiteSpace(directory))
            EnsureAssetFolder(directory);

        AssetDatabase.CreateAsset(clone, targetPath);
        return true;
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        var normalized = assetFolder.Replace("\\", "/").TrimEnd('/');
        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
            return;

        var current = "Assets";
        for (var index = 1; index < parts.Length; index++)
        {
            var next = $"{current}/{parts[index]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[index]);
            current = next;
        }
    }

    private static long GetAssetLastWriteTicks(string assetPath)
    {
        var fullPath = Path.GetFullPath(assetPath);
        return File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0L;
    }

    private static List<MotionGenContactWindow> CloneContactWindows(List<MotionGenContactWindow> contactWindows)
    {
        return contactWindows == null
            ? new List<MotionGenContactWindow>()
            : contactWindows
                .Where(window => window != null)
                .Select(window => new MotionGenContactWindow
                {
                    id = string.IsNullOrWhiteSpace(window.id) ? Guid.NewGuid().ToString("N") : window.id,
                    limb = window.limb,
                    startTime = window.startTime,
                    endTime = window.endTime,
                    enabled = window.enabled,
                    autoDetected = window.autoDetected,
                    anchorPosition = window.anchorPosition
                })
                .ToList();
    }

    private static void RebuildAutoDetectedAnchors(List<SampleFrame> frames, List<MotionGenContactWindow> contactWindows)
    {
        if (frames == null || frames.Count == 0 || contactWindows == null || contactWindows.Count == 0)
            return;

        var ordered = contactWindows
            .Where(window => window != null)
            .OrderBy(window => window.startTime)
            .ThenBy(window => window.limb)
            .ToList();

        MotionGenContactWindow previousWindow = null;
        foreach (var window in ordered)
        {
            if (!window.autoDetected)
                continue;

            var currentPosition = EvaluateEffectorPositionAtTime(frames, window.limb, window.startTime);
            if (previousWindow == null)
            {
                window.anchorPosition = currentPosition;
                previousWindow = window;
                continue;
            }

            var previousPositionAtCurrentTime = EvaluateEffectorPositionAtTime(frames, previousWindow.limb, window.startTime);
            window.anchorPosition = previousWindow.anchorPosition + (currentPosition - previousPositionAtCurrentTime);
            previousWindow = window;
        }
    }

    private static Vector3 EvaluateEffectorPositionAtTime(List<SampleFrame> frames, MotionGenContactLimb limb, float time)
    {
        if (frames == null || frames.Count == 0)
            return Vector3.zero;

        if (time <= frames[0].time)
            return GetEffectorPosition(frames[0], limb);

        for (var index = 1; index < frames.Count; index++)
        {
            if (time > frames[index].time)
                continue;

            var previous = frames[index - 1];
            var next = frames[index];
            var duration = Mathf.Max(0.0001f, next.time - previous.time);
            var t = Mathf.Clamp01((time - previous.time) / duration);
            return Vector3.Lerp(GetEffectorPosition(previous, limb), GetEffectorPosition(next, limb), t);
        }

        return GetEffectorPosition(frames[frames.Count - 1], limb);
    }

    private static Vector3 GetEffectorPosition(SampleFrame frame, MotionGenContactLimb limb)
    {
        return frame != null && frame.effectors.TryGetValue(limb, out var position) ? position : Vector3.zero;
    }
}
#endif
