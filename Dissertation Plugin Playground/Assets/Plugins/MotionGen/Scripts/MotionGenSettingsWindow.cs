#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class MotionGenSettingsWindow : EditorWindow
{
    private MotionGenEditorSettings _settings;

    public static void ShowWindow(MotionGenEditorSettings settings)
    {
        var window = GetWindow<MotionGenSettingsWindow>(true, "MotionGen Settings");
        window._settings = settings;
        window.minSize = new Vector2(320, 260);
        window.ShowUtility();
    }

    private void OnGUI()
    {
        if (_settings == null) return;

        EditorGUILayout.LabelField("Generation Settings", EditorStyles.boldLabel);
        _settings.prompt = EditorGUILayout.TextArea(_settings.prompt, GUILayout.MinHeight(60));

        _settings.fps = EditorGUILayout.IntField("FPS", _settings.fps);
        _settings.durationSeconds = EditorGUILayout.FloatField("Duration (s)", _settings.durationSeconds);
        _settings.seed = EditorGUILayout.IntField("Seed", _settings.seed);
        _settings.format = (MotionGenEditorSettings.MotionFormat)EditorGUILayout.EnumPopup("Format", _settings.format);
        _settings.exportSmplSidecar = EditorGUILayout.ToggleLeft("Export SMPL sidecar (.smpl.json)", _settings.exportSmplSidecar);

        EditorGUILayout.Space();
        if (GUILayout.Button("Save"))
        {
            _settings.Save();
            Close();
        }
    }
}

public class MotionGenEditorSettings : ScriptableObject
{
    public enum MotionFormat { BVH, JSON }

    [Serializable]
    public class BoneCalibrationEntry
    {
        public int bone;
        public Quaternion correction = Quaternion.identity;
    }

    public string prompt = "walk forward";
    public int fps = 30;
    public float durationSeconds = 2.0f;
    public int seed = 0;
    public MotionFormat format = MotionFormat.JSON;
    public bool exportSmplSidecar = true;
    public bool useRetargetCalibration = true;
    public List<BoneCalibrationEntry> retargetCalibration = new List<BoneCalibrationEntry>();

    private const string AssetPath = "Assets/MotionGen/Editor/MotionGenEditorSettings.asset";

    public static MotionGenEditorSettings GetOrCreate()
    {
        var settings = AssetDatabase.LoadAssetAtPath<MotionGenEditorSettings>(AssetPath);
        if (settings == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/MotionGen"))
                AssetDatabase.CreateFolder("Assets", "MotionGen");
            if (!AssetDatabase.IsValidFolder("Assets/MotionGen/Editor"))
                AssetDatabase.CreateFolder("Assets/MotionGen", "Editor");

            settings = CreateInstance<MotionGenEditorSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
        }
        return settings;
    }

    public void Save()
    {
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();
    }

    public void SetCalibration(HumanBodyBones bone, Quaternion correction)
    {
        var key = (int)bone;
        var entry = retargetCalibration.Find(e => e.bone == key);
        if (entry == null)
        {
            entry = new BoneCalibrationEntry { bone = key, correction = correction };
            retargetCalibration.Add(entry);
        }
        else
        {
            entry.correction = correction;
        }
    }

    public bool TryGetCalibration(HumanBodyBones bone, out Quaternion correction)
    {
        var key = (int)bone;
        var entry = retargetCalibration.Find(e => e.bone == key);
        if (entry != null)
        {
            correction = entry.correction;
            return true;
        }

        correction = Quaternion.identity;
        return false;
    }

    public void ClearCalibration()
    {
        retargetCalibration.Clear();
    }
}
#endif