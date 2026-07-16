using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Autohand;

namespace Dicom.Gene
{
    // 3D 球形空间画笔:手上握一个球形笔刷,伸进基因点云里扫过就染色选区
    // 触发 = 徒手捏合(拇指尖↔食指尖合拢),滞回防抖;合拢才染色,松开即停,不是一直触发
    // 球心取食指指尖(无则回退掌心),捏合期间每帧调度 GeneBrushJob 把半径内 cell 累积置位(OR)
    // job 顺带归约:当前选中总数(仅变化才重建点集) + 球心最近命中 cell 的 tag(供空间显示所属部位)
    // 画笔开启期间禁用抓取避免误抓
    [RequireComponent(typeof(GeneColorController))]
    public class GeneBrushSelector : MonoBehaviour
    {
        // 徒手捏合:拇指尖↔食指尖距离阈值,按两指尖半径和缩放;滞回防抖(enter 小于 exit)
        [SerializeField] float _pinchEnterScale = 1.6f;
        [SerializeField] float _pinchExitScale = 2.4f;
        // 笔刷半径(世界米);默认 3cm,范围供 UI 滑条
        [SerializeField] float _brushRadius = 0.03f;
        [SerializeField] float _minRadius = 0.005f;
        [SerializeField] float _maxRadius = 0.15f;

        const int BlockSize = 4096;

        GeneColorController _controller;
        GeneGrabbableSetup _grabbable;
        GeneTagNameTable _tagNameTable;
        Hand[] _hands;
        float _nextHandScan;

        // 每只手的捏合滞回状态(手实例->是否处于捏合中),避免边界抖动反复触发
        readonly System.Collections.Generic.Dictionary<Hand, bool> _pinchState =
            new System.Collections.Generic.Dictionary<Hand, bool>();

        bool _enabled;

        // 笔刷世界球心(捏合激活时更新,供 visual 画指示球)
        Vector3 _brushCenter;
        bool _hasBrushCenter;
        // 是否正在染色(捏合按住中)
        bool _painting;

        // 笔刷球心当前所属标记部位(半径内最近 cell 的 tag);无命中为 int.MinValue
        int _currentTag = int.MinValue;

        // 上次选中总数,用于判断本帧是否新增置位(仅新增才重建点集)
        int _lastSelectedCount;

        // 选中集变化(扫过染色/清除后触发),携带当前选中数;供 overlay 高亮与面板刷新
        public event Action<int> OnSelectionChanged;

        public bool BrushEnabled => _enabled;
        public int SelectedCount { get; private set; }

        // 供 visual 显示笔刷指示球
        public bool Painting => _painting;
        public bool HasBrushCenter => _hasBrushCenter;
        public Vector3 BrushCenterWorld => _brushCenter;

        // 当前所属标记部位:是否命中、tag、名称、分类颜色(供空间文本/指示球)
        public bool HasCurrentTag => _currentTag != int.MinValue;
        public int CurrentTag => _currentTag;
        public string CurrentTagName => ResolveTagName(_currentTag);
        public Color CurrentTagColor => HasCurrentTag ? GeneTagPalette.Color(_currentTag) : UnityEngine.Color.white;

        // 笔刷半径(世界米),clamp 到有效范围;供面板绑定
        public float BrushRadius
        {
            get => _brushRadius;
            set => _brushRadius = Mathf.Clamp(value, _minRadius, _maxRadius);
        }
        public float MinRadius => _minRadius;
        public float MaxRadius => _maxRadius;

        void Awake()
        {
            _controller = GetComponent<GeneColorController>();
            _grabbable = GetComponent<GeneGrabbableSetup>();
        }

        // 注入 tag->名映射(由 Bootstrap 传入,可空则回退 "区域{tag}")
        public void SetTagNameTable(GeneTagNameTable table) => _tagNameTable = table;

        // 暴露已注入的 tag 名表,供 VR 面板复用同一张(工厂未绑时面板从此取,免区域名回退数字)
        public GeneTagNameTable TagNameTable => _tagNameTable;

        // 开关画笔:开启时禁用抓取(防 grip 误抓),关闭时恢复并清笔刷状态
        public void SetEnabled(bool on)
        {
            _enabled = on;
            SetGrabbableEnabled(!on);
            _pinchState.Clear();
            _painting = false;
            _hasBrushCenter = false;
            _currentTag = int.MinValue;
        }

        // 清空选择,回到全量渲染
        public void ClearSelection()
        {
            _controller.ClearSelection();
            SelectedCount = 0;
            _lastSelectedCount = 0;
            OnSelectionChanged?.Invoke(0);
        }

        void Update()
        {
            if (!_enabled) return;
            if (_controller.Model == null || !_controller.Model.NativeReady) return;

            // 悬停(任一 tracked 手)即显示指示球供预览半径;仅捏合的手才染色
            FindHand(out bool hasHand, out bool pinching, out Vector3 center);

            _hasBrushCenter = hasHand;
            _painting = pinching;
            if (!hasHand)
            {
                _currentTag = int.MinValue;
                return;
            }

            _brushCenter = center;
            if (pinching)
                PaintAt(center);
            else
                _currentTag = int.MinValue; // 悬停不染色,指示球中性白便于看清大小
        }

        // 球体扫过染色(分块并行):半径内 cell 累积置位;仅当新增了选中才重建点集与派发
        // 同一 job 归约出当前所属 tag(最近命中 cell)供空间显示
        void PaintAt(Vector3 centerWorld)
        {
            var mask = _controller.EnsureMask();
            if (!mask.IsCreated) return;

            int cellCount = _controller.Model.CellCount;
            int blocks = (cellCount + BlockSize - 1) / BlockSize;

            var blockSelected = new NativeArray<int>(blocks, Allocator.TempJob);
            var blockNearestDistSq = new NativeArray<float>(blocks, Allocator.TempJob);
            var blockNearestTag = new NativeArray<int>(blocks, Allocator.TempJob);

            try
            {
                new GeneBrushJob
                {
                    CellPos = _controller.Model.CellPos,
                    CellTag = _controller.Model.CellTag,
                    LocalToWorld = (float4x4)transform.localToWorldMatrix,
                    BrushCenterWorld = centerWorld,
                    RadiusSqWorld = _brushRadius * _brushRadius,
                    BlockSize = BlockSize,
                    CellCount = cellCount,
                    Mask = mask,
                    BlockSelected = blockSelected,
                    BlockNearestDistSq = blockNearestDistSq,
                    BlockNearestTag = blockNearestTag
                }.Schedule(blocks, 1).Complete();

                // 归约块级结果:总选中数 + 全局最近命中 tag
                int count = 0;
                float nearestDistSq = float.MaxValue;
                int nearestTag = int.MinValue;
                for (int b = 0; b < blocks; b++)
                {
                    count += blockSelected[b];
                    if (blockNearestDistSq[b] < nearestDistSq)
                    {
                        nearestDistSq = blockNearestDistSq[b];
                        nearestTag = blockNearestTag[b];
                    }
                }
                _currentTag = nearestTag;

                if (count == _lastSelectedCount) return;

                _lastSelectedCount = count;
                SelectedCount = count;
                // 露出选区显色(重建点集),并派发供 overlay 高亮/面板刷新
                _controller.ApplySelection();
                OnSelectionChanged?.Invoke(count);
            }
            finally
            {
                if (blockSelected.IsCreated) blockSelected.Dispose();
                if (blockNearestDistSq.IsCreated) blockNearestDistSq.Dispose();
                if (blockNearestTag.IsCreated) blockNearestTag.Dispose();
            }
        }

        // 供 visual 解析任意 tag 的人类可读名(区域空间文本用)
        public string GetTagName(int tag) => ResolveTagName(tag);

        string ResolveTagName(int tag)
        {
            if (tag == int.MinValue) return "";
            if (_tagNameTable != null)
            {
                string name = _tagNameTable.GetName(tag);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return $"区域{tag}";
        }

        // 取笔刷手:任一 tracked 手都作悬停(显示指示球预览半径);捏合的手优先并触发染色
        // 球心:捏合取拇指尖↔食指尖中点,悬停取食指指尖(无则回退掌心)
        // IsPinching 每帧对每只手求值维持滞回状态,故遍历全部手不提前 return
        void FindHand(out bool hasHand, out bool pinching, out Vector3 center)
        {
            hasHand = false;
            pinching = false;
            center = Vector3.zero;
            EnsureHands();
            if (_hands == null) return;

            for (int i = 0; i < _hands.Length; i++)
            {
                var h = _hands[i];
                if (h == null) continue;

                bool pinch = IsPinching(h);
                // 捏合手最高优先:立即锁定为染色手
                if (pinch)
                {
                    hasHand = true;
                    pinching = true;
                    center = BrushOrigin(h);
                    // 继续循环仅为维持其余手的滞回状态,不再改写 center
                    for (int j = i + 1; j < _hands.Length; j++)
                        if (_hands[j] != null) IsPinching(_hands[j]);
                    return;
                }
                // 未捏合:记录首只手作悬停中心(不打断,后续可能出现捏合手覆盖)
                if (!hasHand)
                {
                    hasHand = true;
                    center = HoverOrigin(h);
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

        // 笔刷球心:捏合时取拇指尖与食指尖中点(合拢处),更符合"捏住画"的直觉;无指尖回退掌心
        Vector3 BrushOrigin(Hand h)
        {
            Finger index = FindFinger(h, FingerEnum.index);
            Finger thumb = FindFinger(h, FingerEnum.thumb);
            if (index != null && index.tip != null && thumb != null && thumb.tip != null)
                return (index.tip.position + thumb.tip.position) * 0.5f;
            if (index != null && index.tip != null) return index.tip.position;
            Transform t = h.palmTransform != null ? h.palmTransform : h.transform;
            return t.position;
        }

        // 悬停球心:未捏合时取食指指尖(无则回退掌心),指示球随指尖飘,供瞄准与预览半径
        Vector3 HoverOrigin(Hand h)
        {
            Finger index = FindFinger(h, FingerEnum.index);
            if (index != null && index.tip != null) return index.tip.position;
            Transform t = h.palmTransform != null ? h.palmTransform : h.transform;
            return t.position;
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
