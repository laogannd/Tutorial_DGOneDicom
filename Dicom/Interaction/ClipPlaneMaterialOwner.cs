using System.Collections.Generic;
using UnityEngine;

namespace Dicom.Interaction
{
    // 持有裁切平面手柄运行时创建的材质，手柄销毁时统一 Destroy
    // sharedMaterial 不随 GameObject 自动回收，无此清理会在反复开关裁切平面时泄漏显存
    public class ClipPlaneMaterialOwner : MonoBehaviour
    {
        readonly List<Material> _materials = new List<Material>(2);

        // 登记一份运行时材质，返回原对象便于链式赋值;null 直接忽略
        public Material Register(Material material)
        {
            if (material != null) _materials.Add(material);
            return material;
        }

        void OnDestroy()
        {
            for (int i = 0; i < _materials.Count; i++)
                if (_materials[i] != null) Destroy(_materials[i]);
            _materials.Clear();
        }
    }
}
