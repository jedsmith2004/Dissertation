#if UNITY_EDITOR
using UnityEngine;

namespace MotionGen
{
    internal static class CanonicalSourceBasisProfile
    {
        public static Quaternion GetEffectiveSourceBindLocalRotation(int sourceIndex, Quaternion inferredLocalRotation, global::MotionGenEditorSettings settings)
        {
            if (settings == null || settings.canonicalSourceBasisMode != global::MotionGenEditorSettings.CanonicalSourceBasisMode.ManualOverrides)
                return inferredLocalRotation;

            if (!TryMapSourceIndexToHumanBodyBone(sourceIndex, out var bone))
                return inferredLocalRotation;

            if (!settings.TryGetSourceBasisOverride(bone, out var localCorrection))
                return inferredLocalRotation;

            return localCorrection * inferredLocalRotation;
        }

        public static Quaternion[] BuildEffectiveSourceBindLocalRotations(Quaternion[] inferredLocalRotations, global::MotionGenEditorSettings settings)
        {
            if (inferredLocalRotations == null)
                return null;

            var effective = new Quaternion[inferredLocalRotations.Length];
            for (int i = 0; i < inferredLocalRotations.Length; i++)
                effective[i] = GetEffectiveSourceBindLocalRotation(i, inferredLocalRotations[i], settings);

            return effective;
        }

        private static bool TryMapSourceIndexToHumanBodyBone(int sourceIndex, out HumanBodyBones bone)
        {
            switch (sourceIndex)
            {
                case 0: bone = HumanBodyBones.Hips; return true;
                case 1: bone = HumanBodyBones.LeftUpperLeg; return true;
                case 2: bone = HumanBodyBones.RightUpperLeg; return true;
                case 3: bone = HumanBodyBones.Spine; return true;
                case 4: bone = HumanBodyBones.LeftLowerLeg; return true;
                case 5: bone = HumanBodyBones.RightLowerLeg; return true;
                case 6: bone = HumanBodyBones.Chest; return true;
                case 7: bone = HumanBodyBones.LeftFoot; return true;
                case 8: bone = HumanBodyBones.RightFoot; return true;
                case 9: bone = HumanBodyBones.UpperChest; return true;
                case 12: bone = HumanBodyBones.Neck; return true;
                case 13: bone = HumanBodyBones.LeftShoulder; return true;
                case 14: bone = HumanBodyBones.RightShoulder; return true;
                case 15: bone = HumanBodyBones.Head; return true;
                case 16: bone = HumanBodyBones.LeftUpperArm; return true;
                case 17: bone = HumanBodyBones.RightUpperArm; return true;
                case 18: bone = HumanBodyBones.LeftLowerArm; return true;
                case 19: bone = HumanBodyBones.RightLowerArm; return true;
                default:
                    bone = HumanBodyBones.LastBone;
                    return false;
            }
        }
    }
}
#endif
