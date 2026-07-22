using UnityEngine;
using UnityEngine.UI;

namespace Dicom.UI
{
    // 统一超级面板的顶部标签页切换:DICOM / 基因表达 两页容器互斥显示
    // 标签按钮走 UIPokeBridge 手指戳与射线点击;选中页按钮高亮,另一页变暗
    // 引用由 UnifiedPanelFactory 编辑器工厂绑定
    [AddComponentMenu("Dicom/Unified Panel Tabs")]
    public class UnifiedPanelTabs : MonoBehaviour
    {
        [SerializeField] Button _dicomTabButton;
        [SerializeField] Button _geneTabButton;
        [SerializeField] GameObject _dicomPage;
        [SerializeField] GameObject _genePage;
        [SerializeField] Image _dicomTabImage;
        [SerializeField] Image _geneTabImage;

        // 选中/未选中标签底色
        [SerializeField] Color _activeColor = new Color(0.18f, 0.78f, 0.85f, 1f);
        [SerializeField] Color _inactiveColor = new Color(0.16f, 0.20f, 0.24f, 1f);

        void Start()
        {
            // 幂等订阅:项目禁用 Domain Reload,先移再加防跨 Play 会话重复叠加
            if (_dicomTabButton != null)
            {
                _dicomTabButton.onClick.RemoveListener(ShowDicom);
                _dicomTabButton.onClick.AddListener(ShowDicom);
            }
            if (_geneTabButton != null)
            {
                _geneTabButton.onClick.RemoveListener(ShowGene);
                _geneTabButton.onClick.AddListener(ShowGene);
            }
            // 默认展示 DICOM 页
            ShowDicom();
        }

        public void ShowDicom() => SetPage(true);
        public void ShowGene() => SetPage(false);

        void SetPage(bool dicom)
        {
            if (_dicomPage != null) _dicomPage.SetActive(dicom);
            if (_genePage != null) _genePage.SetActive(!dicom);
            if (_dicomTabImage != null) _dicomTabImage.color = dicom ? _activeColor : _inactiveColor;
            if (_geneTabImage != null) _geneTabImage.color = dicom ? _inactiveColor : _activeColor;
        }

        // 工厂构建时回填引用
        public void Bind(Button dicomTab, Button geneTab, GameObject dicomPage, GameObject genePage)
        {
            _dicomTabButton = dicomTab;
            _geneTabButton = geneTab;
            _dicomPage = dicomPage;
            _genePage = genePage;
            _dicomTabImage = dicomTab != null ? dicomTab.GetComponent<Image>() : null;
            _geneTabImage = geneTab != null ? geneTab.GetComponent<Image>() : null;
        }
    }
}
