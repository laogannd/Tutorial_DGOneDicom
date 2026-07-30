using System.Collections.Generic;
using UnityEngine;

namespace Dicom.Gene
{
    // 药物库:一组可选药物定义,面板据此列按钮
    // 共享资产,GeneDrugController 只读不写,故不需要运行时实例化副本
    [CreateAssetMenu(menuName = "Dicom/Gene Drug Profile", fileName = "GeneDrugProfile")]
    public class GeneDrugProfile : ScriptableObject
    {
        [SerializeField] List<GeneDrugDefinition> _drugs = new List<GeneDrugDefinition>();

        public int Count => _drugs.Count;

        public GeneDrugDefinition Get(int index)
        {
            if (index < 0 || index >= _drugs.Count) return null;
            return _drugs[index];
        }

        public string GetName(int index)
        {
            var d = Get(index);
            return d != null ? d.Name : "";
        }

        // 编辑器工具建默认库时写入
        public void SetDrugs(List<GeneDrugDefinition> drugs) => _drugs = drugs;
    }
}
