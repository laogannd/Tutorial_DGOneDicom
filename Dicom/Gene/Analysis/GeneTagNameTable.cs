using System.Collections.Generic;
using UnityEngine;

namespace Dicom.Gene
{
    // tag 数值 -> 人类可读区域名映射表;数据无区域名称字段时用此补充
    // 未配置或未命中的 tag 由调用方回退为 "区域{tag}"
    [CreateAssetMenu(fileName = "GeneTagNameTable", menuName = "Dicom/Gene Tag Name Table")]
    public class GeneTagNameTable : ScriptableObject
    {
        [System.Serializable]
        public struct Entry
        {
            public int Tag;
            public string Name;
        }

        [SerializeField] List<Entry> _entries = new List<Entry>();

        Dictionary<int, string> _lookup;

        // 查 tag 对应名,未命中返回 null(调用方回退)
        public string GetName(int tag)
        {
            if (_lookup == null)
            {
                _lookup = new Dictionary<int, string>(_entries.Count);
                foreach (var e in _entries)
                    _lookup[e.Tag] = e.Name;
            }
            return _lookup.TryGetValue(tag, out var name) ? name : null;
        }
    }
}
