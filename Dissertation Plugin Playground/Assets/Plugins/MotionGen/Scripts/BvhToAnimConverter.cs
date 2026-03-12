#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BvhToAnimConverter
{
    public static bool TryConvertBvhAssetToAnim(string bvhAssetPath, out AnimationClip clip, out string clipAssetPath)
    {
        clip = null;
        clipAssetPath = null;

        if (string.IsNullOrWhiteSpace(bvhAssetPath) || !bvhAssetPath.EndsWith(".bvh", System.StringComparison.OrdinalIgnoreCase))
            return false;

        var absolutePath = Path.GetFullPath(bvhAssetPath);
        if (!File.Exists(absolutePath))
            return false;

        var bvhText = File.ReadAllText(absolutePath);
        return TryConvertBvhTextToAnim(bvhText, bvhAssetPath, out clip, out clipAssetPath);
    }

    public static bool TryConvertBvhTextToAnim(string bvhText, string sourceBvhAssetPath, out AnimationClip clip, out string clipAssetPath)
    {
        clip = null;
        clipAssetPath = null;

        if (string.IsNullOrWhiteSpace(bvhText) || string.IsNullOrWhiteSpace(sourceBvhAssetPath))
            return false;

        (AnimationClip importedClip, Avatar avatar) = MotionGen.BvhImporter.ImportAsHumanoid(bvhText);
        if (importedClip == null)
            return false;

        var basePath = Path.Combine(Path.GetDirectoryName(sourceBvhAssetPath) ?? "Assets", Path.GetFileNameWithoutExtension(sourceBvhAssetPath));
        clipAssetPath = basePath + ".anim";
        clip = SaveOrReplaceAnimationClip(importedClip, clipAssetPath);

        if (avatar != null)
            Object.DestroyImmediate(avatar);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return clip != null;
    }

    [MenuItem("MotionGen/Convert Selected BVH to Anim")]
    public static void ConvertSelectedBvhToAnim()
    {
        var selected = Selection.activeObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog("Convert BVH", "Select a .bvh asset in the Project window first.", "OK");
            return;
        }

        var assetPath = AssetDatabase.GetAssetPath(selected);
        if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".bvh", System.StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog("Convert BVH", "The selected asset is not a .bvh file.", "OK");
            return;
        }

        var absolutePath = Path.GetFullPath(assetPath);
        if (!File.Exists(absolutePath))
        {
            EditorUtility.DisplayDialog("Convert BVH", $"Could not find file:\n{absolutePath}", "OK");
            return;
        }

        if (!TryConvertBvhAssetToAnim(assetPath, out var clip, out var clipAssetPath))
        {
            EditorUtility.DisplayDialog("Convert BVH", "BVH import failed. Check the Console for details.", "OK");
            return;
        }

        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(clipAssetPath));

        Debug.Log($"[MotionGen] Converted BVH to AnimationClip: {clipAssetPath}");
    }

    private static AnimationClip SaveOrReplaceAnimationClip(AnimationClip importedClip, string assetPath)
    {
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(importedClip, existing);
            Object.DestroyImmediate(importedClip);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        AssetDatabase.CreateAsset(importedClip, assetPath);
        return importedClip;
    }

    [MenuItem("MotionGen/Convert Selected BVH to Anim", true)]
    private static bool ValidateConvertSelectedBvhToAnim()
    {
        var selected = Selection.activeObject;
        if (selected == null)
            return false;

        var assetPath = AssetDatabase.GetAssetPath(selected);
        return !string.IsNullOrWhiteSpace(assetPath)
            && assetPath.EndsWith(".bvh", System.StringComparison.OrdinalIgnoreCase);
    }
}
#endif
