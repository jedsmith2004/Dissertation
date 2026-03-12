#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public class MotionGenSettingsWindow : EditorWindow
{
    private MotionGenEditorSettings _settings;

    public static void ShowWindow(MotionGenEditorSettings settings)
    {
        var window = GetWindow<MotionGenSettingsWindow>(true, "MotionGen Settings");
        window._settings = settings;
        window.minSize = new Vector2(320f, 180f);
        window.ShowUtility();
    }

    private void OnGUI()
    {
        if (_settings == null)
            return;

        EditorGUILayout.LabelField("Backend Connection", EditorStyles.boldLabel);
        _settings.serverHost = EditorGUILayout.TextField("Host", _settings.serverHost);
        _settings.serverPort = EditorGUILayout.IntField("Port", _settings.serverPort);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Generation Defaults", EditorStyles.boldLabel);
        _settings.autoApplyOnGenerate = EditorGUILayout.ToggleLeft("Auto-apply generated clip", _settings.autoApplyOnGenerate);

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
    public string prompt = "walk forward";
    public int fps = 20;
    public float durationSeconds = 2.0f;
    public int seed = 0;
    public string serverHost = "127.0.0.1";
    public int serverPort = 50051;
    public bool autoApplyOnGenerate = true;

    private const string AssetPath = "Assets/MotionGen/Editor/MotionGenEditorSettings.asset";

    public static MotionGenEditorSettings GetOrCreate()
    {
        var settings = AssetDatabase.LoadAssetAtPath<MotionGenEditorSettings>(AssetPath);
        if (settings != null)
            return settings;

        if (!AssetDatabase.IsValidFolder("Assets/MotionGen"))
            AssetDatabase.CreateFolder("Assets", "MotionGen");
        if (!AssetDatabase.IsValidFolder("Assets/MotionGen/Editor"))
            AssetDatabase.CreateFolder("Assets/MotionGen", "Editor");

        settings = CreateInstance<MotionGenEditorSettings>();
        AssetDatabase.CreateAsset(settings, AssetPath);
        AssetDatabase.SaveAssets();
        return settings;
    }

    public void Save()
    {
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();
    }
}
#endif