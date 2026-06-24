using UnityEngine;
using Autohand;

namespace Dicom.Interaction
{
    // 抓取时解除 Kinematic 交给手驱动,完全脱手后重新锁定 Kinematic 定在原地
    // 逻辑与 DicomPanelGrabHandle / DicomGrabbableSetup 一致,抽成可复用组件
    // 供运行时动态创建的可抓取物体(如裁切平面手柄)使用
    [RequireComponent(typeof(Grabbable))]
    [RequireComponent(typeof(Rigidbody))]
    public class GrabKinematicLock : MonoBehaviour
    {
        Grabbable _grabbable;
        Rigidbody _rigidbody;

        void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _grabbable = GetComponent<Grabbable>();
            _grabbable.OnGrabEvent += OnGrabbed;
            _grabbable.OnReleaseEvent += OnReleased;
        }

        void OnDestroy()
        {
            if (_grabbable != null)
            {
                _grabbable.OnGrabEvent -= OnGrabbed;
                _grabbable.OnReleaseEvent -= OnReleased;
            }
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
