using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Dicom.UI
{
    // 可折叠分区:点击 header 展开/收起 body,减少长列表滚动误触
    // header 实现 IPointerClickHandler,配合 UIPokeBridge 手指戳触发;射线点击同样可用
    [AddComponentMenu("Dicom/Collapsible Section")]
    public class CollapsibleSection : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] GameObject _body;
        [SerializeField] TextMeshProUGUI _arrowLabel;
        [SerializeField] bool _expanded = true;

        // 折叠状态下顶层布局需重算:header→card→滚动content,重建到 content 让整列表收缩
        RectTransform _contentRoot;

        void Start()
        {
            // transform=header, parent=card;向上找最外层受布局约束的容器
            // 普通面板:card.parent=滚动 content;统一面板多一层页容器,故不能只取 card.parent
            _contentRoot = ResolveLayoutRoot(transform.parent);
            ApplyState();
        }

        // 从 card 起沿父链向上,取最外层仍带 LayoutGroup/ContentSizeFitter 的 RectTransform
        // 折叠后强制重建它,保证嵌套页容器与滚动 content 高度一并收缩
        static RectTransform ResolveLayoutRoot(Transform card)
        {
            RectTransform root = card as RectTransform;
            var t = card;
            while (t != null)
            {
                if (t is RectTransform rt &&
                    (rt.GetComponent<LayoutGroup>() != null || rt.GetComponent<ContentSizeFitter>() != null))
                    root = rt;
                t = t.parent;
            }
            return root;
        }

        public void OnPointerClick(PointerEventData eventData) => Toggle();

        public void Toggle()
        {
            _expanded = !_expanded;
            ApplyState();
        }

        public void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            ApplyState();
        }

        void ApplyState()
        {
            if (_body != null) _body.SetActive(_expanded);
            if (_arrowLabel != null) _arrowLabel.text = _expanded ? "▼" : "▶";

            // 折叠后内容高度变化,强制重建竖直布局让滚动区收缩
            if (_contentRoot != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_contentRoot);
        }

        // 工厂构建时回填引用
        public void Bind(GameObject body, TextMeshProUGUI arrowLabel, bool expanded)
        {
            _body = body;
            _arrowLabel = arrowLabel;
            _expanded = expanded;
        }
    }
}
