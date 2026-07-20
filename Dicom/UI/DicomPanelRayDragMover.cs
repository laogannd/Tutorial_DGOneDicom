using UnityEngine;
using UnityEngine.EventSystems;
using Autohand;

namespace Dicom.UI
{
    // 世界空间面板的远程射线拖拽:挂在标题条 Image 上,走 AutoHand 的 HandCanvasPointer/AutoInputModule 标准 EventSystem 拖拽事件
    // 与手抓物理 Grabbable(grabbableLayer)解耦,两套交互并存:射线拖拽改 transform,手抓走刚体物理
    // 拖拽中沿射线方向保持初始抓取距离重算面板位置,并实时让 Canvas 正面朝向用户头显,提升交互质感
    // 移动目标是面板根 Canvas;命中点来自 PointerEventData,射线源来自 AutoInputModule.currentPointer
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/Dicom Panel Ray Drag Mover")]
    public class DicomPanelRayDragMover : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField, Tooltip("拖拽时面板正面是否实时朝向用户头显")]
        bool _faceUserWhileDragging = true;

        [SerializeField, Tooltip("朝向仅绕Y轴(保持面板竖直),关则完整朝向用户视线")]
        bool _onlyYaw = false;

        [SerializeField, Tooltip("位置平滑速度,越大跟随越紧")]
        float _positionSmoothing = 16f;

        [SerializeField, Tooltip("朝向平滑速度,越大转向越快")]
        float _rotationSmoothing = 14f;

        Transform _panelRoot;
        Camera _userCamera;

        HandCanvasPointer _pointer;
        bool _dragging;
        bool _initialized;
        float _grabDistance;
        Vector3 _grabOffset;
        Vector3 _targetPosition;
        Quaternion _targetRotation;

        // 外部(如 VRHudFollower)据此暂停自身移动,避免与射线拖拽争抢同一 root transform
        public bool IsDragging => _dragging;

        void Awake()
        {
            // 移动目标是面板根 Canvas:标题条是其子级
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null) _panelRoot = canvas.rootCanvas.transform;
        }

        // 按下瞬间 currentPointer 可能仍是上一指针,这里只置位,真正初始化延后到首帧 OnDrag
        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_panelRoot == null) return;
            _dragging = true;
            _initialized = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging) return;

            if (!_initialized)
            {
                // OnDrag 由 AutoInputModule.Process 调用,此时 currentPointer 已是当前处理指针,取值可靠
                // 优先取 EventSystem 激活的输入模块(标准填充),回退到 eventData.currentInputModule
                var module = EventSystem.current == null ? null : EventSystem.current.currentInputModule as AutoInputModule;
                if (module == null) module = eventData.currentInputModule as AutoInputModule;
                _pointer = module == null ? null : module.currentPointer;
                if (_pointer == null) { _dragging = false; return; }

                Vector3 origin = _pointer.transform.position;
                Vector3 hit = eventData.pointerPressRaycast.worldPosition;
                _grabDistance = Vector3.Distance(origin, hit);
                // 保持面板原点与命中点的世界偏移,拖拽不跳到射线中心
                _grabOffset = _panelRoot.position - hit;
                _initialized = true;
            }

            // 沿当前射线方向按初始抓取距离重算端点,叠加抓取偏移
            Vector3 rayOrigin = _pointer.transform.position;
            Vector3 rayDir = _pointer.transform.forward;
            _targetPosition = rayOrigin + rayDir * _grabDistance + _grabOffset;
            _targetRotation = ComputeFacingRotation(_targetPosition);
        }

        void Update()
        {
            if (!_dragging || !_initialized) return;

            _panelRoot.position = Vector3.Lerp(_panelRoot.position, _targetPosition, Time.deltaTime * _positionSmoothing);
            if (_faceUserWhileDragging)
                _panelRoot.rotation = Quaternion.Slerp(_panelRoot.rotation, _targetRotation, Time.deltaTime * _rotationSmoothing);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            // 松手保持当前位置与朝向
            _dragging = false;
            _initialized = false;
            _pointer = null;
        }

        // 计算面板朝向:Canvas 正面(可见面)朝向用户,即 forward 背离用户
        Quaternion ComputeFacingRotation(Vector3 panelPosition)
        {
            var cam = ResolveUserCamera();
            if (cam == null) return _panelRoot.rotation;

            Vector3 toPanel = panelPosition - cam.transform.position;
            if (_onlyYaw) toPanel.y = 0f;
            if (toPanel.sqrMagnitude < 1e-6f) return _panelRoot.rotation;
            return Quaternion.LookRotation(toPanel.normalized, Vector3.up);
        }

        Camera ResolveUserCamera()
        {
            // 头显相机运行时获取并缓存:属外部环境而非内部配置,允许查找;查不到则不强制朝向
            if (_userCamera == null) _userCamera = Camera.main;
            return _userCamera;
        }
    }
}
