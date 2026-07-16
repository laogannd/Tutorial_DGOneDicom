using UnityEngine;
using Autohand;

namespace Dicom.UI
{
    // 给世界空间 Canvas 面板加可抓取手柄:VR 里可双手抓住把面板拖到顺手位置
    // 复用 DicomGrabbableSetup 模式:Rigidbody(Kinematic) + BoxCollider + Grabbable,挂 grabbableLayer
    // 直接挂在 Canvas 根(DicomPanel)上:抓取移动的就是面板本身
    // 碰撞盒只覆盖顶部手柄条,避免遮挡下方控件的手指戳;尺寸用 Canvas 单位,经 0.001 缩放成米
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/Dicom Panel Grab Handle")]
    public class DicomPanelGrabHandle : MonoBehaviour
    {
        [SerializeField, Tooltip("手柄碰撞盒尺寸(Canvas单位),经面板缩放后为实际米数")]
        Vector3 _colliderSize = new Vector3(520f, 60f, 20f);

        [SerializeField, Tooltip("碰撞盒中心(Canvas单位),相对面板中心")]
        Vector3 _colliderCenter = new Vector3(0f, 450f, 0f);

        [SerializeField, Tooltip("拖拽时手柄碰撞盒忽略的层,防止与玩家自身碰撞冲突")]
        LayerMask _excludeLayers = 0;

        [SerializeField] float _mass = 1f;

        Rigidbody _rigidbody;
        BoxCollider _collider;
        Grabbable _grabbable;

        public Grabbable Grabbable => _grabbable;

        void Awake()
        {
            EnsureComponents();
        }

        void OnDestroy()
        {
            if (_grabbable != null)
            {
                _grabbable.OnGrabEvent -= OnGrabbed;
                _grabbable.OnReleaseEvent -= OnReleased;
            }
        }

        void EnsureComponents()
        {
            // 先建刚体,保证 Grabbable.Awake 能取到 body
            if (_rigidbody == null)
            {
                _rigidbody = GetComponent<Rigidbody>();
                if (_rigidbody == null) _rigidbody = gameObject.AddComponent<Rigidbody>();
            }
            _rigidbody.mass = _mass;
            _rigidbody.useGravity = false;
            // 未抓取时 Kinematic 定在原地,抓起时解锁交给手驱动
            _rigidbody.isKinematic = true;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            if (_collider == null)
            {
                _collider = GetComponent<BoxCollider>();
                if (_collider == null) _collider = gameObject.AddComponent<BoxCollider>();
            }
            _collider.size = _colliderSize;
            _collider.center = _colliderCenter;
            // 排除玩家身体层与可抓取物层(叠加自定义排除层),手柄碰撞盒只与手碰撞
            // 不再弹开玩家、不再推点云模型;抓取走 OverlapSphere 查询,不受 excludeLayers 影响
            VRQuestion.PanelCollisionFilter.Apply(_collider, _excludeLayers);

            // 显式设到 Grabbable 层,AutoHand 的手只在该层做 OverlapSphere 检测
            // 层缺失时 NameToLayer 返回 -1，赋给 layer 会抛异常，守卫后报错跳过
            int grabLayer = LayerMask.NameToLayer(Hand.grabbableLayerNameDefault);
            if (grabLayer >= 0) gameObject.layer = grabLayer;
            else Debug.LogError($"未定义抓取层 '{Hand.grabbableLayerNameDefault}'，面板无法被抓取");

            if (_grabbable == null)
            {
                _grabbable = GetComponent<Grabbable>();
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

        // 工厂构建时按面板实际尺寸回填手柄碰撞盒
        public void Configure(Vector3 size, Vector3 center)
        {
            _colliderSize = size;
            _colliderCenter = center;
        }
    }
}
