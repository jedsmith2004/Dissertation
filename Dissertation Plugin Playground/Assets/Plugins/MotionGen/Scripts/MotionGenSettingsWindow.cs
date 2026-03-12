#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class MotionGenSettingsWindow : EditorWindow
{
    private MotionGenEditorSettings _settings;
    private Vector2 _scroll;

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
        DrawSourceBasisEditor();

        EditorGUILayout.Space();
        DrawCalibrationEditor();

        EditorGUILayout.Space();
        if (GUILayout.Button("Save"))
        {
            _settings.Save();
            Close();
        }
    }

    private void DrawSourceBasisEditor()
    {
        EditorGUILayout.LabelField("Canonical Source Basis", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("This is the structural source-rig basis layer. Use it for canonical T2M-GPT bone-axis assumptions, not per-avatar compensation. These overrides are applied before humanoid transfer.", MessageType.Info);

        _settings.canonicalSourceBasisMode = (MotionGenEditorSettings.CanonicalSourceBasisMode)EditorGUILayout.EnumPopup(
            "Source Basis Mode",
            _settings.canonicalSourceBasisMode);

        if (_settings.sourceBasisOverrides == null)
            _settings.sourceBasisOverrides = new List<MotionGenEditorSettings.SourceBasisEntry>();

        using (new EditorGUI.DisabledScope(_settings.canonicalSourceBasisMode != MotionGenEditorSettings.CanonicalSourceBasisMode.ManualOverrides))
        {
            foreach (HumanBodyBones bone in GetEditableSourceBasisBones())
            {
                if (!_settings.TryGetSourceBasisOverride(bone, out var correction))
                    correction = Quaternion.identity;

                Vector3 euler = ToSignedEuler(correction.eulerAngles);

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField(bone.ToString(), EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                var updatedEuler = EditorGUILayout.Vector3Field("Local Euler", euler);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.SetSourceBasisOverride(bone, Quaternion.Euler(updatedEuler));
                    EditorUtility.SetDirty(_settings);
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Zero"))
                {
                    _settings.SetSourceBasisOverride(bone, Quaternion.identity);
                    EditorUtility.SetDirty(_settings);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Clear Source Basis Overrides"))
        {
            _settings.ClearSourceBasisOverrides();
            EditorUtility.SetDirty(_settings);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawCalibrationEditor()
    {
        EditorGUILayout.LabelField("Manual Retarget Calibration", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use this to manually adjust stored per-bone calibration when automatic capture is insufficient. Angles are applied as quaternions in XYZ Euler order.", MessageType.Info);

        _settings.useRetargetCalibration = EditorGUILayout.ToggleLeft("Use Stored Calibration", _settings.useRetargetCalibration);

        if (_settings.retargetCalibration == null)
            _settings.retargetCalibration = new List<MotionGenEditorSettings.BoneCalibrationEntry>();

        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220));

        foreach (HumanBodyBones bone in GetEditableCalibrationBones())
        {
            if (!_settings.TryGetCalibration(bone, out var correction))
                correction = Quaternion.identity;

            Vector3 euler = ToSignedEuler(correction.eulerAngles);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(bone.ToString(), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var updatedEuler = EditorGUILayout.Vector3Field("Euler", euler);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.SetCalibration(bone, Quaternion.Euler(updatedEuler));
                EditorUtility.SetDirty(_settings);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Zero"))
            {
                _settings.SetCalibration(bone, Quaternion.identity);
                EditorUtility.SetDirty(_settings);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    private static HumanBodyBones[] GetEditableSourceBasisBones()
    {
        return new[]
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.Neck,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
        };
    }

    private static HumanBodyBones[] GetEditableCalibrationBones()
    {
        return new[]
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
        };
    }

    private static Vector3 ToSignedEuler(Vector3 euler)
    {
        return new Vector3(ToSignedAngle(euler.x), ToSignedAngle(euler.y), ToSignedAngle(euler.z));
    }

    private static float ToSignedAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
            angle -= 360f;
        return angle;
    }
}

public class MotionGenEditorSettings : ScriptableObject
{
    public enum MotionFormat { BVH, JSON }
    public enum CanonicalSourceBasisMode
    {
        LegacyInferred,
        ManualOverrides,
    }

    public enum CanonicalRetargetExperimentMode
    {
        Baseline,
        RootInverse,
        LocalPreMultiplyBasis,
        LocalConjugateBasis,
        SelectivePreMultiplyBasis,
        SelectiveConjugateBasis,
    }

    [Serializable]
    public class BoneCalibrationEntry
    {
        public int bone;
        public Quaternion correction = Quaternion.identity;
    }

    [Serializable]
    public class SourceBasisEntry
    {
        public int bone;
        public Quaternion localCorrection = Quaternion.identity;
    }

    public string prompt = "walk forward";
    public int fps = 30;
    public float durationSeconds = 2.0f;
    public int seed = 0;
    public MotionFormat format = MotionFormat.JSON;
    public bool exportSmplSidecar = true;
    public CanonicalSourceBasisMode canonicalSourceBasisMode = CanonicalSourceBasisMode.LegacyInferred;
    public List<SourceBasisEntry> sourceBasisOverrides = new List<SourceBasisEntry>();
    public bool useRetargetCalibration = true;
    public CanonicalRetargetExperimentMode canonicalRetargetExperimentMode = CanonicalRetargetExperimentMode.Baseline;
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

    public void SetSourceBasisOverride(HumanBodyBones bone, Quaternion localCorrection)
    {
        var key = (int)bone;
        var entry = sourceBasisOverrides.Find(e => e.bone == key);
        if (entry == null)
        {
            entry = new SourceBasisEntry { bone = key, localCorrection = localCorrection };
            sourceBasisOverrides.Add(entry);
        }
        else
        {
            entry.localCorrection = localCorrection;
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

    public bool TryGetSourceBasisOverride(HumanBodyBones bone, out Quaternion localCorrection)
    {
        var key = (int)bone;
        var entry = sourceBasisOverrides.Find(e => e.bone == key);
        if (entry != null)
        {
            localCorrection = entry.localCorrection;
            return true;
        }

        localCorrection = Quaternion.identity;
        return false;
    }

    public void ClearCalibration()
    {
        retargetCalibration.Clear();
    }

    public void ClearSourceBasisOverrides()
    {
        sourceBasisOverrides.Clear();
    }
}
#endif