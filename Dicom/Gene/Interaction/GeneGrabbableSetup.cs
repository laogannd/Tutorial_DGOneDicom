using UnityEngine;
using Autohand;

namespace Dicom.Gene
{
    // 运行时为基因点云挂 AutoHand 抓取组件,仿 DicomGrabbableSetup 但依赖 GeneColorController
    // 点云无逐点碰撞,用包围整个体积的 BoxCollider 承载抓取,随重建刷新尺寸
    [RequireComponent(typeof(GeneColorController))]
    public class GeneGrabbableSetup : MonoBehaviour
    {
        [SerializeField] float _mass = 1f;
        [SerializeField] LayerMask _excludeLayers;

        GeneColorController _controller;
        Grabbable _grabbable;
        BoxCollider _collider;
        Rigidbody _rigidbody;

        public Grabbable Grabbable => _grabbable;

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
            _controller = GetComponent<GeneColorController>();
            _controller.OnBoundsChanged += OnBoundsChanged;
            EnsureComponents();
        }

        void OnDestroy()
        {
            if (_controller != null) _controller.OnBoundsChanged -= OnBoundsChanged;
            if (_grabbable != null)
            {
                _grabbable.OnGrabEvent -= OnGrabbed;
                _grabbable.OnReleaseEvent -= OnReleased;
            }
        }

        void OnBoundsChanged(Bounds bounds)
        {
            EnsureComponents();
            _collider.center = bounds.center;
            _collider.size = bounds.size;
            _grabbable.body = _rigidbody;
            _grabbable.UpdateGrabbableColliderSettings();
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
            _rigidbody.isKinematic = true;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            if (_collider == null)
            {
                _collider = gameObject.GetComponent<BoxCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            }

            int grabLayer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);
            if (grabLayer >= 0) gameObject.layer = grabLayer;
            else Debug.LogError($"未定义抓取层 '{Hand.grabbableLayerNameDefault}'，基因点云无法被抓取");

            _rigidbody.excludeLayers = _excludeLayers;
            _collider.excludeLayers = _excludeLayers;

            if (_grabbable == null)
            {
                _grabbable = gameObject.GetComponent<Grabbable>();
                if (_grabbable == null) _grabbable = gameObject.AddComponent<Grabbable>();
                _grabbable.OnGrabEvent += OnGrabbed;
                _grabbable.OnReleaseEvent += OnReleased;
            }
            _grabbable.body = _rigidbody;
            _grabbable.singleHandOnly = false;
            _grabbable.instantGrab = false;
        }

        void OnGrabbed(Hand hand, Grabbable grab)
        {
            _rigidbody.isKinematic = false;
        }

        void OnReleased(Hand hand, Grabbable grab)
        {
            if (_grabbable.HeldCount() == 0)
                _rigidbody.isKinematic = true;
        }
    }
}
