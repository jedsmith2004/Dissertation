#if UNITY_EDITOR
using System.IO;
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
        _settings.defaultExportDirectory = EditorGUILayout.TextField("Default Export Path", _settings.defaultExportDirectory);
        _settings.defaultMirrorRootAssetPath = EditorGUILayout.TextField("Mirror Asset Root", _settings.defaultMirrorRootAssetPath);
        _settings.defaultGenerationNamePrefix = EditorGUILayout.TextField("Default Name Prefix", _settings.defaultGenerationNamePrefix);
        _settings.versionCount = Mathf.Max(1, EditorGUILayout.IntField("Default Versions", _settings.versionCount));
        _settings.autoApplyOnGenerate = EditorGUILayout.ToggleLeft("Auto-apply generated clip", _settings.autoApplyOnGenerate);

        EditorGUILayout.HelpBox("Mirror Asset Root must stay under Assets/ so Unity can import and preview generated clips.", MessageType.Info);

        EditorGUILayout.Space();
        if (GUILayout.Button("Save"))
        {
            _settings.EnsureDefaults();
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
    public string seedText = "";
    public string generationName = "";
    public int versionCount = 1;
    public string serverHost = "127.0.0.1";
    public int serverPort = 50051;
    public bool autoApplyOnGenerate = true;
    public string defaultExportDirectory = "";
    public string defaultMirrorRootAssetPath = "Assets/MotionGen/Generated/Mirrored";
    public string defaultGenerationNamePrefix = "motiongen";

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
        settings.EnsureDefaults();
        AssetDatabase.CreateAsset(settings, AssetPath);
        AssetDatabase.SaveAssets();
        return settings;
    }

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = "walk forward";

        fps = Mathf.Max(1, fps);
        durationSeconds = Mathf.Max(0.1f, durationSeconds);
        versionCount = Mathf.Max(1, versionCount);

        if (string.IsNullOrWhiteSpace(defaultGenerationNamePrefix))
            defaultGenerationNamePrefix = "motiongen";

        if (string.IsNullOrWhiteSpace(defaultMirrorRootAssetPath) || !defaultMirrorRootAssetPath.Replace("\\", "/").StartsWith("Assets/"))
            defaultMirrorRootAssetPath = "Assets/MotionGen/Generated/Mirrored";

        defaultMirrorRootAssetPath = defaultMirrorRootAssetPath.Replace("\\", "/").TrimEnd('/');

        if (string.IsNullOrWhiteSpace(defaultExportDirectory))
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            defaultExportDirectory = Path.Combine(projectRoot, "MotionGenExports");
        }
    }

    public void Save()
    {
        EnsureDefaults();
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();
    }
}
#endif