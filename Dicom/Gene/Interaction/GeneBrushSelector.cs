using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Autohand;

namespace Dicom.Gene
{
    // 空间套索:捏合指向点云画圈,松开时以手部射线视锥投影选中圈内 cell,写 GeneColorController 选中掩码(mode2)
    // 激活信号 = 控制器 grip(GetTriggerAxis) 或 徒手捏合(拇指尖↔食指尖) 任一达标,两者并存
    // 锥顶 = 激活手 palmTransform 起始位置;画圈期间采样 palmTransform.forward 方向,松开构建闭合多边形
    // 画笔开启期间禁用抓取避免误抓;命中测试单次(松开那帧),绝不每帧重建
    [RequireComponent(typeof(GeneColorController))]
    public class GeneBrushSelector : MonoBehaviour
    {
        // 控制器 grip 按下阈值,超过视为激活
        [SerializeField] float _triggerThreshold = 0.5f;
        // 徒手捏合:拇指尖↔食指尖距离阈值,按两指尖半径和缩放;滞回防抖(enter 小于 exit)
        [SerializeField] float _pinchEnterScale = 1.6f;
        [SerializeField] float _pinchExitScale = 2.4f;
        // 采样节流:相邻采样方向夹角超过此角度(度)才记一点,防同向堆积
        [SerializeField] float _sampleAngleDeg = 1.5f;
        // 采样点上限,防长时间画圈溢出
        [SerializeField] int _maxSamples = 256;

        GeneColorController _controller;
        GeneGrabbableSetup _grabbable;
        Hand[] _hands;
        float _nextHandScan;

        // 每只手的捏合滞回状态(手实例->是否处于捏合中),避免边界抖动反复触发
        readonly Dictionary<Hand, bool> _pinchState = new Dictionary<Hand, bool>();

        bool _enabled;

        // 画圈状态:锥顶固定为起笔 palm 位置,采样存 palmTransform.forward 方向
        bool _drawing;
        float3 _apex;
        float _drawDistance = 1f;
        float3 _lastSampleDir;
        readonly List<float3> _sampleDirs = new List<float3>(256);
        // 供 visual 画线:采样方向反算到锥顶前固定距离的世界点
        readonly List<Vector3> _trajectoryWorld = new List<Vector3>(256);

        // 选中集变化(画圈/清除后触发),携带当前选中数;供 overlay 高亮与面板刷新
        public event Action<int> OnSelectionChanged;

        public bool BrushEnabled => _enabled;
        public int SelectedCount { get; private set; }

        // 画圈中:供 visual 显示轨迹线
        public bool Drawing => _drawing;
        public Vector3 Apex => (Vector3)_apex;
        public IReadOnlyList<Vector3> TrajectoryWorld => _trajectoryWorld;

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

            FindActiveHand(out bool active, out Vector3 origin, out Vector3 forward);

            if (active)
            {
                if (!_drawing) BeginDraw(origin);
                SampleDraw(forward);
            }
            else if (_drawing)
            {
                CommitLasso();
                EndDraw();
            }
        }

        // 起笔:锁定锥顶,记画圈显示距离(锥顶到模型中心),清采样
        void BeginDraw(Vector3 origin)
        {
            _drawing = true;
            _apex = (float3)origin;
            _drawDistance = Mathf.Max(0.05f, Vector3.Distance(origin, transform.position));
            _sampleDirs.Clear();
            _trajectoryWorld.Clear();
        }

        void EndDraw()
        {
            _drawing = false;
            _sampleDirs.Clear();
            _trajectoryWorld.Clear();
        }

        // 采样当前射线方向,按夹角节流,反算世界点供画线
        void SampleDraw(Vector3 forward)
        {
            float3 dir = math.normalizesafe((float3)forward);
            if (math.lengthsq(dir) < 1e-8f) return;

            if (_sampleDirs.Count > 0)
            {
                if (_sampleDirs.Count >= _maxSamples) return;
                // 夹角不足阈值则跳过,避免同向堆积
                if (math.dot(dir, _lastSampleDir) >= math.cos(math.radians(_sampleAngleDeg)))
                    return;
            }

            _lastSampleDir = dir;
            _sampleDirs.Add(dir);
            _trajectoryWorld.Add((Vector3)(_apex + dir * _drawDistance));
        }

        // 松开:采样点 >=3 时构建视锥基与多边形,调度 Job 置位掩码(累积 OR)
        void CommitLasso()
        {
            if (_sampleDirs.Count < 3) return;

            // 视锥前向轴取采样方向均值;右/上向由世界上向叉乘得正交基,退化时换参考轴
            float3 forward = float3.zero;
            for (int i = 0; i < _sampleDirs.Count; i++) forward += _sampleDirs[i];
            forward = math.normalizesafe(forward, new float3(0f, 0f, 1f));

            float3 refUp = math.abs(math.dot(forward, math.up())) > 0.99f ? math.right() : math.up();
            float3 right = math.normalizesafe(math.cross(refUp, forward), math.right());
            float3 up = math.cross(forward, right);

            // 采样方向投影到 f=1 平面得多边形 uv;背向的采样点跳过
            var poly = new List<float2>(_sampleDirs.Count);
            for (int i = 0; i < _sampleDirs.Count; i++)
            {
                float3 d = _sampleDirs[i];
                float f = math.dot(d, forward);
                if (f <= 1e-4f) continue;
                poly.Add(new float2(math.dot(d, right) / f, math.dot(d, up) / f));
            }
            if (poly.Count < 3) return;

            var polygon = new NativeArray<float2>(poly.Count, Allocator.TempJob);
            try
            {
                for (int i = 0; i < poly.Count; i++) polygon[i] = poly[i];

                var mask = _controller.EnsureMask();
                new GeneLassoJob
                {
                    CellPos = _controller.Model.CellPos,
                    LocalToWorld = (float4x4)transform.localToWorldMatrix,
                    Apex = _apex,
                    Forward = forward,
                    Right = right,
                    Up = up,
                    Polygon = polygon,
                    Mask = mask
                }.Schedule(_controller.Model.CellCount, 4096).Complete();
            }
            finally
            {
                if (polygon.IsCreated) polygon.Dispose();
            }

            RaiseSelectionChanged();
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
