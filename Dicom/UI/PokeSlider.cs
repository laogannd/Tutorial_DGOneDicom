using Autohand;
using UnityEngine;
using UnityEngine.UI;

namespace Dicom.UI
{
    // 手指可推滑块：UIPokeBridge 只发 click 无法拖 Slider，这里用 AutoHand 触碰位置投影到轨道实时设值
    // 挂在带 Slider 的物体上，自动配 BoxCollider + HandTouchEvent；兼容手指触碰，射线扣扳机仍可正常拖动
    [RequireComponent(typeof(Slider))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Dicom/Poke Slider")]
    public class PokeSlider : MonoBehaviour
    {
        [SerializeField, Tooltip("BoxCollider Z轴厚度(米)")]
        float _colliderDepth = 0.02f;

        [SerializeField, Tooltip("Collider中心相对Canvas表面的前移距离(米)")]
        float _colliderForwardOffset = 0.006f;

        [SerializeField, Range(0f, 1f)]
        float _touchHapticAmplitude = 0.15f;

        [SerializeField, Range(0f, 0.5f)]
        float _touchHapticDuration = 0.02f;

        Slider _slider;
        RectTransform _rect;
        BoxCollider _boxCollider;
        HandTouchEvent _touchEvent;
        Hand _activeHand;
        Vector2 _lastSyncedSize;

        void Awake()
        {
            _slider = GetComponent<Slider>();
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

            // 触碰持续期间，每帧把手掌位置投影到轨道求 normalized 值
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

        // 手位置转滑块本地坐标，按方向取 x 或 y 归一化，映射到 min..max
        void UpdateValueFromHand(Hand hand)
        {
            Vector3 local = _rect.InverseTransformPoint(hand.palmTransform.position);
            Rect r = _rect.rect;

            bool vertical = _slider.direction == Slider.Direction.BottomToTop || _slider.direction == Slider.Direction.TopToBottom;
            float t = vertical
                ? Mathf.InverseLerp(r.yMin, r.yMax, local.y)
                : Mathf.InverseLerp(r.xMin, r.xMax, local.x);

            if (_slider.direction == Slider.Direction.RightToLeft || _slider.direction == Slider.Direction.TopToBottom)
                t = 1f - t;

            _slider.normalizedValue = Mathf.Clamp01(t);
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
