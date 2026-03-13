#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public enum MotionGenContactLimb
{
    LeftFoot,
    RightFoot,
    LeftHand,
    RightHand
}

[Serializable]
public class MotionGenContactWindow
{
    public string id;
    public MotionGenContactLimb limb;
    public float startTime;
    public float endTime;
    public bool enabled = true;
    public bool autoDetected = true;
    public Vector3 anchorPosition;
}

[Serializable]
public class MotionGenPostProcessSettings
{
    public bool enableRootSmoothing = true;
    public int rootSmoothingWindow = 2;
    public float rootSmoothingBlend = 0.45f;

    public bool enableMotionSmoothing = true;
    public int motionSmoothingWindow = 2;
    public float motionSmoothingBlend = 0.25f;

    public bool enableContactLocking = true;
    public bool contactLockFeet = true;
    public bool contactLockHands = false;
    public float contactVelocityThreshold = 0.04f;
    public float contactHeightThreshold = 0.08f;
    public float contactMinDuration = 0.12f;
    public int ikIterations = 6;

    public void EnsureDefaults()
    {
        rootSmoothingWindow = Mathf.Clamp(rootSmoothingWindow, 1, 12);
        rootSmoothingBlend = Mathf.Clamp01(rootSmoothingBlend);
        motionSmoothingWindow = Mathf.Clamp(motionSmoothingWindow, 1, 12);
        motionSmoothingBlend = Mathf.Clamp01(motionSmoothingBlend);
        contactVelocityThreshold = Mathf.Max(0.0001f, contactVelocityThreshold);
        contactHeightThreshold = Mathf.Max(0.001f, contactHeightThreshold);
        contactMinDuration = Mathf.Max(0.04f, contactMinDuration);
        ikIterations = Mathf.Clamp(ikIterations, 1, 20);
    }

    public MotionGenPostProcessSettings Clone()
    {
        return new MotionGenPostProcessSettings
        {
            enableRootSmoothing = enableRootSmoothing,
            rootSmoothingWindow = rootSmoothingWindow,
            rootSmoothingBlend = rootSmoothingBlend,
            enableMotionSmoothing = enableMotionSmoothing,
            motionSmoothingWindow = motionSmoothingWindow,
            motionSmoothingBlend = motionSmoothingBlend,
            enableContactLocking = enableContactLocking,
            contactLockFeet = contactLockFeet,
            contactLockHands = contactLockHands,
            contactVelocityThreshold = contactVelocityThreshold,
            contactHeightThreshold = contactHeightThreshold,
            contactMinDuration = contactMinDuration,
            ikIterations = ikIterations
        };
    }
}

[Serializable]
public class MotionGenPostProcessResult
{
    public string sourceClipAssetPath;
    public string referenceClipAssetPath;
    public string processedClipAssetPath;
    public long sourceLastWriteTicks;
    public MotionGenPostProcessSettings settings;
    public List<MotionGenContactWindow> contactWindows = new List<MotionGenContactWindow>();
}
#endif
