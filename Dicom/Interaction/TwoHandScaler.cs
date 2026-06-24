using System.Collections.Generic;
using UnityEngine;
using Autohand;

namespace Dicom.Interaction
{
    // 双手抓取同一 Grabbable 时，按两手掌间距相对抓取瞬间的比值缩放模型
    // AutoHand 无内置双手缩放，这里自写。每帧轮询 HeldCount，避免依赖事件签名
    // 缩放范围以 DicomModelTransform 的适配缩放为基准做相对倍率,避免与点云 0.002 级适配缩放冲突
    [RequireComponent(typeof(Grabbable))]
    public class TwoHandScaler : MonoBehaviour
    {
        // 相对适配缩放的倍率下限/上限(无 DicomModelTransform 时退回按当前缩放为基准)
        [SerializeField] float _minFactor = 0.2f;
        [SerializeField] float _maxFactor = 5f;

        Grabbable _grabbable;
        DicomModelTransform _modelTransform;
        bool _scaling;
        float _startHandDistance;
        Vector3 _startScale;

        void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            _modelTransform = GetComponent<DicomModelTransform>();
        }

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
            // 基准为适配缩放(无组件则用抓取瞬间缩放),按倍率区间 clamp,适配 0.002 级点云缩放
            float baseScale = _modelTransform != null ? _modelTransform.FitScale : _startScale.x;
            float clamped = Mathf.Clamp(target.x, baseScale * _minFactor, baseScale * _maxFactor);
            transform.localScale = Vector3.one * clamped;
            // 缩放后通知 UI 实时刷新尺寸数值
            if (_modelTransform != null) _modelTransform.RaisePoseChanged();
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
