using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Autohand;

namespace Dicom.Gene
{
    // 空间画笔:球形涂抹 + 盒框选,写 GeneColorController 的选中掩码(mode2)
    // 用扳机(GetTriggerAxis)驱动,与 grip 抓取/缩放错开;画笔开启期间禁用抓取避免误抓
    // 命中测试在 local 空间(掌心经 InverseTransformPoint 转 local),节流触发,绝不每帧重建
    [RequireComponent(typeof(GeneColorController))]
    public class GeneBrushSelector : MonoBehaviour
    {
        public enum BrushMode { Sphere, Box }

        // 世界空间画笔半径(米),经缩放换算到 local
        [SerializeField] float _worldRadius = 0.03f;
        // 扳机按下阈值,超过视为涂抹中
        [SerializeField] float _triggerThreshold = 0.5f;

        GeneColorController _controller;
        GeneGrabbableSetup _grabbable;
        Hand[] _hands;
        float _nextHandScan;

        BrushMode _mode = BrushMode.Sphere;
        bool _enabled;

        // 球形涂抹节流:记录上次涂抹的 local 球心,位移超半径一半才再测
        float3 _lastPaintLocal;
        bool _hasLastPaint;

        // 盒框选:扳机按下瞬间记 local 起点,松开对起点↔当前 local AABB 置位
        bool _boxDragging;
        float3 _boxStartLocal;
        Vector3 _boxStartWorld;
        Vector3 _boxCurrentWorld;
        bool _prevTriggerDown;

        // 选中集变化(涂抹/框选/清除后触发),携带当前选中数;供 overlay 高亮与面板刷新
        public event Action<int> OnSelectionChanged;

        public BrushMode Mode => _mode;
        public bool BrushEnabled => _enabled;
        public float WorldRadius => _worldRadius;
        public int SelectedCount { get; private set; }

        // 盒拖动预览的世界空间中心/尺寸,供 GeneBrushVisual 显示;非拖动时尺寸为零
        public bool BoxDragging => _boxDragging;
        public Vector3 BoxCenterWorld => (_boxStartWorld + _boxCurrentWorld) * 0.5f;
        public Vector3 BoxSizeWorld => new Vector3(
            Mathf.Abs(_boxCurrentWorld.x - _boxStartWorld.x),
            Mathf.Abs(_boxCurrentWorld.y - _boxStartWorld.y),
            Mathf.Abs(_boxCurrentWorld.z - _boxStartWorld.z));
        // 当前涂抹手的掌心世界位置,供球形笔刷预览体跟随;无手时为零
        public Vector3 ActivePalmWorld { get; private set; }
        public bool HasActivePalm { get; private set; }

        void Awake()
        {
            _controller = GetComponent<GeneColorController>();
            _grabbable = GetComponent<GeneGrabbableSetup>();
        }

        // 开关画笔:开启时禁用抓取(防扳机误抓),关闭时恢复
        public void SetEnabled(bool on)
        {
            _enabled = on;
            SetGrabbableEnabled(!on);
            _hasLastPaint = false;
            _boxDragging = false;
            HasActivePalm = false;
        }

        public void SetMode(BrushMode mode)
        {
            _mode = mode;
            _hasLastPaint = false;
            _boxDragging = false;
        }

        public void SetWorldRadius(float r) => _worldRadius = Mathf.Max(0.001f, r);

        // 清空选择,回到全量渲染
        public void ClearSelection()
        {
            _controller.ClearSelection();
            _hasLastPaint = false;
            _boxDragging = false;
            SelectedCount = 0;
            OnSelectionChanged?.Invoke(0);
        }

        void Update()
        {
            if (!_enabled) return;
            if (_controller.Model == null || !_controller.Model.NativeReady) return;

            Hand hand = FindTriggerHand(out float triggerAxis);
            bool triggerDown = hand != null && triggerAxis >= _triggerThreshold;

            HasActivePalm = hand != null;
            if (hand != null) ActivePalmWorld = PalmPos(hand);

            if (_mode == BrushMode.Sphere)
                UpdateSphere(hand, triggerDown);
            else
                UpdateBox(hand, triggerDown);

            _prevTriggerDown = triggerDown;
        }

        // 球形涂抹:扳机按住时按位移节流,球内 cell 累积置位
        void UpdateSphere(Hand hand, bool triggerDown)
        {
            if (!triggerDown || hand == null) { _hasLastPaint = false; return; }

            float3 centerLocal = (float3)transform.InverseTransformPoint(PalmPos(hand));
            float radiusLocal = _worldRadius / Mathf.Max(transform.lossyScale.x, 1e-6f);

            // 位移不足半径一半则跳过,避免每帧重复测同一片
            if (_hasLastPaint && math.distance(centerLocal, _lastPaintLocal) < radiusLocal * 0.5f)
                return;

            _lastPaintLocal = centerLocal;
            _hasLastPaint = true;

            var mask = _controller.EnsureMask();
            new BrushSphereJob
            {
                CellPos = _controller.Model.CellPos,
                CenterLocal = centerLocal,
                RadiusLocalSq = radiusLocal * radiusLocal,
                Mask = mask
            }.Schedule(_controller.Model.CellCount, 4096).Complete();

            RaiseSelectionChanged();
        }

        // 盒框选:扳机按下记起点,按住更新预览,松开对 local AABB 置位(仅一次)
        void UpdateBox(Hand hand, bool triggerDown)
        {
            if (triggerDown && hand != null)
            {
                Vector3 palm = PalmPos(hand);
                if (!_prevTriggerDown)
                {
                    _boxStartWorld = palm;
                    _boxStartLocal = (float3)transform.InverseTransformPoint(palm);
                    _boxDragging = true;
                }
                _boxCurrentWorld = palm;
            }
            else if (_prevTriggerDown && _boxDragging)
            {
                // 松开:提交盒选
                CommitBox(hand);
                _boxDragging = false;
            }
        }

        void CommitBox(Hand hand)
        {
            float3 endLocal = hand != null
                ? (float3)transform.InverseTransformPoint(PalmPos(hand))
                : (float3)transform.InverseTransformPoint(_boxCurrentWorld);

            float3 min = math.min(_boxStartLocal, endLocal);
            float3 max = math.max(_boxStartLocal, endLocal);

            var mask = _controller.EnsureMask();
            new BrushBoxJob
            {
                CellPos = _controller.Model.CellPos,
                MinLocal = min,
                MaxLocal = max,
                Mask = mask
            }.Schedule(_controller.Model.CellCount, 4096).Complete();

            RaiseSelectionChanged();
        }

        // 统计当前选中数并派发;主线程遍历掩码,涂抹已节流故频率低
        void RaiseSelectionChanged()
        {
            var mask = _controller.EnsureMask();
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] != 0) count++;
            SelectedCount = count;
            OnSelectionChanged?.Invoke(count);
        }

        // 取第一只扳机按下的手;无则取第一只有效手供预览体跟随
        Hand FindTriggerHand(out float triggerAxis)
        {
            triggerAxis = 0f;
            EnsureHands();
            if (_hands == null) return null;

            Hand fallback = null;
            for (int i = 0; i < _hands.Length; i++)
            {
                var h = _hands[i];
                if (h == null) continue;
                if (fallback == null) fallback = h;
                float t = h.GetTriggerAxis();
                if (t > triggerAxis) { triggerAxis = t; }
                if (t >= _triggerThreshold) return h;
            }
            triggerAxis = 0f;
            return fallback;
        }

        Vector3 PalmPos(Hand h) =>
            h.palmTransform != null ? h.palmTransform.position : h.transform.position;

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
