using UnityEngine;
using UnityEngine.Rendering;

using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 画笔可视化:半透明球/盒预览体跟手(纯 transform 更新,零重建) + 选中 overlay 高亮点云
    // overlay 是第二个 DicomPointCloud,挂在基因点云子物体(identity 局部变换),故与主点云同一 local->world
    // overlay 仅在选中集变化时重建一次(涂抹已节流,频率低),不每帧重建 136k 点
    [RequireComponent(typeof(GeneBrushSelector))]
    public class GeneBrushVisual : MonoBehaviour
    {
        [SerializeField] Color _brushColor = new Color(0.2f, 0.85f, 1f, 0.25f);
        // overlay 高亮点用 colormap 顶端强度显示(1=最亮),点比主点云略大更醒目
        [SerializeField] float _overlayPointSize = 0.004f;

        GeneBrushSelector _brush;
        GeneColorController _controller;
        DicomPointCloud _mainCloud;

        Transform _spherePreview;
        Transform _boxPreview;
        Material _previewMaterial;

        DicomPointCloud _overlay;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _brush = GetComponent<GeneBrushSelector>();
            _controller = GetComponent<GeneColorController>();
            _mainCloud = GetComponent<DicomPointCloud>();
            _brush.OnSelectionChanged += OnSelectionChanged;
        }

        void OnDestroy()
        {
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
            if (_previewMaterial != null) Destroy(_previewMaterial);
        }

        // 选中集变化后重建 overlay 高亮(Job 已节流,此处频率低)
        // overlay 仅作涂抹期实时反馈;画笔关闭后由 Update 清空,避免恒定强度盖住区域表达显色
        void OnSelectionChanged(int count)
        {
            if (!_brush.BrushEnabled) return;
            EnsureOverlay();
            _controller.BuildOverlay(_overlay, 1f);
            // 显色态改走每实例 property block,overlay 需复制主点云显色态才能用同一 colormap 高亮
            _controller.ApplyColorState(_overlay);
        }

        void Update()
        {
            bool active = _brush.BrushEnabled;
            if (!active)
            {
                SetPreviewActive(_spherePreview, false);
                SetPreviewActive(_boxPreview, false);
                // 画笔关闭:清空 overlay 让主点云区域表达显色可见
                if (_overlay != null && _overlay.PointCount > 0)
                    _overlay.SetPoints(default, 0);
                return;
            }

            EnsurePreviews();

            if (_brush.Mode == GeneBrushSelector.BrushMode.Sphere)
            {
                SetPreviewActive(_boxPreview, false);
                bool show = _brush.HasActivePalm;
                SetPreviewActive(_spherePreview, show);
                if (show)
                {
                    _spherePreview.position = _brush.ActivePalmWorld;
                    // 预览体直径 = 2*世界半径;球 primitive 原始直径 1
                    _spherePreview.localScale = Vector3.one * (_brush.WorldRadius * 2f);
                }
            }
            else
            {
                SetPreviewActive(_spherePreview, false);
                bool show = _brush.BoxDragging;
                SetPreviewActive(_boxPreview, show);
                if (show)
                {
                    _boxPreview.position = _brush.BoxCenterWorld;
                    _boxPreview.rotation = Quaternion.identity;
                    _boxPreview.localScale = _brush.BoxSizeWorld;
                }
            }
        }

        // overlay 点云挂在子物体,identity 局部变换 -> 与主点云同一 local->world,cell local 坐标直接对齐
        void EnsureOverlay()
        {
            if (_overlay != null) return;

            var go = new GameObject("GeneSelectionOverlay");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            _overlay = go.AddComponent<DicomPointCloud>();
            // 复用主点云材质;显色态由 ApplyColorState 复制到 overlay 实例 property block
            // overlay 恒定强度=1 显示为 colormap 顶端色
            if (_mainCloud != null && _mainCloud.Material != null)
                _overlay.SetMaterial(_mainCloud.Material);
            _overlay.SetPointSize(_overlayPointSize);
        }

        void EnsurePreviews()
        {
            if (_previewMaterial == null) _previewMaterial = CreateMaterial(_brushColor);

            if (_spherePreview == null)
                _spherePreview = CreatePreview(PrimitiveType.Sphere, "BrushSpherePreview");
            if (_boxPreview == null)
                _boxPreview = CreatePreview(PrimitiveType.Cube, "BrushBoxPreview");
        }

        // 预览体是世界空间独立物体(不随点云缩放),移除碰撞体避免干扰抓取检测
        Transform CreatePreview(PrimitiveType type, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.GetComponent<MeshRenderer>().sharedMaterial = _previewMaterial;
            go.SetActive(false);
            return go.transform;
        }

        void SetPreviewActive(Transform t, bool active)
        {
            if (t != null && t.gameObject.activeSelf != active)
                t.gameObject.SetActive(active);
        }

        // URP Unlit 半透明材质,内置管线回退;参考 DicomBoundingBoxVisualizer
        static Material CreateMaterial(Color color)
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor(_BaseColorId, color);
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent;
                return mat;
            }

            var sprite = Shader.Find("Sprites/Default");
            if (sprite != null) return new Material(sprite) { color = color };
            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null) return new Material(unlit) { color = color };

            Debug.LogWarning("画笔预览所需 shader 均被剥离(建议加入 Always Included Shaders)");
            return null;
        }
    }
}
