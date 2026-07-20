using UnityEngine;
using Autohand;

namespace Dicom.UI
{
    // 世界空间面板的软跟随 HUD:惰性角度死区跟随头显 + 正面朝向用户
    // 与抓取/射线拖拽解耦:被抓取(Grabbable 持有)或射线拖拽中时暂停跟随,松手后从当前位置继续
    // 惰性策略:头转出死区角度才柔和拉回视野正前方,小幅转头与手动微调保留,避免面板黏眼致晕
    // 直接挂在面板根 Canvas 上,每帧改 root transform(Kinematic 刚体未抓取时不与物理冲突)
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/VR Hud Follower")]
    public class VRHudFollower : MonoBehaviour
    {
        [Header("跟随目标")]
        [SerializeField, Tooltip("跟随的头显相机,空则运行时取 Camera.main")]
        Camera _camera;

        [Header("位置")]
        [SerializeField, Tooltip("面板到头显的水平距离(米)")]
        float _distance = 0.75f;
        [SerializeField, Tooltip("相对视线中心的垂直偏移(米,负=略低于视线)")]
        float _heightOffset = -0.1f;

        [Header("惰性跟随")]
        [SerializeField, Tooltip("头显视线与面板方向夹角超过此角度才开始拉回")]
        float _recenterAngle = 32f;
        [SerializeField, Tooltip("拉回到此夹角内停止,回到静止")]
        float _settleAngle = 4f;

        [Header("平滑")]
        [SerializeField, Tooltip("位置平滑速度,越大跟随越紧")]
        float _positionSmoothing = 6f;
        [SerializeField, Tooltip("朝向平滑速度,越大转向越快")]
        float _rotationSmoothing = 8f;
        [SerializeField, Tooltip("仅绕 Y 轴朝向(保持面板竖直)")]
        bool _onlyYaw = true;

        DicomPanelGrabHandle _grabHandle;
        DicomPanelRayDragMover[] _movers;
        // 拉回进行中:一旦触发持续拉回直到进入静止角度,期间不因抖动反复启停
        bool _recentering;
        // 首帧强制吸附到视野正前方,避免生成后停在世界原点
        bool _snapPending = true;

        // 工厂构建时回填跟随相机
        public void SetCamera(Camera cam) => _camera = cam;

        void Awake()
        {
            _grabHandle = GetComponent<DicomPanelGrabHandle>();
            _movers = GetComponentsInChildren<DicomPanelRayDragMover>(true);
        }

        void Update()
        {
            var cam = ResolveCamera();
            if (cam == null) return;
            // 被抓取或射线拖拽中:让位给交互,不动 transform,并记为已在视野(松手后不立即拉回)
            if (IsInteracting()) { _recentering = false; return; }

            Vector3 camPos = cam.transform.position;
            Vector3 viewDir = cam.transform.forward;
            if (_onlyYaw) viewDir.y = 0f;
            if (viewDir.sqrMagnitude < 1e-6f) return;
            viewDir.Normalize();

            // 面板相对头显的当前水平方向与视线的夹角,决定是否需要拉回
            Vector3 toPanel = transform.position - camPos;
            if (_onlyYaw) toPanel.y = 0f;
            float angle = toPanel.sqrMagnitude < 1e-6f ? 999f : Vector3.Angle(viewDir, toPanel);

            if (_snapPending) { SnapToFront(camPos, viewDir); _snapPending = false; return; }

            if (angle > _recenterAngle) _recentering = true;
            else if (angle <= _settleAngle) _recentering = false;

            if (_recentering) FollowFront(camPos, viewDir);
            FaceCamera(camPos);
        }

        // 目标位置:视野正前方 _distance 处,叠加垂直偏移
        Vector3 TargetPosition(Vector3 camPos, Vector3 viewDir)
        {
            return camPos + viewDir * _distance + Vector3.up * _heightOffset;
        }

        // 首帧瞬移到正前方并正对用户,无平滑
        void SnapToFront(Vector3 camPos, Vector3 viewDir)
        {
            transform.position = TargetPosition(camPos, viewDir);
            transform.rotation = FacingRotation(camPos);
        }

        // 柔和拉回视野正前方
        void FollowFront(Vector3 camPos, Vector3 viewDir)
        {
            Vector3 target = TargetPosition(camPos, viewDir);
            transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * _positionSmoothing);
        }

        // 面板正面(Canvas 可见面)朝向用户:forward 背离用户
        void FaceCamera(Vector3 camPos)
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, FacingRotation(camPos), Time.deltaTime * _rotationSmoothing);
        }

        Quaternion FacingRotation(Vector3 camPos)
        {
            Vector3 toPanel = transform.position - camPos;
            if (_onlyYaw) toPanel.y = 0f;
            if (toPanel.sqrMagnitude < 1e-6f) return transform.rotation;
            return Quaternion.LookRotation(toPanel.normalized, Vector3.up);
        }

        // 抓取持有中或任一标题条射线拖拽中视为交互中
        bool IsInteracting()
        {
            if (_grabHandle != null && _grabHandle.Grabbable != null && _grabHandle.Grabbable.HeldCount() > 0)
                return true;
            if (_movers != null)
                for (int i = 0; i < _movers.Length; i++)
                    if (_movers[i] != null && _movers[i].IsDragging) return true;
            return false;
        }

        Camera ResolveCamera()
        {
            // 头显相机属外部环境,允许运行时查找并缓存
            if (_camera == null) _camera = Camera.main;
            return _camera;
        }
    }
}
