using System.Collections.Generic;
using UnityEngine;
using Autohand;

namespace Dicom.Interaction
{
    // 远程射线操控:挂在点云模型上(与 DicomModelTransform 同物体),自动发现场景中的 DicomRayPointer
    // 只响应"指向自己"且按下 grip 的射线,故每个模型各挂一个互不干扰,无需手动拖引用
    //
    // 单手 grip 指向自己:模型跟随该手射线锚点平移(锚点 = 射线起点 + 方向 * 命中距离)
    // 双手 grip 同时指向自己:两手连线长度比控缩放,连线方向变化控旋转,绕两手中点
    // 与扳机近距离物理抓取并存:自身被物理抓取(HeldCount>0)时让位
    [DefaultExecutionOrder(5)]
    public class DicomRayManipulator : MonoBehaviour
    {
        // 手动指定射线指针;留空则运行时自动查找场景内所有 DicomRayPointer
        [SerializeField] DicomRayPointer[] _pointers;
        // 自动查找重试间隔:手与模型创建时机不定,查不到时按间隔重试
        [SerializeField] float _autoFindRetryInterval = 0.5f;

        // 认接口故 DICOM/基因通用
        IPointCloudManipulable _self;
        Grabbable _grabbable;

        // 本帧指向自己且 grip 的指针,复用列表避免每帧 GC
        readonly List<DicomRayPointer> _active = new List<DicomRayPointer>(2);

        // 双手状态
        bool _dual;
        DicomRayPointer _dualA, _dualB;
        Vector3 _dualStartVector, _dualLastVector;
        float _dualStartScale;

        // 单手状态
        DicomRayPointer _single;
        Vector3 _lastAnchor;

        float _nextFindTime;

        void Awake()
        {
            _self = GetComponent<IPointCloudManipulable>();
            if (_self == null)
            {
                Debug.LogError("DicomRayManipulator 未找到 IPointCloudManipulable(需与 DicomModelTransform/GeneModelTransform 同物体),已禁用");
                enabled = false;
                return;
            }
            // Grabbable 由 GrabbableSetup 在更早的 AddComponent 中建好,这里取得到;取不到则物理抓取判定返回 false
            _grabbable = GetComponent<Grabbable>();
            EnsurePointers();
        }

        void Update()
        {
            EnsurePointers();
            // 自身正被扳机物理抓取时,让位给物理抓取,不做射线操控
            if (IsPhysicallyHeld()) { Reset(); return; }
            // 画笔开启时 GeneGrabbableSetup 会禁用 Grabbable:此时让位给套索,避免 grip 同时驱动拖动与画圈
            // DICOM 侧 Grabbable 常开,不受影响
            if (_grabbable != null && !_grabbable.enabled) { Reset(); return; }

            CollectActive();

            if (_active.Count >= 2) { ExitSingle(); Dual(_active[0], _active[1]); }
            else if (_active.Count == 1) { ExitDual(); Single(_active[0]); }
            else Reset();
        }

        // 收集指向自己且按下 grip 的指针
        void CollectActive()
        {
            _active.Clear();
            if (_pointers == null) return;
            for (int i = 0; i < _pointers.Length; i++)
            {
                var p = _pointers[i];
                if (p != null && p.IsDragging && p.Target == _self)
                    _active.Add(p);
            }
        }

        // 双手缩放+旋转:连线长度比控缩放,连线方向逐帧增量控旋转,绕两手中点
        void Dual(DicomRayPointer a, DicomRayPointer b)
        {
            Vector3 v = b.RayOrigin - a.RayOrigin;
            if (v.sqrMagnitude < 1e-8f) return;

            // 首次进入或参与的手变化时重新记录基准,避免跳变
            if (!_dual || a != _dualA || b != _dualB)
            {
                _dual = true;
                _dualA = a; _dualB = b;
                _dualStartVector = v;
                _dualLastVector = v;
                _dualStartScale = _self.CurrentScale;
                return;
            }

            Vector3 pivot = (a.RayOrigin + b.RayOrigin) * 0.5f;
            float ratio = v.magnitude / Mathf.Max(_dualStartVector.magnitude, 1e-6f);
            // ScaleAroundWorld 内部 clamp 到 Min/MaxScale
            _self.ScaleAroundWorld(pivot, _dualStartScale * ratio);

            Quaternion delta = Quaternion.FromToRotation(_dualLastVector, v);
            _self.RotateAroundWorld(pivot, delta);
            _dualLastVector = v;
        }

        // 单手拖动:模型跟随射线锚点平移
        void Single(DicomRayPointer p)
        {
            Vector3 anchor = p.RayOrigin + p.RayDirection * p.HitDistance;

            // 起拖或换手:锁定锚点,本帧不平移,下一帧起按增量跟随
            if (_single != p)
            {
                _single = p;
                _lastAnchor = anchor;
                return;
            }

            Vector3 delta = anchor - _lastAnchor;
            if (delta.sqrMagnitude > 0f) _self.TranslateWorld(delta);
            _lastAnchor = anchor;
        }

        void ExitDual() { _dual = false; _dualA = null; _dualB = null; }
        void ExitSingle() { _single = null; }
        void Reset() { ExitDual(); ExitSingle(); }

        bool IsPhysicallyHeld() => _grabbable != null && _grabbable.HeldCount() > 0;

        // 确保有指针引用:已有则不动;否则按间隔自动查找场景内所有 DicomRayPointer
        void EnsurePointers()
        {
            if (_pointers != null && _pointers.Length > 0) return;
            if (Time.unscaledTime < _nextFindTime) return;
            _nextFindTime = Time.unscaledTime + _autoFindRetryInterval;
            _pointers = FindObjectsByType<DicomRayPointer>(FindObjectsSortMode.None);
        }
    }
}
