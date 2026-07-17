using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Dicom.Gene
{
    // 覆盖率信标:每个解剖区域在其 local 质心放一个信标球(模型子物体,随模型缩放/旋转/抓取)
    // 未画=暗淡小点(仍可辨区域位置),已画=按覆盖率(已画cell/该区域总cell)发亮变大,满覆盖最亮最大
    // 手(笔刷球心)就近的信标浮出世界文本"区域名 覆盖率%";painting 时让位给 BrushVisual 的成分文本
    // 目的:一眼看清全部区域空间分布 + 哪片还没画/画了多少,消除"只见已画不知漏哪"的困惑
    [RequireComponent(typeof(GeneBrushSelector))]
    [RequireComponent(typeof(GeneColorController))]
    public class GeneCoverageBeacons : MonoBehaviour
    {
        // 信标直径占模型最大维度比例:未画到满覆盖插值
        [SerializeField] float _minSizeFrac = 0.018f;
        [SerializeField] float _maxSizeFrac = 0.045f;
        // 信标不透明度:未画淡(仍可见)到满覆盖实
        [SerializeField] float _minAlpha = 0.18f;
        [SerializeField] float _maxAlpha = 0.9f;
        // 就近浮出文本的世界距离阈值(米);手笔刷球心离信标近于此才显名
        [SerializeField] float _labelWorldDist = 0.12f;
        // 文本上抬 = 信标世界直径 * 此系数(+2cm 保底),贴合信标大小
        [SerializeField] float _labelHeightFrac = 0.9f;
        [SerializeField] float _labelFontSize = 4f;
        [SerializeField] float _labelScale = 0.008f;

        GeneBrushSelector _brush;
        GeneColorController _controller;

        // 单区域信标:tag/总数/local质心/球体变换/渲染器(用 MPB 逐球着色不建多材质)
        class Beacon { public int Tag; public int Total; public Vector3 LocalCentroid; public Transform Tr; public Renderer Rend; }
        readonly List<Beacon> _beacons = new List<Beacon>();

        Material _beaconMaterial;
        MaterialPropertyBlock _mpb;
        readonly Dictionary<int, int> _paintedBuf = new Dictionary<int, int>();

        Transform _label;
        TextMeshPro _labelText;
        Camera _camera;
        TMP_FontAsset _injectedFont;
        float _maxDimLocal;
        bool _destroyed;
        // 上帧画笔开关态,翻转时整组信标显隐(信标是勾画辅助,画笔关时不干扰表达视图)
        bool _beaconsShown;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int _ZTestId = Shader.PropertyToID("_ZTest");
        static readonly int _TmpZTestId = Shader.PropertyToID("unity_GUIZTestMode");

        // 注入区域文本字体(项目中文字体);须在建文本前调用
        public void SetFont(TMP_FontAsset font) => _injectedFont = font;

        void Awake()
        {
            _brush = GetComponent<GeneBrushSelector>();
            _controller = GetComponent<GeneColorController>();
            _mpb = new MaterialPropertyBlock();
            // 花名册加载后建信标;选区变化后刷新覆盖率显色
            _controller.OnLoaded += OnModelLoaded;
            _brush.OnSelectionChanged += OnSelectionChanged;
        }

        void OnDestroy()
        {
            _destroyed = true;
            if (_controller != null) _controller.OnLoaded -= OnModelLoaded;
            if (_brush != null) _brush.OnSelectionChanged -= OnSelectionChanged;
            // 信标是模型子物体随之销毁,但文本是根物体须显式清;材质实例一并回收
            if (_label != null) Destroy(_label.gameObject);
            if (_beaconMaterial != null) Destroy(_beaconMaterial);
        }

        // 模型加载完:据花名册重建全部信标(先清旧的),记录模型最大维度供尺寸/文本高度
        void OnModelLoaded(GeneModelData _)
        {
            if (_destroyed) return;
            ClearBeacons();

            Vector3 size = _controller.ModelLocalSize;
            _maxDimLocal = Mathf.Max(size.x, size.y, size.z, 1e-4f);

            var roster = _controller.RegionRoster;
            for (int i = 0; i < roster.Count; i++)
                _beacons.Add(CreateBeacon(roster[i]));

            RefreshCoverage();
        }

        // 选区变化即刷新覆盖率(画/清均触发)
        void OnSelectionChanged(int count)
        {
            if (_destroyed) return;
            RefreshCoverage();
        }

        // 建单个信标:去碰撞体 sphere,挂模型下(继承模型 local->world),初始暗淡态
        Beacon CreateBeacon(GeneColorController.RegionInfo info)
        {
            if (_beaconMaterial == null) _beaconMaterial = CreateMaterial();

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"GeneBeacon_{info.Tag}";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var tr = go.transform;
            // 挂模型 transform 下,localPosition=质心(mm);随模型缩放/旋转/抓取一致运动
            tr.SetParent(transform, false);
            tr.localPosition = info.LocalCentroid;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _beaconMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // 初始隐藏,画笔开启时整组显出
            go.SetActive(false);

            return new Beacon { Tag = info.Tag, Total = info.Total, LocalCentroid = info.LocalCentroid, Tr = tr, Rend = mr };
        }

        void ClearBeacons()
        {
            for (int i = 0; i < _beacons.Count; i++)
                if (_beacons[i].Tr != null) Destroy(_beacons[i].Tr.gameObject);
            _beacons.Clear();
        }

        // 逐信标算覆盖率(已画/总数)->尺寸+不透明度+色;未画淡暗小,满覆盖亮实大
        // 尺寸用 local 直径(mm),故信标世界大小随模型缩放自适应;色用区域分类色,亮度随覆盖率
        void RefreshCoverage()
        {
            _controller.CollectPaintedByTag(_paintedBuf);

            for (int i = 0; i < _beacons.Count; i++)
            {
                var b = _beacons[i];
                if (b.Tr == null || b.Rend == null) continue;

                _paintedBuf.TryGetValue(b.Tag, out int painted);
                float frac = b.Total > 0 ? Mathf.Clamp01((float)painted / b.Total) : 0f;

                float diaLocal = _maxDimLocal * Mathf.Lerp(_minSizeFrac, _maxSizeFrac, frac);
                b.Tr.localScale = Vector3.one * diaLocal;

                Color baseColor = GeneTagPalette.Color(b.Tag);
                // 未画:压暗降饱和(仍可辨位置);已画:向满亮插值,alpha 随覆盖率升
                Color c = Color.Lerp(baseColor * 0.35f, baseColor, frac);
                c.a = Mathf.Lerp(_minAlpha, _maxAlpha, frac);

                b.Rend.GetPropertyBlock(_mpb);
                _mpb.SetColor(_BaseColorId, c);
                b.Rend.SetPropertyBlock(_mpb);
            }
        }

        void Update()
        {
            if (_destroyed) return;

            // 画笔开关翻转:整组信标显隐(仅在变化时遍历,避免每帧开销)
            bool wantShow = _brush.BrushEnabled && _beacons.Count > 0;
            if (wantShow != _beaconsShown) SetBeaconsActive(wantShow);

            // 画笔关或无信标:隐藏文本
            if (!_brush.BrushEnabled || _beacons.Count == 0)
            {
                SetLabelActive(false);
                return;
            }

            // painting 时让位给 BrushVisual 的成分文本,避免两条文本打架
            if (_brush.Painting || !_brush.HasBrushCenter)
            {
                SetLabelActive(false);
                return;
            }

            UpdateNearestLabel(_brush.BrushCenterWorld);
        }

        // 找离笔刷球心最近且在阈值内的信标,浮出其"区域名 覆盖率%";超阈值隐藏
        void UpdateNearestLabel(Vector3 brushWorld)
        {
            Beacon nearest = null;
            float bestSq = _labelWorldDist * _labelWorldDist;
            for (int i = 0; i < _beacons.Count; i++)
            {
                var b = _beacons[i];
                if (b.Tr == null) continue;
                float d = (b.Tr.position - brushWorld).sqrMagnitude;
                if (d < bestSq) { bestSq = d; nearest = b; }
            }

            if (nearest == null) { SetLabelActive(false); return; }

            EnsureLabel();
            if (_label == null || _labelText == null) return;

            _paintedBuf.TryGetValue(nearest.Tag, out int painted);
            float pct = nearest.Total > 0 ? painted * 100f / nearest.Total : 0f;
            string hex = ColorUtility.ToHtmlStringRGB(GeneTagPalette.Color(nearest.Tag));
            string name = _brush.GetTagName(nearest.Tag);
            _labelText.text = $"<color=#{hex}>{name}</color> {pct:F0}%";

            SetLabelActive(true);
            // 文本浮在信标正上方,偏移=信标世界直径*系数(随模型缩放/信标大小自适应,始终贴合)
            float worldDia = nearest.Tr.lossyScale.y;
            _label.position = nearest.Tr.position + Vector3.up * (worldDia * _labelHeightFrac + 0.02f);
            BillboardLabel();
        }

        void BillboardLabel()
        {
            var cam = GetCamera();
            if (cam == null || _label == null) return;
            _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position, Vector3.up);
        }

        void SetLabelActive(bool on)
        {
            if (_label != null && _label.gameObject.activeSelf != on) _label.gameObject.SetActive(on);
        }

        // 整组信标显隐;显出时顺带刷新一次覆盖率,确保开画笔即见最新状态
        void SetBeaconsActive(bool on)
        {
            _beaconsShown = on;
            for (int i = 0; i < _beacons.Count; i++)
                if (_beacons[i].Tr != null && _beacons[i].Tr.gameObject.activeSelf != on)
                    _beacons[i].Tr.gameObject.SetActive(on);
            if (on) RefreshCoverage();
        }

        // 信标文本:世界空间 TextMeshPro,置顶不被点云遮挡;根物体,billboard 朝相机
        void EnsureLabel()
        {
            if (_label != null) return;

            var go = new GameObject("GeneBeaconLabel");
            _label = go.transform;
            _label.localScale = Vector3.one * _labelScale;

            _labelText = go.AddComponent<TextMeshPro>();
            var font = ResolveFont();
            if (font != null) _labelText.font = font;
            _labelText.alignment = TextAlignmentOptions.Center;
            _labelText.fontSize = _labelFontSize;
            _labelText.enableWordWrapping = false;
            _labelText.richText = true;
            _labelText.color = Color.white;
            // TMP SDF 的 ZTest 走全局变量 unity_GUIZTestMode,材质实例写此属性覆盖为 Always 才置顶
            var fontMat = _labelText.fontMaterial;
            if (fontMat != null)
                fontMat.SetFloat(_TmpZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);

            _labelText.rectTransform.sizeDelta = new Vector2(24f, 8f);
            go.SetActive(false);
        }

        // URP Unlit 半透明置顶材质,内置管线回退;逐信标色由 MPB 覆盖
        static Material CreateMaterial()
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor(_BaseColorId, Color.white);
                mat.SetFloat("_Surface", 1f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                if (mat.HasProperty(_ZTestId))
                    mat.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                return mat;
            }

            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null) return new Material(unlit) { color = Color.white };

            Debug.LogWarning("覆盖率信标所需 shader 均被剥离(建议加入 Always Included Shaders)");
            return null;
        }

        // 字体回退链:注入字体 -> 场景现有含中文 TMP 字体 -> 已加载资源含中文字形字体 -> TMP 默认
        TMP_FontAsset ResolveFont()
        {
            if (_injectedFont != null) return _injectedFont;
            foreach (var t in FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
                if (t.font != null && t.font.HasCharacter('区')) return t.font;
            var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].HasCharacter('区')) return all[i];
            return TMP_Settings.defaultFontAsset;
        }

        Camera GetCamera()
        {
            if (_camera != null) return _camera;
            _camera = Camera.main;
            if (_camera == null) _camera = Camera.current;
            if (_camera == null) _camera = FindObjectOfType<Camera>();
            return _camera;
        }
    }
}
