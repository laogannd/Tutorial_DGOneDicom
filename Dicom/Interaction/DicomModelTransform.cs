using System;
using UnityEngine;

using Dicom.Core;
using Dicom.PointCloud;

namespace Dicom.Interaction
{
    // 点云模型初始尺寸适配与位姿复位
    // 体素位置按 (xyz - half) * Spacing(mm) 布局,原始 1:1 世界尺寸可达数百米,点云大到看不见
    // 加载完成后据体积最大维度自动算出适配缩放,把模型压到 _targetWorldSize 米级
    // 同时记录加载时的 Home 位姿(由放置点决定),供一键复位防止模型被甩飞/抓丢
    [RequireComponent(typeof(PointCloudController))]
    public class DicomModelTransform : MonoBehaviour
    {
        // 适配后模型最大维度的目标世界尺寸(米),0.5 约等于手动 0.002 缩放 512 体积的效果
        [SerializeField] float _targetWorldSize = 0.5f;
        // 缩放允许范围,UI 滑块与复位都在此区间内
        [SerializeField] float _minScale = 0.0002f;
        [SerializeField] float _maxScale = 0.05f;

        PointCloudController _controller;
        Rigidbody _rigidbody;

        // 加载时算出的适配缩放,作为缩放基准与 Reset 目标
        float _fitScale = 0.002f;
        // Home 位姿:加载完成瞬间的本地位姿,Reset 回到这里
        Vector3 _homeLocalPosition;
        Quaternion _homeLocalRotation = Quaternion.identity;
        bool _hasHome;

        // 适配缩放,供 TwoHandScaler 以此为基准做相对倍率缩放
        public float FitScale => _fitScale;
        public float MinScale => _minScale;
        public float MaxScale => _maxScale;
        public float CurrentScale => transform.localScale.x;

        // 模型当前在世界中的呈现尺寸(米):局部包围盒按 mm 布局,乘当前缩放得世界尺寸
        // 随缩放交互实时变化;数据源未就绪或无可见点时为零
        public Vector3 CurrentWorldSize
        {
            get
            {
                if (_controller == null) return Vector3.zero;
                Vector3 local = _controller.LocalBounds.size;
                return local * transform.localScale.x;
            }
        }

        // 位姿(平移/旋转/缩放)变化时触发,供 UI 实时刷新尺寸数值
        // 射线拖动与缩放器改完 transform 后调 RaisePoseChanged 主动通知
        public event Action OnPoseChanged;

        void Awake()
        {
            _controller = GetComponent<PointCloudController>();
            _controller.OnLoaded += OnDatasetLoaded;
            _rigidbody = GetComponent<Rigidbody>();
        }

        void OnDestroy()
        {
            if (_controller != null)
                _controller.OnLoaded -= OnDatasetLoaded;
        }

        // 加载完成才知道体积尺寸,据此算适配缩放并记录 Home 位姿
        void OnDatasetLoaded(DicomDataset dataset)
        {
            if (_rigidbody == null) _rigidbody = GetComponent<Rigidbody>();

            float maxDim = Mathf.Max(
                dataset.Width * dataset.Spacing.x,
                dataset.Height * dataset.Spacing.y,
                dataset.Depth * dataset.Spacing.z);

            // 体积最大维度缩到目标世界尺寸,clamp 防极端数据集
            _fitScale = Mathf.Clamp(_targetWorldSize / Mathf.Max(maxDim, 1e-4f), _minScale, _maxScale);
            transform.localScale = Vector3.one * _fitScale;

            // 记录此刻位姿为 Home(放置点),Reset 回到这里
            _homeLocalPosition = transform.localPosition;
            _homeLocalRotation = transform.localRotation;
            _hasHome = true;

            ClearVelocity();
        }

        // 复位位置/旋转/缩放到加载时的 Home 状态,清速度防漂移,解决模型被甩飞或抓丢
        // 复位前先开启 Is Kinematic 锁定物理,避免物理引擎在设位姿同帧干扰导致回不到初始位置,设完再恢复原状态
        public void ResetTransform()
        {
            if (!_hasHome) return;

            bool wasKinematic = _rigidbody != null && _rigidbody.isKinematic;
            if (_rigidbody != null) _rigidbody.isKinematic = true;

            transform.localPosition = _homeLocalPosition;
            transform.localRotation = _homeLocalRotation;
            transform.localScale = Vector3.one * _fitScale;

            if (_rigidbody != null) _rigidbody.isKinematic = wasKinematic;
            ClearVelocity();
            OnPoseChanged?.Invoke();
        }

        // 直接设缩放(UI 滑块用),约束到允许范围,等比缩放
        public void SetScale(float scale)
        {
            transform.localScale = Vector3.one * Mathf.Clamp(scale, _minScale, _maxScale);
            OnPoseChanged?.Invoke();
        }

        // 世界空间平移(射线拖动用),改完通知 UI 刷新尺寸位置
        public void TranslateWorld(Vector3 delta)
        {
            ApplyWorldPosition(transform.position + delta);
            OnPoseChanged?.Invoke();
        }

        // 绕世界轴点旋转(双手射线旋转用),改完通知 UI
        // RotateAround 等价于: pos = pivot + delta*(pos-pivot); rot = delta*rot,拆开经刚体驱动避免插值打架
        public void RotateAroundWorld(Vector3 worldPivot, Quaternion delta)
        {
            Vector3 pos = worldPivot + delta * (transform.position - worldPivot);
            Quaternion rot = delta * transform.rotation;
            ApplyWorldPosition(pos);
            ApplyWorldRotation(rot);
            OnPoseChanged?.Invoke();
        }

        // 绕世界轴点等比缩放(双手射线缩放用):缩放同时让模型相对枢轴位置随之缩放,枢轴下的点保持贴合
        public void ScaleAroundWorld(Vector3 worldPivot, float targetScale)
        {
            float clamped = Mathf.Clamp(targetScale, _minScale, _maxScale);
            float ratio = clamped / Mathf.Max(transform.localScale.x, 1e-8f);
            Vector3 offset = transform.position - worldPivot;
            ApplyWorldPosition(worldPivot + offset * ratio);
            // 缩放不归 PhysX 管,插值不碰 localScale,直接写
            transform.localScale = Vector3.one * clamped;
            OnPoseChanged?.Invoke();
        }

        // 经刚体 MovePosition 驱动:Kinematic+Interpolate 时直接写 transform 会与插值打架致抽搐闪回
        // 走 MovePosition 让插值在物理步间平滑过渡;无刚体或非 Kinematic 时退回直接写
        void ApplyWorldPosition(Vector3 worldPosition)
        {
            if (_rigidbody != null && _rigidbody.isKinematic)
                _rigidbody.MovePosition(worldPosition);
            else
                transform.position = worldPosition;
        }

        void ApplyWorldRotation(Quaternion worldRotation)
        {
            if (_rigidbody != null && _rigidbody.isKinematic)
                _rigidbody.MoveRotation(worldRotation);
            else
                transform.rotation = worldRotation;
        }

        // 主动通知位姿变化(外部直接改 transform 后调用)
        public void RaisePoseChanged() => OnPoseChanged?.Invoke();

        // 清零刚体线速度/角速度,避免复位后残留动量把模型重新甩出
        void ClearVelocity()
        {
            if (_rigidbody == null || _rigidbody.isKinematic) return;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}
