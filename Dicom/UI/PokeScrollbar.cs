using Autohand;
using UnityEngine;
using UnityEngine.UI;

namespace Dicom.UI
{
    // 手指可推滚动条:仿 PokeSlider,用 AutoHand 触碰位置投影到轨道实时设 Scrollbar.value
    // 挂在带 Scrollbar 的物体上,自动配 BoxCollider + HandTouchEvent;射线拖把手仍可正常用
    [RequireComponent(typeof(Scrollbar))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/Poke Scrollbar")]
    public class PokeScrollbar : MonoBehaviour
    {
        [SerializeField, Tooltip("BoxCollider Z轴厚度(米)")]
        float _colliderDepth = 0.04f;

        [SerializeField, Tooltip("Collider中心相对Canvas表面的前移距离(米)")]
        float _colliderForwardOffset = 0.012f;

        [SerializeField, Range(0f, 1f)]
        float _touchHapticAmplitude = 0.15f;

        [SerializeField, Range(0f, 0.5f)]
        float _touchHapticDuration = 0.02f;

        Scrollbar _scrollbar;
        RectTransform _rect;
        BoxCollider _boxCollider;
        HandTouchEvent _touchEvent;
        Hand _activeHand;
        Vector2 _lastSyncedSize;

        void Awake()
        {
            _scrollbar = GetComponent<Scrollbar>();
            _rect = GetComponent<RectTransform>();
            EnsureComponents();
            SyncColliderSize();
        }

        void OnEnable()
        {
            _touchEvent.HandStartTouchEvent += OnHandTouch;
            _touchEvent.HandStopTouchEvent += OnHandUntouch;
        }

        void OnDisable()
        {
            _touchEvent.HandStartTouchEvent -= OnHandTouch;
            _touchEvent.HandStopTouchEvent -= OnHandUntouch;
            _activeHand = null;
        }

        void LateUpdate()
        {
            if (_rect.rect.size != _lastSyncedSize) SyncColliderSize();

            if (_activeHand != null) UpdateValueFromHand(_activeHand);
        }

        void OnHandTouch(Hand hand)
        {
            _activeHand = hand;
            PlayHaptic(hand);
        }

        void OnHandUntouch(Hand hand)
        {
            if (_activeHand == hand) _activeHand = null;
        }

        // 手位置转滚动条本地坐标,按方向取 y 或 x 归一化,映射到 0..1
        void UpdateValueFromHand(Hand hand)
        {
            Vector3 local = _rect.InverseTransformPoint(hand.palmTransform.position);
            Rect r = _rect.rect;

            bool vertical = _scrollbar.direction == Scrollbar.Direction.BottomToTop || _scrollbar.direction == Scrollbar.Direction.TopToBottom;
            float t = vertical
                ? Mathf.InverseLerp(r.yMin, r.yMax, local.y)
                : Mathf.InverseLerp(r.xMin, r.xMax, local.x);

            if (_scrollbar.direction == Scrollbar.Direction.RightToLeft || _scrollbar.direction == Scrollbar.Direction.TopToBottom)
                t = 1f - t;

            _scrollbar.value = Mathf.Clamp01(t);
        }

        void PlayHaptic(Hand hand)
        {
            if (_touchHapticAmplitude <= 0f || _touchHapticDuration <= 0f) return;
            hand.PlayHapticVibration(_touchHapticDuration, _touchHapticAmplitude);
        }

        void EnsureComponents()
        {
            _boxCollider = GetComponent<BoxCollider>();
            if (_boxCollider == null) _boxCollider = gameObject.AddComponent<BoxCollider>();
            _boxCollider.isTrigger = false;
            // 排除玩家身体层与可抓取物层,只与手碰撞:面板不再弹开玩家/推点云,手指推滚动条照常
            VRQuestion.PanelCollisionFilter.Apply(_boxCollider);

            _touchEvent = GetComponent<HandTouchEvent>();
            if (_touchEvent == null) _touchEvent = gameObject.AddComponent<HandTouchEvent>();
            _touchEvent.oneHanded = true;
        }

        void SyncColliderSize()
        {
            Rect r = _rect.rect;
            _lastSyncedSize = r.size;
            _boxCollider.size = new Vector3(r.width, r.height, _colliderDepth);
            _boxCollider.center = new Vector3(r.center.x, r.center.y, -_colliderForwardOffset);
        }
    }
}
