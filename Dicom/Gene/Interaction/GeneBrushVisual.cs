using TMPro;
using UnityEngine;

using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 笔刷可视化:
    // 1) 半透明球体指示器跟随笔刷球心,直径=笔刷直径,颜色随当前所属标记部位(捏合时更实,悬停淡)
    // 2) 球上方世界空间 TMP 文本显示所属部位名,始终朝向相机(billboard),文字用部位同色
    // 3) 选中 overlay 高亮点云(第二 DicomPointCloud,identity 子物体,与主点云同 local->world)
    // overlay 仅在选中集变化时重建一次,不每帧重建 136k 点
    [RequireComponent(typeof(GeneBrushSelector))]
    public class GeneBrushVisual : MonoBehaviour
    {
        // 指示球基础不透明度(染色中用此值,悬停减半)
        [SerializeField] float _sphereAlpha = 0.35f;
        // overlay 高亮点用 colormap 顶端强度显示(1=最亮),点比主点云略大更醒目
        [SerializeField] float _overlayPointSize = 0.004f;
        // 文本相对球心上方偏移(米,取球半径倍数,故随半径缩放)
        [SerializeField] float _labelHeightScale = 1.4f;
        // 文本世界字号(米级世界文本,配合小 localScale)
        [SerializeField] float _labelFontSize = 4f;
        [SerializeField] float _labelScale = 0.01f;

        GeneBrushSelector _brush;
        GeneColorController _controller;
        DicomPointCloud _mainCloud;

        Transform _sphere;
        Material _sphereMaterial;

        Transform _label;
        TextMeshPro _labelText;

        Camera _camera;

        DicomPointCloud _overlay;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int _ZTestId = Shader.PropertyToID("_ZTest");

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
            // 指示球/文本是场景根物体(非子物体),须显式销毁避免残留
            if (_sphere != null) Destroy(_sphere.gameObject);
            if (_label != null) Destroy(_label.gameObject);
            if (_sphereMaterial != null) Destroy(_sphereMaterial);
        }

        // 选中集变化后重建 overlay 高亮(单次触发,频率低)
        // overlay 仅作画笔期实时反馈;画笔关闭后由 Update 清空,避免恒定强度盖住区域表达显色
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
            if (!_brush.BrushEnabled)
            {
                SetActive(false);
                // 画笔关闭:清空 overlay 让主点云区域表达显色可见
                if (_overlay != null && _overlay.PointCount > 0)
                    _overlay.SetPoints(default, 0);
                return;
            }

            UpdateBrushGizmo();
        }

        // 有笔刷球心时显示指示球+文本:球跟随球心直径=2*半径,颜色随所属部位;文本显示部位名并 billboard
        void UpdateBrushGizmo()
        {
            bool show = _brush.HasBrushCenter;
            EnsureSphere();
            EnsureLabel();
            SetActive(show);
            if (!show) return;

            Color tagColor = _brush.CurrentTagColor;

            _sphere.position = _brush.BrushCenterWorld;
            // 指示球是场景根物体,localScale 即世界尺寸
            _sphere.localScale = Vector3.one * (_brush.BrushRadius * 2f);

            if (_sphereMaterial != null && _sphereMaterial.HasProperty(_BaseColorId))
            {
                Color c = tagColor;
                // 染色中更实,悬停(未捏合)淡一半
                c.a = _brush.Painting ? _sphereAlpha : _sphereAlpha * 0.5f;
                _sphereMaterial.SetColor(_BaseColorId, c);
            }

            // 文本:球上方,内容=所属部位名(无命中留空),颜色=部位色,billboard 朝相机
            if (_labelText != null)
            {
                string name = _brush.HasCurrentTag ? _brush.CurrentTagName : "";
                if (_labelText.text != name) _labelText.text = name;
                _labelText.color = tagColor;

                _label.gameObject.SetActive(!string.IsNullOrEmpty(name));
                if (!string.IsNullOrEmpty(name))
                {
                    _label.position = _brush.BrushCenterWorld + Vector3.up * (_brush.BrushRadius * _labelHeightScale);
                    var cam = GetCamera();
                    if (cam != null)
                        _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position, Vector3.up);
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

        // 指示球:内置 Sphere primitive,去掉碰撞体,场景根物体(不随点云缩放),半透明置顶材质
        void EnsureSphere()
        {
            if (_sphere != null) return;
            if (_sphereMaterial == null) _sphereMaterial = CreateMaterial(Color.white);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "GeneBrushSphere";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            // 世界空间跟随:不作为点云子物体,避免继承点云缩放
            _sphere = go.transform;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = _sphereMaterial;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            go.SetActive(false);
        }

        // 空间文本:世界空间 TextMeshPro(MeshRenderer 版),置顶不被点云遮挡,居中
        void EnsureLabel()
        {
            if (_label != null) return;

            var go = new GameObject("GeneBrushLabel");
            _label = go.transform;
            _label.localScale = Vector3.one * _labelScale;

            _labelText = go.AddComponent<TextMeshPro>();
            _labelText.alignment = TextAlignmentOptions.Center;
            _labelText.fontSize = _labelFontSize;
            _labelText.enableWordWrapping = false;
            _labelText.color = Color.white;
            // 置顶:文字始终可见不被点云遮挡
            if (_labelText.fontMaterial != null && _labelText.fontMaterial.HasProperty(_ZTestId))
                _labelText.fontMaterial.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);

            var rt = _labelText.rectTransform;
            rt.sizeDelta = new Vector2(20f, 4f);

            go.SetActive(false);
        }

        void SetActive(bool active)
        {
            if (_sphere != null && _sphere.gameObject.activeSelf != active)
                _sphere.gameObject.SetActive(active);
            // 文本额外受"有无部位名"控制,关闭画笔时一并隐藏
            if (!active && _label != null && _label.gameObject.activeSelf)
                _label.gameObject.SetActive(false);
        }

        Camera GetCamera()
        {
            if (_camera == null) _camera = Camera.main;
            return _camera;
        }

        // URP Unlit 半透明材质,内置管线回退
        static Material CreateMaterial(Color color)
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor(_BaseColorId, color);
                MakeTransparent(mat);
                return mat;
            }

            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null)
                return new Material(unlit) { color = color };

            Debug.LogWarning("笔刷指示球所需 shader 均被剥离(建议加入 Always Included Shaders)");
            return null;
        }

        // URP Unlit 透明模式:开启混合,写深度关闭,放到透明队列
        static void MakeTransparent(Material mat)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty(_ZTestId))
                mat.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
    }
}
