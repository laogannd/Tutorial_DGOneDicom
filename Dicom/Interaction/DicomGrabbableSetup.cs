using UnityEngine;
using Autohand;

using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // 运行时为点云模型挂上 AutoHand 抓取所需组件
    // 点云无逐点碰撞，用一个包围整个体积的 BoxCollider 承载抓取
    [RequireComponent(typeof(PointCloudController))]
    public class DicomGrabbableSetup : MonoBehaviour
    {
        [SerializeField] float _mass = 1f;

        // 预设要忽略的碰撞层:无论 Physics 碰撞矩阵如何,点云碰撞体都不与这些层产生物理碰撞
        // 自身仍在 AutoHand 的 grabbable 层,排除层不影响手的抓取检测
        [SerializeField] LayerMask _excludeLayers;

        PointCloudController _controller;
        Grabbable _grabbable;
        BoxCollider _collider;
        Rigidbody _rigidbody;

        public Grabbable Grabbable => _grabbable;

        // 预设排除层:运行时动态挂载时由外部(如 DicomDemoBootstrap)统一传入
        // 赋值即应用到已建好的刚体/碰撞体,后续重建也会保持
        public LayerMask ExcludeLayers
        {
            get => _excludeLayers;
            set
            {
                _excludeLayers = value;
                if (_rigidbody != null) _rigidbody.excludeLayers = value;
                if (_collider != null) _collider.excludeLayers = value;
            }
        }

        void Awake()
        {
            _controller = GetComponent<PointCloudController>();
            // 订阅包围盒变化:每次重建(调阈值/方向/归一化)都刷新碰撞盒,紧贴当前可见点云
            _controller.OnBoundsChanged += OnBoundsChanged;
            // 立即建好刚体/碰撞体/Grabbable，避免 TwoHandScaler 的 RequireComponent
            // 抢先创建 Grabbable 时刚体与碰撞体尚未就绪，导致 Grabbable.Awake 注册失败
            EnsureComponents();
        }

        void OnDestroy()
        {
            if (_controller != null)
                _controller.OnBoundsChanged -= OnBoundsChanged;
            if (_grabbable != null)
            {
                _grabbable.OnGrabEvent -= OnGrabbed;
                _grabbable.OnReleaseEvent -= OnReleased;
            }
        }

        // 点云重建后据可见点真实 AABB 配置碰撞盒;过滤后点云常偏一侧,中心随之偏移
        void OnBoundsChanged(Bounds bounds)
        {
            EnsureComponents();
            _collider.center = bounds.center;
            _collider.size = bounds.size;
            // 碰撞盒尺寸变更后让 Grabbable 重新登记碰撞体与抓取层
            _grabbable.body = _rigidbody;
            _grabbable.UpdateGrabbableColliderSettings();
        }

        void EnsureComponents()
        {
            // 先建刚体，保证后续 Grabbable.Awake 能取到 body
            if (_rigidbody == null)
            {
                _rigidbody = gameObject.GetComponent<Rigidbody>();
                if (_rigidbody == null) _rigidbody = gameObject.AddComponent<Rigidbody>();
            }
            _rigidbody.mass = _mass;
            _rigidbody.useGravity = false;
            // 未被抓取时锁定为 Kinematic 定在原地,抓起时由事件解锁交给手/物理驱动
            _rigidbody.isKinematic = true;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // 再建碰撞体，Grabbable 注册抓取碰撞体时才能扫到
            if (_collider == null)
            {
                _collider = gameObject.GetComponent<BoxCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            }

            // 显式设到 Grabbable 层，AutoHand 的手只在该层做 OverlapSphere 检测
            gameObject.layer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);

            // 应用预设排除层:刚体与碰撞体都设,确保物理碰撞被忽略且重建后仍生效
            _rigidbody.excludeLayers = _excludeLayers;
            _collider.excludeLayers = _excludeLayers;

            // 最后建 Grabbable，此时刚体与碰撞体均已就绪
            if (_grabbable == null)
            {
                _grabbable = gameObject.GetComponent<Grabbable>();
                if (_grabbable == null) _grabbable = gameObject.AddComponent<Grabbable>();
                // Grabbable 运行时动态创建,无法在 Inspector 拖事件,代码订阅抓取/释放切换 Kinematic
                _grabbable.OnGrabEvent += OnGrabbed;
                _grabbable.OnReleaseEvent += OnReleased;
            }
            _grabbable.body = _rigidbody;
            // 允许双手同时抓取，供 TwoHandScaler 缩放使用
            _grabbable.singleHandOnly = false;
            _grabbable.instantGrab = false;
        }

        // 被手抓起即解除 Kinematic,交给手/物理驱动
        void OnGrabbed(Hand hand, Grabbable grab)
        {
            _rigidbody.isKinematic = false;
        }

        // 完全脱手(无任何手持有)才重新锁定为 Kinematic 定在原地
        void OnReleased(Hand hand, Grabbable grab)
        {
            if (_grabbable.HeldCount() == 0)
                _rigidbody.isKinematic = true;
        }
    }
}
