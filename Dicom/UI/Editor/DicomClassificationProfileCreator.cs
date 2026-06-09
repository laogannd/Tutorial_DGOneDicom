using UnityEditor;
using UnityEngine;

using Dicom.Core;

namespace Dicom.UI.EditorTools
{
    // 一键生成带 CT 默认区间的分类配置资产，省去手填六类 HU 区间
    // 菜单 Assets/Dicom/创建 CT 默认分类配置
    public static class DicomClassificationProfileCreator
    {
        const string ProfileDir = "Assets/!!Workspace/_Workspace/Script/Dicom/Profiles";

        [MenuItem("Assets/Dicom/创建 CT 默认分类配置", false, 20)]
        public static void CreateCtDefault()
        {
            EnsureDir(ProfileDir);
            var profile = ScriptableObject.CreateInstance<DicomClassificationProfile>();
            profile.ResetToCtDefaults();

            string path = AssetDatabase.GenerateUniqueAssetPath(ProfileDir + "/CtClassificationProfile.asset");
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            Debug.Log($"CT 默认分类配置已创建: {path}");
        }

        static void EnsureDir(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(dir);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureDir(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
