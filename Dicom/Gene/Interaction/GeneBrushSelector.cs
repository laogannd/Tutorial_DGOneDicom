using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Autohand;

namespace Dicom.Gene
{
    // 相机视角套索:手射线用于瞄准,命中点投到相机视口得 2D 光标,用户在视野里画闭合圈(等价屏幕圈选)
    // 松开时以相机为锥顶把每个 cell 投到视口做多边形内点判定,并按深度切片收窄选区
    // 激活信号 = 控制器 grip(GetTriggerAxis) 或 徒手捏合(拇指尖↔食指尖) 任一达标,两者并存
    // 视口光标做 EMA 低通消抖(治乱飞);圈画在相机正前方固定距离,天然不被点云遮挡
    // 画笔开启期间禁用抓取避免误抓;命中测试单次(松开那帧),绝不每帧重建
    [RequireComponent(typeof(GeneColorController))]
    public class GeneBrushSelector : MonoBehaviour
    {
        // 控制器 grip 按下阈值,超过视为激活
        [SerializeField] float _triggerThreshold = 0.5f;
        // 徒手捏合:拇指尖↔食指尖距离阈值,按两指尖半径和缩放;滞回防抖(enter 小于 exit)
        [SerializeField] float _pinchEnterScale = 1.6f;
        [SerializeField] float _pinchExitScale = 2.4f;
        // 视口光标 EMA 平滑系数(0..1,越小越平滑越跟手弱);治手势追踪抖动
        [SerializeField] float _smoothK = 0.35f;
        // 采样节流:相邻视口光标距离超此值(视口单位 0..1)才记一点,防同点堆积
        [SerializeField] float _sampleMinDist = 0.01f;
        // 采样点上限,防长时间画圈溢出
        [SerializeField] int _maxSamples = 256;
        // 套索圈画在相机正前方的距离(米),仅影响可视化不影响命中
        [SerializeField] float _lineDistance = 0.5f;

        GeneColorController _controller;
        GeneGrabbableSetup _grabbable;
        Hand[] _hands;
        float _nextHandScan;

        Camera _camera;

        // 每只手的捏合滞回状态(手实例->是否处于捏合中),避免边界抖动反复触发
        readonly Dictionary<Hand, bool> _pinchState = new Dictionary<Hand, bool>();

        bool _enabled;

        // 深度切片:中心(0..1,0.5=模型中心)与厚度(0..1,1=全深度=兼容旧穿透)
        float _depthCenter = 0.5f;
        float _depthThickness = 1f;

        // 画圈状态:视口空间(0..1)采样点 + EMA 光标
        bool _drawing;
        float2 _smoothed;
        bool _hasSmoothed;
        readonly List<float2> _viewportSamples = new List<float2>(256);
        // 供 visual 画线:视口采样反投到相机正前方固定距离的世界点,每帧重建随头动跟随
        readonly List<Vector3> _trajectoryWorld = new List<Vector3>(256);

        // 选中集变化(画圈/清除后触发),携带当前选中数;供 overlay 高亮与面板刷新
        public event Action<int> OnSelectionChanged;

        public bool BrushEnabled => _enabled;
        public int SelectedCount { get; private set; }

        // 画圈中:供 visual 显示轨迹线
        public bool Drawing => _drawing;
        public IReadOnlyList<Vector3> TrajectoryWorld => _trajectoryWorld;

        // 深度切片参数(面板绑定),clamp 到有效范围
        public float DepthCenter
        {
            get => _depthCenter;
            set => _depthCenter = Mathf.Clamp01(value);
        }
        public float DepthThickness
        {
            get => _depthThickness;
            set => _depthThickness = Mathf.Clamp01(value);
        }

        void Awake()
        {
            _controller = GetComponent<GeneColorController>();
            _grabbable = GetComponent<GeneGrabbableSetup>();
        }

        // 开关画笔:开启时禁用抓取(防 grip 误抓),关闭时恢复并清画圈状态
        public void SetEnabled(bool on)
        {
            _enabled = on;
            SetGrabbableEnabled(!on);
            _pinchState.Clear();
            EndDraw();
        }

        // 清空选择,回到全量渲染
        public void ClearSelection()
        {
            _controller.ClearSelection();
            SelectedCount = 0;
            OnSelectionChanged?.Invoke(0);
        }

        void Update()
        {
            if (!_enabled) return;
            if (_controller.Model == null || !_controller.Model.NativeReady) return;

            var cam = GetCamera();
            if (cam == null) return;

            FindActiveHand(out bool active, out Vector3 origin, out Vector3 forward);

            if (active)
            {
                if (!_drawing) BeginDraw();
                SampleDraw(cam, origin, forward);
            }
            else if (_drawing)
            {
                CommitLasso(cam);
                EndDraw();
            }
        }

        void BeginDraw()
        {
            _drawing = true;
            _hasSmoothed = false;
            _viewportSamples.Clear();
            _trajectoryWorld.Clear();
        }

        void EndDraw()
        {
            _drawing = false;
            _hasSmoothed = false;
            _viewportSamples.Clear();
            _trajectoryWorld.Clear();
        }

        // 采样:手射线与"面向相机、过模型中心"的平面求交得世界光标 -> 视口 -> EMA 平滑 -> 按距离节流
        // 每帧重建轨迹世界点(反投到相机正前方固定距离),使圈随头动稳定显示在眼前
        void SampleDraw(Camera cam, Vector3 origin, Vector3 forward)
        {
            if (!RayToViewPlane(cam, origin, forward, out float2 viewport)) return;

            if (!_hasSmoothed)
            {
                _smoothed = viewport;
                _hasSmoothed = true;
            }
            else
            {
                _smoothed = math.lerp(_smoothed, viewport, Mathf.Clamp01(_smoothK));
            }

            bool add = _viewportSamples.Count == 0;
            if (!add && _viewportSamples.Count < _maxSamples)
                add = math.distance(_smoothed, _viewportSamples[_viewportSamples.Count - 1]) >= _sampleMinDist;

            if (add) _viewportSamples.Add(_smoothed);

            RebuildTrajectory(cam);
        }

        // 手射线与过模型中心、法线朝相机的平面求交,命中点转视口(0..1)
        bool RayToViewPlane(Camera cam, Vector3 origin, Vector3 forward, out float2 viewport)
        {
            viewport = default;

            Vector3 planePoint = transform.position;
            Vector3 n = cam.transform.forward;
            float denom = Vector3.Dot(forward, n);
            // 射线与平面近平行:无稳定交点,跳过
            if (Mathf.Abs(denom) < 1e-4f) return false;

            float t = Vector3.Dot(planePoint - origin, n) / denom;
            // 交点在射线背后:跳过
            if (t <= 0f) return false;

            Vector3 world = origin + forward * t;
            Vector3 vp = cam.WorldToViewportPoint(world);
            // 相机背后:排除
            if (vp.z <= 0f) return false;

            viewport = new float2(vp.x, vp.y);
            return true;
        }

        // 视口采样反投到相机正前方 _lineDistance 处的世界点,供 LineRenderer 画圈
        void RebuildTrajectory(Camera cam)
        {
            _trajectoryWorld.Clear();
            for (int i = 0; i < _viewportSamples.Count; i++)
            {
                float2 uv = _viewportSamples[i];
                _trajectoryWorld.Add(cam.ViewportToWorldPoint(new Vector3(uv.x, uv.y, _lineDistance)));
            }
        }

        // 松开:采样点 >=3 时以相机视口多边形 + 深度切片调度 Job 置位掩码(累积 OR)
        void CommitLasso(Camera cam)
        {
            if (_viewportSamples.Count < 3) return;

            float4x4 viewProj = math.mul((float4x4)cam.projectionMatrix, (float4x4)cam.worldToCameraMatrix);
            float4x4 localToWorld = (float4x4)transform.localToWorldMatrix;

            // 深度范围:模型局部包围盒 8 角投到视口取深度(clip.w)极值,按中心/厚度收窄
            if (!ComputeDepthRange(viewProj, localToWorld, out float depthMin, out float depthMax)) return;

            var polygon = new NativeArray<float2>(_viewportSamples.Count, Allocator.TempJob);
            try
            {
                for (int i = 0; i < _viewportSamples.Count; i++) polygon[i] = _viewportSamples[i];

                var mask = _controller.EnsureMask();
                new GeneLassoJob
                {
                    CellPos = _controller.Model.CellPos,
                    LocalToWorld = localToWorld,
                    ViewProj = viewProj,
                    Polygon = polygon,
                    DepthMin = depthMin,
                    DepthMax = depthMax,
                    Mask = mask
                }.Schedule(_controller.Model.CellCount, 4096).Complete();
            }
            finally
            {
                if (polygon.IsCreated) polygon.Dispose();
            }

            RaiseSelectionChanged();
        }

        // 模型局部包围盒 8 角变换到裁剪空间取 w(视深度)极值;按 _depthCenter/_depthThickness 收窄为切片
        bool ComputeDepthRange(float4x4 viewProj, float4x4 localToWorld, out float depthMin, out float depthMax)
        {
            depthMin = 0f;
            depthMax = 0f;

            Bounds b = _controller.LocalBounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;

            float lo = float.MaxValue;
            float hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                float4 world = math.mul(localToWorld, new float4((float3)corner, 1f));
                float4 clip = math.mul(viewProj, world);
                if (clip.w <= 1e-5f) continue;
                lo = math.min(lo, clip.w);
                hi = math.max(hi, clip.w);
            }
            // 全部角在相机背后:无有效深度
            if (lo > hi) return false;

            float mid = math.lerp(lo, hi, _depthCenter);
            float half = (hi - lo) * _depthThickness * 0.5f;
            depthMin = mid - half;
            depthMax = mid + half;
            return true;
        }

        // 统计当前选中数并派发;画圈单次触发故频率低
        void RaiseSelectionChanged()
        {
            var mask = _controller.EnsureMask();
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] != 0) count++;
            SelectedCount = count;
            OnSelectionChanged?.Invoke(count);
        }

        // 取激活手:grip 达标或徒手捏合任一即激活;射线原点=掌心,方向=掌心 forward
        // IsPinching 每帧对每只手求值维持滞回状态,故遍历全部手不提前 return
        void FindActiveHand(out bool active, out Vector3 origin, out Vector3 forward)
        {
            active = false;
            origin = Vector3.zero;
            forward = Vector3.forward;
            EnsureHands();
            if (_hands == null) return;

            for (int i = 0; i < _hands.Length; i++)
            {
                var h = _hands[i];
                if (h == null) continue;

                bool grip = h.GetTriggerAxis() >= _triggerThreshold;
                bool pinch = IsPinching(h);
                if ((grip || pinch) && !active)
                {
                    active = true;
                    PalmPose(h, out origin, out forward);
                }
            }
        }

        // 捏合检测:拇指尖↔食指尖距离,按两指尖半径和缩放,滞回防抖(每手独立状态)
        bool IsPinching(Hand h)
        {
            Finger index = FindFinger(h, FingerEnum.index);
            Finger thumb = FindFinger(h, FingerEnum.thumb);
            if (index == null || thumb == null || index.tip == null || thumb.tip == null)
                return false;

            float dist = Vector3.Distance(index.tip.position, thumb.tip.position);
            float radiusSum = index.tipRadius + thumb.tipRadius;
            float enter = radiusSum * _pinchEnterScale;
            float exit = radiusSum * _pinchExitScale;

            bool was = _pinchState.TryGetValue(h, out bool s) && s;
            // 滞回:未捏合时须缩到 enter 以内才触发,捏合中须张到 exit 以外才释放
            bool now = was ? dist <= exit : dist <= enter;
            _pinchState[h] = now;
            return now;
        }

        static Finger FindFinger(Hand h, FingerEnum type)
        {
            if (h.fingers == null) return null;
            for (int i = 0; i < h.fingers.Length; i++)
                if (h.fingers[i] != null && h.fingers[i].fingerType == type)
                    return h.fingers[i];
            return null;
        }

        // 射线原点与朝向取掌心 transform;无 palm 回退手物体本身
        void PalmPose(Hand h, out Vector3 origin, out Vector3 forward)
        {
            Transform t = h.palmTransform != null ? h.palmTransform : h.transform;
            origin = t.position;
            forward = t.forward;
        }

        // 主相机缓存,丢失时重取(不静默默认:取不到返回 null 由调用方跳过)
        Camera GetCamera()
        {
            if (_camera == null) _camera = Camera.main;
            return _camera;
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

        void SetGrabbableEnabled(bool on)
        {
            if (_grabbable == null) _grabbable = GetComponent<GeneGrabbableSetup>();
            if (_grabbable != null && _grabbable.Grabbable != null)
                _grabbable.Grabbable.enabled = on;
        }
    }
}
