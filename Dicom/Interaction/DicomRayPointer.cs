using UnityEngine;
using Autohand;

namespace Dicom.Interaction
{
    // 单手远程射线指向器:挂在 AutoHand 的左/右手物体上
    // 用 grip(握持键)触发远程拖动,与扳机近距离物理抓取错开,两套并存
    // 自身只负责命中检测、grip 状态轮询、射线显示;具体拖动/缩放/旋转由 DicomRayManipulator 读取本组件状态执行
    //
    // 装配:挂到手物体上,可选指定 pointerTransform(射线起点,默认用 Hand.palmTransform)
    // 与 LineRenderer(显示射线,可不填)。DicomRayManipulator 引用左右两个本组件
    public class DicomRayPointer : MonoBehaviour
    {
        [SerializeField] Hand _hand;
        // 射线起点与朝向,未指定则用 hand.palmTransform
        [SerializeField] Transform _pointerTransform;
        // 命中检测层,默认 Grabbable 层(与近距离抓取同层)
        [SerializeField] LayerMask _layers;
        [SerializeField] float _maxRange = 8f;
        [SerializeField] float _sphereRadius = 0.03f;
        // 射线可视化,可不填
        [SerializeField] LineRenderer _line;

        // 当前射线悬停/拖动命中的点云模型,无命中为 null
        DicomModelTransform _target;
        Vector3 _rayOrigin;
        Vector3 _rayDirection;
        float _hitDistance;
        bool _gripping;

        public Hand Hand => _hand;
        public DicomModelTransform Target => _target;
        public Vector3 RayOrigin => _rayOrigin;
        public Vector3 RayDirection => _rayDirection;
        // 命中点到射线起点的距离,拖动时锚点 = RayOrigin + RayDirection * HitDistance
        public float HitDistance => _hitDistance;
        public bool Gripping => _gripping;
        // 正在拖动:grip 按下且有目标
        public bool IsDragging => _gripping && _target != null;

        void Awake()
        {
            if (_hand == null) _hand = GetComponentInParent<Hand>();
            if (_pointerTransform == null && _hand != null) _pointerTransform = _hand.palmTransform;
            if (_layers == 0) _layers = LayerMask.GetMask(Hand.grabbableLayerNameDefault);
        }

        void Update()
        {
            if (_pointerTransform == null) return;

            _rayOrigin = _pointerTransform.position;
            _rayDirection = _pointerTransform.forward;
            // 轮询 grip(握持键)状态,避免依赖事件签名;由 HandControllerLink 每帧驱动 squeezing
            _gripping = _hand != null && _hand.squeezing;

            // 拖动中保持目标不变,仅更新命中距离会引入跳变,这里锁定拖动开始时的锚点距离
            // 故拖动中不重新检测目标,松开 grip 才解除
            if (IsDragging)
            {
                UpdateLine(_rayOrigin + _rayDirection * _hitDistance);
                return;
            }

            DetectTarget();
        }

        // grip 未按下时持续检测射线命中的点云模型
        void DetectTarget()
        {
            bool hit = Physics.SphereCast(_rayOrigin, _sphereRadius, _rayDirection,
                out RaycastHit info, _maxRange, _layers);

            if (hit)
            {
                var model = info.transform.GetComponent<DicomModelTransform>();
                _target = model;
                _hitDistance = info.distance;
                UpdateLine(info.point);
            }
            else
            {
                _target = null;
                _hitDistance = _maxRange;
                UpdateLine(_rayOrigin + _rayDirection * _maxRange);
            }
        }

        // 拖动开始瞬间(grip 按下)目标与命中距离已被锁定:IsDragging 后不再 DetectTarget,
        // _hitDistance 保持命中瞬间值,锚点距离天然冻结,无需外部干预

        void UpdateLine(Vector3 end)
        {
            if (_line == null) return;
            // 仅悬停到目标或拖动时显示射线,空指向不显示
            bool show = _target != null || IsDragging;
            _line.enabled = show;
            if (!show) return;
            _line.positionCount = 2;
            _line.SetPosition(0, _rayOrigin);
            _line.SetPosition(1, end);
        }
    }
}
