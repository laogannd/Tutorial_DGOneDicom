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
        [SerializeField] bool _kinematicWhenIdle = true;
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
        }

        void OnDestroy()
        {
            if (_controller != null)
                _controller.OnLoaded -= OnDatasetLoaded;
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
        }

        void EnsureComponents()
        {
            if (_rigidbody == null)
            {
                _rigidbody = gameObject.GetComponent<Rigidbody>();
                if (_rigidbody == null) _rigidbody = gameObject.AddComponent<Rigidbody>();
            }
            _rigidbody.mass = _mass;
            _rigidbody.useGravity = false;
            _rigidbody.isKinematic = _kinematicWhenIdle;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            if (_collider == null)
            {
                _collider = gameObject.GetComponent<BoxCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            }

            if (_grabbable == null)
            {
                _grabbable = gameObject.GetComponent<Grabbable>();
                if (_grabbable == null) _grabbable = gameObject.AddComponent<Grabbable>();
            }
            // 允许双手同时抓取，供 TwoHandScaler 缩放使用
            _grabbable.singleHandOnly = false;
            _grabbable.instantGrab = false;
        }
    }
}
