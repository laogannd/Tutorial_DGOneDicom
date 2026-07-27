using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Autohand;

using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // VR 空间测距卡尺:徒手捏合在点云上放测量点,两点成一段,实时显示真实解剖距离
    // 精度关键:端点存为模型局部坐标。点云局部空间坐标单位即真实毫米((voxel-half)*Spacing mm),
    // 故 Vector3.Distance(localA, localB) 恒等真实解剖 mm,与模型缩放/旋转/平移完全解耦
    //
    // 端点球与连线是模型 transform 子物体(随缩放/旋转/抓取一致运动);距离文本是根物体 billboard 朝相机置顶
    // 触发复用基因画笔已验证的徒手捏合(拇指尖↔食指尖合拢,滞回防抖),每次捏合边沿放一个点
    // 挂在点云模型物体上(与 DicomModelTransform 同物体),Bootstrap 运行时 AddComponent
    [RequireComponent(typeof(DicomModelTransform))]
    public class DicomMeasureTool : MonoBehaviour
    {
        // 徒手捏合:拇指尖↔食指尖距离阈值,按两指尖半径和缩放;滞回防抖(enter 小于 exit)
        [SerializeField] float _pinchEnterScale = 1.6f;
        [SerializeField] float _pinchExitScale = 2.4f;
        // 端点球直径占模型最大维度比例:随模型缩放自适应世界大小
        [SerializeField] float _pointSizeFrac = 0.02f;
        // 距离文本上抬 = 段中点端点球世界直径 * 此系数(+2cm 保底)
        [SerializeField] float _labelHeightFrac = 1.2f;
        [SerializeField] float _labelFontSize = 4f;
        [SerializeField] float _labelScale = 0.008f;
        // 连线宽度占端点球世界直径比例
        [SerializeField] float _lineWidthFrac = 0.15f;

        PointCloudController _controller;
        Hand[] _hands;
        float _nextHandScan;

        // 每只手的捏合滞回状态,避免边界抖动反复触发
        readonly Dictionary<Hand, bool> _pinchState = new Dictionary<Hand, bool>();

        bool _enabled;
        // 模型最大维度(局部 mm),供端点球/文本尺寸自适应
        float _maxDimLocal = 1f;

        // 一条完整测量段:两端点局部坐标(mm) + 可视化对象
        class Segment
        {
            public Vector3 LocalA;
            public Vector3 LocalB;
            public Transform BallA;
            public Transform BallB;
            public LineRenderer Line;
            public Transform Label;
            public TextMeshPro LabelText;
        }
        readonly List<Segment> _segments = new List<Segment>();

        // 待完成段:已放下第一个点,等第二个点闭合
        Segment _pending;

        Material _ballMaterial;
        Material _lineMaterial;
        MaterialPropertyBlock _mpb;
        TMP_FontAsset _injectedFont;
        Camera _camera;
        bool _destroyed;

        static readonly int _BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int _ColorId = Shader.PropertyToID("_Color");
        static readonly int _ZTestId = Shader.PropertyToID("_ZTest");
        static readonly int _TmpZTestId = Shader.PropertyToID("unity_GUIZTestMode");

        static readonly Color _MeasureColor = new Color(1f, 0.85f, 0.2f, 1f);

        // 段数变化(放点/撤销/清空)时触发,携带当前完整段数;供面板刷新列表
        public event Action OnMeasurementsChanged;

        public bool MeasureEnabled => _enabled;
        // 已完成段数(不含待闭合的 pending)
        public int SegmentCount => _segments.Count;

        void Awake()
        {
            _controller = GetComponent<PointCloudController>();
            _mpb = new MaterialPropertyBlock();
        }

        void OnDestroy()
        {
            _destroyed = true;
            ClearAll();
            if (_ballMaterial != null) Destroy(_ballMaterial);
            if (_lineMaterial != null) Destroy(_lineMaterial);
        }

        // 注入距离文本字体(项目中文字体);须在建文本前调用
        public void SetFont(TMP_FontAsset font) => _injectedFont = font;

        // 开关测量:关闭时丢弃未闭合的待定点(避免残留半段),已完成段保留
        public void SetEnabled(bool on)
        {
            _enabled = on;
            _pinchState.Clear();
            if (!on) DiscardPending();
        }

        // 取第 i 段真实距离(mm);越界返回 0
        public float GetDistanceMm(int i)
        {
            if (i < 0 || i >= _segments.Count) return 0f;
            return Vector3.Distance(_segments[i].LocalA, _segments[i].LocalB);
        }

        // 撤销最近一次操作:优先丢未闭合的待定点,否则删最后一条完整段
        public void UndoLast()
        {
            if (_pending != null) { DiscardPending(); return; }
            if (_segments.Count == 0) return;
            DestroySegment(_segments[_segments.Count - 1]);
            _segments.RemoveAt(_segments.Count - 1);
            OnMeasurementsChanged?.Invoke();
        }

        // 清空所有测量
        public void ClearAll()
        {
            DiscardPending();
            for (int i = 0; i < _segments.Count; i++) DestroySegment(_segments[i]);
            _segments.Clear();
            OnMeasurementsChanged?.Invoke();
        }

        void Update()
        {
            if (!_enabled || _destroyed) return;

            RefreshMaxDim();
            FindHand(out bool hasHand, out bool pinchEdge, out Vector3 tipWorld);

            // 捏合下降沿放一个点(局部坐标 = 世界指尖逆变换到模型局部空间,即真实 mm 坐标)
            if (pinchEdge)
            {
                Vector3 local = transform.InverseTransformPoint(tipWorld);
                PlacePoint(local);
            }

            // 待定点预览:第一个点已放、悬停第二点时,预览球+连线跟随指尖,实时显示距离
            if (_pending != null && hasHand)
                UpdatePendingPreview(transform.InverseTransformPoint(tipWorld));

            UpdateVisuals();
        }

        // 放点:无待定段则起新段(放 A);有待定段则闭合(放 B 并成完整段)
        void PlacePoint(Vector3 local)
        {
            if (_pending == null)
            {
                _pending = new Segment { LocalA = local, LocalB = local };
                _pending.BallA = CreateBall();
                _pending.BallB = CreateBall();
                _pending.Line = CreateLine();
                _pending.Label = CreateLabel(out _pending.LabelText);
            }
            else
            {
                _pending.LocalB = local;
                _segments.Add(_pending);
                _pending = null;
                OnMeasurementsChanged?.Invoke();
            }
        }

        // 待定段第二端点跟随指尖预览
        void UpdatePendingPreview(Vector3 local) => _pending.LocalB = local;

        // 逐段刷新可视化:端点球位置/大小、连线端点/宽度、距离文本内容/朝向
        void UpdateVisuals()
        {
            float ballDiaLocal = _maxDimLocal * _pointSizeFrac;

            for (int i = 0; i < _segments.Count; i++) LayoutSegment(_segments[i], ballDiaLocal);
            if (_pending != null) LayoutSegment(_pending, ballDiaLocal);
        }

        // 摆放单段:球贴局部坐标随模型变换;连线用局部坐标(LineRenderer useWorldSpace=false);文本世界 billboard
        void LayoutSegment(Segment s, float ballDiaLocal)
        {
            if (s.BallA != null) { s.BallA.localPosition = s.LocalA; s.BallA.localScale = Vector3.one * ballDiaLocal; }
            if (s.BallB != null) { s.BallB.localPosition = s.LocalB; s.BallB.localScale = Vector3.one * ballDiaLocal; }

            if (s.Line != null)
            {
                s.Line.SetPosition(0, s.LocalA);
                s.Line.SetPosition(1, s.LocalB);
                // 世界线宽 = 局部球直径 * 系数 * 模型缩放,随缩放自适应
                float worldWidth = ballDiaLocal * _lineWidthFrac * transform.lossyScale.x;
                s.Line.widthMultiplier = Mathf.Max(worldWidth, 1e-4f);
            }

            if (s.Label != null && s.LabelText != null)
            {
                float distMm = Vector3.Distance(s.LocalA, s.LocalB);
                s.LabelText.text = FormatDistance(distMm);

                // 文本浮在段中点上方,偏移随端点球世界直径自适应
                Vector3 midWorld = transform.TransformPoint((s.LocalA + s.LocalB) * 0.5f);
                float worldDia = ballDiaLocal * transform.lossyScale.x;
                s.Label.position = midWorld + Vector3.up * (worldDia * _labelHeightFrac + 0.02f);
                s.Label.localScale = Vector3.one * _labelScale;
                BillboardLabel(s.Label);
            }
        }

        // mm 值格式化:小于 10mm 显 mm,否则显 cm 两位小数
        static string FormatDistance(float mm)
        {
            if (mm < 10f) return $"{mm:F1} mm";
            return $"{mm / 10f:F2} cm";
        }

        void BillboardLabel(Transform label)
        {
            var cam = GetCamera();
            if (cam == null) return;
            label.rotation = Quaternion.LookRotation(label.position - cam.transform.position, Vector3.up);
        }

        // 模型最大维度(局部 mm):端点球/文本据此按比例自适应;数据未就绪时保留上次值
        void RefreshMaxDim()
        {
            if (_controller == null) return;
            Vector3 size = _controller.LocalBounds.size;
            float m = Mathf.Max(size.x, size.y, size.z);
            if (m > 1e-4f) _maxDimLocal = m;
        }

        // === 可视化对象工厂 ===

        // 端点球:去碰撞体 sphere,挂模型下继承局部->世界变换,MPB 着色不建多材质
        Transform CreateBall()
        {
            if (_ballMaterial == null) _ballMaterial = CreateBallMaterial();

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "MeasurePoint";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var tr = go.transform;
            tr.SetParent(transform, false);

            var mr = go.GetComponent<MeshRenderer>();
            if (_ballMaterial != null) mr.sharedMaterial = _ballMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.GetPropertyBlock(_mpb);
            _mpb.SetColor(_BaseColorId, _MeasureColor);
            _mpb.SetColor(_ColorId, _MeasureColor);
            mr.SetPropertyBlock(_mpb);
            return tr;
        }

        // 连线:模型子物体,useWorldSpace=false 用局部坐标随模型变换;两端点
        LineRenderer CreateLine()
        {
            if (_lineMaterial == null) _lineMaterial = CreateLineMaterial();

            var go = new GameObject("MeasureLine");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.numCapVertices = 2;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            if (_lineMaterial != null) lr.sharedMaterial = _lineMaterial;
            lr.startColor = _MeasureColor;
            lr.endColor = _MeasureColor;
            return lr;
        }

        // 距离文本:世界空间 TextMeshPro 根物体,ZTest Always 置顶不被点云遮挡,billboard 朝相机
        Transform CreateLabel(out TextMeshPro text)
        {
            var go = new GameObject("MeasureLabel");
            var tr = go.transform;
            tr.localScale = Vector3.one * _labelScale;

            text = go.AddComponent<TextMeshPro>();
            var font = ResolveFont();
            if (font != null) text.font = font;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = _labelFontSize;
            text.enableWordWrapping = false;
            text.color = _MeasureColor;
            var fontMat = text.fontMaterial;
            if (fontMat != null)
                fontMat.SetFloat(_TmpZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);
            text.rectTransform.sizeDelta = new Vector2(24f, 8f);
            return tr;
        }

        void DestroySegment(Segment s)
        {
            if (s == null) return;
            if (s.BallA != null) Destroy(s.BallA.gameObject);
            if (s.BallB != null) Destroy(s.BallB.gameObject);
            if (s.Line != null) Destroy(s.Line.gameObject);
            if (s.Label != null) Destroy(s.Label.gameObject);
        }

        void DiscardPending()
        {
            if (_pending == null) return;
            DestroySegment(_pending);
            _pending = null;
        }

        // === 捏合检测(复用基因画笔已验证范式) ===

        // 找测量手:任一 tracked 手取指尖世界位置作瞄准点;捏合下降沿(未捏->捏)返回一次放点信号
        void FindHand(out bool hasHand, out bool pinchEdge, out Vector3 tipWorld)
        {
            hasHand = false;
            pinchEdge = false;
            tipWorld = Vector3.zero;
            EnsureHands();
            if (_hands == null) return;

            for (int i = 0; i < _hands.Length; i++)
            {
                var h = _hands[i];
                if (h == null) continue;

                Finger index = FindFinger(h, FingerEnum.index);
                if (index == null || index.tip == null) continue;

                if (!hasHand)
                {
                    hasHand = true;
                    tipWorld = index.tip.position;
                }

                // 每只手每帧求值维持滞回;取首个出现下降沿的手作放点
                bool was = _pinchState.TryGetValue(h, out bool s) && s;
                bool now = IsPinching(h, was);
                _pinchState[h] = now;
                if (now && !was && !pinchEdge)
                {
                    pinchEdge = true;
                    tipWorld = index.tip.position;
                }
            }
        }

        // 捏合检测:拇指尖↔食指尖距离,按两指尖半径和缩放,滞回防抖
        bool IsPinching(Hand h, bool was)
        {
            Finger index = FindFinger(h, FingerEnum.index);
            Finger thumb = FindFinger(h, FingerEnum.thumb);
            if (index == null || thumb == null || index.tip == null || thumb.tip == null)
                return false;

            float dist = Vector3.Distance(index.tip.position, thumb.tip.position);
            float radiusSum = index.tipRadius + thumb.tipRadius;
            float enter = radiusSum * _pinchEnterScale;
            float exit = radiusSum * _pinchExitScale;
            return was ? dist <= exit : dist <= enter;
        }

        static Finger FindFinger(Hand h, FingerEnum type)
        {
            if (h.fingers == null) return null;
            for (int i = 0; i < h.fingers.Length; i++)
                if (h.fingers[i] != null && h.fingers[i].fingerType == type)
                    return h.fingers[i];
            return null;
        }

        void EnsureHands()
        {
            bool needScan = _hands == null || _hands.Length == 0;
            if (!needScan)
                for (int i = 0; i < _hands.Length; i++)
                    if (_hands[i] == null) { needScan = true; break; }
            if (!needScan) return;
            if (Time.time < _nextHandScan) return;

            _nextHandScan = Time.time + 1f;
            _hands = FindObjectsByType<Hand>(FindObjectsSortMode.None);
        }

        // === 材质工厂:URP Unlit 优先,内置管线回退,全剥离则警告(防 Pico 材质剥离) ===

        static Material CreateBallMaterial()
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            if (urp != null)
            {
                var mat = new Material(urp);
                mat.SetColor(_BaseColorId, _MeasureColor);
                return mat;
            }
            var unlit = Shader.Find("Unlit/Color");
            if (unlit != null) return new Material(unlit) { color = _MeasureColor };

            Debug.LogWarning("测量端点球所需 shader 均被剥离(建议加入 Always Included Shaders)");
            return null;
        }

        // 连线材质:置顶不被点云遮挡(ZTest Always),便于内部测量可见
        static Material CreateLineMaterial()
        {
            var urp = Shader.Find("Universal Render Pipeline/Unlit");
            Material mat = null;
            if (urp != null)
            {
                mat = new Material(urp);
                mat.SetColor(_BaseColorId, _MeasureColor);
            }
            else
            {
                var sprite = Shader.Find("Sprites/Default");
                if (sprite != null) mat = new Material(sprite);
            }
            if (mat == null)
            {
                Debug.LogWarning("测量连线所需 shader 均被剥离(建议加入 Always Included Shaders)");
                return null;
            }
            if (mat.HasProperty(_ZTestId))
                mat.SetInt(_ZTestId, (int)UnityEngine.Rendering.CompareFunction.Always);
            return mat;
        }

        // 字体回退链:注入字体 -> 场景现有含中文 TMP 字体 -> 已加载资源含中文字形 -> TMP 默认
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
