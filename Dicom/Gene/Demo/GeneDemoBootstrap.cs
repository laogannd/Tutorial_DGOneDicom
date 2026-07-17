using System.IO;
using UnityEngine;

using Dicom.Core;
using Dicom.Demo;
using Dicom.Interaction;
using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 基因系统示例引导:创建点云与交互组件,数据从 persistentDataPath/<子目录> 加载
    // 组件创建(Setup)与数据加载(LoadDefault)分离,支持统一面板切到基因标签才延迟加载
    // 仿 DicomDemoBootstrap,便于 Play Mode 验证与 Pico 上 adb push 测试数据
    public class GeneDemoBootstrap : MonoBehaviour
    {
        [SerializeField] string _relativeDir = "gene";
        [SerializeField] Material _pointMaterial;
        [SerializeField] DicomLutProfile _lutProfile;
        // 区域空间文本字体(项目中文字体);空则 GeneBrushVisual 运行时自动查找含中文字形的字体
        [SerializeField] TMPro.TMP_FontAsset _regionLabelFont;
        // tag->区域名映射,传给调试面板(可空,回退 "区域{tag}")
        [SerializeField] GeneTagNameTable _tagNameTable;
        [SerializeField] LayerMask _excludeLayers;
        // 组件创建完是否立即加载数据;统一面板模式下设 false,由面板切标签触发
        [SerializeField] bool _autoLoadOnStart = false;
        // 加载完成后自动选中的默认基因(空则不自动选,由面板选)
        [SerializeField] string _defaultGene = "";
        // 未画取区域(灰色幽灵底图)淡显不透明度(0=近全透,1=不透明);点云密集叠加会累积,取低值才真透
        [SerializeField, Range(0f, 1f)] float _selectionFadeAlpha = 0.12f;
        [SerializeField] bool _attachDebugPanel = true;

        GeneColorController _controller;
        bool _componentsReady;

        public GeneColorController Controller => _controller;

        void Start()
        {
            Setup();
            if (_autoLoadOnStart) LoadDefault();
        }

        // 创建点云与交互组件并绑定统一面板;只建一次,不加载数据
        public void Setup()
        {
            if (_componentsReady) return;
            _componentsReady = true;

            var go = new GameObject("GenePointCloud");
            go.transform.SetParent(transform, false);

            var pc = go.AddComponent<DicomPointCloud>();
            SetMaterial(pc);

            _controller = go.AddComponent<GeneColorController>();
            if (_lutProfile != null) _controller.SetLutProfile(_lutProfile);
            _controller.SelectionFade = _selectionFadeAlpha;

            // 先挂 GrabbableSetup(Awake 建刚体/碰撞体/Grabbable),再挂 ModelTransform
            var grabbableSetup = go.AddComponent<GeneGrabbableSetup>();
            grabbableSetup.ExcludeLayers = _excludeLayers;
            var modelTransform = go.AddComponent<GeneModelTransform>();
            // 远程射线操控:自治组件,Awake 自动发现场景内的 DicomRayPointer(与 DICOM 同源),认接口故支持基因物体
            go.AddComponent<DicomRayManipulator>();
            // 画笔选择器 + 可视化(mode2);默认不启用,由面板开关
            var brush = go.AddComponent<GeneBrushSelector>();
            brush.SetTagNameTable(_tagNameTable);
            var brushVisual = go.AddComponent<GeneBrushVisual>();
            // 注入中文字体须在 EnsureLabel 建文本前(此处组件刚加,Awake 未建文本),保证区域名不空白
            if (_regionLabelFont != null) brushVisual.SetFont(_regionLabelFont);
            // 覆盖率信标:每区域一个信标球标记已画/未画,消除"只见已画不知漏哪"
            var beacons = go.AddComponent<GeneCoverageBeacons>();
            if (_regionLabelFont != null) beacons.SetFont(_regionLabelFont);

            if (!string.IsNullOrEmpty(_defaultGene))
                _controller.OnLoaded += _ => _controller.SelectGene(_defaultGene);

            _controller.OnError += e => Debug.LogError($"基因数据加载错误: {e.Message}");

            // 接入统一面板:优先绑定场景已有面板,否则新建一个
            if (_attachDebugPanel)
            {
                var panel = FindObjectOfType<UnifiedDebugPanel>();
                if (panel == null) panel = gameObject.AddComponent<UnifiedDebugPanel>();
                panel.BindGene(this, _controller, modelTransform, brush, _tagNameTable);
            }
        }

        // 从 persistentDataPath/<_relativeDir> 加载数据;组件未建则先建
        public void LoadDefault()
        {
            Setup();
            string dir = Path.Combine(Application.persistentDataPath, _relativeDir);
            _controller.Load(dir);
        }

        void SetMaterial(DicomPointCloud pc)
        {
            if (_pointMaterial == null) return;
            var field = typeof(DicomPointCloud).GetField("_material",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) field.SetValue(pc, _pointMaterial);
        }
    }
}
