using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Dicom.UI
{
    // 整面板折叠:点标题条折叠按钮把大面板收成一条标题条,再点展开
    // 折叠时隐藏 body(标签栏+滚动区+滚动条),面板根高度收到标题条高,抓取碰撞盒同步收缩
    // 跟随由 VRHudFollower 持续居中,收起后小标题条仍跟随视野,随手展开
    // 引用由 UnifiedPanelFactory 编辑器工厂绑定
    [AddComponentMenu("Dicom/Unified Panel Collapser")]
    public class UnifiedPanelCollapser : MonoBehaviour
    {
        [SerializeField] RectTransform _panelRoot;
        [SerializeField] GameObject[] _bodyObjects;
        [SerializeField] TextMeshProUGUI _arrowLabel;
        [SerializeField] DicomPanelGrabHandle _grabHandle;
        [SerializeField] Button _collapseButton;
        [SerializeField] VRHudFollower _follower;

        [SerializeField] float _fullHeight = 1000f;
        [SerializeField] float _titleBarHeight = 90f;
        [SerializeField] bool _expanded = true;

        // 运行时自绑按钮:编辑器期加的 onClick 监听器不序列化,存预制体/重进 Play 会丢失
        // 幂等订阅:项目禁用 Domain/Scene Reload,先移再加防跨 Play 会话重复叠加
        void Start()
        {
            if (_collapseButton != null)
            {
                _collapseButton.onClick.RemoveListener(Toggle);
                _collapseButton.onClick.AddListener(Toggle);
            }
            // 已存在于场景的旧面板未回填 follower(新增序列化字段为空),按面板根同居组件兜底取
            if (_follower == null) _follower = GetComponent<VRHudFollower>();
            ApplyState();
        }

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
            // body 整体显隐:标签栏/滚动区/滚动条
            if (_bodyObjects != null)
                for (int i = 0; i < _bodyObjects.Length; i++)
                    if (_bodyObjects[i] != null) _bodyObjects[i].SetActive(_expanded);

            // 面板根高度:展开=全高,折叠=标题条高;pivot 居中,收缩后小标题条随跟随居中于视野
            if (_panelRoot != null)
            {
                float h = _expanded ? _fullHeight : _titleBarHeight;
                var size = _panelRoot.sizeDelta;
                size.y = h;
                _panelRoot.sizeDelta = size;
            }

            // 抓取碰撞盒同步:标题条在根内的中心位置随高度变化,折叠时归到根中心;宽度不变
            if (_grabHandle != null && _panelRoot != null)
            {
                float h = _expanded ? _fullHeight : _titleBarHeight;
                float centerY = h * 0.5f - _titleBarHeight * 0.5f;
                _grabHandle.ApplyCollider(
                    new Vector3(_panelRoot.sizeDelta.x, _titleBarHeight, 20f),
                    new Vector3(0f, centerY, 0f));
            }

            if (_arrowLabel != null) _arrowLabel.text = _expanded ? "▲" : "▼";

            // 通知跟随组件:折叠时把标题条沿相机上方向收出视野中心,不挡视野
            if (_follower != null) _follower.SetCollapsed(!_expanded);
        }

        // 工厂构建时回填引用;按钮由本组件在运行时 Start 自绑,不在编辑器期加监听器
        public void Bind(RectTransform panelRoot, GameObject[] bodyObjects, TextMeshProUGUI arrowLabel,
            DicomPanelGrabHandle grabHandle, Button collapseButton, VRHudFollower follower,
            float fullHeight, float titleBarHeight, bool expanded)
        {
            _panelRoot = panelRoot;
            _bodyObjects = bodyObjects;
            _arrowLabel = arrowLabel;
            _grabHandle = grabHandle;
            _collapseButton = collapseButton;
            _follower = follower;
            _fullHeight = fullHeight;
            _titleBarHeight = titleBarHeight;
            _expanded = expanded;
        }
    }
}
