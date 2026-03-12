#if UNITY_EDITOR
using System.IO;
using MotionGen;
using UnityEditor;
using UnityEngine;

public static class BvhToAnimConverter
{
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

        var bvhText = File.ReadAllText(absolutePath);
        (AnimationClip clip, Avatar avatar) = BvhImporter.ImportAsHumanoid(bvhText);
        if (clip == null)
        {
            EditorUtility.DisplayDialog("Convert BVH", "BVH import failed. Check the Console for details.", "OK");
            return;
        }

        var basePath = Path.Combine(Path.GetDirectoryName(assetPath) ?? "Assets", Path.GetFileNameWithoutExtension(assetPath));
        var clipAssetPath = AssetDatabase.GenerateUniqueAssetPath(basePath + ".anim");
        AssetDatabase.CreateAsset(clip, clipAssetPath);

        if (avatar != null)
        {
            var avatarAssetPath = AssetDatabase.GenerateUniqueAssetPath(basePath + "_Avatar.asset");
            AssetDatabase.CreateAsset(avatar, avatarAssetPath);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(clipAssetPath));

        Debug.Log($"[MotionGen] Converted BVH to AnimationClip: {clipAssetPath}");
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
