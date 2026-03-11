#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Grpc.Core;
using Motion;
using MotionGen;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public class MotionGenWindow : EditorWindow
{
    private const string GeneratedRoot = "Assets/MotionGen/Generated";
    private const bool AutoOpenAnimationWindowOnEnable = false;
    private const bool ModifyEditorSelection = false;
    private const float RetargetMuscleScale = 1.0f;

    private enum SidebarTab { Animations, Keyframes }
    private SidebarTab _activeTab = SidebarTab.Animations;

    private Animator _selectedAnimator;
    private AnimationClip _activeClip;
    private float _currentPreviewTime;
    private MotionGenEditorSettings _settings;
    private bool _isGenerating;
    private string _generateStatus = "Idle";
    private bool _autoApplyOnGenerate = true;
    private string _lastGeneratedClipAssetPath;
    private bool _isPreviewPlaying;
    private bool _loopPreview = true;
    private double _lastPreviewEditorTime;

    [MenuItem("Window/MotionGen")]
    public static void ShowWindow()
    {
        var window = GetWindow<MotionGenWindow>();
        window.titleContent = new GUIContent("MotionGen");
        window.Show();
        window.Focus();
    }

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        EditorApplication.update += UpdatePreviewPlayback;
        _settings = MotionGenEditorSettings.GetOrCreate();
        OnSelectionChanged();

        if (AutoOpenAnimationWindowOnEnable)
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                OpenAnimationWindowAndFocus();
            };
        }
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
        EditorApplication.update -= UpdatePreviewPlayback;
        _isPreviewPlaying = false;

        if (AnimationMode.InAnimationMode())
            AnimationMode.StopAnimationMode();
    }

    private void OnSelectionChanged()
    {
        _isPreviewPlaying = false;
        _selectedAnimator = null;
        _activeClip = null;
        _currentPreviewTime = 0f;

        if (Selection.activeGameObject != null)
        {
            var animator = Selection.activeGameObject.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
            {
                _selectedAnimator = animator;
            }
        }

        _activeTab = _selectedAnimator != null ? SidebarTab.Keyframes : SidebarTab.Animations;
        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();

        EditorGUILayout.BeginHorizontal();

        // Left: Scene/Timeline actions
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        DrawSceneAndAnimationPanel();
        EditorGUILayout.EndVertical();

        // Right: Sidebar
        EditorGUILayout.BeginVertical(GUILayout.Width(320));
        DrawSidebar();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("MotionGen", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Settings", EditorStyles.toolbarButton))
        {
            MotionGenSettingsWindow.ShowWindow(_settings);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSceneAndAnimationPanel()
    {
        EditorGUILayout.HelpBox("Use the Scene view to select a humanoid. Animation clip controls are available below.", MessageType.Info);

        EditorGUILayout.Space();

        if (_selectedAnimator == null)
        {
            EditorGUILayout.HelpBox("No humanoid selected.", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("Selected Humanoid:", _selectedAnimator.name);

        if (GUILayout.Button("Create / Open Animation Clip"))
        {
            EnsureFolders();
            EnsureAnimationClipForSelection();
        }

        if (GUILayout.Button("Open Animation Window"))
        {
            OpenAnimationWindowAndFocus();
        }

        EditorGUILayout.Space();
        _settings.useRetargetCalibration = EditorGUILayout.ToggleLeft("Use Retarget Calibration", _settings.useRetargetCalibration);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Capture T-Pose Calibration"))
        {
            CaptureRetargetCalibrationFromCurrentPose();
        }
        if (GUILayout.Button("Auto Capture (Pose/Restore)"))
        {
            AutoCaptureRetargetCalibration();
        }
        if (GUILayout.Button("Clear Calibration"))
        {
            _settings.ClearCalibration();
            _settings.Save();
            Debug.Log("[MotionGen] Cleared retarget calibration.");
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField("Calibrated Bones", _settings.retargetCalibration != null ? _settings.retargetCalibration.Count.ToString() : "0");

        if (TryGetOrCreateAnimationClip(out var clip))
        {
            if (_activeClip == null)
                _activeClip = clip;

            var shownClip = _activeClip != null ? _activeClip : clip;
            var duration = Mathf.Max(GetClipDuration(shownClip), 0.01f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Active Clip", shownClip.name);
            _currentPreviewTime = EditorGUILayout.Slider("Preview Time", _currentPreviewTime, 0f, duration);

            if (GUILayout.Button("Sample Pose At Time"))
            {
                SampleCurrentPose();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(_isPreviewPlaying ? "Pause Preview" : "Play Preview"))
            {
                TogglePreviewPlayback();
            }
            if (GUILayout.Button("Stop Preview"))
            {
                StopPreviewPlayback();
            }
            EditorGUILayout.EndHorizontal();

            _loopPreview = EditorGUILayout.ToggleLeft("Loop Preview", _loopPreview);
        }
    }

    private void TogglePreviewPlayback()
    {
        if (_isPreviewPlaying)
        {
            _isPreviewPlaying = false;
            return;
        }

        AnimationClip clip = _activeClip;
        if (clip == null)
        {
            if (!TryGetOrCreateAnimationClip(out clip))
                return;

            _activeClip = clip;
        }

        _isPreviewPlaying = true;
        _lastPreviewEditorTime = EditorApplication.timeSinceStartup;

        if (!AnimationMode.InAnimationMode())
            AnimationMode.StartAnimationMode();

        SampleCurrentPose();
    }

    private void StopPreviewPlayback()
    {
        _isPreviewPlaying = false;
        _currentPreviewTime = 0f;
        SampleCurrentPose();
    }

    private void CaptureRetargetCalibrationFromCurrentPose()
    {
        if (_selectedAnimator == null || !_selectedAnimator.isHuman)
        {
            Debug.LogWarning("[MotionGen] Select a humanoid Animator before calibration.");
            return;
        }

        var maps = GetT2MHumanoidJointMaps();
        int count = 0;

        foreach (var map in maps)
        {
            var bone = _selectedAnimator.GetBoneTransform(map.bone);
            var child = _selectedAnimator.GetBoneTransform(map.childBone);
            if (bone == null || child == null)
                continue;

            var bindDir = (child.position - bone.position);
            if (bindDir.sqrMagnitude < 1e-8f)
                continue;

            var srcDir = GetSourceReferenceDirectionUnity(map.jointIndex, map.childJointIndex);
            if (srcDir.sqrMagnitude < 1e-8f)
                continue;

            var correction = Quaternion.FromToRotation(srcDir.normalized, bindDir.normalized);
            _settings.SetCalibration(map.bone, correction);
            count++;
        }

        _settings.Save();
        Debug.Log($"[MotionGen] Captured retarget calibration for {count} bones. Keep avatar in reference T-pose for best results.");
    }

    private void AutoCaptureRetargetCalibration()
    {
        if (_selectedAnimator == null || !_selectedAnimator.isHuman || _selectedAnimator.avatar == null || !_selectedAnimator.avatar.isValid)
        {
            Debug.LogWarning("[MotionGen] Auto-capture requires a valid humanoid avatar.");
            return;
        }

        var poseHandler = new HumanPoseHandler(_selectedAnimator.avatar, _selectedAnimator.transform);
        HumanPose originalPose = default;
        poseHandler.GetHumanPose(ref originalPose);

        try
        {
            var neutralPose = originalPose;
            if (neutralPose.muscles == null || neutralPose.muscles.Length != HumanTrait.MuscleCount)
                neutralPose.muscles = new float[HumanTrait.MuscleCount];

            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                neutralPose.muscles[i] = 0f;

            // Keep body transform stable while neutralizing limbs.
            neutralPose.bodyPosition = originalPose.bodyPosition;
            neutralPose.bodyRotation = originalPose.bodyRotation;

            poseHandler.SetHumanPose(ref neutralPose);
            SceneView.RepaintAll();

            CaptureRetargetCalibrationFromCurrentPose();
            Debug.Log("[MotionGen] Auto-capture finished (neutral pose -> calibration -> restored pose).");
        }
        finally
        {
            poseHandler.SetHumanPose(ref originalPose);
            SceneView.RepaintAll();
        }
    }

    private void UpdatePreviewPlayback()
    {
        if (!_isPreviewPlaying || _selectedAnimator == null || _activeClip == null)
            return;

        var now = EditorApplication.timeSinceStartup;
        var delta = (float)(now - _lastPreviewEditorTime);
        _lastPreviewEditorTime = now;

        if (delta <= 0f)
            return;

        var duration = Mathf.Max(GetClipDuration(_activeClip), 0.01f);
        _currentPreviewTime += delta;

        if (_currentPreviewTime >= duration)
        {
            if (_loopPreview)
            {
                _currentPreviewTime %= duration;
            }
            else
            {
                _currentPreviewTime = duration;
                _isPreviewPlaying = false;
            }
        }

        SampleCurrentPose();
        Repaint();
    }

    private void OpenAnimationWindowAndFocus()
    {
        if (ModifyEditorSelection && _selectedAnimator != null)
            SafeSelectGameObject(_selectedAnimator.gameObject);

        EditorApplication.ExecuteMenuItem("Window/Animation/Animation");

        var animationWindowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.AnimationWindow");
        if (animationWindowType != null)
        {
            var animationWindow = EditorWindow.GetWindow(animationWindowType);
            if (animationWindow != null)
                animationWindow.Focus();
        }
    }

    private void DrawSidebar()
    {
        // Auto-switch tabs
        _activeTab = _selectedAnimator != null ? SidebarTab.Keyframes : SidebarTab.Animations;

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_selectedAnimator == null))
        {
            if (GUILayout.Toggle(_activeTab == SidebarTab.Keyframes, "Keyframes", EditorStyles.toolbarButton))
                _activeTab = SidebarTab.Keyframes;
        }
        if (GUILayout.Toggle(_activeTab == SidebarTab.Animations, "Animations", EditorStyles.toolbarButton))
            _activeTab = SidebarTab.Animations;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        if (_activeTab == SidebarTab.Keyframes)
        {
            DrawKeyframePanel();
        }
        else
        {
            DrawAnimationsPanel();
        }

        EditorGUILayout.Space();
        DrawGeneratePanel();
    }

    private void DrawKeyframePanel()
    {
        EditorGUILayout.LabelField("Keyframe Editor", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select a keyframe on the Timeline to edit transforms. (Hookup pending)", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Prev Keyframe")) { GoToAdjacentKeyframe(-1); }
        if (GUILayout.Button("Next Keyframe")) { GoToAdjacentKeyframe(+1); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        if (GUILayout.Button("Create Keyframe"))
        {
            CreateKeyframeAtCurrentTime();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Transform Overrides", EditorStyles.boldLabel);

        // Placeholder UI
        EditorGUILayout.Vector3Field("Position", Vector3.zero);
        EditorGUILayout.Vector3Field("Rotation", Vector3.zero);
        EditorGUILayout.Vector3Field("Scale", Vector3.one);
    }

    private void CreateKeyframeAtCurrentTime()
    {
        if (_selectedAnimator == null) return;

        if (!TryGetOrCreateAnimationClip(out var clip)) return;

        _activeClip = clip;
        var t = Mathf.Max(0f, _currentPreviewTime);
        var tr = _selectedAnimator.transform;

        AddOrUpdateKey(clip, tr, "m_LocalPosition.x", t, tr.localPosition.x);
        AddOrUpdateKey(clip, tr, "m_LocalPosition.y", t, tr.localPosition.y);
        AddOrUpdateKey(clip, tr, "m_LocalPosition.z", t, tr.localPosition.z);

        AddOrUpdateKey(clip, tr, "localEulerAnglesRaw.x", t, tr.localEulerAngles.x);
        AddOrUpdateKey(clip, tr, "localEulerAnglesRaw.y", t, tr.localEulerAngles.y);
        AddOrUpdateKey(clip, tr, "localEulerAnglesRaw.z", t, tr.localEulerAngles.z);

        AddOrUpdateKey(clip, tr, "m_LocalScale.x", t, tr.localScale.x);
        AddOrUpdateKey(clip, tr, "m_LocalScale.y", t, tr.localScale.y);
        AddOrUpdateKey(clip, tr, "m_LocalScale.z", t, tr.localScale.z);

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();

        SampleCurrentPose();
        Debug.Log($"[MotionGen] Keyframe created at {t:0.###}s");
    }

    private void GoToAdjacentKeyframe(int direction)
    {
        if (_selectedAnimator == null) return;

        if (!TryGetOrCreateAnimationClip(out var clip)) return;

        _activeClip = clip;
        var times = GetKeyTimes(clip);
        if (times.Count == 0) return;

        var current = _currentPreviewTime;
        float target = current;

        if (direction < 0)
        {
            var previous = times.Where(t => t < current - 0.0001f).ToList();
            if (previous.Count > 0)
                target = previous.Max();
        }
        else
        {
            var next = times.Where(t => t > current + 0.0001f).ToList();
            if (next.Count > 0)
                target = next.Min();
        }

        _currentPreviewTime = Mathf.Max(0f, target);
        SampleCurrentPose();
        Repaint();
    }

    private List<float> GetKeyTimes(AnimationClip clip)
    {
        var times = new HashSet<float>();

        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null) continue;
            foreach (var key in curve.keys)
                times.Add(key.time);
        }

        return times.OrderBy(t => t).ToList();
    }

    private void AddOrUpdateKey(AnimationClip clip, Transform tr, string propertyName, float time, float value)
    {
        var binding = new EditorCurveBinding
        {
            path = AnimationUtility.CalculateTransformPath(tr, _selectedAnimator.transform),
            type = typeof(Transform),
            propertyName = propertyName
        };

        var curve = AnimationUtility.GetEditorCurve(clip, binding) ?? new AnimationCurve();
        curve.AddKey(new Keyframe(time, value));
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private bool TryGetOrCreateAnimationClip(out AnimationClip clip)
    {
        clip = null;

        if (_selectedAnimator == null)
            return false;

        EnsureFolders();

        var clipPath = $"{GeneratedRoot}/{_selectedAnimator.name}_MotionGen.anim";
        clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);

        if (clip == null)
        {
            clip = new AnimationClip();
            AssetDatabase.CreateAsset(clip, clipPath);
            AssetDatabase.SaveAssets();
        }
        return true;
    }

    private void EnsureAnimationClipForSelection()
    {
        if (!TryGetOrCreateAnimationClip(out var clip))
            return;

        EnsureAnimatorControllerAndState(clip);

        _activeClip = clip;
        _currentPreviewTime = 0f;
        SafeSelectObject(clip);
        SampleCurrentPose();
    }

    private void EnsureAnimatorControllerAndState(AnimationClip clip)
    {
        if (_selectedAnimator == null || clip == null)
            return;

        var controller = _selectedAnimator.runtimeAnimatorController as AnimatorController;
        if (controller == null)
        {
            var controllerPath = $"{GeneratedRoot}/{_selectedAnimator.name}_MotionGen.controller";
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);

            if (controller == null)
            {
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            }

            _selectedAnimator.runtimeAnimatorController = controller;
        }

        if (controller.layers == null || controller.layers.Length == 0)
            return;

        var sm = controller.layers[0].stateMachine;
        var state = sm.states.FirstOrDefault(s => s.state != null && s.state.name == "MotionGen").state;
        if (state == null)
            state = sm.AddState("MotionGen");

        // Do not continuously overwrite generated clips every GUI repaint.
        if (state.motion == null)
            state.motion = clip;

        if (sm.defaultState == null)
            sm.defaultState = state;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
    }

    private float GetClipDuration(AnimationClip clip)
    {
        if (clip == null)
            return Mathf.Max(_settings != null ? _settings.durationSeconds : 1f, 0.1f);

        return Mathf.Max(clip.length, _settings != null ? _settings.durationSeconds : 1f, 0.1f);
    }

    private void SampleCurrentPose()
    {
        if (_selectedAnimator == null || _activeClip == null)
            return;

        if (!AnimationMode.InAnimationMode())
            AnimationMode.StartAnimationMode();

        _currentPreviewTime = Mathf.Clamp(_currentPreviewTime, 0f, GetClipDuration(_activeClip));
        AnimationMode.SampleAnimationClip(_selectedAnimator.gameObject, _activeClip, _currentPreviewTime);
        SceneView.RepaintAll();
    }

    private void DrawAnimationsPanel()
    {
        EditorGUILayout.LabelField("Generated Animations", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("This will list all generated clips for quick reapply.", MessageType.Info);

        // TODO: Scan GeneratedRoot and list clips
        if (GUILayout.Button("Refresh List")) { /* TODO */ }
    }

    private void DrawGeneratePanel()
    {
        EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
        _settings.prompt = EditorGUILayout.TextArea(_settings.prompt, GUILayout.MinHeight(60));
        EditorGUILayout.LabelField("Status", _generateStatus);
        _autoApplyOnGenerate = EditorGUILayout.ToggleLeft("Auto-apply generated clip", _autoApplyOnGenerate);
        _settings.exportSmplSidecar = EditorGUILayout.ToggleLeft("Export SMPL sidecar (.smpl.json)", _settings.exportSmplSidecar);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(_isGenerating))
        {
            if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate"))
            {
                GenerateMotion();
            }
        }
        if (GUILayout.Button("Settings"))
        {
            MotionGenSettingsWindow.ShowWindow(_settings);
        }
        EditorGUILayout.EndHorizontal();

        using (new EditorGUI.DisabledScope(_selectedAnimator == null || string.IsNullOrWhiteSpace(_lastGeneratedClipAssetPath)))
        {
            if (GUILayout.Button("Apply Last Generated Clip"))
            {
                ApplyLastGeneratedClip();
            }
        }
    }

    private async void GenerateMotion()
    {
        var humanoidToReselect = _selectedAnimator != null ? _selectedAnimator.gameObject : null;

        if (string.IsNullOrWhiteSpace(_settings.prompt))
        {
            _generateStatus = "Prompt is empty.";
            return;
        }

        _isGenerating = true;
        _generateStatus = "Sending request...";
        Repaint();

        try
        {
            using var client = new MotionClient();
            var response = await client.GenerateAsync(
                _settings.prompt,
                _settings.fps,
                _settings.durationSeconds,
                _settings.seed,
                ToGrpcFormat(_settings.format)
            );

            if (response == null || response.Data == null || response.Data.Length == 0)
            {
                _generateStatus = "Backend returned empty data.";
                Debug.LogWarning("[MotionGen] Generate returned empty data.");
                return;
            }

            EnsureFolders();

            var safeFileName = string.IsNullOrWhiteSpace(response.Filename)
                ? GetDefaultGeneratedFileName(response.Format)
                : response.Filename;

            var assetPath = $"{GeneratedRoot}/{safeFileName}";
            var absolutePath = Path.GetFullPath(assetPath);

            File.WriteAllBytes(absolutePath, response.Data.ToByteArray());
            AssetDatabase.Refresh();

            if (_selectedAnimator != null)
            {
                if (response.Format == Motion.MotionFormat.Json)
                {
                    if (TryCreateClipFromGeneratedJson(response, safeFileName, out var generatedClip))
                    {
                        _lastGeneratedClipAssetPath = AssetDatabase.GetAssetPath(generatedClip);

                        if (_settings.exportSmplSidecar)
                        {
                            if (TryCreateSmplSidecarFromClip(generatedClip, safeFileName, out var smplPath))
                                Debug.Log($"[MotionGen] SMPL sidecar saved: {smplPath}");
                            else
                                Debug.LogWarning("[MotionGen] Failed to export SMPL sidecar JSON.");
                        }

                        if (_autoApplyOnGenerate)
                        {
                            ApplyGeneratedClipNonDestructive(generatedClip);
                        }
                        else
                        {
                            Debug.Log($"[MotionGen] Generated clip ready. Click 'Apply Last Generated Clip' to apply: {generatedClip.name}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[MotionGen] JSON received but could not convert to AnimationClip. File saved for inspection.");
                    }
                }
                else
                {
                    // ── BVH path: parse in C# and build Humanoid clip via HumanPoseHandler ──
                    var bvhText = System.Text.Encoding.UTF8.GetString(response.Data.ToByteArray());
                    var (bvhClip, bvhAvatar) = BvhImporter.ImportAsHumanoid(bvhText);
                    if (bvhClip != null)
                    {
                        // Save the clip as a .anim asset.
                        var clipName = Path.GetFileNameWithoutExtension(safeFileName);
                        var clipAssetPath = $"{GeneratedRoot}/{clipName}.anim";
                        AssetDatabase.CreateAsset(bvhClip, clipAssetPath);

                        // Save the avatar alongside it.
                        if (bvhAvatar != null)
                        {
                            var avatarAssetPath = $"{GeneratedRoot}/{clipName}_Avatar.asset";
                            AssetDatabase.CreateAsset(bvhAvatar, avatarAssetPath);
                        }

                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();

                        _lastGeneratedClipAssetPath = clipAssetPath;

                        if (_autoApplyOnGenerate)
                        {
                            ApplyGeneratedClipNonDestructive(bvhClip);
                        }
                        else
                        {
                            Debug.Log($"[MotionGen] BVH clip ready. Click 'Apply Last Generated Clip' to apply: {bvhClip.name}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[MotionGen] BVH saved but could not convert to Humanoid AnimationClip.");
                    }
                }
            }
            else if (response.Format == Motion.MotionFormat.Json && _settings.exportSmplSidecar)
            {
                if (TryCreateSmplSidecarFromGeneratedJson(response, safeFileName, out var smplPath))
                    Debug.Log($"[MotionGen] SMPL sidecar (joint-only) saved: {smplPath}");
                else
                    Debug.LogWarning("[MotionGen] Failed to export SMPL sidecar JSON.");
            }

            _generateStatus = $"Generated: {safeFileName} ({response.Data.Length} bytes)";
            Debug.Log($"[MotionGen] Generate OK | file={safeFileName} | bytes={response.Data.Length} | meta={response.Meta}");
        }
        catch (RpcException ex)
        {
            _generateStatus = $"RPC failed: {ex.StatusCode}";
            Debug.LogError($"[MotionGen] Generate RPC failed: {ex.StatusCode} - {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            _generateStatus = "Generate failed.";
            Debug.LogError($"[MotionGen] Generate failed: {ex.Message}");
        }
        finally
        {
            _isGenerating = false;

            if (humanoidToReselect != null)
            {
                SafeSelectGameObject(humanoidToReselect);
            }

            Repaint();
        }
    }

    private bool TryCreateSmplSidecarFromGeneratedJson(GenerateReply response, string sourceFileName, out string smplAssetPath)
    {
        smplAssetPath = null;

        try
        {
            var jsonText = Encoding.UTF8.GetString(response.Data.ToByteArray());
            var motion = JsonUtility.FromJson<GeneratedMotionJson>(jsonText);
            if (motion == null || motion.frames == null || motion.frames.Length == 0)
                return false;

            var smpl = new SmplMotionJson
            {
                schema = "smpl-unity-v1",
                note = "Joint-only SMPL-style sidecar exported from MotionGen JSON. Contains root translation + 22 joints per frame.",
                jointOnly = true,
                fps = motion.fps > 0 ? motion.fps : _settings.fps,
                frames = new SmplFrame[motion.frames.Length],
            };

            for (int i = 0; i < motion.frames.Length; i++)
            {
                var frame = motion.frames[i];
                var joints = frame.joints ?? Array.Empty<GeneratedVec3>();
                var outJoints = new GeneratedVec3[joints.Length];

                for (int j = 0; j < joints.Length; j++)
                {
                    outJoints[j] = new GeneratedVec3
                    {
                        x = joints[j].x,
                        y = joints[j].y,
                        z = joints[j].z,
                    };
                }

                smpl.frames[i] = new SmplFrame
                {
                    trans = new GeneratedVec3
                    {
                        x = frame.position != null ? frame.position.x : 0f,
                        y = frame.position != null ? frame.position.y : 0f,
                        z = frame.position != null ? frame.position.z : 0f,
                    },
                    rootYawDeg = frame.rotationEuler != null ? frame.rotationEuler.y : 0f,
                    joints22 = outJoints,
                };
            }

            EnsureFolders();
            var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
            smplAssetPath = $"{GeneratedRoot}/{baseName}.smpl.json";
            var absolutePath = Path.GetFullPath(smplAssetPath);

            File.WriteAllText(absolutePath, JsonUtility.ToJson(smpl, true), Encoding.UTF8);
            AssetDatabase.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MotionGen] SMPL sidecar export failed: {ex.Message}");
            return false;
        }
    }

    private bool TryCreateSmplSidecarFromClip(AnimationClip clip, string sourceFileName, out string smplAssetPath)
    {
        smplAssetPath = null;

        if (_selectedAnimator == null || clip == null)
            return false;

        try
        {
            var fps = Mathf.Max(1, Mathf.RoundToInt(clip.frameRate > 0f ? clip.frameRate : _settings.fps));
            var duration = Mathf.Max(0.01f, GetClipDuration(clip));
            var frameCount = Mathf.Max(1, Mathf.CeilToInt(duration * fps));
            var maps = GetDefaultSmplBoneNameMap();

            var root = _selectedAnimator.transform;
            var boneTransforms = new Dictionary<string, Transform>();
            foreach (var m in maps)
            {
                var t = _selectedAnimator.GetBoneTransform(m.bone);
                if (t != null)
                    boneTransforms[m.name] = t;
            }

            var poseRestore = new Dictionary<Transform, Quaternion>();
            foreach (var t in boneTransforms.Values)
            {
                if (t != null && !poseRestore.ContainsKey(t))
                    poseRestore[t] = t.localRotation;
            }

            var originalRootPos = root.localPosition;
            var originalRootRot = root.localRotation;

            var startedAnimationModeHere = false;
            if (!AnimationMode.InAnimationMode())
            {
                AnimationMode.StartAnimationMode();
                startedAnimationModeHere = true;
            }

            var smpl = new SmplMotionJson
            {
                schema = "smpl-unity-v1",
                note = "Unity-humanoid local bone rotations sampled from generated AnimationClip. No FBX required.",
                jointOnly = false,
                fps = fps,
                frames = new SmplFrame[frameCount],
            };

            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    var t = Mathf.Min(duration, i / (float)fps);
                    AnimationMode.SampleAnimationClip(_selectedAnimator.gameObject, clip, t);

                    var bones = new SmplBoneRotation[boneTransforms.Count];
                    var k = 0;
                    foreach (var kv in boneTransforms)
                    {
                        var q = kv.Value.localRotation;
                        bones[k++] = new SmplBoneRotation
                        {
                            name = kv.Key,
                            rotation = new GeneratedQuat { x = q.x, y = q.y, z = q.z, w = q.w }
                        };
                    }

                    var rq = root.localRotation;
                    smpl.frames[i] = new SmplFrame
                    {
                        trans = new GeneratedVec3 { x = root.localPosition.x, y = root.localPosition.y, z = root.localPosition.z },
                        rootYawDeg = root.localEulerAngles.y,
                        rootRotation = new GeneratedQuat { x = rq.x, y = rq.y, z = rq.z, w = rq.w },
                        bones = bones,
                    };
                }
            }
            finally
            {
                foreach (var kv in poseRestore)
                    kv.Key.localRotation = kv.Value;

                root.localPosition = originalRootPos;
                root.localRotation = originalRootRot;

                if (startedAnimationModeHere && AnimationMode.InAnimationMode())
                    AnimationMode.StopAnimationMode();
            }

            EnsureFolders();
            var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
            smplAssetPath = $"{GeneratedRoot}/{baseName}.smpl.json";
            var absolutePath = Path.GetFullPath(smplAssetPath);
            File.WriteAllText(absolutePath, JsonUtility.ToJson(smpl, true), Encoding.UTF8);
            AssetDatabase.Refresh();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MotionGen] SMPL sidecar export from clip failed: {ex.Message}");
            return false;
        }
    }

    private static SmplBoneMapEntry[] GetDefaultSmplBoneNameMap()
    {
        return new[]
        {
            new SmplBoneMapEntry("pelvis", HumanBodyBones.Hips),
            new SmplBoneMapEntry("spine1", HumanBodyBones.Spine),
            new SmplBoneMapEntry("spine2", HumanBodyBones.Chest),
            new SmplBoneMapEntry("neck", HumanBodyBones.Neck),
            new SmplBoneMapEntry("head", HumanBodyBones.Head),

            new SmplBoneMapEntry("left_hip", HumanBodyBones.LeftUpperLeg),
            new SmplBoneMapEntry("left_knee", HumanBodyBones.LeftLowerLeg),
            new SmplBoneMapEntry("left_ankle", HumanBodyBones.LeftFoot),

            new SmplBoneMapEntry("right_hip", HumanBodyBones.RightUpperLeg),
            new SmplBoneMapEntry("right_knee", HumanBodyBones.RightLowerLeg),
            new SmplBoneMapEntry("right_ankle", HumanBodyBones.RightFoot),

            new SmplBoneMapEntry("left_shoulder", HumanBodyBones.LeftUpperArm),
            new SmplBoneMapEntry("left_elbow", HumanBodyBones.LeftLowerArm),
            new SmplBoneMapEntry("left_wrist", HumanBodyBones.LeftHand),

            new SmplBoneMapEntry("right_shoulder", HumanBodyBones.RightUpperArm),
            new SmplBoneMapEntry("right_elbow", HumanBodyBones.RightLowerArm),
            new SmplBoneMapEntry("right_wrist", HumanBodyBones.RightHand),
        };
    }

    private static Motion.MotionFormat ToGrpcFormat(MotionGenEditorSettings.MotionFormat format)
    {
        return format == MotionGenEditorSettings.MotionFormat.JSON
            ? Motion.MotionFormat.Json
            : Motion.MotionFormat.Bvh;
    }

    private static string GetDefaultGeneratedFileName(Motion.MotionFormat format)
    {
        return format == Motion.MotionFormat.Json
            ? "generated_motion.json"
            : "generated_motion.bvh";
    }

    private bool TryCreateClipFromGeneratedJson(GenerateReply response, string sourceFileName, out AnimationClip clip)
    {
        clip = null;

        try
        {
            var jsonText = Encoding.UTF8.GetString(response.Data.ToByteArray());
            var motion = JsonUtility.FromJson<GeneratedMotionJson>(jsonText);
            if (motion == null || motion.frames == null || motion.frames.Length == 0)
                return false;

            EnsureFolders();

            var baseName = Path.GetFileNameWithoutExtension(sourceFileName);
            var clipPath = $"{GeneratedRoot}/{baseName}.anim";

            clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                clip = new AnimationClip();
                AssetDatabase.CreateAsset(clip, clipPath);
            }

            clip.ClearCurves();

            var fps = motion.fps > 0 ? motion.fps : _settings.fps;
            clip.frameRate = Mathf.Max(1, fps);
            var frameTime = 1f / Mathf.Max(1, fps);
            var rootPath = AnimationUtility.CalculateTransformPath(_selectedAnimator.transform, _selectedAnimator.transform);

            // Negate Z to match the right-hand → left-hand conversion applied to joints.
            SetCurveFromFrames(clip, rootPath, "m_LocalPosition.x", motion.frames, frameTime, f => f.position.x);
            SetCurveFromFrames(clip, rootPath, "m_LocalPosition.y", motion.frames, frameTime, f => f.position.y);
            SetCurveFromFrames(clip, rootPath, "m_LocalPosition.z", motion.frames, frameTime, f => -f.position.z);

            var usedFullJointSolve = TrySetFullBodyRotationCurvesFromJoints(clip, motion, frameTime);
            if (!usedFullJointSolve)
            {
                SetCurveFromFrames(clip, rootPath, "localEulerAnglesRaw.x", motion.frames, frameTime, f => f.rotationEuler.x);
                SetCurveFromFrames(clip, rootPath, "localEulerAnglesRaw.y", motion.frames, frameTime, f => -f.rotationEuler.y);
                SetCurveFromFrames(clip, rootPath, "localEulerAnglesRaw.z", motion.frames, frameTime, f => f.rotationEuler.z);
            }

            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MotionGen] JSON->Clip conversion failed: {ex.Message}");
            return false;
        }
    }

    // Old TryImportBvhAsHumanoid / GetBvhHumanBoneMapping / GetBvhSkeletonBones removed.
    // BVH import is now handled by BvhImporter.ImportAsHumanoid() which parses the BVH
    // in C# and uses AvatarBuilder + HumanPoseHandler to produce a Humanoid AnimationClip.

    private static void SetCurveFromFrames(AnimationClip clip, string path, string propertyName, GeneratedFrame[] frames, float frameTime, Func<GeneratedFrame, float> selector)
    {
        var curve = new AnimationCurve();
        for (int i = 0; i < frames.Length; i++)
        {
            curve.AddKey(new Keyframe(i * frameTime, selector(frames[i])));
        }

        var binding = new EditorCurveBinding
        {
            path = path,
            type = typeof(Transform),
            propertyName = propertyName
        };

        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private static void SetCurveFromFramesSmoothed(AnimationClip clip, string path, string propertyName, GeneratedFrame[] frames, float frameTime, Func<GeneratedFrame, float> selector, int window)
    {
        if (window <= 1)
        {
            SetCurveFromFrames(clip, path, propertyName, frames, frameTime, selector);
            return;
        }

        var values = new float[frames.Length];
        for (int i = 0; i < frames.Length; i++) values[i] = selector(frames[i]);

        var half = Mathf.Max(1, window / 2);
        var smoothed = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            var start = Mathf.Max(0, i - half);
            var end = Mathf.Min(values.Length - 1, i + half);
            float sum = 0f;
            int count = 0;
            for (int k = start; k <= end; k++)
            {
                sum += values[k];
                count++;
            }
            smoothed[i] = sum / Mathf.Max(1, count);
        }

        var curve = new AnimationCurve();
        for (int i = 0; i < smoothed.Length; i++)
            curve.AddKey(new Keyframe(i * frameTime, smoothed[i]));

        var binding = new EditorCurveBinding
        {
            path = path,
            type = typeof(Transform),
            propertyName = propertyName
        };
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private bool TrySetFullBodyRotationCurvesFromJoints(AnimationClip clip, GeneratedMotionJson motion, float frameTime)
    {
        if (_selectedAnimator == null || motion?.frames == null || motion.frames.Length == 0)
            return false;

        if (motion.frames.Any(f => f.joints == null || f.joints.Length < 22))
            return false;

        var maps = GetT2MHumanoidJointMaps();
        var joints = BuildSmoothedUnityJoints(motion.frames);
        var solvedBones = new List<SolvedBone>();

        foreach (var map in maps)
        {
            var bone = _selectedAnimator.GetBoneTransform(map.bone);
            var child = _selectedAnimator.GetBoneTransform(map.childBone);
            if (bone == null || child == null)
                continue;

            var bindDir = (child.position - bone.position);
            if (bindDir.sqrMagnitude < 1e-8f)
                continue;

            solvedBones.Add(new SolvedBone
            {
                map = map,
                transform = bone,
                bindLocalRotation = bone.localRotation,
                bindLocalDirection = bone.InverseTransformDirection(bindDir).normalized,
                directionCorrection = Quaternion.identity,
                path = AnimationUtility.CalculateTransformPath(bone, _selectedAnimator.transform),
                rotX = new AnimationCurve(),
                rotY = new AnimationCurve(),
                rotZ = new AnimationCurve(),
                rotW = new AnimationCurve(),
                hasPrevRotation = false,
                prevLocalRotation = bone.localRotation,
            });
        }

        if (solvedBones.Count == 0)
            return false;

        var useHumanoidCurves = _selectedAnimator.isHuman && _selectedAnimator.avatar != null && _selectedAnimator.avatar.isHuman;
        HumanPoseHandler poseHandler = null;
        HumanPose pose = default;
        AnimationCurve[] muscleCurves = null;

        if (useHumanoidCurves)
        {
            poseHandler = new HumanPoseHandler(_selectedAnimator.avatar, _selectedAnimator.transform);
            muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
            for (int m = 0; m < HumanTrait.MuscleCount; m++)
                muscleCurves[m] = new AnimationCurve();
        }

        var originalRotations = new Dictionary<Transform, Quaternion>();
        foreach (var b in solvedBones)
            if (!originalRotations.ContainsKey(b.transform))
                originalRotations[b.transform] = b.transform.localRotation;

        try
        {
            for (int i = 0; i < motion.frames.Length; i++)
            {
                foreach (var b in solvedBones)
                    b.transform.localRotation = b.bindLocalRotation;

                var time = i * frameTime;

                foreach (var b in solvedBones)
                {
                    var from = joints[i, b.map.jointIndex];
                    var to = joints[i, b.map.childJointIndex];
                    var targetDir = to - from;

                    if (targetDir.sqrMagnitude > 1e-8f)
                    {
                        targetDir.Normalize();
                        targetDir = b.directionCorrection * targetDir;
                        var parentWorld = b.transform.parent != null ? b.transform.parent.rotation : Quaternion.identity;
                        var bindWorldRotAtParent = parentWorld * b.bindLocalRotation;
                        var bindWorldDirAtParent = bindWorldRotAtParent * b.bindLocalDirection;

                        var deltaWorld = Quaternion.FromToRotation(bindWorldDirAtParent, targetDir);
                        var targetWorldRot = deltaWorld * bindWorldRotAtParent;
                        Quaternion targetLocal;

                        if (b.transform.parent != null)
                            targetLocal = Quaternion.Inverse(parentWorld) * targetWorldRot;
                        else
                            targetLocal = targetWorldRot;

                        b.transform.localRotation = targetLocal;
                        b.prevLocalRotation = targetLocal;
                    }

                    var q = b.transform.localRotation;
                    if (b.hasPrevRotation && Quaternion.Dot(b.prevRotation, q) < 0f)
                        q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

                    b.prevRotation = q;
                    b.hasPrevRotation = true;

                    if (!useHumanoidCurves)
                    {
                        b.rotX.AddKey(time, q.x);
                        b.rotY.AddKey(time, q.y);
                        b.rotZ.AddKey(time, q.z);
                        b.rotW.AddKey(time, q.w);
                    }
                }

                if (useHumanoidCurves)
                {
                    poseHandler.GetHumanPose(ref pose);
                    for (int m = 0; m < HumanTrait.MuscleCount; m++)
                    {
                        muscleCurves[m].AddKey(time, pose.muscles[m]);
                    }
                }
            }

            if (useHumanoidCurves)
            {
                for (int m = 0; m < HumanTrait.MuscleCount; m++)
                {
                    SetHumanoidCurve(clip, HumanTrait.MuscleName[m], muscleCurves[m]);
                }
            }
            else
            {
                foreach (var b in solvedBones)
                {
                    SetCurve(clip, b.path, "m_LocalRotation.x", b.rotX);
                    SetCurve(clip, b.path, "m_LocalRotation.y", b.rotY);
                    SetCurve(clip, b.path, "m_LocalRotation.z", b.rotZ);
                    SetCurve(clip, b.path, "m_LocalRotation.w", b.rotW);
                }
            }

            return true;
        }
        finally
        {
            foreach (var kv in originalRotations)
                kv.Key.localRotation = kv.Value;
        }
    }

    private static void SetCurve(AnimationClip clip, string path, string propertyName, AnimationCurve curve)
    {
        var binding = new EditorCurveBinding
        {
            path = path,
            type = typeof(Transform),
            propertyName = propertyName
        };
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private static void SetHumanoidCurve(AnimationClip clip, string musclePropertyName, AnimationCurve curve)
    {
        var binding = new EditorCurveBinding
        {
            path = string.Empty,
            type = typeof(Animator),
            propertyName = musclePropertyName
        };
        AnimationUtility.SetEditorCurve(clip, binding, curve);
    }

    private static T2MJointMap[] GetT2MHumanoidJointMaps()
    {
        return new[]
        {
            new T2MJointMap(HumanBodyBones.Hips, HumanBodyBones.Spine, 0, 3),

            new T2MJointMap(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 1, 4),
            new T2MJointMap(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 4, 7),
            new T2MJointMap(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, 7, 10),

            new T2MJointMap(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, 2, 5),
            new T2MJointMap(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, 5, 8),
            new T2MJointMap(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, 8, 11),

            new T2MJointMap(HumanBodyBones.Spine, HumanBodyBones.Chest, 3, 6),
            new T2MJointMap(HumanBodyBones.Chest, HumanBodyBones.UpperChest, 6, 9),
            new T2MJointMap(HumanBodyBones.UpperChest, HumanBodyBones.Neck, 9, 12),
            new T2MJointMap(HumanBodyBones.Neck, HumanBodyBones.Head, 12, 15),

            // SMPL arm chain: 13=L_collar, 16=L_shoulder, 18=L_elbow, 20=L_wrist
            new T2MJointMap(HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, 9, 13),
            new T2MJointMap(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 16, 18),
            new T2MJointMap(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 18, 20),

            // SMPL arm chain: 14=R_collar, 17=R_shoulder, 19=R_elbow, 21=R_wrist
            new T2MJointMap(HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, 9, 14),
            new T2MJointMap(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 17, 19),
            new T2MJointMap(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 19, 21),
        };
    }

    private static readonly Vector3[] T2MRawOffsets22 =
    {
        new Vector3(0,0,0),
        new Vector3(1,0,0),
        new Vector3(-1,0,0),
        new Vector3(0,1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,1,0),
        new Vector3(0,0,1),
        new Vector3(0,0,1),
        new Vector3(0,1,0),
        new Vector3(1,0,0),
        new Vector3(-1,0,0),
        new Vector3(0,0,1),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
        new Vector3(0,-1,0),
    };

    private static Vector3 GetSourceReferenceDirectionUnity(int jointIndex, int childJointIndex)
    {
        if (childJointIndex < 0 || childJointIndex >= T2MRawOffsets22.Length)
            return Vector3.forward;

        var d = T2MRawOffsets22[childJointIndex];
        return new Vector3(d.x, d.y, -d.z);
    }

    private static Vector3[,] BuildSmoothedUnityJoints(GeneratedFrame[] frames)
    {
        var count = frames.Length;
        var joints = new Vector3[count, 22];

        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < 22; j++)
            {
                var v = frames[i].joints[j];
                // T2M uses a different handedness; convert to Unity-style space.
                joints[i, j] = new Vector3(v.x, v.y, -v.z);
            }
        }

        // Fix: T2M-GPT root joint (0) Y can be unreliable; use average of
        // hip joints (1=R_hip, 2=L_hip) which have correct absolute height.
        for (int i = 0; i < count; i++)
            joints[i, 0] = new Vector3(joints[i, 0].x, (joints[i, 1].y + joints[i, 2].y) / 2f, joints[i, 0].z);

        // No additional temporal smoothing here — the rotation Slerp provides
        // sufficient filtering; extra position smoothing smears contacts.
        return joints;
    }

    private void ApplyTwoBoneIKRefinement(Vector3[,] joints, int frameIndex, HumanBodyBones upperBoneId, HumanBodyBones lowerBoneId, HumanBodyBones endBoneId, int targetJointIndex)
    {
        var upper = _selectedAnimator.GetBoneTransform(upperBoneId);
        var lower = _selectedAnimator.GetBoneTransform(lowerBoneId);
        var end = _selectedAnimator.GetBoneTransform(endBoneId);
        if (upper == null || lower == null || end == null)
            return;

        var target = _selectedAnimator.transform.TransformPoint(joints[frameIndex, targetJointIndex]);

        var curToEnd = end.position - upper.position;
        var tgtToEnd = target - upper.position;
        if (curToEnd.sqrMagnitude > 1e-8f && tgtToEnd.sqrMagnitude > 1e-8f)
        {
            var rot = Quaternion.FromToRotation(curToEnd, tgtToEnd) * upper.rotation;
            upper.rotation = Quaternion.Slerp(upper.rotation, rot, 0.6f);
        }

        curToEnd = end.position - lower.position;
        tgtToEnd = target - lower.position;
        if (curToEnd.sqrMagnitude > 1e-8f && tgtToEnd.sqrMagnitude > 1e-8f)
        {
            var rot = Quaternion.FromToRotation(curToEnd, tgtToEnd) * lower.rotation;
            lower.rotation = Quaternion.Slerp(lower.rotation, rot, 0.7f);
        }
    }

    private void ApplyGeneratedClipNonDestructive(AnimationClip clip)
    {
        if (_selectedAnimator == null || clip == null)
            return;

        EnsureAnimatorControllerAndState(clip);

        var controller = _selectedAnimator.runtimeAnimatorController as AnimatorController;
        if (controller == null)
        {
            var controllerPath = $"{GeneratedRoot}/{_selectedAnimator.name}_MotionGen.controller";
            controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                        ?? AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            _selectedAnimator.runtimeAnimatorController = controller;
        }

        var sm = controller.layers[0].stateMachine;
        var stateName = "MotionGen";
        var state = sm.states.FirstOrDefault(s => s.state != null && s.state.name == stateName).state;
        if (state == null)
            state = sm.AddState(stateName);

        state.motion = clip;
        sm.defaultState = state;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        _activeClip = clip;
        _currentPreviewTime = 0f;
        SafeSelectObject(clip);
        SampleCurrentPose();

        Debug.Log($"[MotionGen] Applied generated clip non-destructively: {clip.name}");
    }

    private void ApplyLastGeneratedClip()
    {
        if (_selectedAnimator == null)
        {
            _generateStatus = "Select a humanoid first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastGeneratedClipAssetPath))
        {
            _generateStatus = "No generated clip available.";
            return;
        }

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(_lastGeneratedClipAssetPath);
        if (clip == null)
        {
            _generateStatus = "Generated clip missing. Regenerate motion.";
            return;
        }

        ApplyGeneratedClipNonDestructive(clip);
        _generateStatus = $"Applied: {clip.name}";
    }

    [Serializable]
    private class GeneratedMotionJson
    {
        public int fps = 30;
        public GeneratedFrame[] frames;
    }

    [Serializable]
    private class GeneratedFrame
    {
        public GeneratedVec3 position;
        public GeneratedVec3 rotationEuler;
        public GeneratedVec3[] joints;
    }

    [Serializable]
    private class GeneratedVec3
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private class SmplMotionJson
    {
        public string schema;
        public string note;
        public bool jointOnly;
        public int fps;
        public SmplFrame[] frames;
    }

    [Serializable]
    private class SmplFrame
    {
        public GeneratedVec3 trans;
        public float rootYawDeg;
        public GeneratedQuat rootRotation;
        public GeneratedVec3[] joints22;
        public SmplBoneRotation[] bones;
    }

    [Serializable]
    private class SmplBoneRotation
    {
        public string name;
        public GeneratedQuat rotation;
    }

    [Serializable]
    private class GeneratedQuat
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }

    private struct SmplBoneMapEntry
    {
        public string name;
        public HumanBodyBones bone;

        public SmplBoneMapEntry(string name, HumanBodyBones bone)
        {
            this.name = name;
            this.bone = bone;
        }
    }

    private struct T2MJointMap
    {
        public HumanBodyBones bone;
        public HumanBodyBones childBone;
        public int jointIndex;
        public int childJointIndex;

        public T2MJointMap(HumanBodyBones bone, HumanBodyBones childBone, int jointIndex, int childJointIndex)
        {
            this.bone = bone;
            this.childBone = childBone;
            this.jointIndex = jointIndex;
            this.childJointIndex = childJointIndex;
        }
    }

    private class SolvedBone
    {
        public T2MJointMap map;
        public Transform transform;
        public Quaternion bindLocalRotation;
        public Vector3 bindLocalDirection;
        public Quaternion directionCorrection;
        public string path;
        public AnimationCurve rotX;
        public AnimationCurve rotY;
        public AnimationCurve rotZ;
        public AnimationCurve rotW;
        public Quaternion prevRotation;
        public bool hasPrevRotation;
        public Quaternion prevLocalRotation;
    }

    private void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/MotionGen"))
            AssetDatabase.CreateFolder("Assets", "MotionGen");
        if (!AssetDatabase.IsValidFolder(GeneratedRoot))
            AssetDatabase.CreateFolder("Assets/MotionGen", "Generated");
    }

    private static void SafeSelectObject(UnityEngine.Object obj)
    {
        if (!ModifyEditorSelection)
            return;

        if (obj == null)
            return;

        EditorApplication.delayCall += () =>
        {
            if (obj == null)
                return;

            try
            {
                Selection.activeObject = obj;
            }
            catch
            {
                // Ignore transient inspector/selection race errors.
            }
        };
    }

    private static void SafeSelectGameObject(GameObject go)
    {
        if (!ModifyEditorSelection)
            return;

        if (go == null)
            return;

        EditorApplication.delayCall += () =>
        {
            if (go == null)
                return;

            try
            {
                Selection.activeGameObject = go;
            }
            catch
            {
                // Ignore transient inspector/selection race errors.
            }
        };
    }
}
#endif