using UnityEditor;
using UnityEngine;

using Dicom.Core;

namespace Dicom.Gene.EditorTools
{
    // 一键在场景生成基因模块加载对象:补上基因数据加载入口(此前 GeneDemoBootstrap 从未进场景导致数据不加载)
    // 菜单 GameObject/Dicom/创建基因模块(数据加载);自动赋值点云材质与 LUT,运行时从 persistentDataPath/gene 读数据
    public static class GeneModuleFactory
    {
        // 复用现有点云材质与 LUT 配置(GUID 固定,随资源移动仍可解析)
        const string PointMaterialGuid = "ae3bf3648f1f49b4c9753c50237223ce";
        const string LutProfileGuid = "aee5976504c223e4da3c5d59dc9466ec";

        [MenuItem("GameObject/Dicom/创建基因模块(数据加载)", false, 14)]
        public static void CreateGeneModule()
        {
            var go = new GameObject("GeneModule");
            var bootstrap = go.AddComponent<GeneDemoBootstrap>();

            var so = new SerializedObject(bootstrap);
            SetObject(so, "_pointMaterial", LoadByGuid<Material>(PointMaterialGuid));
            SetObject(so, "_lutProfile", LoadByGuid<DicomLutProfile>(LutProfileGuid));

            // 项目内若已有 tag->区域名映射资源,自动绑上(无则留空,面板回退 "区域{tag}")
            var tagTable = FindFirstAsset<GeneTagNameTable>();
            if (tagTable != null) SetObject(so, "_tagNameTable", tagTable);

            so.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "创建基因模块");
            EditorGUIUtility.PingObject(go);
            Debug.Log("已创建基因模块 GeneModule。运行后切到统一面板“基因”标签即从 persistentDataPath/gene 加载数据。记得保存场景。");
        }

        static void SetObject(SerializedObject so, string field, Object value)
        {
            var prop = so.FindProperty(field);
            if (prop != null) prop.objectReferenceValue = value;
        }

        static T LoadByGuid<T>(string guid) where T : Object
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        static T FindFirstAsset<T>() where T : Object
        {
            var guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
            if (guids.Length == 0) return null;
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }
    }
}
