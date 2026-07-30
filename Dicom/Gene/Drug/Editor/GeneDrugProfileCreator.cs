using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

using Dicom.Gene;

namespace Dicom.Gene.EditorTools
{
    // 一键生成默认药物库资产:菜单 Assets/Dicom/创建基因药物库(示例)
    // 示例药覆盖三类作用:整体增强、整体抑制、靶基因增强,数值可在 Inspector 直接调
    public static class GeneDrugProfileCreator
    {
        public const string AssetDir = "Assets/!!Workspace/_Workspace/Script/Dicom/Data/Gene";
        public const string AssetName = "GeneDrugProfile.asset";

        [MenuItem("Assets/Dicom/创建基因药物库(示例)", false, 20)]
        public static void CreateAsset()
        {
            var profile = CreateDefaultProfile();
            EnsureDir(AssetDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(AssetDir + "/" + AssetName);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = profile;
            EditorGUIUtility.PingObject(profile);
            Debug.Log($"基因药物库已创建: {path}");
        }

        // 项目内已有则取第一个,否则新建一个默认库(供 GeneModuleFactory 自动绑定)
        public static GeneDrugProfile FindOrCreate()
        {
            var guids = AssetDatabase.FindAssets("t:" + nameof(GeneDrugProfile));
            if (guids.Length > 0)
            {
                string existing = AssetDatabase.GUIDToAssetPath(guids[0]);
                return AssetDatabase.LoadAssetAtPath<GeneDrugProfile>(existing);
            }

            var profile = CreateDefaultProfile();
            EnsureDir(AssetDir);
            string path = AssetDatabase.GenerateUniqueAssetPath(AssetDir + "/" + AssetName);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }

        static GeneDrugProfile CreateDefaultProfile()
        {
            var profile = ScriptableObject.CreateInstance<GeneDrugProfile>();
            profile.SetDrugs(new List<GeneDrugDefinition>
            {
                new GeneDrugDefinition
                {
                    Name = "促表达剂 A",
                    Description = "整体上调表达强度,满剂量约 2 倍",
                    MaxDose = 1f, DefaultDose = 1f,
                    GlobalScale = 2f, Bias = 0f, Hill = 1f
                },
                new GeneDrugDefinition
                {
                    Name = "抑制剂 B",
                    Description = "整体下调表达强度,满剂量约 0.35 倍",
                    MaxDose = 1f, DefaultDose = 1f,
                    GlobalScale = 0.35f, Bias = 0f, Hill = 1f
                },
                new GeneDrugDefinition
                {
                    Name = "阻断剂 C",
                    Description = "整体压低并把弱表达清零(阈值型)",
                    MaxDose = 1f, DefaultDose = 1f,
                    GlobalScale = 0.6f, Bias = -0.15f, Hill = 1.5f
                }
            });
            return profile;
        }

        static void EnsureDir(string dir)
        {
            if (AssetDatabase.IsValidFolder(dir)) return;
            string[] parts = dir.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
