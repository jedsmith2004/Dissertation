#if UNITY_EDITOR
using System;
using System.IO;
using Motion;
using UnityEditor;
using UnityEngine;

public class MotionGenSettingsWindow : EditorWindow
{
    private MotionGenEditorSettings _settings;
    private string _backendStatus = "Idle";
    private string _installerStatus = "Not run yet.";
    private bool _isBusy;
    private Vector2 _modelStatusScroll;
    private MotionGenModelStatusSnapshot _modelStatus;

    public static void ShowWindow(MotionGenEditorSettings settings)
    {
        var window = GetWindow<MotionGenSettingsWindow>(true, "MotionGen Settings");
        window._settings = settings;
        window.minSize = new Vector2(520f, 520f);
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
        DrawLocalBackendSection();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Generation Defaults", EditorStyles.boldLabel);
        _settings.model = (MotionModel)EditorGUILayout.EnumPopup("Default Model", _settings.model);
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

    private void DrawLocalBackendSection()
    {
        EditorGUILayout.LabelField("Local Backend (Windows)", EditorStyles.boldLabel);
        _settings.backendRootPath = EditorGUILayout.TextField("Backend Root", _settings.backendRootPath);
        _settings.backendManifestPath = EditorGUILayout.TextField("Manifest Path", _settings.backendManifestPath);
        _settings.backendPythonExecutable = EditorGUILayout.TextField("Python Executable", _settings.backendPythonExecutable);
        _settings.modelDownloadBaseUrl = EditorGUILayout.TextField("Model Base URL (optional)", _settings.modelDownloadBaseUrl);

        EditorGUILayout.HelpBox(
            "Model artifacts are manifest-driven. Set artifact URLs in backend_manifest.json, or provide a base URL for output-path based downloads.",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_isBusy))
        {
            if (GUILayout.Button("Ping Backend"))
                _ = PingBackendAsync();
            if (GUILayout.Button("Start Local Backend"))
                _ = StartBackendAsync();
            if (GUILayout.Button("Stop Local Backend"))
                StopBackend();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Backend Status", _backendStatus);
        if (!string.IsNullOrWhiteSpace(MotionGenLocalBackendManager.LastBackendLogLine))
            EditorGUILayout.LabelField("Backend Log", MotionGenLocalBackendManager.LastBackendLogLine);

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_isBusy))
        {
            if (GUILayout.Button("Refresh Model Status"))
                _ = RefreshModelStatusAsync();

            if (GUILayout.Button("Install T2M-GPT"))
                _ = InstallModelAsync("t2m_gpt");

            if (GUILayout.Button("Install MoMask"))
                _ = InstallModelAsync("momask");
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Installer Status", _installerStatus, EditorStyles.wordWrappedLabel);

        if (_modelStatus?.models == null || _modelStatus.models.Length == 0)
            return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Model Snapshot (backend v{_modelStatus.backend_version})", EditorStyles.boldLabel);
        _modelStatusScroll = EditorGUILayout.BeginScrollView(_modelStatusScroll, GUILayout.MinHeight(140f), GUILayout.MaxHeight(220f));
        foreach (var model in _modelStatus.models)
        {
            if (model == null)
                continue;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(model.display_name ?? model.id ?? "model", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status", model.status ?? "unknown");
            EditorGUILayout.LabelField("Install Root", model.install_root ?? "");

            if (model.missing_files != null && model.missing_files.Length > 0)
                EditorGUILayout.LabelField("Missing", string.Join(", ", model.missing_files));

            if (model.corrupt_files != null && model.corrupt_files.Length > 0)
                EditorGUILayout.LabelField("Corrupt", string.Join(", ", model.corrupt_files));

            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
    }

    private async System.Threading.Tasks.Task PingBackendAsync()
    {
        _isBusy = true;
        _backendStatus = "Pinging backend...";
        SafeRepaint();

        var result = await MotionGenLocalBackendManager.PingBackendAsync(_settings);
        _backendStatus = result.message;
        _isBusy = false;
        SafeRepaint();
    }

    private async System.Threading.Tasks.Task StartBackendAsync()
    {
        _isBusy = true;
        _backendStatus = "Starting local backend...";
        SafeRepaint();

        var result = await MotionGenLocalBackendManager.StartBackendAndWaitForHealthAsync(_settings);
        _backendStatus = result.message;
        _isBusy = false;
        SafeRepaint();
    }

    private void StopBackend()
    {
        var result = MotionGenLocalBackendManager.StopBackendProcess();
        _backendStatus = result.message;
        SafeRepaint();
    }

    private async System.Threading.Tasks.Task RefreshModelStatusAsync()
    {
        _isBusy = true;
        _installerStatus = "Querying model status...";
        SafeRepaint();

        var result = await MotionGenLocalBackendManager.QueryModelStatusAsync(_settings);
        if (result.ok)
        {
            _modelStatus = result.snapshot;
            _installerStatus = "Model status refreshed.";
        }
        else
        {
            _installerStatus = result.message;
        }

        _isBusy = false;
        SafeRepaint();
    }

    private async System.Threading.Tasks.Task InstallModelAsync(string modelId)
    {
        _isBusy = true;
        _installerStatus = $"Installing '{modelId}'...";
        SafeRepaint();

        var result = await MotionGenLocalBackendManager.InstallModelAsync(
            _settings,
            modelId,
            progress =>
            {
                _installerStatus = progress;
                SafeRepaint();
            });

        _installerStatus = result.message;
        _isBusy = false;
        SafeRepaint();

        var statusResult = await MotionGenLocalBackendManager.QueryModelStatusAsync(_settings);
        if (statusResult.ok)
            _modelStatus = statusResult.snapshot;
        SafeRepaint();
    }

    private void SafeRepaint()
    {
        EditorApplication.delayCall += Repaint;
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
    public MotionModel model = MotionModel.T2MGpt;
    public string serverHost = "127.0.0.1";
    public int serverPort = 50051;
    public bool autoApplyOnGenerate = true;
    public string defaultExportDirectory = "";
    public string defaultMirrorRootAssetPath = "Assets/MotionGen/Generated/Mirrored";
    public string defaultGenerationNamePrefix = "motiongen";
    public string backendRootPath = "";
    public string backendManifestPath = "";
    public string backendPythonExecutable = "";
    public string modelDownloadBaseUrl = "";

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

        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        if (string.IsNullOrWhiteSpace(backendRootPath))
            backendRootPath = Path.Combine(root, "motion-backend");

        if (string.IsNullOrWhiteSpace(backendManifestPath))
            backendManifestPath = Path.Combine(backendRootPath, "packaging", "backend_manifest.json");
    }

    public void Save()
    {
        EnsureDefaults();
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssets();
    }
}
#endif