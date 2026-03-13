#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
    private const string PlaybackControllerAssetPath = GeneratedRoot + "/MotionGenPlayback.controller";
    private const string PlaybackStateName = "MotionGen";
    private const int DefaultPathKeyCount = 4;
    private const int PathSampleCount = 40;
    private const string DebugLogPath = @"C:\Users\jack\OneDrive\Desktop\Coding\Dissertation\.cursor\debug.log";
    private const string DebugRunId = "path-tab-crash-pre";

    private enum MotionGenTab
    {
        Generate,
        Library,
        PathEdit,
        Post
    }

    [Serializable]
    private class MotionReplyMeta
    {
        public string model;
        public string pipeline;
        public string prompt;
        public int seed;
        public int requested_fps;
        public int native_fps;
        public int native_frame_count;
        public int native_joint_count;
        public float duration_seconds_request;
        public int batch_index;
        public int batch_count;
        public int resolved_seed;
        public string seed_mode;
    }

    [Serializable]
    private class EditRangeDraft
    {
        public float startSeconds;
        public float endSeconds;
    }

    private MotionGenEditorSettings _settings;
    private MotionGenGenerationHistory _history;
    private Animator _selectedAnimator;
    private bool _isGenerating;
    private string _status = "Idle";
    private string _selectedSessionId;
    private string _selectedGenerationItemId;
    private Vector2 _historyScroll;
    private Texture2D _selectedPreviewTexture;
    private bool _isScenePreviewPlaying;
    private float _scenePreviewTime;
    private string _scenePreviewClipAssetPath;
    private double _lastPreviewUpdateTime;
    private MotionGenTab _activeTab;
    private string _pathEditClipAssetPath;
    private bool _showPathOverlay = true;
    private bool _lockPathEditY = true;
    private bool _pathEditNeedsPreviewRefresh;
    private int _selectedPathKeyIndex = -1;
    private List<MotionGenPathEditKey> _pathEditKeys = new List<MotionGenPathEditKey>();
    private List<Vector3> _generatedPathSamples = new List<Vector3>();
    private readonly HashSet<string> _expandedSessionIds = new HashSet<string>();
    private bool _isCapturingRootPathSamples;
    private Vector2 _postContactsScroll;
    private string _editPrompt = string.Empty;
    private int _editVersionCount = 1;
    private string _editSeedText = string.Empty;
    private readonly List<EditRangeDraft> _editRangeDrafts = new List<EditRangeDraft>();

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
        _settings.EnsureDefaults();
        _history = MotionGenGenerationHistory.GetOrCreate();
        EditorApplication.update += OnEditorUpdate;
        SceneView.duringSceneGui += OnSceneGUI;
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged();
        EnsureValidSelection();
    }

    private void OnDisable()
    {
        StopScenePreview();
        EditorApplication.update -= OnEditorUpdate;
        SceneView.duringSceneGui -= OnSceneGUI;
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

        _pathEditClipAssetPath = null;
        _generatedPathSamples.Clear();
        _pathEditNeedsPreviewRefresh = false;

        if (_selectedAnimator == null)
            StopScenePreview();

        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();

        EditorGUILayout.Space();
        DrawSelectionInfo();

        EditorGUILayout.Space();
        DrawTabs();

        EditorGUILayout.Space();
        switch (_activeTab)
        {
            case MotionGenTab.Generate:
                DrawGenerateTab();
                break;
            case MotionGenTab.Library:
                DrawLibraryTab();
                break;
            case MotionGenTab.PathEdit:
                DrawPathEditTab();
                break;
            case MotionGenTab.Post:
                DrawPostTab();
                break;
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
            EditorGUILayout.HelpBox("Select a humanoid Animator in the scene to enable preview, auto-apply, and path editing.", MessageType.Info);
            return;
        }

        EditorGUILayout.HelpBox($"Selected humanoid: {_selectedAnimator.name}", MessageType.None);
    }

    private void DrawTabs()
    {
        _activeTab = (MotionGenTab)GUILayout.Toolbar((int)_activeTab, new[] { "Generate", "Library", "Path Edit", "Post" });
    }

    private void DrawGenerateTab()
    {
        DrawGenerationForm();

        if (_history != null && _history.sessions.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical("box");
            var latestSession = _history.sessions[0];
            EditorGUILayout.LabelField("Latest Batch", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(latestSession.generationName, EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("Open Latest In Library"))
            {
                ExpandAndSelectLatest(latestSession);
                _activeTab = MotionGenTab.Library;
            }
            EditorGUILayout.EndVertical();
        }
    }

    private void DrawGenerationForm()
    {
        EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
        _settings.prompt = EditorGUILayout.TextArea(_settings.prompt, GUILayout.MinHeight(60f));
        DrawExportPathRow();
        _settings.generationName = EditorGUILayout.TextField("Generation Name", _settings.generationName);
        _settings.model = (MotionModel)EditorGUILayout.EnumPopup("Model", _settings.model);
        _settings.fps = Mathf.Max(1, EditorGUILayout.IntField("FPS", _settings.fps));
        _settings.durationSeconds = Mathf.Max(0.1f, EditorGUILayout.FloatField("Duration (s)", _settings.durationSeconds));
        _settings.versionCount = Mathf.Max(1, EditorGUILayout.IntField("Versions", _settings.versionCount));
        _settings.seedText = EditorGUILayout.TextField("Seed (blank = random)", _settings.seedText);
        _settings.autoApplyOnGenerate = EditorGUILayout.ToggleLeft("Auto-apply generated clip", _settings.autoApplyOnGenerate);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Mirror Root", NormalizeMirrorRootAssetPath(_settings.defaultMirrorRootAssetPath));
        EditorGUILayout.LabelField("Resolved Name", ResolveGenerationName());
        EditorGUILayout.LabelField("Status", _status);

        using (new EditorGUI.DisabledScope(_isGenerating))
        {
            if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate Versions"))
                _ = GenerateMotionBatchAsync();
        }
    }

    private void DrawExportPathRow()
    {
        EditorGUILayout.BeginHorizontal();
        _settings.defaultExportDirectory = EditorGUILayout.TextField("Export Path", _settings.defaultExportDirectory);
        if (GUILayout.Button("Browse", GUILayout.Width(70f)))
        {
            var selectedPath = EditorUtility.OpenFolderPanel("Choose Motion Export Folder", ResolveExportDirectory(), string.Empty);
            if (!string.IsNullOrWhiteSpace(selectedPath))
                _settings.defaultExportDirectory = selectedPath;
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawLibraryTab()
    {
        DrawHistoryPanel();
    }

    private void DrawHistoryPanel()
    {
        EditorGUILayout.LabelField("Library", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh"))
        {
            _history = MotionGenGenerationHistory.GetOrCreate();
            EnsureValidSelection();
            _status = "Reloaded generation history.";
        }

        if (GUILayout.Button("Prune Missing"))
        {
            _history.PruneMissingEntries();
            EnsureValidSelection();
            _status = "Removed missing generation entries.";
        }
        EditorGUILayout.EndHorizontal();

        DrawSelectedItemPreview(GetSelectedHistoryItem());

        _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll, GUILayout.MinHeight(220f));
        if (_history == null || _history.sessions.Count == 0)
        {
            EditorGUILayout.HelpBox("No past generations yet.", MessageType.Info);
        }
        else
        {
            foreach (var session in _history.sessions)
                DrawSession(session);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawSelectedItemPreview(MotionGenGenerationItem item)
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Selected Preview", EditorStyles.boldLabel);

        if (item == null)
        {
            SyncScenePreviewSelection(null);
            EditorGUILayout.HelpBox("Select a generated version below to preview, inspect, or edit its path.", MessageType.Info);
            EditorGUILayout.EndVertical();
            return;
        }

        var clip = LoadPreferredClip(item);
        var meta = ParseMeta(item.metaJson);
        SyncScenePreviewSelection(item);
        EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false);
        EditorGUILayout.LabelField("Name", item.displayName);
        EditorGUILayout.LabelField("Model", meta?.model ?? "Unknown");
        EditorGUILayout.LabelField("Resolved Seed", item.resolvedSeed.ToString());
        EditorGUILayout.LabelField("External BVH", item.externalBvhPath);
        EditorGUILayout.LabelField("Active Variant", HasFreshProcessedClip(item) ? "Processed" : "Source");
        if (item.isEditResult)
        {
            EditorGUILayout.LabelField("Type", "Edited Variant");
            if (!string.IsNullOrWhiteSpace(item.sourceClipAssetPath))
                EditorGUILayout.LabelField("Source Clip", item.sourceClipAssetPath);
            if (!string.IsNullOrWhiteSpace(item.editPrompt))
                EditorGUILayout.LabelField("Edit Prompt", item.editPrompt);
            if (item.editRanges != null && item.editRanges.Count > 0)
            {
                var ranges = string.Join(", ", item.editRanges.Select(range => $"[{range.startSeconds:0.00}s-{range.endSeconds:0.00}s]"));
                EditorGUILayout.LabelField("Edit Windows", ranges);
            }
        }

        _selectedPreviewTexture = clip != null
            ? AssetPreview.GetAssetPreview(clip) ?? AssetPreview.GetMiniThumbnail(clip)
            : null;

        if (_selectedPreviewTexture != null)
            GUILayout.Label(_selectedPreviewTexture, GUILayout.Width(96f), GUILayout.Height(96f));

        DrawScenePreviewControls(clip);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(clip == null || _selectedAnimator == null))
        {
            if (GUILayout.Button("Apply Selected"))
            {
                StopScenePreview(resetTime: false);
                ApplyGeneratedClip(clip);
                _status = $"Applied {clip.name}";
            }
        }

        if (GUILayout.Button("Inspect Clip") && clip != null)
            InspectClip(clip);

        if (GUILayout.Button("Open Animation Window") && clip != null)
            OpenAnimationWindow(clip);

        if (GUILayout.Button("Edit Path") && clip != null)
        {
            LoadPathEditSelection(item);
            _activeTab = MotionGenTab.PathEdit;
        }

        if (GUILayout.Button("Reveal Export"))
            RevealExport(item.externalBvhPath);
        EditorGUILayout.EndHorizontal();

        if (meta != null)
            EditorGUILayout.LabelField("Backend Meta", $"fps={meta.requested_fps}, nativeFps={meta.native_fps}, nativeFrames={meta.native_frame_count}, seedMode={meta.seed_mode}");

        DrawEditSection(item, clip);

        EditorGUILayout.EndVertical();
    }

    private void DrawScenePreviewControls(AnimationClip clip)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);

        if (_selectedAnimator == null)
        {
            EditorGUILayout.HelpBox("Select a humanoid Animator in the scene to scrub and preview this clip here.", MessageType.Info);
            return;
        }

        if (clip == null)
        {
            EditorGUILayout.HelpBox("The selected history item does not have a valid imported clip to preview.", MessageType.Warning);
            return;
        }

        var clipLength = GetClipLength(clip);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(_isScenePreviewPlaying ? "Pause" : "Play", GUILayout.Width(60f)))
            {
                if (_isScenePreviewPlaying)
                    PauseScenePreview();
                else
                    PlayScenePreview(clip);
            }

            if (GUILayout.Button("Stop", GUILayout.Width(60f)))
                StopScenePreview();

            EditorGUILayout.LabelField($"Time {_scenePreviewTime:0.00}s / {clipLength:0.00}s");
        }

        EditorGUI.BeginChangeCheck();
        var nextTime = EditorGUILayout.Slider("Preview Time", _scenePreviewTime, 0f, clipLength);
        if (EditorGUI.EndChangeCheck())
        {
            _scenePreviewTime = nextTime;
            RequestPreviewRefresh();
        }
    }

    private void DrawSession(MotionGenGenerationSession session)
    {
        if (session == null)
            return;

        EditorGUILayout.BeginVertical("box");
        var isExpanded = _expandedSessionIds.Contains(session.id);
        var nextExpanded = EditorGUILayout.Foldout(
            isExpanded,
            $"{session.generationName}  |  {TryFormatTimestamp(session.createdAtUtc)}  |  {session.versionCount} versions",
            true);

        if (nextExpanded)
            _expandedSessionIds.Add(session.id);
        else
            _expandedSessionIds.Remove(session.id);

        if (nextExpanded)
        {
            EditorGUILayout.LabelField("Prompt", session.prompt);
            var sessionMeta = ParseMeta(session.items.FirstOrDefault()?.metaJson);
            if (sessionMeta != null && !string.IsNullOrWhiteSpace(sessionMeta.model))
                EditorGUILayout.LabelField("Model", sessionMeta.model);
            EditorGUILayout.LabelField("Export Path", session.exportDirectory);
            EditorGUILayout.LabelField("Seed Mode", session.usedRandomSeed ? "Random per version" : $"Base {session.baseSeed} + increment");

            foreach (var item in session.items)
                DrawSessionItem(session, item);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawSessionItem(MotionGenGenerationSession session, MotionGenGenerationItem item)
    {
        if (item == null)
            return;

        var isSelected = session.id == _selectedSessionId && item.id == _selectedGenerationItemId;
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Toggle(isSelected, item.displayName, "Button"))
        {
            _selectedSessionId = session.id;
            _selectedGenerationItemId = item.id;
        }

        var clip = LoadPreferredClip(item);
        using (new EditorGUI.DisabledScope(clip == null || _selectedAnimator == null))
        {
            if (GUILayout.Button("Apply", GUILayout.Width(52f)))
            {
                ApplyGeneratedClip(clip);
                _selectedSessionId = session.id;
                _selectedGenerationItemId = item.id;
                _status = $"Applied {clip.name}";
            }
        }

        if (GUILayout.Button("Path", GUILayout.Width(46f)) && clip != null)
        {
            _selectedSessionId = session.id;
            _selectedGenerationItemId = item.id;
            LoadPathEditSelection(item);
            _activeTab = MotionGenTab.PathEdit;
        }

        if (GUILayout.Button("Reveal", GUILayout.Width(60f)))
            RevealExport(item.externalBvhPath);

        EditorGUILayout.EndHorizontal();
    }

    private void DrawEditSection(MotionGenGenerationItem sourceItem, AnimationClip sourceClip)
    {
        if (sourceItem == null || sourceClip == null)
            return;

        EnsureDefaultEditRanges(sourceClip);

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Edit Selected Clip (MoMask)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Non-destructive edit: generate new clip variants for selected time windows without overwriting the source clip.",
            MessageType.Info);

        _editPrompt = EditorGUILayout.TextField("Edit Prompt", _editPrompt);
        _editVersionCount = Mathf.Max(1, EditorGUILayout.IntField("Edited Versions", _editVersionCount));
        _editSeedText = EditorGUILayout.TextField("Edit Seed (blank = random)", _editSeedText);

        EditorGUILayout.LabelField("Time Windows (seconds)", EditorStyles.boldLabel);
        for (var index = 0; index < _editRangeDrafts.Count; index++)
            DrawEditRangeRow(sourceClip, index);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Add Window"))
            _editRangeDrafts.Add(new EditRangeDraft { startSeconds = 0f, endSeconds = Mathf.Min(0.5f, GetClipLength(sourceClip)) });
        if (GUILayout.Button("Full Clip"))
        {
            _editRangeDrafts.Clear();
            _editRangeDrafts.Add(new EditRangeDraft { startSeconds = 0f, endSeconds = GetClipLength(sourceClip) });
        }
        EditorGUILayout.EndHorizontal();

        using (new EditorGUI.DisabledScope(_isGenerating || _selectedAnimator == null || string.IsNullOrWhiteSpace(_editPrompt)))
        {
            if (GUILayout.Button(_isGenerating ? "Editing..." : "Generate Edited Variants"))
                _ = GenerateEditedBatchAsync(sourceItem, sourceClip);
        }

        if (_selectedAnimator == null)
            EditorGUILayout.HelpBox("Select a humanoid Animator in the scene to sample source joints for editing.", MessageType.Warning);
        else if (string.IsNullOrWhiteSpace(_editPrompt))
            EditorGUILayout.HelpBox("Enter an edit prompt to generate variants.", MessageType.None);

        EditorGUILayout.EndVertical();
    }

    private void DrawEditRangeRow(AnimationClip sourceClip, int index)
    {
        var entry = _editRangeDrafts[index];
        var maxTime = GetClipLength(sourceClip);

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        var nextStart = EditorGUILayout.FloatField($"Start {index + 1}", entry.startSeconds);
        var nextEnd = EditorGUILayout.FloatField("End", entry.endSeconds);
        if (GUILayout.Button("X", GUILayout.Width(24f)))
        {
            _editRangeDrafts.RemoveAt(index);
            EditorGUILayout.EndHorizontal();
            return;
        }

        if (EditorGUI.EndChangeCheck())
        {
            entry.startSeconds = Mathf.Clamp(nextStart, 0f, maxTime);
            entry.endSeconds = Mathf.Clamp(nextEnd, entry.startSeconds, maxTime);
        }
        EditorGUILayout.EndHorizontal();
    }

    private async Task GenerateEditedBatchAsync(MotionGenGenerationItem sourceItem, AnimationClip sourceClip)
    {
        if (sourceItem == null || sourceClip == null)
            return;

        if (_selectedAnimator == null || !_selectedAnimator.isHuman)
        {
            _status = "Editing requires a selected humanoid Animator in the scene.";
            Repaint();
            return;
        }

        if (string.IsNullOrWhiteSpace(_editPrompt))
        {
            _status = "Edit prompt is empty.";
            Repaint();
            return;
        }

        if (!TryResolveEditSeedSettings(out var useRandomSeed, out var baseSeed))
        {
            _status = "Edit seed must be blank or a valid integer.";
            Repaint();
            return;
        }

        var editRanges = BuildEditRangeRequests(sourceClip);
        if (editRanges.Count == 0)
        {
            _status = "Add at least one non-empty edit window.";
            Repaint();
            return;
        }

        if (!TryBuildSourceMotionJointSequence(sourceClip, _settings.fps, out var sourceMotion, out var sourceError))
        {
            _status = sourceError ?? "Failed to build source motion for editing.";
            Repaint();
            return;
        }

        var sourceSession = _history?.sessions?.FirstOrDefault(session => session != null && session.id == _selectedSessionId);
        var generationName = $"{sourceItem.displayName}_edit";
        var exportDirectory = ResolveExportDirectory();
        var mirrorRootAssetPath = NormalizeMirrorRootAssetPath(_settings.defaultMirrorRootAssetPath);

        _isGenerating = true;
        _status = "Sending edit batch request...";
        Repaint();

        try
        {
            using var client = new MotionClient(_settings.serverHost, _settings.serverPort);
            var reply = await client.EditBatchAsync(
                _editPrompt,
                _settings.fps,
                _editVersionCount,
                useRandomSeed,
                baseSeed,
                MotionModel.Momask,
                sourceMotion,
                editRanges);

            if (reply == null || reply.Items == null || reply.Items.Count == 0)
                throw new InvalidOperationException("Backend returned no edited motions.");

            var session = SaveEditedBatch(
                generationName,
                exportDirectory,
                mirrorRootAssetPath,
                reply,
                useRandomSeed,
                baseSeed,
                sourceSession,
                sourceItem,
                _editPrompt,
                editRanges);

            _history.AddSession(session);
            ExpandAndSelectLatest(session);

            var autoApplyClip = LoadPreferredClip(session.items.FirstOrDefault());
            if (_settings.autoApplyOnGenerate && _selectedAnimator != null && autoApplyClip != null)
                ApplyGeneratedClip(autoApplyClip);

            _activeTab = MotionGenTab.Library;
            _status = $"Generated {session.items.Count} edited motion version(s).";
        }
        catch (RpcException ex)
        {
            _status = $"Edit RPC failed: {ex.StatusCode}";
            Debug.LogError($"[MotionGen] Edit batch RPC failed: {ex.StatusCode} - {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _status = "Edit failed.";
            Debug.LogError($"[MotionGen] Edit batch failed: {ex}");
        }
        finally
        {
            _isGenerating = false;
            Repaint();
        }
    }

    private bool TryResolveEditSeedSettings(out bool useRandomSeed, out int baseSeed)
    {
        var seedText = (_editSeedText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(seedText))
        {
            useRandomSeed = true;
            baseSeed = 0;
            return true;
        }

        useRandomSeed = false;
        return int.TryParse(seedText, out baseSeed);
    }

    private List<EditRange> BuildEditRangeRequests(AnimationClip sourceClip)
    {
        var maxTime = GetClipLength(sourceClip);
        var result = new List<EditRange>();

        foreach (var draft in _editRangeDrafts)
        {
            if (draft == null)
                continue;

            var start = Mathf.Clamp(draft.startSeconds, 0f, maxTime);
            var end = Mathf.Clamp(draft.endSeconds, start, maxTime);
            if (end <= start)
                continue;

            result.Add(new EditRange
            {
                StartSeconds = start,
                EndSeconds = end
            });
        }

        return result;
    }

    private void EnsureDefaultEditRanges(AnimationClip sourceClip)
    {
        if (_editRangeDrafts.Count > 0)
            return;

        _editRangeDrafts.Add(new EditRangeDraft
        {
            startSeconds = 0f,
            endSeconds = GetClipLength(sourceClip)
        });
    }

    private bool TryBuildSourceMotionJointSequence(AnimationClip sourceClip, int sampleFps, out MotionJointSequence sequence, out string error)
    {
        sequence = null;
        error = null;

        if (_selectedAnimator == null || !_selectedAnimator.isHuman)
        {
            error = "A selected humanoid Animator is required to sample source joints.";
            return false;
        }

        var joints = ResolveEditJointTransforms(_selectedAnimator, out error);
        if (joints == null)
            return false;

        var fps = Mathf.Max(1, sampleFps);
        var frameCount = Mathf.Max(4, Mathf.CeilToInt(GetClipLength(sourceClip) * fps) + 1);
        var positions = new List<float>(frameCount * joints.Length * 3);

        var go = _selectedAnimator.gameObject;
        AnimationMode.StartAnimationMode();
        try
        {
            for (var frame = 0; frame < frameCount; frame++)
            {
                var time = Mathf.Min(GetClipLength(sourceClip), frame / (float)fps);
                AnimationMode.SampleAnimationClip(go, sourceClip, time);

                for (var jointIndex = 0; jointIndex < joints.Length; jointIndex++)
                {
                    var transform = joints[jointIndex];
                    if (transform == null)
                    {
                        error = "Missing humanoid transform while sampling source motion.";
                        return false;
                    }

                    var position = transform.position;
                    positions.Add(position.x);
                    positions.Add(position.y);
                    positions.Add(position.z);
                }
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            _selectedAnimator.Rebind();
            _selectedAnimator.Update(0f);
        }

        sequence = new MotionJointSequence
        {
            Fps = fps,
            FrameCount = frameCount,
            JointCount = joints.Length
        };
        sequence.JointPositions.Add(positions);
        return true;
    }

    private static Transform[] ResolveEditJointTransforms(Animator animator, out string error)
    {
        error = null;

        var required = new[]
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.Spine,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.Chest,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightToes,
            HumanBodyBones.Neck,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand
        };

        var transforms = new Transform[required.Length];
        for (var index = 0; index < required.Length; index++)
        {
            var bone = required[index];
            var transform = animator.GetBoneTransform(bone);

            if (transform == null && bone == HumanBodyBones.UpperChest)
                transform = animator.GetBoneTransform(HumanBodyBones.Chest);

            if (transform == null)
            {
                error = $"Selected humanoid is missing bone mapping for {bone}.";
                return null;
            }

            transforms[index] = transform;
        }

        return transforms;
    }

    private MotionGenGenerationSession SaveEditedBatch(
        string generationName,
        string exportDirectory,
        string mirrorRootAssetPath,
        BatchEditReply reply,
        bool useRandomSeed,
        int baseSeed,
        MotionGenGenerationSession sourceSession,
        MotionGenGenerationItem sourceItem,
        string editPrompt,
        List<EditRange> editRanges)
    {
        Directory.CreateDirectory(exportDirectory);

        var sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}".Substring(0, 24);
        var mirrorSessionAssetPath = EnsureAssetFolder($"{mirrorRootAssetPath}/{SanitizeFileName(generationName)}_{sessionId}");
        var session = new MotionGenGenerationSession
        {
            id = sessionId,
            generationName = generationName,
            prompt = editPrompt,
            exportDirectory = exportDirectory,
            mirrorDirectoryAssetPath = mirrorSessionAssetPath,
            createdAtUtc = DateTime.UtcNow.ToString("O"),
            versionCount = reply.Items.Count,
            usedRandomSeed = useRandomSeed,
            baseSeed = baseSeed
        };

        var pendingWrites = new List<(GenerateReply replyItem, string baseName, string externalPath, string mirrorBvhAssetPath)>();
        for (var index = 0; index < reply.Items.Count; index++)
        {
            var itemReply = reply.Items[index];
            if (itemReply == null || itemReply.Data == null || itemReply.Data.Length == 0)
                continue;

            if (itemReply.Format != MotionFormat.Bvh)
                throw new InvalidOperationException($"Unexpected response format: {itemReply.Format}");

            var baseName = BuildVersionBaseName(generationName, index);
            var externalPath = GetUniqueExternalFilePath(Path.Combine(exportDirectory, $"{baseName}.bvh"));
            var mirrorBvhAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{mirrorSessionAssetPath}/{baseName}.bvh");

            File.WriteAllBytes(externalPath, itemReply.Data.ToByteArray());
            File.WriteAllBytes(Path.GetFullPath(mirrorBvhAssetPath), itemReply.Data.ToByteArray());
            pendingWrites.Add((itemReply, baseName, externalPath, mirrorBvhAssetPath));
        }

        AssetDatabase.Refresh();

        foreach (var pendingWrite in pendingWrites)
        {
            if (!BvhToAnimConverter.TryConvertBvhAssetToAnim(pendingWrite.mirrorBvhAssetPath, out var clip, out var clipAssetPath))
                throw new InvalidOperationException($"BVH import failed for {pendingWrite.mirrorBvhAssetPath}.");

            var meta = ParseMeta(pendingWrite.replyItem.Meta);
            session.items.Add(new MotionGenGenerationItem
            {
                id = Guid.NewGuid().ToString("N"),
                displayName = pendingWrite.baseName,
                clipName = clip != null ? clip.name : pendingWrite.baseName,
                externalBvhPath = pendingWrite.externalPath,
                mirroredBvhAssetPath = pendingWrite.mirrorBvhAssetPath,
                clipAssetPath = clipAssetPath,
                backendFilename = pendingWrite.replyItem.Filename,
                metaJson = pendingWrite.replyItem.Meta,
                resolvedSeed = meta != null && meta.resolved_seed != 0 ? meta.resolved_seed : meta != null ? meta.seed : 0,
                isEditResult = true,
                sourceSessionId = sourceSession?.id,
                sourceItemId = sourceItem?.id,
                sourceClipAssetPath = sourceItem?.clipAssetPath,
                editPrompt = editPrompt,
                editRanges = editRanges
                    .Select(range => new MotionGenEditRangeRecord
                    {
                        startSeconds = range.StartSeconds,
                        endSeconds = range.EndSeconds
                    })
                    .ToList()
            });
        }

        return session;
    }

    private void DrawPathEditTab()
    {
        var selectedItem = GetSelectedHistoryItem();
        var clip = LoadSourceClip(selectedItem);
        SyncScenePreviewAssetPath(selectedItem?.clipAssetPath);

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:DrawPathEditTab:entry",
            "Path Edit tab entered",
            "H1",
            $"{{\"hasSelectedItem\":{ToJsonBool(selectedItem != null)},\"hasClip\":{ToJsonBool(clip != null)},\"selectedItemId\":\"{EscapeJson(selectedItem?.id)}\",\"pathEditClipAssetPath\":\"{EscapeJson(_pathEditClipAssetPath)}\"}}");
        #endregion

        EditorGUILayout.LabelField("Root Path Editor", EditorStyles.boldLabel);
        if (selectedItem == null || clip == null)
        {
            EditorGUILayout.HelpBox("Select a generated version in the Library tab, then choose Edit Path.", MessageType.Info);
            return;
        }

        LoadPathEditSelection(selectedItem);

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Selected Clip", clip.name);
        EditorGUILayout.LabelField("Generation", selectedItem.displayName);
        EditorGUILayout.LabelField("Status", _showPathOverlay ? "Scene overlay enabled" : "Scene overlay hidden");
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Overlay Controls", EditorStyles.boldLabel);
        _showPathOverlay = EditorGUILayout.ToggleLeft("Show root path overlay in Scene view", _showPathOverlay);
        EditorGUI.BeginChangeCheck();
        _lockPathEditY = EditorGUILayout.ToggleLeft("Lock Y while moving path keys", _lockPathEditY);
        if (EditorGUI.EndChangeCheck())
            PersistPathEditState(selectedItem);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh Generated Path"))
            RefreshPathEditSamples(clip);

        if (GUILayout.Button("Auto Generate Keys"))
        {
            AutoGeneratePathKeys(clip);
            PersistPathEditState(selectedItem);
            SceneView.RepaintAll();
            Repaint();
        }

        if (GUILayout.Button("Reset Path"))
        {
            AutoGeneratePathKeys(clip);
            PersistPathEditState(selectedItem);
            SceneView.RepaintAll();
            Repaint();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();

        DrawScenePreviewControls(clip);

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Path Keys", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Add Key At Current Time"))
            AddPathKeyAtCurrentTime(clip);

        using (new EditorGUI.DisabledScope(_selectedPathKeyIndex < 0 || _selectedPathKeyIndex >= _pathEditKeys.Count))
        {
            if (GUILayout.Button("Set Key To Current Root"))
                SetSelectedPathKeyToCurrentRoot();

            if (GUILayout.Button("Delete Selected Key"))
                DeleteSelectedPathKey();
        }
        EditorGUILayout.EndHorizontal();

        if (_pathEditKeys.Count == 0)
        {
            EditorGUILayout.HelpBox("No path keys yet. Auto generate keys or add one at the current preview time.", MessageType.Info);
        }
        else
        {
            for (var index = 0; index < _pathEditKeys.Count; index++)
                DrawPathKeyRow(index);
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(_pathEditKeys.Count == 0))
        {
            if (GUILayout.Button("Bake Corrected Clip"))
            {
                BakePathEditsIntoClip(clip);
                _status = $"Baked corrected root path into {clip.name}.";
                RequestPreviewRefresh();
            }
        }

        if (GUILayout.Button("Open In Animation Window"))
            OpenAnimationWindow(clip);

        EditorGUILayout.HelpBox(
            "Path keys are edited in the scene overlay first. Use Bake Corrected Clip to write the corrected root path into the animation curves for playback.",
            MessageType.Info);
        EditorGUILayout.EndVertical();
    }

    private void DrawPathKeyRow(int index)
    {
        var key = _pathEditKeys[index];
        var isSelected = index == _selectedPathKeyIndex;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Toggle(isSelected, $"Key {index + 1}", "Button", GUILayout.Width(60f)))
            _selectedPathKeyIndex = index;

        EditorGUILayout.LabelField($"{key.time:0.00}s", GUILayout.Width(52f));
        EditorGUILayout.LabelField($"P ({key.position.x:0.00}, {key.position.y:0.00}, {key.position.z:0.00})");

        if (GUILayout.Button("Focus", GUILayout.Width(55f)))
        {
            _selectedPathKeyIndex = index;
            SceneView.lastActiveSceneView?.LookAt(key.position);
        }
        EditorGUILayout.EndHorizontal();

        if (!isSelected)
            return;

        EditorGUI.BeginChangeCheck();
        var nextTime = EditorGUILayout.FloatField("Time", key.time);
        var nextPosition = EditorGUILayout.Vector3Field("Position", key.position);
        if (EditorGUI.EndChangeCheck())
        {
            key.time = Mathf.Clamp(nextTime, 0f, GetClipLength(LoadSourceClip(GetSelectedHistoryItem())));
            if (_lockPathEditY)
                nextPosition.y = key.position.y;

            key.position = nextPosition;
            SortPathKeys();
            _selectedPathKeyIndex = FindClosestPathKeyIndex(key.time);
            PersistPathEditState(GetSelectedHistoryItem());
            SceneView.RepaintAll();
            Repaint();
        }
    }

    private void DrawPostTab()
    {
        var selectedItem = GetSelectedHistoryItem();
        var sourceClip = LoadSourceClip(selectedItem);
        SyncScenePreviewSelection(selectedItem);

        EditorGUILayout.LabelField("Post-Processing", EditorStyles.boldLabel);
        if (selectedItem == null || sourceClip == null)
        {
            EditorGUILayout.HelpBox("Select a generated version in the Library tab to review and apply post-processing.", MessageType.Info);
            return;
        }

        EnsurePostProcessState(selectedItem);
        var referenceClip = LoadReferenceClip(selectedItem);
        var processedClip = LoadProcessedClip(selectedItem);
        var hasFreshProcessedClip = HasFreshProcessedClip(selectedItem);

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Clip Variants", EditorStyles.boldLabel);
        EditorGUILayout.ObjectField("Source Clip", sourceClip, typeof(AnimationClip), false);
        EditorGUILayout.ObjectField("Reference Clip", referenceClip, typeof(AnimationClip), false);
        EditorGUILayout.ObjectField("Processed Clip", processedClip, typeof(AnimationClip), false);
        EditorGUILayout.LabelField("Active Preview", hasFreshProcessedClip ? "Processed clip" : "Source clip");
        if (selectedItem.postProcessingEnabled && !hasFreshProcessedClip)
            EditorGUILayout.HelpBox("Post-processing is enabled, but the processed clip is missing or out of date. Apply it again to refresh.", MessageType.Warning);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Inspect Source"))
            InspectClip(sourceClip);
        using (new EditorGUI.DisabledScope(referenceClip == null))
        {
            if (GUILayout.Button("Inspect Reference"))
                InspectClip(referenceClip);
        }
        using (new EditorGUI.DisabledScope(processedClip == null))
        {
            if (GUILayout.Button("Inspect Processed"))
                InspectClip(processedClip);
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical("box");
        EditorGUI.BeginChangeCheck();
        selectedItem.postProcessingEnabled = EditorGUILayout.ToggleLeft("Enable Post-Processing", selectedItem.postProcessingEnabled);
        if (EditorGUI.EndChangeCheck())
            PersistPostProcessState(selectedItem);

        DrawPostProcessSettings(selectedItem);
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("Contact Review", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Auto-detect candidate support windows, review them here, then apply. You can enable feet, hands, or both for clips such as handstands.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(true))
        {
            if (GUILayout.Button("Auto Detect / Refresh Contacts"))
                DetectPostProcessContacts(selectedItem, sourceClip);
        }

        EditorGUILayout.HelpBox("Contact review is temporarily disabled while contact locking is being reworked.", MessageType.None);

        if (_selectedAnimator == null)
            EditorGUILayout.HelpBox("Select a humanoid animator in the scene to detect contacts and apply post-processing.", MessageType.Warning);

        if (selectedItem.reviewedContactWindows == null || selectedItem.reviewedContactWindows.Count == 0)
        {
            EditorGUILayout.HelpBox("No reviewed contact windows yet.", MessageType.None);
        }
        else
        {
            _postContactsScroll = EditorGUILayout.BeginScrollView(_postContactsScroll, GUILayout.MinHeight(160f), GUILayout.MaxHeight(260f));
            for (var index = 0; index < selectedItem.reviewedContactWindows.Count; index++)
                DrawContactWindowRow(selectedItem, selectedItem.reviewedContactWindows[index], index);
            EditorGUILayout.EndScrollView();
        }

        using (new EditorGUI.DisabledScope(true))
        {
            if (GUILayout.Button("Clear Contacts"))
            {
                selectedItem.reviewedContactWindows.Clear();
                PersistPostProcessState(selectedItem);
            }
        }
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical("box");
        DrawScenePreviewControls(hasFreshProcessedClip ? processedClip : sourceClip);

        using (new EditorGUI.DisabledScope(!selectedItem.postProcessingEnabled || _selectedAnimator == null))
        {
            if (GUILayout.Button("Apply Post-Processing"))
                ApplyPostProcessingToItem(selectedItem, sourceClip);
        }
        EditorGUILayout.EndVertical();
    }

    private void DrawPostProcessSettings(MotionGenGenerationItem item)
    {
        var settings = item.postProcessSettings ?? new MotionGenPostProcessSettings();
        settings.EnsureDefaults();
        item.postProcessSettings = settings;

        EditorGUILayout.LabelField("Passes", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        settings.enableRootSmoothing = EditorGUILayout.ToggleLeft("Root Smoothing", settings.enableRootSmoothing);
        using (new EditorGUI.DisabledScope(!settings.enableRootSmoothing))
        {
            settings.rootSmoothingWindow = EditorGUILayout.IntSlider("Root Window", settings.rootSmoothingWindow, 1, 8);
            settings.rootSmoothingBlend = EditorGUILayout.Slider("Root Blend", settings.rootSmoothingBlend, 0f, 1f);
        }

        settings.enableMotionSmoothing = EditorGUILayout.ToggleLeft("Motion Smoothing", settings.enableMotionSmoothing);
        using (new EditorGUI.DisabledScope(!settings.enableMotionSmoothing))
        {
            settings.motionSmoothingWindow = EditorGUILayout.IntSlider("Motion Window", settings.motionSmoothingWindow, 1, 8);
            settings.motionSmoothingBlend = EditorGUILayout.Slider("Motion Blend", settings.motionSmoothingBlend, 0f, 1f);
        }

        settings.enableContactLocking = false;
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ToggleLeft("Contact Locking (Work In Progress)", false);
            EditorGUILayout.ToggleLeft("Feet Eligible", settings.contactLockFeet);
            EditorGUILayout.ToggleLeft("Hands Eligible", settings.contactLockHands);
            settings.contactVelocityThreshold = EditorGUILayout.Slider("Velocity Threshold", settings.contactVelocityThreshold, 0.005f, 0.15f);
            settings.contactHeightThreshold = EditorGUILayout.Slider("Height Threshold", settings.contactHeightThreshold, 0.01f, 0.3f);
            settings.contactMinDuration = EditorGUILayout.Slider("Min Contact Duration", settings.contactMinDuration, 0.04f, 0.6f);
            settings.ikIterations = EditorGUILayout.IntSlider("IK Iterations", settings.ikIterations, 1, 12);
        }
        EditorGUILayout.HelpBox("Contact locking is temporarily disabled until the support-driven trajectory workflow is stable.", MessageType.None);

        if (EditorGUI.EndChangeCheck())
        {
            settings.EnsureDefaults();
            PersistPostProcessState(item);
        }
    }

    private void DrawContactWindowRow(MotionGenGenerationItem item, MotionGenContactWindow window, int index)
    {
        if (window == null)
            return;

        EditorGUILayout.BeginVertical("box");
        EditorGUI.BeginChangeCheck();
        window.enabled = EditorGUILayout.ToggleLeft($"{window.limb} ({window.startTime:0.00}s - {window.endTime:0.00}s)", window.enabled);
        window.limb = (MotionGenContactLimb)EditorGUILayout.EnumPopup("Limb", window.limb);
        window.startTime = Mathf.Max(0f, EditorGUILayout.FloatField("Start", window.startTime));
        window.endTime = Mathf.Max(window.startTime, EditorGUILayout.FloatField("End", window.endTime));
        window.anchorPosition = EditorGUILayout.Vector3Field("Anchor", window.anchorPosition);
        if (EditorGUI.EndChangeCheck())
            PersistPostProcessState(item);

        if (GUILayout.Button($"Remove Contact {index + 1}"))
        {
            item.reviewedContactWindows.RemoveAt(index);
            PersistPostProcessState(item);
        }
        EditorGUILayout.EndVertical();
    }

    private async Task GenerateMotionBatchAsync()
    {
        if (_settings == null)
            return;

        if (string.IsNullOrWhiteSpace(_settings.prompt))
        {
            _status = "Prompt is empty.";
            Repaint();
            return;
        }

        if (!TryResolveSeedSettings(out var useRandomSeed, out var baseSeed))
        {
            _status = "Seed must be blank or a valid integer.";
            Repaint();
            return;
        }

        var generationName = ResolveGenerationName();
        var exportDirectory = ResolveExportDirectory();
        var mirrorRootAssetPath = NormalizeMirrorRootAssetPath(_settings.defaultMirrorRootAssetPath);

        _settings.EnsureDefaults();
        _settings.Save();
        _isGenerating = true;
        _status = "Sending batch request...";
        Repaint();

        try
        {
            using var client = new MotionClient(_settings.serverHost, _settings.serverPort);
            var reply = await client.GenerateBatchAsync(
                _settings.prompt,
                _settings.fps,
                _settings.durationSeconds,
                _settings.versionCount,
                useRandomSeed,
                baseSeed,
                _settings.model);

            if (reply == null || reply.Items == null || reply.Items.Count == 0)
                throw new InvalidOperationException("Backend returned no generated motions.");

            var session = SaveGenerationBatch(generationName, exportDirectory, mirrorRootAssetPath, reply, useRandomSeed, baseSeed);
            _history.AddSession(session);
            ExpandAndSelectLatest(session);

            var autoApplyClip = LoadPreferredClip(session.items.FirstOrDefault());
            if (_settings.autoApplyOnGenerate && _selectedAnimator != null && autoApplyClip != null)
                ApplyGeneratedClip(autoApplyClip);

            _activeTab = MotionGenTab.Library;
            _status = $"Generated {session.items.Count} {_settings.model} motion version(s).";
            Debug.Log($"[MotionGen] Batch generate OK | model={_settings.model} | count={session.items.Count}");
        }
        catch (RpcException ex)
        {
            _status = $"RPC failed: {ex.StatusCode}";
            Debug.LogError($"[MotionGen] Batch generate RPC failed: {ex.StatusCode} - {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _status = "Generate failed.";
            Debug.LogError($"[MotionGen] Batch generate failed: {ex}");
        }
        finally
        {
            _isGenerating = false;
            Repaint();
        }
    }

    private MotionGenGenerationSession SaveGenerationBatch(
        string generationName,
        string exportDirectory,
        string mirrorRootAssetPath,
        BatchGenerateReply reply,
        bool useRandomSeed,
        int baseSeed)
    {
        Directory.CreateDirectory(exportDirectory);

        var sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}".Substring(0, 24);
        var mirrorSessionAssetPath = EnsureAssetFolder($"{mirrorRootAssetPath}/{SanitizeFileName(generationName)}_{sessionId}");
        var session = new MotionGenGenerationSession
        {
            id = sessionId,
            generationName = generationName,
            prompt = _settings.prompt,
            exportDirectory = exportDirectory,
            mirrorDirectoryAssetPath = mirrorSessionAssetPath,
            createdAtUtc = DateTime.UtcNow.ToString("O"),
            versionCount = reply.Items.Count,
            usedRandomSeed = useRandomSeed,
            baseSeed = baseSeed
        };

        var pendingWrites = new List<(GenerateReply replyItem, string baseName, string externalPath, string mirrorBvhAssetPath)>();
        for (var index = 0; index < reply.Items.Count; index++)
        {
            var itemReply = reply.Items[index];
            if (itemReply == null || itemReply.Data == null || itemReply.Data.Length == 0)
                continue;

            if (itemReply.Format != MotionFormat.Bvh)
                throw new InvalidOperationException($"Unexpected response format: {itemReply.Format}");

            var baseName = BuildVersionBaseName(generationName, index);
            var externalPath = GetUniqueExternalFilePath(Path.Combine(exportDirectory, $"{baseName}.bvh"));
            var mirrorBvhAssetPath = AssetDatabase.GenerateUniqueAssetPath($"{mirrorSessionAssetPath}/{baseName}.bvh");

            File.WriteAllBytes(externalPath, itemReply.Data.ToByteArray());
            File.WriteAllBytes(Path.GetFullPath(mirrorBvhAssetPath), itemReply.Data.ToByteArray());
            pendingWrites.Add((itemReply, baseName, externalPath, mirrorBvhAssetPath));
        }

        AssetDatabase.Refresh();

        foreach (var pendingWrite in pendingWrites)
        {
            if (!BvhToAnimConverter.TryConvertBvhAssetToAnim(pendingWrite.mirrorBvhAssetPath, out var clip, out var clipAssetPath))
                throw new InvalidOperationException($"BVH import failed for {pendingWrite.mirrorBvhAssetPath}.");

            var meta = ParseMeta(pendingWrite.replyItem.Meta);
            session.items.Add(new MotionGenGenerationItem
            {
                id = Guid.NewGuid().ToString("N"),
                displayName = pendingWrite.baseName,
                clipName = clip != null ? clip.name : pendingWrite.baseName,
                externalBvhPath = pendingWrite.externalPath,
                mirroredBvhAssetPath = pendingWrite.mirrorBvhAssetPath,
                clipAssetPath = clipAssetPath,
                backendFilename = pendingWrite.replyItem.Filename,
                metaJson = pendingWrite.replyItem.Meta,
                resolvedSeed = meta != null && meta.resolved_seed != 0 ? meta.resolved_seed : meta != null ? meta.seed : 0
            });
        }

        return session;
    }

    private static string BuildVersionBaseName(string generationName, int index)
    {
        return $"{SanitizeFileName(generationName)}_v{index + 1:000}";
    }

    private bool TryResolveSeedSettings(out bool useRandomSeed, out int baseSeed)
    {
        var seedText = (_settings.seedText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(seedText))
        {
            useRandomSeed = true;
            baseSeed = 0;
            return true;
        }

        useRandomSeed = false;
        return int.TryParse(seedText, out baseSeed);
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

    private void OnEditorUpdate()
    {
        if (!_isScenePreviewPlaying && !_pathEditNeedsPreviewRefresh)
            return;

        var item = GetSelectedHistoryItem();
        var clip = _activeTab == MotionGenTab.PathEdit ? LoadSourceClip(item) : LoadPreferredClip(item);
        if (_selectedAnimator == null || clip == null)
        {
            if (_isScenePreviewPlaying)
                StopScenePreview();

            _pathEditNeedsPreviewRefresh = false;
            return;
        }

        if (_pathEditNeedsPreviewRefresh)
        {
            #region agent log
            DebugLog(
                "MotionGenWindow.cs:OnEditorUpdate:beforePreviewRefresh",
                "Processing queued preview refresh",
                "H2",
                $"{{\"scenePreviewTime\":{_scenePreviewTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"isScenePreviewPlaying\":{ToJsonBool(_isScenePreviewPlaying)},\"hasAnimator\":{ToJsonBool(_selectedAnimator != null)},\"clipName\":\"{EscapeJson(clip.name)}\"}}");
            #endregion
            _pathEditNeedsPreviewRefresh = false;
            SampleScenePreview(clip, _scenePreviewTime, false);
            SceneView.RepaintAll();
            Repaint();
        }

        if (!_isScenePreviewPlaying)
            return;

        var now = EditorApplication.timeSinceStartup;
        var delta = Mathf.Max(0f, (float)(now - _lastPreviewUpdateTime));
        _lastPreviewUpdateTime = now;

        var clipLength = GetClipLength(clip);
        _scenePreviewTime = clipLength <= 0f ? 0f : Mathf.Repeat(_scenePreviewTime + delta, clipLength);
        SampleScenePreview(clip, _scenePreviewTime, false);
        SceneView.RepaintAll();
        Repaint();
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (_activeTab != MotionGenTab.PathEdit || !_showPathOverlay)
            return;

        var item = GetSelectedHistoryItem();
        var clip = LoadSourceClip(item);
        if (_selectedAnimator == null || clip == null)
            return;

        LoadPathEditSelection(item);
        DrawGeneratedPathOverlay();
        DrawCorrectedPathOverlay();
        DrawCurrentPreviewMarker();
        DrawPathKeyHandles();
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

    private string ResolveGenerationName()
    {
        var trimmedName = (_settings.generationName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedName))
            return SanitizeFileName(trimmedName);

        var prefix = string.IsNullOrWhiteSpace(_settings.defaultGenerationNamePrefix)
            ? "motiongen"
            : SanitizeFileName(_settings.defaultGenerationNamePrefix.Trim());
        return $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private string ResolveExportDirectory()
    {
        _settings.EnsureDefaults();
        return Path.GetFullPath(_settings.defaultExportDirectory);
    }

    private static string NormalizeMirrorRootAssetPath(string assetPath)
    {
        var normalized = string.IsNullOrWhiteSpace(assetPath)
            ? "Assets/MotionGen/Generated/Mirrored"
            : assetPath.Replace("\\", "/").TrimEnd('/');

        if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            normalized = "Assets/MotionGen/Generated/Mirrored";

        return normalized;
    }

    private static string EnsureAssetFolder(string assetPath)
    {
        EnsureFolders();

        var normalized = NormalizeMirrorRootAssetPath(assetPath);
        var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid asset path: {assetPath}");

        var currentPath = "Assets";
        for (var index = 1; index < parts.Length; index++)
        {
            var nextPath = $"{currentPath}/{parts[index]}";
            if (!AssetDatabase.IsValidFolder(nextPath))
                AssetDatabase.CreateFolder(currentPath, parts[index]);
            currentPath = nextPath;
        }

        return currentPath;
    }

    private static string GetUniqueExternalFilePath(string fullPath)
    {
        if (!File.Exists(fullPath))
            return fullPath;

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index:00}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Unable to create unique file path for {fullPath}");
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedChars = value
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(sanitizedChars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "motiongen" : sanitized;
    }

    private MotionGenGenerationItem GetSelectedHistoryItem()
    {
        EnsureValidSelection();

        if (_history == null)
            return null;

        return _history.sessions
            .Where(session => session != null && session.id == _selectedSessionId)
            .SelectMany(session => session.items)
            .FirstOrDefault(item => item != null && item.id == _selectedGenerationItemId);
    }

    private void PersistPathEditState(MotionGenGenerationItem item)
    {
        if (item == null || _history == null)
            return;

        item.pathEditLockY = _lockPathEditY;
        item.pathEditKeys = _pathEditKeys
            .Select(key => new MotionGenPathEditKey
            {
                id = key.id,
                time = key.time,
                position = key.position
            })
            .ToList();

        _history.Save();
    }

    private void EnsurePostProcessState(MotionGenGenerationItem item)
    {
        if (item == null)
            return;

        item.postProcessSettings ??= new MotionGenPostProcessSettings();
        item.postProcessSettings.EnsureDefaults();
        item.reviewedContactWindows ??= new List<MotionGenContactWindow>();
    }

    private void PersistPostProcessState(MotionGenGenerationItem item)
    {
        if (item == null || _history == null)
            return;

        EnsurePostProcessState(item);
        item.postProcessSettings.EnsureDefaults();
        item.reviewedContactWindows = item.reviewedContactWindows
            .Where(window => window != null)
            .OrderBy(window => window.startTime)
            .ThenBy(window => window.limb)
            .ToList();
        _history.Save();
    }

    private void DetectPostProcessContacts(MotionGenGenerationItem item, AnimationClip sourceClip)
    {
        if (item == null || sourceClip == null || _selectedAnimator == null)
            return;

        EnsurePostProcessState(item);
        if (!MotionGenPostProcessor.TryDetectContactWindows(
                sourceClip,
                _selectedAnimator,
                item.postProcessSettings,
                out var contactWindows,
                out var error))
        {
            _status = error ?? "Contact detection failed.";
            return;
        }

        item.reviewedContactWindows = contactWindows;
        PersistPostProcessState(item);
        _status = contactWindows.Count > 0
            ? $"Detected {contactWindows.Count} contact windows."
            : "Auto-detect found no contact windows. Try increasing the velocity/height thresholds or disable contact locking for this clip.";
    }

    private void ApplyPostProcessingToItem(MotionGenGenerationItem item, AnimationClip sourceClip)
    {
        if (item == null || sourceClip == null || _selectedAnimator == null)
            return;

        EnsurePostProcessState(item);
        item.postProcessSettings.enableContactLocking = false;
        if (!MotionGenPostProcessor.TryApplyPostProcessing(
                sourceClip,
                item.clipAssetPath,
                _selectedAnimator,
                item.postProcessSettings,
                item.reviewedContactWindows,
                item.referenceClipAssetPath,
                item.processedClipAssetPath,
                out var result,
                out var error))
        {
            _status = error ?? "Post-processing failed.";
            return;
        }

        item.referenceClipAssetPath = result.referenceClipAssetPath;
        item.referenceSourceClipAssetPath = result.sourceClipAssetPath;
        item.referenceSourceLastWriteTicks = result.sourceLastWriteTicks;
        item.processedClipAssetPath = result.processedClipAssetPath;
        item.postProcessedSourceLastWriteTicks = result.sourceLastWriteTicks;
        item.postProcessedAtUtc = DateTime.UtcNow.ToString("O");
        item.postProcessSettings = result.settings ?? item.postProcessSettings;
        item.reviewedContactWindows = result.contactWindows ?? item.reviewedContactWindows;
        PersistPostProcessState(item);

        var processedClip = LoadProcessedClip(item);
        if (processedClip != null)
        {
            ApplyGeneratedClip(processedClip);
            _status = $"Applied processed clip {processedClip.name}.";
        }
        else
        {
            _status = "Post-processing completed, but the processed clip could not be loaded.";
        }

        RequestPreviewRefresh();
        Repaint();
    }

    private Vector3 EvaluateCorrectedRootPosition(AnimationClip clip, float time, bool fallBackToOriginal)
    {
        if (_pathEditKeys == null || _pathEditKeys.Count == 0)
            return fallBackToOriginal ? SampleRootPositionAtTime(clip, time, false) : Vector3.zero;

        if (_pathEditKeys.Count == 1)
            return _pathEditKeys[0].position;

        var clampedTime = Mathf.Clamp(time, 0f, GetClipLength(clip));
        if (clampedTime <= _pathEditKeys[0].time)
            return _pathEditKeys[0].position;

        for (var index = 1; index < _pathEditKeys.Count; index++)
        {
            var nextKey = _pathEditKeys[index];
            if (clampedTime > nextKey.time)
                continue;

            var previousKey = _pathEditKeys[index - 1];
            var segmentDuration = Mathf.Max(0.0001f, nextKey.time - previousKey.time);
            var segmentT = Mathf.Clamp01((clampedTime - previousKey.time) / segmentDuration);
            return Vector3.Lerp(previousKey.position, nextKey.position, segmentT);
        }

        return _pathEditKeys[_pathEditKeys.Count - 1].position;
    }

    private void EnsureValidSelection()
    {
        if (_history == null || _history.sessions.Count == 0)
        {
            _selectedSessionId = null;
            _selectedGenerationItemId = null;
            return;
        }

        var selectedExists = _history.sessions.Any(session =>
            session != null &&
            session.id == _selectedSessionId &&
            session.items != null &&
            session.items.Any(item => item != null && item.id == _selectedGenerationItemId));

        if (selectedExists)
            return;

        ExpandAndSelectLatest(_history.sessions.FirstOrDefault(session => session != null && session.items.Count > 0));
    }

    private void ExpandAndSelectLatest(MotionGenGenerationSession session)
    {
        if (session == null || session.items == null || session.items.Count == 0)
            return;

        _selectedSessionId = session.id;
        _selectedGenerationItemId = session.items[0].id;
        _expandedSessionIds.Add(session.id);
    }

    private static AnimationClip LoadSourceClip(MotionGenGenerationItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.clipAssetPath))
            return null;

        return AssetDatabase.LoadAssetAtPath<AnimationClip>(item.clipAssetPath);
    }

    private static AnimationClip LoadReferenceClip(MotionGenGenerationItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.referenceClipAssetPath))
            return null;

        return AssetDatabase.LoadAssetAtPath<AnimationClip>(item.referenceClipAssetPath);
    }

    private static AnimationClip LoadProcessedClip(MotionGenGenerationItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.processedClipAssetPath))
            return null;

        return AssetDatabase.LoadAssetAtPath<AnimationClip>(item.processedClipAssetPath);
    }

    private static AnimationClip LoadPreferredClip(MotionGenGenerationItem item)
    {
        return HasFreshProcessedClip(item) ? LoadProcessedClip(item) : LoadSourceClip(item);
    }

    private static bool HasFreshProcessedClip(MotionGenGenerationItem item)
    {
        if (item == null || !item.postProcessingEnabled || string.IsNullOrWhiteSpace(item.processedClipAssetPath))
            return false;

        if (LoadProcessedClip(item) == null)
            return false;

        return item.postProcessedSourceLastWriteTicks != 0L
            && item.postProcessedSourceLastWriteTicks == GetClipAssetLastWriteTicks(item.clipAssetPath);
    }

    private static long GetClipAssetLastWriteTicks(string clipAssetPath)
    {
        if (string.IsNullOrWhiteSpace(clipAssetPath))
            return 0L;

        var fullPath = Path.GetFullPath(clipAssetPath);
        return File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0L;
    }

    private void SyncScenePreviewSelection(MotionGenGenerationItem item)
    {
        var clipAssetPath = HasFreshProcessedClip(item) ? item?.processedClipAssetPath : item?.clipAssetPath;
        SyncScenePreviewAssetPath(clipAssetPath);
    }

    private void SyncScenePreviewAssetPath(string clipAssetPath)
    {
        if (string.Equals(_scenePreviewClipAssetPath, clipAssetPath, StringComparison.Ordinal))
            return;

        StopScenePreview();
        _scenePreviewClipAssetPath = clipAssetPath;
    }

    private void LoadPathEditSelection(MotionGenGenerationItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.clipAssetPath))
            return;

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:LoadPathEditSelection:entry",
            "LoadPathEditSelection entered",
            "H1",
            $"{{\"itemId\":\"{EscapeJson(item.id)}\",\"clipAssetPath\":\"{EscapeJson(item.clipAssetPath)}\",\"currentPathEditClipAssetPath\":\"{EscapeJson(_pathEditClipAssetPath)}\",\"generatedPathSampleCount\":{_generatedPathSamples.Count},\"savedPathKeyCount\":{(item.pathEditKeys == null ? 0 : item.pathEditKeys.Count)}}}");
        #endregion

        if (string.Equals(_pathEditClipAssetPath, item.clipAssetPath, StringComparison.Ordinal) && _generatedPathSamples.Count > 0)
            return;

        _pathEditClipAssetPath = item.clipAssetPath;
        _selectedPathKeyIndex = -1;

        var clip = LoadSourceClip(item);
        if (clip == null)
        {
            _generatedPathSamples.Clear();
            _pathEditKeys.Clear();
            return;
        }

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:LoadPathEditSelection:beforeRefresh",
            "Refreshing generated path samples for path edit",
            "H3",
            $"{{\"clipName\":\"{EscapeJson(clip.name)}\",\"clipLength\":{GetClipLength(clip).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
        #endregion
        RefreshPathEditSamples(clip);
        #region agent log
        DebugLog(
            "MotionGenWindow.cs:LoadPathEditSelection:afterRefresh",
            "Generated path samples refreshed",
            "H3",
            $"{{\"generatedPathSampleCount\":{_generatedPathSamples.Count}}}");
        #endregion
        if (item.pathEditKeys != null && item.pathEditKeys.Count > 0)
        {
            _lockPathEditY = item.pathEditLockY;
            _pathEditKeys = item.pathEditKeys
                .Select(key => new MotionGenPathEditKey
                {
                    id = key.id,
                    time = key.time,
                    position = key.position
                })
                .OrderBy(key => key.time)
                .ToList();
            _selectedPathKeyIndex = _pathEditKeys.Count > 0 ? 0 : -1;
        }
        else
        {
            AutoGeneratePathKeys(clip);
            PersistPathEditState(item);
        }

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:LoadPathEditSelection:beforePreviewRefresh",
            "Queueing preview refresh after loading path edit selection",
            "H2",
            $"{{\"pathKeyCount\":{_pathEditKeys.Count},\"scenePreviewTime\":{_scenePreviewTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
        #endregion
        RequestPreviewRefresh();
    }

    private void RefreshPathEditSamples(AnimationClip clip)
    {
        _generatedPathSamples = CaptureRootPathSamples(clip, PathSampleCount);
    }

    private void AutoGeneratePathKeys(AnimationClip clip)
    {
        _pathEditKeys.Clear();
        if (clip == null)
            return;

        var clipLength = GetClipLength(clip);
        if (_generatedPathSamples.Count == 0)
            RefreshPathEditSamples(clip);

        for (var index = 0; index < DefaultPathKeyCount; index++)
        {
            var normalized = DefaultPathKeyCount <= 1 ? 0f : index / (float)(DefaultPathKeyCount - 1);
            var time = normalized * clipLength;
            _pathEditKeys.Add(new MotionGenPathEditKey
            {
                id = Guid.NewGuid().ToString("N"),
                time = time,
                position = SampleRootPositionAtTime(clip, time, false)
            });
        }

        SortPathKeys();
        _selectedPathKeyIndex = _pathEditKeys.Count > 0 ? 0 : -1;
    }

    private void AddPathKeyAtCurrentTime(AnimationClip clip)
    {
        if (clip == null)
            return;

        _pathEditKeys.Add(new MotionGenPathEditKey
        {
            id = Guid.NewGuid().ToString("N"),
            time = _scenePreviewTime,
            position = _pathEditKeys.Count > 0
                ? EvaluateCorrectedRootPosition(clip, _scenePreviewTime, false)
                : SampleRootPositionAtTime(clip, _scenePreviewTime, false)
        });

        SortPathKeys();
        _selectedPathKeyIndex = FindClosestPathKeyIndex(_scenePreviewTime);
        PersistPathEditState(GetSelectedHistoryItem());
        SceneView.RepaintAll();
        Repaint();
    }

    private void DeleteSelectedPathKey()
    {
        if (_selectedPathKeyIndex < 0 || _selectedPathKeyIndex >= _pathEditKeys.Count)
            return;

        _pathEditKeys.RemoveAt(_selectedPathKeyIndex);
        _selectedPathKeyIndex = Mathf.Clamp(_selectedPathKeyIndex, 0, _pathEditKeys.Count - 1);
        PersistPathEditState(GetSelectedHistoryItem());
        SceneView.RepaintAll();
        Repaint();
    }

    private void SetSelectedPathKeyToCurrentRoot()
    {
        if (_selectedPathKeyIndex < 0 || _selectedPathKeyIndex >= _pathEditKeys.Count || _selectedAnimator == null)
            return;

        var currentPosition = GetCurrentPreviewPathReferencePosition();
        if (_lockPathEditY)
            currentPosition.y = _pathEditKeys[_selectedPathKeyIndex].position.y;

        _pathEditKeys[_selectedPathKeyIndex].position = currentPosition;
        PersistPathEditState(GetSelectedHistoryItem());
        SceneView.RepaintAll();
        Repaint();
    }

    private void SortPathKeys()
    {
        _pathEditKeys = _pathEditKeys.OrderBy(key => key.time).ToList();
    }

    private int FindClosestPathKeyIndex(float time)
    {
        if (_pathEditKeys.Count == 0)
            return -1;

        var bestIndex = 0;
        var bestDistance = Mathf.Abs(_pathEditKeys[0].time - time);
        for (var index = 1; index < _pathEditKeys.Count; index++)
        {
            var distance = Mathf.Abs(_pathEditKeys[index].time - time);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private void PlayScenePreview(AnimationClip clip)
    {
        if (_selectedAnimator == null || clip == null)
            return;

        _isScenePreviewPlaying = true;
        _lastPreviewUpdateTime = EditorApplication.timeSinceStartup;
        RequestPreviewRefresh();
    }

    private void PauseScenePreview()
    {
        _isScenePreviewPlaying = false;
    }

    private void StopScenePreview(bool resetTime = true)
    {
        _isScenePreviewPlaying = false;
        if (resetTime)
            _scenePreviewTime = 0f;

        if (AnimationMode.InAnimationMode())
            AnimationMode.StopAnimationMode();

        SceneView.RepaintAll();
    }

    private void SampleScenePreview(AnimationClip clip, float time)
    {
        SampleScenePreview(clip, time, false);
    }

    private void SampleScenePreview(AnimationClip clip, float time, bool applyPathEdits)
    {
        if (_selectedAnimator == null || clip == null)
            return;

        if (!_isCapturingRootPathSamples)
        {
            #region agent log
            DebugLog(
                "MotionGenWindow.cs:SampleScenePreview:entry",
                "SampleScenePreview entered",
                "H2",
                $"{{\"clipName\":\"{EscapeJson(clip.name)}\",\"time\":{time.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"applyPathEdits\":{ToJsonBool(applyPathEdits)},\"inAnimationMode\":{ToJsonBool(AnimationMode.InAnimationMode())},\"pathKeyCount\":{_pathEditKeys.Count}}}");
            #endregion
        }

        if (!AnimationMode.InAnimationMode())
            AnimationMode.StartAnimationMode();

        var clampedTime = Mathf.Clamp(time, 0f, GetClipLength(clip));
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(_selectedAnimator.gameObject, clip, clampedTime);
        AnimationMode.EndSampling();

        if (applyPathEdits)
        {
            var currentReferencePosition = GetCurrentPreviewPathReferencePosition();
            var correctedRootPosition = EvaluateCorrectedRootPosition(clip, clampedTime, true);
            _selectedAnimator.transform.position += correctedRootPosition - currentReferencePosition;
        }

        SceneView.RepaintAll();

        if (!_isCapturingRootPathSamples)
        {
            #region agent log
            DebugLog(
                "MotionGenWindow.cs:SampleScenePreview:exit",
                "SampleScenePreview completed",
                "H2",
                $"{{\"clampedTime\":{clampedTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"rootX\":{_selectedAnimator.transform.position.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"rootY\":{_selectedAnimator.transform.position.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},\"rootZ\":{_selectedAnimator.transform.position.z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
            #endregion
        }
    }

    private List<Vector3> CaptureRootPathSamples(AnimationClip clip, int sampleCount)
    {
        var points = new List<Vector3>();
        if (_selectedAnimator == null || clip == null)
            return points;

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:CaptureRootPathSamples:entry",
            "CaptureRootPathSamples entered",
            "H3",
            $"{{\"clipName\":\"{EscapeJson(clip.name)}\",\"sampleCount\":{sampleCount},\"scenePreviewTime\":{_scenePreviewTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
        #endregion

        var previousTime = _scenePreviewTime;
        var wasPlaying = _isScenePreviewPlaying;
        PauseScenePreview();
        _isCapturingRootPathSamples = true;

        var clipLength = GetClipLength(clip);
        for (var index = 0; index < sampleCount; index++)
        {
            var normalized = sampleCount <= 1 ? 0f : index / (float)(sampleCount - 1);
            var sampleTime = clipLength * normalized;
            SampleScenePreview(clip, sampleTime, false);
            points.Add(GetCurrentPreviewPathReferencePosition());
        }
        _isCapturingRootPathSamples = false;

        _scenePreviewTime = previousTime;
        SampleScenePreview(clip, previousTime, false);
        if (wasPlaying)
            PlayScenePreview(clip);

        #region agent log
        DebugLog(
            "MotionGenWindow.cs:CaptureRootPathSamples:exit",
            "CaptureRootPathSamples completed",
            "H3",
            $"{{\"capturedPoints\":{points.Count},\"restoredPreviewTime\":{_scenePreviewTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}}}");
        #endregion

        return points;
    }

    private Vector3 SampleRootPositionAtTime(AnimationClip clip, float time, bool applyPathEdits = true)
    {
        if (_selectedAnimator == null || clip == null)
            return Vector3.zero;

        var previousTime = _scenePreviewTime;
        var wasPlaying = _isScenePreviewPlaying;
        PauseScenePreview();
        SampleScenePreview(clip, time, applyPathEdits);
        var sampledPosition = GetCurrentPreviewPathReferencePosition();
        _scenePreviewTime = previousTime;
        SampleScenePreview(clip, previousTime, false);
        if (wasPlaying)
            PlayScenePreview(clip);
        return sampledPosition;
    }

    private void DrawGeneratedPathOverlay()
    {
        if (_generatedPathSamples.Count < 2)
            return;

        Handles.color = new Color(1f, 1f, 1f, 0.35f);
        Handles.DrawAAPolyLine(4f, _generatedPathSamples.ToArray());
        Handles.SphereHandleCap(0, _generatedPathSamples[0], Quaternion.identity, 0.05f, EventType.Repaint);
        Handles.SphereHandleCap(0, _generatedPathSamples[_generatedPathSamples.Count - 1], Quaternion.identity, 0.05f, EventType.Repaint);
    }

    private void DrawCorrectedPathOverlay()
    {
        if (_pathEditKeys.Count < 2)
            return;

        Handles.color = new Color(0.2f, 0.8f, 0.3f, 0.95f);
        Handles.DrawAAPolyLine(5f, _pathEditKeys.Select(key => key.position).ToArray());
    }

    private void DrawCurrentPreviewMarker()
    {
        if (_selectedAnimator == null)
            return;

        Handles.color = Color.cyan;
        Handles.SphereHandleCap(0, GetCurrentPreviewPathReferencePosition(), Quaternion.identity, 0.06f, EventType.Repaint);
    }

    private void DrawPathKeyHandles()
    {
        for (var index = 0; index < _pathEditKeys.Count; index++)
        {
            var key = _pathEditKeys[index];
            var handleSize = HandleUtility.GetHandleSize(key.position) * 0.08f;
            Handles.color = index == _selectedPathKeyIndex ? Color.yellow : Color.green;

            if (Handles.Button(key.position, Quaternion.identity, handleSize, handleSize, Handles.SphereHandleCap))
                _selectedPathKeyIndex = index;

            if (_selectedPathKeyIndex != index)
                continue;

            EditorGUI.BeginChangeCheck();
            var nextPosition = Handles.PositionHandle(key.position, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                if (_lockPathEditY)
                    nextPosition.y = key.position.y;
                key.position = nextPosition;
                PersistPathEditState(GetSelectedHistoryItem());
                SceneView.RepaintAll();
                Repaint();
            }
        }
    }

    private Vector3 GetCurrentPreviewPathReferencePosition()
    {
        if (_selectedAnimator == null)
            return Vector3.zero;

        var hips = _selectedAnimator.GetBoneTransform(HumanBodyBones.Hips);
        if (hips != null)
            return hips.position;

        if (_selectedAnimator.avatar != null && _selectedAnimator.avatar.isHuman)
            return _selectedAnimator.rootPosition;

        return _selectedAnimator.transform.position;
    }

    private static AnimationCurve GetAnimatorFloatCurve(AnimationClip clip, string propertyName)
    {
        if (clip == null || string.IsNullOrWhiteSpace(propertyName))
            return null;

        return AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName));
    }

    private static void SetAnimatorFloatCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
    {
        if (clip == null || string.IsNullOrWhiteSpace(propertyName))
            return;

        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName), curve);
    }

    private void BakePathEditsIntoClip(AnimationClip clip)
    {
        if (clip == null || _selectedAnimator == null || _pathEditKeys == null || _pathEditKeys.Count == 0)
            return;

        var rootX = GetAnimatorFloatCurve(clip, "RootT.x");
        var rootY = GetAnimatorFloatCurve(clip, "RootT.y");
        var rootZ = GetAnimatorFloatCurve(clip, "RootT.z");
        if (rootX == null || rootY == null || rootZ == null)
            return;

        var correctedX = new AnimationCurve();
        var correctedY = new AnimationCurve();
        var correctedZ = new AnimationCurve();
        var clipLength = GetClipLength(clip);
        var sampleCount = Mathf.Max(2, Mathf.RoundToInt(clipLength * Mathf.Max(1f, clip.frameRate)) + 1);
        var previousTime = _scenePreviewTime;
        var wasPlaying = _isScenePreviewPlaying;

        PauseScenePreview();
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var sampleTime = sampleIndex == sampleCount - 1
                ? clipLength
                : Mathf.Min(clipLength, sampleIndex / Mathf.Max(1f, clip.frameRate));

            SampleScenePreview(clip, sampleTime, false);
            var currentReferencePosition = GetCurrentPreviewPathReferencePosition();
            var correctedRootPosition = EvaluateCorrectedRootPosition(clip, sampleTime, true);
            var rootOffset = correctedRootPosition - currentReferencePosition;
            var currentCurvePosition = new Vector3(rootX.Evaluate(sampleTime), rootY.Evaluate(sampleTime), rootZ.Evaluate(sampleTime));
            var bakedCurvePosition = currentCurvePosition + rootOffset;

            correctedX.AddKey(new Keyframe(sampleTime, bakedCurvePosition.x));
            correctedY.AddKey(new Keyframe(sampleTime, bakedCurvePosition.y));
            correctedZ.AddKey(new Keyframe(sampleTime, bakedCurvePosition.z));
        }

        SetAnimatorFloatCurve(clip, "RootT.x", correctedX);
        SetAnimatorFloatCurve(clip, "RootT.y", correctedY);
        SetAnimatorFloatCurve(clip, "RootT.z", correctedZ);
        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();

        _scenePreviewTime = previousTime;
        SampleScenePreview(clip, previousTime, false);
        if (wasPlaying)
            PlayScenePreview(clip);
    }

    private void RequestPreviewRefresh()
    {
        _pathEditNeedsPreviewRefresh = true;
        Repaint();
    }

    private static float GetClipLength(AnimationClip clip)
    {
        return clip == null ? 0f : Mathf.Max(0.01f, clip.length);
    }

    private void InspectClip(AnimationClip clip)
    {
        if (clip == null)
            return;

        Selection.activeObject = clip;
        EditorGUIUtility.PingObject(clip);
    }

    private void OpenAnimationWindow(AnimationClip clip)
    {
        var animationWindowType = Type.GetType("UnityEditor.AnimationWindow,UnityEditor");
        if (animationWindowType == null)
        {
            _status = "Animation Window type was not found.";
            return;
        }

        if (_selectedAnimator != null)
            Selection.activeGameObject = _selectedAnimator.gameObject;
        else if (clip != null)
            Selection.activeObject = clip;

        EditorWindow.GetWindow(animationWindowType);
    }

    private static MotionReplyMeta ParseMeta(string metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
            return null;

        try
        {
            return JsonUtility.FromJson<MotionReplyMeta>(metaJson);
        }
        catch
        {
            return null;
        }
    }

    private static string TryFormatTimestamp(string createdAtUtc)
    {
        if (DateTime.TryParse(createdAtUtc, out var timestamp))
            return timestamp.ToLocalTime().ToString("g");

        return createdAtUtc;
    }

    private static void RevealExport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (File.Exists(path) || Directory.Exists(path))
        {
            EditorUtility.RevealInFinder(path);
            return;
        }

        var parentDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentDirectory) && Directory.Exists(parentDirectory))
            EditorUtility.RevealInFinder(parentDirectory);
    }

    private static void DebugLog(string location, string message, string hypothesisId, string dataJson)
    {
        try
        {
            File.AppendAllText(
                DebugLogPath,
                "{" +
                $"\"id\":\"{Guid.NewGuid():N}\"," +
                $"\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}," +
                $"\"runId\":\"{DebugRunId}\"," +
                $"\"hypothesisId\":\"{EscapeJson(hypothesisId)}\"," +
                $"\"location\":\"{EscapeJson(location)}\"," +
                $"\"message\":\"{EscapeJson(message)}\"," +
                $"\"data\":{(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson)}" +
                "}" + Environment.NewLine);
        }
        catch
        {
            // Intentionally swallow debug logging failures.
        }
    }

    private static string EscapeJson(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    private static string ToJsonBool(bool value)
    {
        return value ? "true" : "false";
    }
}
#endif
