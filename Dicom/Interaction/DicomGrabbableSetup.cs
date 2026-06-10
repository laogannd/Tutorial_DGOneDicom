using UnityEngine;
using Autohand;

using Dicom.Core;
using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // 运行时为点云模型挂上 AutoHand 抓取所需组件
    // 点云无逐点碰撞，用一个包围整个体积的 BoxCollider 承载抓取
    [RequireComponent(typeof(PointCloudController))]
    public class DicomGrabbableSetup : MonoBehaviour
    {
        [SerializeField] float _mass = 1f;

        PointCloudController _controller;
        Grabbable _grabbable;
        BoxCollider _collider;
        Rigidbody _rigidbody;

        public Grabbable Grabbable => _grabbable;

        void Awake()
        {
            _controller = GetComponent<PointCloudController>();
            _controller.OnLoaded += OnDatasetLoaded;
            // 立即建好刚体/碰撞体/Grabbable，避免 TwoHandScaler 的 RequireComponent
            // 抢先创建 Grabbable 时刚体与碰撞体尚未就绪，导致 Grabbable.Awake 注册失败
            EnsureComponents();
        }

        void OnDestroy()
        {
            if (_controller != null)
                _controller.OnLoaded -= OnDatasetLoaded;
            if (_grabbable != null)
            {
                _grabbable.OnGrabEvent -= OnGrabbed;
                _grabbable.OnReleaseEvent -= OnReleased;
            }
        }

        // 加载完成才知道体积尺寸，据此配置碰撞盒
        void OnDatasetLoaded(DicomDataset dataset)
        {
            EnsureComponents();
            // 包围盒尺寸 = 体素数 * 间距，与 Job 中以中心为原点的点布局对齐
            var size = new Vector3(
                dataset.Width * dataset.Spacing.x,
                dataset.Height * dataset.Spacing.y,
                dataset.Depth * dataset.Spacing.z);
            _collider.center = Vector3.zero;
            _collider.size = size;
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
