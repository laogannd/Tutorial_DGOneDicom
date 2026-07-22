using UnityEngine;
using Autohand;

namespace Dicom.UI
{
    // 世界空间面板跟随,只有两种模式,不做任何花哨滤波:
    // Smooth=纯连续指数平滑,面板始终柔和居中到视野正前方固定距离处,并正对用户
    //   无死区、无角度拉回状态机、无相机低通、无偏移捕获 —— 这些叠加才是 VR 里发飘卡顿的根源
    //   仅在抓取/射线拖拽时冻结,让位给交互防争抢 transform
    // None=硬锁:每帧把面板焊到相机完整位姿(位置+旋转含俯仰),相对视野零偏差跟头转动
    //   不依赖手动拖到 Camera 子物体,运行时按捕获的相对位姿重建;交互中让位,松手后按新落点重锁
    // 关键:Smooth 下一旦被抓取/射线拖拽移动过,松手即自动切到 None,把落点焊死为死锁关系
    //   —— 用户手动摆放视为"就放这",不再漂回视野正前方(那种归位跟随是最烦人的)
    // 折叠时按相机上方向抬升面板,标题条向上收起退出视野中心,不再挡视野
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/VR Hud Follower")]
    public class VRHudFollower : MonoBehaviour
    {
        // Smooth=平滑跟随;None=硬锁(每帧焊到相机位姿,相对视野零偏差)
        public enum FollowMode { Smooth, None }

        [Header("模式")]
        [SerializeField, Tooltip("Smooth=平滑跟随视野正前方;None=硬锁焊死在头显上,相对视野零偏差跟头转动")]
        FollowMode _mode = FollowMode.Smooth;

        [Header("跟随目标")]
        [SerializeField, Tooltip("跟随的头显相机,空则运行时取 Camera.main")]
        Camera _camera;

        [Header("位置")]
        [SerializeField, Tooltip("面板到头显的水平距离(米)")]
        float _distance = 0.75f;
        [SerializeField, Tooltip("相对视线中心的垂直偏移(米,负=略低于视线)")]
        float _heightOffset = -0.1f;

        [Header("平滑")]
        [SerializeField, Tooltip("位置平滑速度,越大跟随越紧越快")]
        float _positionSmoothing = 6f;
        [SerializeField, Tooltip("朝向平滑速度,越大转向越快")]
        float _rotationSmoothing = 8f;
        [SerializeField, Tooltip("仅绕 Y 轴朝向(保持面板竖直)")]
        bool _onlyYaw = true;

        [Header("交互")]
        [SerializeField, Tooltip("被抓取/射线拖拽移动后自动切到硬锁,把落点焊死为死锁关系,不再漂回视野正前方")]
        bool _lockAfterDrag = true;

        [Header("折叠")]
        [SerializeField, Tooltip("折叠时沿相机上方向抬升面板的距离(米),把标题条向上收出视野中心;0=不抬升")]
        float _collapseRaise = 0.28f;

        DicomPanelGrabHandle _grabHandle;
        DicomPanelRayDragMover[] _movers;
        // 首帧直接吸附到视野正前方,避免生成后停在世界原点再飘过来
        bool _snapPending = true;
        // 折叠态:折叠时按相机上方向额外抬升,标题条退出视野中心;由 UnifiedPanelCollapser 通知
        bool _collapsed;
        // None 硬锁:相机本地空间下捕获的面板相对位姿,每帧据此重建世界位姿实现零偏差焊接
        Vector3 _lockLocalPos;
        Quaternion _lockLocalRot;
        bool _lockReady;
        // 上一帧是否在交互:检测交互结束边沿,None 模式下松手后按新落点重捕获锁定位姿
        bool _wasInteracting;

        // 工厂构建时回填跟随相机
        public void SetCamera(Camera cam) => _camera = cam;

        // 运行时切换跟随模式;切回 Smooth 时重新吸附到视野正前方,切到 None 时按当前落点重新捕获锁定位姿
        public void SetMode(FollowMode mode)
        {
            _mode = mode;
            if (mode == FollowMode.Smooth) _snapPending = true;
            else _lockReady = false;
        }

        // 折叠状态通知:折叠时按相机上方向抬升面板,标题条向上收出视野中心;展开还原
        // 硬锁基准恒为展开等价、不随折叠变化,抬升仅在每帧重建时叠加;此处不重捕获,避免基准被抬升污染
        public void SetCollapsed(bool collapsed)
        {
            _collapsed = collapsed;
        }

        void Awake()
        {
            _grabHandle = GetComponent<DicomPanelGrabHandle>();
            _movers = GetComponentsInChildren<DicomPanelRayDragMover>(true);
        }

        void LateUpdate()
        {
            var cam = ResolveCamera();
            if (cam == null) return;

            // None:硬锁焊到相机位姿,交互中让位、松手按新落点重锁,平时每帧据相对位姿重建世界位姿
            if (_mode == FollowMode.None) { HardLock(cam); return; }

            // 交互中(抓取/射线拖拽):让位给交互,不动 transform,标记本帧在交互
            if (IsInteracting()) { _snapPending = false; _wasInteracting = true; return; }

            // 交互刚结束的下降沿:开启死锁则把当前落点焊死为硬锁关系,不再漂回视野正前方
            if (_wasInteracting)
            {
                _wasInteracting = false;
                if (_lockAfterDrag) { SetMode(FollowMode.None); HardLock(cam); return; }
            }

            Vector3 camPos = cam.transform.position;
            Vector3 viewDir = cam.transform.forward;
            if (_onlyYaw) viewDir.y = 0f;
            if (viewDir.sqrMagnitude < 1e-6f) return;
            viewDir.Normalize();

            // 折叠时沿相机上方向抬升,标题条向上收出视野中心
            Vector3 raise = _collapsed ? cam.transform.up * _collapseRaise : Vector3.zero;
            Vector3 targetPos = camPos + viewDir * _distance + Vector3.up * _heightOffset + raise;
            Quaternion targetRot = FacingRotation(targetPos, camPos);

            // 首帧瞬移,之后帧率无关连续指数平滑,无死区无状态机
            if (_snapPending)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                _snapPending = false;
                return;
            }

            float tp = 1f - Mathf.Exp(-_positionSmoothing * Time.deltaTime);
            float tr = 1f - Mathf.Exp(-_rotationSmoothing * Time.deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, targetPos, tp),
                Quaternion.Slerp(transform.rotation, targetRot, tr));
        }

        // None 硬锁:把面板焊到相机完整位姿(含俯仰),相对视野零偏差
        // 首次进入或交互刚结束时按当前落点捕获相机本地空间下的相对位姿,之后每帧据此重建世界位姿
        void HardLock(Camera cam)
        {
            Transform camT = cam.transform;

            // 交互中(抓取/射线拖拽):让位给交互,不动 transform,标记待重捕获
            if (IsInteracting())
            {
                _wasInteracting = true;
                _lockReady = false;
                return;
            }

            // 首次锁定 或 交互刚结束:按面板当前落点捕获相对相机的本地位姿
            // 捕获基准恒为展开态:若此刻处于折叠抬升状态,先扣除抬升,避免展开/折叠切换时抬升被重复计入
            if (!_lockReady || _wasInteracting)
            {
                Vector3 basePos = transform.position - (_collapsed ? camT.up * _collapseRaise : Vector3.zero);
                _lockLocalPos = camT.InverseTransformPoint(basePos);
                _lockLocalRot = Quaternion.Inverse(camT.rotation) * transform.rotation;
                _lockReady = true;
                _wasInteracting = false;
                return;
            }

            // 每帧据相对位姿重建世界位姿,焊死在头显上;折叠时沿相机上方向抬升,标题条收出视野中心
            Vector3 raise = _collapsed ? camT.up * _collapseRaise : Vector3.zero;
            transform.SetPositionAndRotation(
                camT.TransformPoint(_lockLocalPos) + raise,
                camT.rotation * _lockLocalRot);
        }

        // 面板正对用户的朝向(可选仅 yaw 保持竖直)
        Quaternion FacingRotation(Vector3 panelPos, Vector3 camPos)
        {
            Vector3 toCam = panelPos - camPos;
            if (_onlyYaw) toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-6f) return transform.rotation;
            return Quaternion.LookRotation(toCam.normalized, Vector3.up);
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
