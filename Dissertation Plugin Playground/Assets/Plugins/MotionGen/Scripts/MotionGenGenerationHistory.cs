#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[Serializable]
public class MotionGenPathEditKey
{
    public string id;
    public float time;
    public Vector3 position;
    public Quaternion rotation = Quaternion.identity;
}

[Serializable]
public class MotionGenGenerationItem
{
    public string id;
    public string displayName;
    public string clipName;
    public string externalBvhPath;
    public string mirroredBvhAssetPath;
    public string clipAssetPath;
    public string backendFilename;
    public string metaJson;
    public int resolvedSeed;
    public bool pathEditLockY = true;
    public List<MotionGenPathEditKey> pathEditKeys = new List<MotionGenPathEditKey>();
    public bool postProcessingEnabled;
    public string referenceClipAssetPath;
    public string referenceSourceClipAssetPath;
    public long referenceSourceLastWriteTicks;
    public string processedClipAssetPath;
    public long postProcessedSourceLastWriteTicks;
    public string postProcessedAtUtc;
    public MotionGenPostProcessSettings postProcessSettings = new MotionGenPostProcessSettings();
    public List<MotionGenContactWindow> reviewedContactWindows = new List<MotionGenContactWindow>();
}

[Serializable]
public class MotionGenGenerationSession
{
    public string id;
    public string generationName;
    public string prompt;
    public string exportDirectory;
    public string mirrorDirectoryAssetPath;
    public string createdAtUtc;
    public int versionCount;
    public bool usedRandomSeed;
    public int baseSeed;
    public List<MotionGenGenerationItem> items = new List<MotionGenGenerationItem>();
}

public class MotionGenGenerationHistory : ScriptableObject
{
    public List<MotionGenGenerationSession> sessions = new List<MotionGenGenerationSession>();

    private const string AssetPath = "Assets/MotionGen/Editor/MotionGenGenerationHistory.asset";

    public static MotionGenGenerationHistory GetOrCreate()
    {
        var history = AssetDatabase.LoadAssetAtPath<MotionGenGenerationHistory>(AssetPath);
        if (history != null)
            return history;

        if (!AssetDatabase.IsValidFolder("Assets/MotionGen"))
            AssetDatabase.CreateFolder("Assets", "MotionGen");
        if (!AssetDatabase.IsValidFolder("Assets/MotionGen/Editor"))
            AssetDatabase.CreateFolder("Assets/MotionGen", "Editor");

        history = CreateInstance<MotionGenGenerationHistory>();
        AssetDatabase.CreateAsset(history, AssetPath);
        AssetDatabase.SaveAssets();
        return history;
    }

    public void AddSession(MotionGenGenerationSession session)
    {
        if (session == null)
            return;

        sessions.Insert(0, session);
        Save();
    }

    public void PruneMissingEntries()
    {
        foreach (var session in sessions)
        {
            session.items = session.items
                .Where(item => item != null && IsUsable(item))
                .ToList();
        }

        sessions = sessions
            .Where(session => session != null && session.items != null && session.items.Count > 0)
            .ToList();

        Save();
    }

    public void Save()
    {
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();
    }

    private static bool IsUsable(MotionGenGenerationItem item)
    {
        var hasExternal = !string.IsNullOrWhiteSpace(item.externalBvhPath) && File.Exists(item.externalBvhPath);
        var hasMirror = !string.IsNullOrWhiteSpace(item.mirroredBvhAssetPath)
            && AssetDatabase.LoadAssetAtPath<TextAsset>(item.mirroredBvhAssetPath) != null;
        var hasClip = !string.IsNullOrWhiteSpace(item.clipAssetPath)
            && AssetDatabase.LoadAssetAtPath<AnimationClip>(item.clipAssetPath) != null;
        return hasExternal || hasMirror || hasClip;
    }
}
#endif
