using UnityEditor;
using UnityEngine;

using Dicom.Core;

namespace Dicom.UI.EditorTools
{
    // 一键生成带示例断点(-700~700 步长 200 的 8 色)的断点显色配置资产，省去手填断点
    // 菜单 Assets/Dicom/创建示例断点配置
    public static class DicomBreakpointProfileCreator
    {
        const string ProfileDir = "Assets/!!Workspace/_Workspace/Script/Dicom/Profiles";

        [MenuItem("Assets/Dicom/创建示例断点配置", false, 21)]
        public static void CreateExample()
        {
            EnsureDir(ProfileDir);
            var profile = ScriptableObject.CreateInstance<DicomBreakpointProfile>();
            profile.ResetToExampleStops();

            string path = AssetDatabase.GenerateUniqueAssetPath(ProfileDir + "/DicomBreakpointProfile.asset");
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            Debug.Log($"示例断点配置已创建: {path}");
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
