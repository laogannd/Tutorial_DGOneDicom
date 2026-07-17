using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

using Dicom.PointCloud;

namespace Dicom.Gene
{
    // 笔刷可视化:
    // 1) 半透明球体指示器跟随笔刷球心(手上),颜色随最近所属部位(捏合时更实,悬停淡)
    // 2) 悬停在选中区域质心上方的世界空间 TMP 文本,列出区域内全部 tag(主导优先+占比),
    //    每个 tag 用其分类色;一条 LineRenderer 指向线连到质心。勾画中实时刷新
    // 3) 选区显色:选了具体基因则清 overlay,让主点云基因表达 LUT 真实配色透出;
    //    未选基因(无表达值可映射)才用第二 DicomPointCloud 恒定强度高亮
    [RequireComponent(typeof(GeneBrushSelector))]
    public class GeneBrushVisual : MonoBehaviour
    {
        // 指示球基础不透明度(染色中用此值,悬停减半)
        [SerializeField] float _sphereAlpha = 0.35f;
        // overlay 高亮点用 colormap 顶端强度显示(1=最亮),点比主点云略大更醒目
        [SerializeField] float _overlayPointSize = 0.004f;
        // 文本相对质心上方偏移(米)
        [SerializeField] float _labelHeight = 0.08f;
        // 文本世界字号(米级世界文本,配合小 localScale)
        [SerializeField] float _labelFontSize = 4f;
        [SerializeField] float _labelScale = 0.01f;
        // 指向线宽(世界米)
        [SerializeField] float _lineWidth = 0.002f;

        GeneBrushSelector _brush;
        GeneColorController _controller;
        DicomPointCloud _mainCloud;

        Transform _sphere;
        Material _sphereMaterial;

        Transform _label;
        TextMeshPro _labelText;

        LineRenderer _line;
        Material _lineMaterial;

        Camera _camera;
        DicomPointCloud _overlay;
        // 未画区域幽灵底图:全模型未画 cell 暗淡灰白显示,已画处点消失露出空洞,一眼看清漏哪
        // 仅画笔开且未选基因时显示(选基因时主点云已铺全模型淡显,幽灵冗余);近 13.6 万点故节流重建
        DicomPointCloud _ghost;
        bool _ghostDirty;
        float _ghostNextRebuild;
        readonly StringBuilder _sb = new StringBuilder(256);

        // 幽灵点:恒定强度(Intensity 模式下配灰色 Tint 得暗淡灰白)+ 低不透明度 + 略小点尺寸
        [SerializeField] float _ghostIntensity = 0.7f;
        [SerializeField, Range(0f, 1f)] float _ghostAlpha = 0.16f;
        [SerializeField] float _ghostPointSize = 0.0018f;
        [SerializeField] Color _ghostTint = new Color(0.6f, 0.62f, 0.66f, 1f);
        // 幽灵重建最小间隔(秒):选区变化置脏,Update 里按此节流重建,避免每笔刷帧重建 13.6 万点抖动
        [SerializeField] float _ghostRebuildInterval = 0.25f;

        // 区域文本字体:由 Bootstrap/面板注入(项目中文字体);为空则运行时回退解析
        TMP_FontAsset _injectedFont;

        // 组件销毁中:辅助根物体(球/文本/线)将被销毁,期间任何延迟回调(选区变化)须早退,防访问已毁 Transform
        bool _destroyed;

        // 当前区域:是否有内容、质心世界坐标、文本世界坐标、主导色(供每帧 billboard/指向线)
        bool _hasRegion;
        Vector3 _regionCentroidWorld;
        Vector3 _regionLabelWorld;
        Color _dominantColor = Color.white;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
        // URP Unlit(球/线)用 _ZTest;TMP SDF 的 ZTest 绑定全局变量 unity_GUIZTestMode,
        // 材质实例写此属性即可覆盖为 Always 置顶,故两者用不同 id
        static readonly int _ZTestId = Shader.PropertyToID("_ZTest");
        static readonly int _TmpZTestId = Shader.PropertyToID("unity_GUIZTestMode");

        // 注入区域文本字体(项目中文字体);须在 EnsureLabel 建文本前调用才生效
        public void SetFont(TMP_FontAsset font) => _injectedFont = font;

        void Awake()
        {
            _brush = GetComponent<GeneBrushSelector>();
            _controller = GetComponent<GeneColorController>();
            _mainCloud = GetComponent<DicomPointCloud>();
            _brush.OnSelectionChanged += OnSelectionChanged;
        }

        void OnDestroy()
        {
            _destroyed = true;
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
            // 指示球/文本/指向线是场景根物体(非子物体),须显式销毁避免残留
            if (_sphere != null) Destroy(_sphere.gameObject);
            if (_label != null) Destroy(_label.gameObject);
            if (_line != null) Destroy(_line.gameObject);
            // 幽灵是子物体随本物体销毁,但显式清理更稳妥
            if (_ghost != null) Destroy(_ghost.gameObject);
            if (_sphereMaterial != null) Destroy(_sphereMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        // 选中集变化后:选了基因清 overlay(主点云表达色透出),未选基因才恒定高亮;并刷新区域文本
        void OnSelectionChanged(int count)
        {
            if (_destroyed || _brush == null || !_brush.BrushEnabled) return;

            if (_controller.CurrentGene != null)
            {
                // 已有表达值:主点云 RebuildPoints 已用真实 LUT 表达色渲染选中 cell,overlay/幽灵会盖掉,故清空
                if (_overlay != null && _overlay.PointCount > 0) _overlay.SetPoints(default, 0);
                if (_ghost != null && _ghost.PointCount > 0) _ghost.SetPoints(default, 0);
            }
            else
            {
                // 无基因可映射:用 overlay 恒定强度即时高亮选区(量小),幽灵底图节流重建标脏
                EnsureOverlay();
                _controller.BuildOverlay(_overlay, 1f);
                _controller.ApplyColorState(_overlay);
                _ghostDirty = true;
            }

            RefreshRegionLabel();
        }

        void Update()
        {
            if (!_brush.BrushEnabled)
            {
                SetSphereActive(false);
                HideRegion();
                if (_overlay != null && _overlay.PointCount > 0) _overlay.SetPoints(default, 0);
                if (_ghost != null && _ghost.PointCount > 0) _ghost.SetPoints(default, 0);
                return;
            }

            UpdateBrushCursor();
            UpdateGhost();
            // 质心固定,但相机会动:每帧让区域文本朝相机
            if (_hasRegion) BillboardRegionLabel();
        }

        // 幽灵底图:仅画笔开且未选基因时显示(选基因时主点云已铺全模型淡显);节流重建避免每帧 13.6 万点抖动
        // 首次进入(未画/无掩码)也须建一次,让全模型未画区域立即以灰白呈现
        void UpdateGhost()
        {
            if (_controller.CurrentGene != null)
            {
                if (_ghost != null && _ghost.PointCount > 0) _ghost.SetPoints(default, 0);
                return;
            }

            // 未选基因:确保幽灵存在;首帧强制建一次(PointCount==0 且未标脏也建),之后按脏标+节流重建
            EnsureGhost();
            bool firstBuild = _ghost != null && _ghost.PointCount == 0;
            if ((_ghostDirty || firstBuild) && Time.unscaledTime >= _ghostNextRebuild)
            {
                _ghostDirty = false;
                _ghostNextRebuild = Time.unscaledTime + _ghostRebuildInterval;
                _controller.BuildGhost(_ghost, _ghostIntensity);
            }
        }

        // 指示球跟随笔刷球心:直径=2*半径,颜色随最近所属部位;染色中更实,悬停淡
        void UpdateBrushCursor()
        {
            bool show = _brush.HasBrushCenter;
            EnsureSphere();
            SetSphereActive(show);
            if (!show) return;

            _sphere.position = _brush.BrushCenterWorld;
            _sphere.localScale = Vector3.one * (_brush.BrushRadius * 2f);

            if (_sphereMaterial != null && _sphereMaterial.HasProperty(_BaseColorId))
            {
                Color c = _brush.CurrentTagColor;
                c.a = _brush.Painting ? _sphereAlpha : _sphereAlpha * 0.5f;
                _sphereMaterial.SetColor(_BaseColorId, c);
            }
        }

        // 汇总选中区域全部 tag(主导优先+占比)刷新空间文本,指向线连质心;无选中隐藏
        void RefreshRegionLabel()
        {
            if (_destroyed) return;
            EnsureLabel();
            EnsureLine();
            // Ensure 后仍可能因外部销毁/资源剥离而缺失,拿不到有效对象直接安全退出,不访问已毁 Transform
            if (_label == null || _labelText == null || _line == null) return;

            if (!_controller.CollectRegionSummary(out var shares, out Vector3 localCentroid, out int total)
                || total == 0)
            {
                HideRegion();
                return;
            }

            _dominantColor = GeneTagPalette.Color(shares[0].Tag);
            // local 质心 -> world(模型缩放/旋转经 transform)
            _regionCentroidWorld = transform.TransformPoint(localCentroid);
            _regionLabelWorld = _regionCentroidWorld + Vector3.up * _labelHeight;
            _hasRegion = true;

            _labelText.text = BuildLabelText(shares, total);
            _label.gameObject.SetActive(true);
            _label.position = _regionLabelWorld;
            BillboardRegionLabel();

            _line.gameObject.SetActive(true);
            _line.SetPosition(0, _regionLabelWorld);
            _line.SetPosition(1, _regionCentroidWorld);
            _line.startColor = _line.endColor = _dominantColor;
        }

        // 富文本:第一行主导区域(加粗),其余按占比降序;每个 tag 名用其分类色,尾随百分比
        string BuildLabelText(List<GeneColorController.TagShare> shares, int total)
        {
            _sb.Clear();
            for (int i = 0; i < shares.Count; i++)
            {
                int tag = shares[i].Tag;
                float pct = shares[i].Count * 100f / total;
                string hex = ColorUtility.ToHtmlStringRGB(GeneTagPalette.Color(tag));
                string name = _brush.GetTagName(tag);
                if (i > 0) _sb.Append('\n');
                _sb.Append("<color=#").Append(hex).Append('>');
                if (i == 0) _sb.Append("<b>").Append(name).Append("</b>");
                else _sb.Append(name);
                _sb.Append(' ').Append(pct.ToString("F0")).Append('%').Append("</color>");
            }
            return _sb.ToString();
        }

        // 文本朝相机(billboard),质心不变故位置沿用缓存
        void BillboardRegionLabel()
        {
            var cam = GetCamera();
            if (cam == null || _label == null) return;
            _label.rotation = Quaternion.LookRotation(_regionLabelWorld - cam.transform.position, Vector3.up);
        }

        void HideRegion()
        {
            _hasRegion = false;
            if (_label != null && _label.gameObject.activeSelf) _label.gameObject.SetActive(false);
            if (_line != null && _line.gameObject.activeSelf) _line.gameObject.SetActive(false);
        }

        // overlay 点云挂子物体,identity 局部变换 -> 与主点云同一 local->world,cell local 坐标直接对齐
        void EnsureOverlay()
        {
            if (_overlay != null) return;

            var go = new GameObject("GeneSelectionOverlay");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            _overlay = go.AddComponent<DicomPointCloud>();
            if (_mainCloud != null && _mainCloud.Material != null)
                _overlay.SetMaterial(_mainCloud.Material);
            _overlay.SetPointSize(_overlayPointSize);
        }

        // 幽灵点云挂子物体,identity 局部变换 -> 与主点云同一 local->world,cell local 坐标直接对齐
        // 复用主材质,但显色态独立:Intensity 模式 + 灰色 Tint + 低 alpha,得暗淡去饱和灰白底图
        void EnsureGhost()
        {
            if (_ghost != null) return;

            var go = new GameObject("GeneUnpaintedGhost");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            _ghost = go.AddComponent<DicomPointCloud>();
            if (_mainCloud != null && _mainCloud.Material != null)
                _ghost.SetMaterial(_mainCloud.Material);
            _ghost.SetPointSize(_ghostPointSize);
            // Intensity 模式:color = saturate(g*gain)*tint;窗宽窗位全通使 g=intensity,tint 灰得暗淡灰白
            _ghost.SetColorMode((float)Dicom.PointCloud.DicomColorMode.Intensity);
            _ghost.SetWindow(0.5f, 1f);
            _ghost.SetNormalize(0f, 1f);
            _ghost.SetTint(_ghostTint.r, _ghostTint.g, _ghostTint.b, 1f);
            // 幽灵点全 Selected=0,走淡显 Pass 按此 alpha 半透明
            _ghost.SetAlpha(_ghostAlpha);
        }

        // 指示球:内置 Sphere primitive,去碰撞体,场景根物体(不随点云缩放),半透明置顶材质
        void EnsureSphere()
        {
            if (_sphere != null) return;
            if (_sphereMaterial == null) _sphereMaterial = CreateMaterial(Color.white);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "GeneBrushSphere";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
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

        // 区域空间文本:世界空间 TextMeshPro(MeshRenderer 版),置顶不被点云遮挡,居中
        void EnsureLabel()
        {
            if (_label != null) return;

            var go = new GameObject("GeneRegionLabel");
            _label = go.transform;
            _label.localScale = Vector3.one * _labelScale;

            _labelText = go.AddComponent<TextMeshPro>();
            // 区域名含中文,须用含中文字形的字体,否则渲染空白(看似"没出现")
            var font = ResolveFont();
            if (font != null) _labelText.font = font;
            _labelText.alignment = TextAlignmentOptions.Center;
            _labelText.fontSize = _labelFontSize;
            _labelText.enableWordWrapping = false;
            _labelText.richText = true;
            _labelText.color = Color.white;
            // TMP SDF 的 ZTest 走全局变量 unity_GUIZTestMode,材质实例写此属性覆盖为 Always 才置顶,
            // 不被点云遮挡;旧代码写 _ZTest 属性名 TMP 无此属性故从未生效
            var fontMat = _labelText.fontMaterial;
            if (fontMat != null)
                fontMat.SetFloat(_TmpZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);

            _labelText.rectTransform.sizeDelta = new Vector2(24f, 12f);
            go.SetActive(false);
        }

        // 指向线:两点 LineRenderer,世界空间,置顶,主导色;连文本与区域质心
        void EnsureLine()
        {
            if (_line != null) return;
            if (_lineMaterial == null) _lineMaterial = CreateLineMaterial();

            var go = new GameObject("GeneRegionLeader");
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = 2;
            _line.numCapVertices = 2;
            _line.widthMultiplier = _lineWidth;
            _line.sharedMaterial = _lineMaterial;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.alignment = LineAlignment.View;
            go.SetActive(false);
        }

        void SetSphereActive(bool active)
        {
            if (_sphere != null && _sphere.gameObject.activeSelf != active)
                _sphere.gameObject.SetActive(active);
        }

        // 字体回退链(区域名含中文,须拿到含中文字形的字体,否则渲染空白看似"没出现"):
        // 注入字体 -> 场景现有含中文的 TMP 字体 -> 已加载资源里任一含中文字形的字体 -> TMP 默认
        // 用 '区'(区域名常用字)探测字形覆盖,避免误选到不含中文的 LiberationSans
        TMP_FontAsset ResolveFont()
        {
            if (_injectedFont != null) return _injectedFont;

            // 借用场景已存在的 TMP 文本字体(GenePanelUI/UnifiedDebugPanel 通常已配项目中文字体)
            foreach (var t in FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
                if (t.font != null && HasChinese(t.font)) return t.font;
            foreach (var t in FindObjectsByType<TextMeshPro>(FindObjectsSortMode.None))
                if (t != this._labelText && t.font != null && HasChinese(t.font)) return t.font;

            // 全量扫已加载字体资源,挑第一个含中文字形的(NotoSansSC 等)
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && HasChinese(all[i])) return all[i];

            return TMP_Settings.defaultFontAsset;
        }

        // 探测字体是否含中文字形('区'为区域名常用字)
        static bool HasChinese(TMP_FontAsset font) => font.HasCharacter('区');

        // 相机用于文本 billboard;Pico 上主相机未必打 MainCamera tag,故 Camera.main 为空时回退场景任意相机
        Camera GetCamera()
        {
            if (_camera != null) return _camera;
            _camera = Camera.main;
            if (_camera == null) _camera = Camera.current;
            if (_camera == null) _camera = FindObjectOfType<Camera>();
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

        // 指向线材质:URP Unlit 顶点色置顶;LineRenderer 用 startColor/endColor 着色
        static Material CreateLineMaterial()
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            Shader s = urp != null ? urp : Shader.Find("Sprites/Default");
            if (s == null)
            {
                Debug.LogWarning("指向线所需 shader 被剥离(建议加入 Always Included Shaders)");
                return null;
            }
            var mat = new Material(s);
            MakeTransparent(mat);
            // 置顶,不被点云遮挡
            if (mat.HasProperty(_ZTestId))
                mat.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);
            return mat;
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
