#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Motion;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public class MotionGenWindow : EditorWindow
{
    private const string GeneratedRoot = "Assets/MotionGen/Generated";
    private const string GeneratedBvhAssetPath = GeneratedRoot + "/generated.bvh";
    private const string PlaybackControllerAssetPath = GeneratedRoot + "/MotionGenPlayback.controller";
    private const string PlaybackStateName = "MotionGen";

    private MotionGenEditorSettings _settings;
    private Animator _selectedAnimator;
    private bool _isGenerating;
    private string _status = "Idle";
    private string _lastGeneratedClipAssetPath;

    [MenuItem("Window/MotionGen")]
    public static void ShowWindow()
    {
        var window = GetWindow<MotionGenWindow>();
        window.titleContent = new GUIContent("MotionGen");
        window.Show();
    }

    private void OnEnable()
    {
        _settings = MotionGenEditorSettings.GetOrCreate();
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged();
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        _selectedAnimator = null;

        var selectedObject = Selection.activeGameObject;
        if (selectedObject != null)
        {
            var animator = selectedObject.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                _selectedAnimator = animator;
        }

        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();

        EditorGUILayout.Space();
        DrawSelectionInfo();

        EditorGUILayout.Space();
        DrawGenerationForm();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(_selectedAnimator == null || string.IsNullOrWhiteSpace(_lastGeneratedClipAssetPath)))
        {
            if (GUILayout.Button("Apply Last Generated Clip"))
                ApplyLastGeneratedClip();
        }
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("MotionGen", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Settings", EditorStyles.toolbarButton))
            MotionGenSettingsWindow.ShowWindow(_settings);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSelectionInfo()
    {
        if (_selectedAnimator == null)
        {
            EditorGUILayout.HelpBox("Select a humanoid Animator in the scene to enable auto-apply.", MessageType.Info);
            return;
        }

        EditorGUILayout.HelpBox($"Selected humanoid: {_selectedAnimator.name}", MessageType.None);
    }

    private void DrawGenerationForm()
    {
        EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
        _settings.prompt = EditorGUILayout.TextArea(_settings.prompt, GUILayout.MinHeight(60f));
        _settings.fps = Mathf.Max(1, EditorGUILayout.IntField("FPS", _settings.fps));
        _settings.durationSeconds = Mathf.Max(0.1f, EditorGUILayout.FloatField("Duration (s)", _settings.durationSeconds));
        _settings.seed = EditorGUILayout.IntField("Seed", _settings.seed);
        _settings.autoApplyOnGenerate = EditorGUILayout.ToggleLeft("Auto-apply generated clip", _settings.autoApplyOnGenerate);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Status", _status);

        using (new EditorGUI.DisabledScope(_isGenerating))
        {
            if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate"))
                _ = GenerateMotionAsync();
        }
    }

    private async Task GenerateMotionAsync()
    {
        if (_settings == null)
            return;

        if (string.IsNullOrWhiteSpace(_settings.prompt))
        {
            _status = "Prompt is empty.";
            Repaint();
            return;
        }

        _settings.Save();
        _isGenerating = true;
        _status = "Sending request...";
        Repaint();

        try
        {
            using var client = new MotionClient(_settings.serverHost, _settings.serverPort);
            var response = await client.GenerateAsync(
                _settings.prompt,
                _settings.fps,
                _settings.durationSeconds,
                _settings.seed);

            if (response == null || response.Data == null || response.Data.Length == 0)
                throw new InvalidOperationException("Backend returned empty data.");

            if (response.Format != MotionFormat.Bvh)
                throw new InvalidOperationException($"Unexpected response format: {response.Format}");

            EnsureFolders();
            File.WriteAllBytes(Path.GetFullPath(GeneratedBvhAssetPath), response.Data.ToByteArray());
            AssetDatabase.Refresh();

            if (!BvhToAnimConverter.TryConvertBvhAssetToAnim(GeneratedBvhAssetPath, out var generatedClip, out var clipAssetPath))
                throw new InvalidOperationException("BVH import failed.");

            _lastGeneratedClipAssetPath = clipAssetPath;

            if (_settings.autoApplyOnGenerate && _selectedAnimator != null)
                ApplyGeneratedClip(generatedClip);

            _status = "Generated generated.bvh and generated.anim";
            Debug.Log($"[MotionGen] Generate OK | meta={response.Meta}");
        }
        catch (RpcException ex)
        {
            _status = $"RPC failed: {ex.StatusCode}";
            Debug.LogError($"[MotionGen] Generate RPC failed: {ex.StatusCode} - {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _status = "Generate failed.";
            Debug.LogError($"[MotionGen] Generate failed: {ex.Message}");
        }
        finally
        {
            _isGenerating = false;
            Repaint();
        }
    }

    private void ApplyLastGeneratedClip()
    {
        if (_selectedAnimator == null)
        {
            _status = "Select a humanoid first.";
            return;
        }

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(_lastGeneratedClipAssetPath);
        if (clip == null)
        {
            _status = "Generated clip missing. Regenerate motion.";
            return;
        }

        ApplyGeneratedClip(clip);
        _status = $"Applied {clip.name}";
    }

    private void ApplyGeneratedClip(AnimationClip clip)
    {
        if (_selectedAnimator == null || clip == null)
            return;

        PrepareAnimatorForPlayback();

        var controller = GetOrCreatePlaybackController();
        if (controller == null)
            return;

        var stateMachine = controller.layers[0].stateMachine;
        var state = stateMachine.states.FirstOrDefault(entry => entry.state != null && entry.state.name == PlaybackStateName).state;
        if (state == null)
            state = stateMachine.AddState(PlaybackStateName);

        state.motion = clip;
        stateMachine.defaultState = state;

        _selectedAnimator.runtimeAnimatorController = controller;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        _selectedAnimator.Rebind();
        _selectedAnimator.Update(0f);
        _selectedAnimator.Play(PlaybackStateName, 0, 0f);
        _selectedAnimator.Update(0f);
    }

    private void PrepareAnimatorForPlayback()
    {
        if (_selectedAnimator == null)
            return;

        _selectedAnimator.enabled = true;
        _selectedAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        _selectedAnimator.applyRootMotion = true;
    }

    private static AnimatorController GetOrCreatePlaybackController()
    {
        EnsureFolders();

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(PlaybackControllerAssetPath);
        if (controller != null)
            return controller;

        return AnimatorController.CreateAnimatorControllerAtPath(PlaybackControllerAssetPath);
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/MotionGen"))
            AssetDatabase.CreateFolder("Assets", "MotionGen");

        if (!AssetDatabase.IsValidFolder(GeneratedRoot))
            AssetDatabase.CreateFolder("Assets/MotionGen", "Generated");
    }
}
#endif
