using UnityEngine;
using UnityEngine.Rendering;
using Autohand;

using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // 点云碰撞包围盒的 12 条线框边可视化,手靠近 FadeIn 显示,手离开 FadeOut 消失
    // 线框作为点云物体子物体,自动跟随抓取/缩放;盒尺寸取自 DicomGrabbableSetup 创建的 BoxCollider
    [RequireComponent(typeof(PointCloudController))]
    public class DicomBoundingBoxVisualizer : MonoBehaviour
    {
        // 手掌到盒面距离小于此值开始显示
        [SerializeField] float _fadeInDistance = 0.15f;
        // 大于此值开始消失,与 FadeIn 形成滞回防止边界处闪烁
        [SerializeField] float _fadeOutDistance = 0.25f;
        // 透明度变化速度(每秒),越大淡入淡出越快
        [SerializeField] float _fadeSpeed = 6f;
        // 线框青色,与裁切平面手柄高亮一致
        [SerializeField] Color _lineColor = new Color(0.18f, 0.78f, 0.85f, 1f);
        // 边粗细占盒平均尺寸比例,使不同体积下观感一致
        [SerializeField] float _thicknessRatio = 0.004f;
        [SerializeField] float _minThickness = 0.0015f;

        PointCloudController _controller;
        BoxCollider _collider;
        Transform _wireframe;
        Material _lineMaterial;

        // 当前/目标透明度,滞回可见状态
        float _currentAlpha;
        bool _visible;

        // 缓存场景内的手,避免每帧 FindObjects 产生 GC
        Hand[] _hands;
        float _nextHandScan;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _controller = GetComponent<PointCloudController>();
            _collider = GetComponent<BoxCollider>();
            // 订阅包围盒变化:每次重建都按当前可见点云真实 AABB 重建线框
            _controller.OnBoundsChanged += OnBoundsChanged;
        }

        void OnDestroy()
        {
            if (_controller != null) _controller.OnBoundsChanged -= OnBoundsChanged;
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        // 点云重建后据真实 AABB 构建线框;直接用事件传来的 bounds,不依赖 collider 更新顺序
        void OnBoundsChanged(Bounds bounds)
        {
            if (_collider == null) _collider = GetComponent<BoxCollider>();
            BuildWireframe(bounds);
        }

        void Update()
        {
            if (_wireframe == null || _collider == null) return;

            float dist = MinPalmDistance();

            // 滞回:未显示时进入 FadeIn 范围才显示,已显示时超出 FadeOut 范围才消失
            if (_visible)
            {
                if (dist > _fadeOutDistance) _visible = false;
            }
            else
            {
                if (dist < _fadeInDistance) _visible = true;
            }

            float target = _visible ? 1f : 0f;
            if (!Mathf.Approximately(_currentAlpha, target))
            {
                _currentAlpha = Mathf.MoveTowards(_currentAlpha, target, _fadeSpeed * Time.deltaTime);
                ApplyAlpha();
            }
        }

        // 取所有手掌到盒面的最小距离,无手时返回极大值
        float MinPalmDistance()
        {
            EnsureHands();
            float min = float.MaxValue;
            if (_hands == null) return min;

            for (int i = 0; i < _hands.Length; i++)
            {
                var hand = _hands[i];
                if (hand == null) continue;
                // palmTransform 未初始化时退回手物体本身
                Vector3 palm = hand.palmTransform != null ? hand.palmTransform.position : hand.transform.position;
                Vector3 closest = _collider.ClosestPoint(palm);
                float d = Vector3.Distance(palm, closest);
                if (d < min) min = d;
            }
            return min;
        }

        // 懒查找并缓存手;无手或有手被销毁时按冷却周期重扫
        void EnsureHands()
        {
            bool needScan = _hands == null || _hands.Length == 0;
            if (!needScan)
            {
                for (int i = 0; i < _hands.Length; i++)
                    if (_hands[i] == null) { needScan = true; break; }
            }
            if (!needScan) return;
            if (Time.time < _nextHandScan) return;

            _nextHandScan = Time.time + 1f;
            _hands = FindObjectsByType<Hand>(FindObjectsSortMode.None);
        }

        // 据传入 AABB 构建 12 条边;销毁旧线框后重建。空盒(无可见点)只清线框不重建
        void BuildWireframe(Bounds bounds)
        {
            if (_wireframe != null) Destroy(_wireframe.gameObject);
            _wireframe = null;

            Vector3 s = bounds.size;
            // 无可见点时尺寸为零,清掉线框即可,避免建出零尺寸退化边
            if (s.x <= 1e-5f && s.y <= 1e-5f && s.z <= 1e-5f)
            {
                _currentAlpha = 0f;
                _visible = false;
                return;
            }

            var root = new GameObject("BoundingBoxWireframe");
            _wireframe = root.transform;
            _wireframe.SetParent(transform, false);
            _wireframe.localPosition = Vector3.zero;
            _wireframe.localRotation = Quaternion.identity;
            _wireframe.localScale = Vector3.one;

            Vector3 c = bounds.center;
            Vector3 h = s * 0.5f;

            float t = Mathf.Max(_minThickness, (s.x + s.y + s.z) / 3f * _thicknessRatio);

            if (_lineMaterial == null) _lineMaterial = CreateMaterial(_lineColor);

            // 沿 X 的 4 条边:固定 y/z 角,长度沿 x
            for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    CreateEdge(new Vector3(c.x, c.y + h.y * sy, c.z + h.z * sz), new Vector3(s.x, t, t));

            // 沿 Y 的 4 条边
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    CreateEdge(new Vector3(c.x + h.x * sx, c.y, c.z + h.z * sz), new Vector3(t, s.y, t));

            // 沿 Z 的 4 条边
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    CreateEdge(new Vector3(c.x + h.x * sx, c.y + h.y * sy, c.z), new Vector3(t, t, s.z));

            // 初始隐藏
            _currentAlpha = 0f;
            _visible = false;
            ApplyAlpha();
        }

        // 用扁平 Cube 做一条边,移除自带碰撞体避免干扰抓取检测
        void CreateEdge(Vector3 localPos, Vector3 scale)
        {
            var edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            edge.name = "Edge";
            var col = edge.GetComponent<Collider>();
            if (col != null) Destroy(col);

            edge.transform.SetParent(_wireframe, false);
            edge.transform.localPosition = localPos;
            edge.transform.localRotation = Quaternion.identity;
            edge.transform.localScale = scale;
            edge.GetComponent<MeshRenderer>().sharedMaterial = _lineMaterial;
        }

        // 共用材质改 alpha 整体淡入淡出;alpha 趋零时禁用线框根省渲染
        void ApplyAlpha()
        {
            if (_lineMaterial != null)
            {
                Color col = _lineColor;
                col.a *= _currentAlpha;
                _lineMaterial.SetColor(_BaseColorId, col);
                // 回退材质(Sprites/Default 等)无 _BaseColor,用 color 兜底
                _lineMaterial.color = col;
            }
            if (_wireframe != null)
            {
                bool active = _currentAlpha > 0.001f;
                if (_wireframe.gameObject.activeSelf != active)
                    _wireframe.gameObject.SetActive(active);
            }
        }

        // URP Unlit 透明材质,内置管线回退;参考 ClipPlaneHandleBuilder 的透明设置
        static Material CreateMaterial(Color color)
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor("_BaseColor", color);
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
            // 内置 shader 被剥离(URP 构建常见):判空避免 new Material(null) 在真机崩溃
            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null) return new Material(unlit) { color = color };

            Debug.LogWarning("包围盒线框所需 shader 均被剥离，改用无材质(建议加入 Always Included Shaders)");
            return null;
        }
    }
}
