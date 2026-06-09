using System.Collections.Generic;
using UnityEngine;
using Autohand;

namespace Dicom.Interaction
{
    // 双手抓取同一 Grabbable 时，按两手掌间距相对抓取瞬间的比值缩放模型
    // AutoHand 无内置双手缩放，这里自写。每帧轮询 HeldCount，避免依赖事件签名
    [RequireComponent(typeof(Grabbable))]
    public class TwoHandScaler : MonoBehaviour
    {
        [SerializeField] float _minScale = 0.1f;
        [SerializeField] float _maxScale = 10f;

        Grabbable _grabbable;
        bool _scaling;
        float _startHandDistance;
        Vector3 _startScale;

        void Awake() => _grabbable = GetComponent<Grabbable>();

        void Update()
        {
            var hands = _grabbable.GetHeldBy();
            // 双手齐抓才进入缩放，松开一只手即退出
            if (hands == null || hands.Count < 2)
            {
                _scaling = false;
                return;
            }

            float dist = HandDistance(hands);
            if (!_scaling)
            {
                _scaling = true;
                _startHandDistance = Mathf.Max(dist, 0.0001f);
                _startScale = transform.localScale;
                return;
            }

            float ratio = dist / _startHandDistance;
            Vector3 target = _startScale * ratio;
            float clamped = Mathf.Clamp(target.x, _minScale, _maxScale);
            transform.localScale = Vector3.one * clamped;
        }

        // 取前两只手的掌心间距
        static float HandDistance(List<Hand> hands)
        {
            Transform a = hands[0].palmTransform;
            Transform b = hands[1].palmTransform;
            return Vector3.Distance(a.position, b.position);
        }
    }
}
