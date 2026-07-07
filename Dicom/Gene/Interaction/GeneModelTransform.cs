using System;
using UnityEngine;

namespace Dicom.Gene
{
    // 基因点云的初始尺寸适配与位姿复位,仿 DicomModelTransform 但依赖 GeneColorController
    // cell 按 (grid - center) * spacing(mm) 布局,原始尺寸偏大,加载完成据体积最大维度缩到目标世界尺寸
    [RequireComponent(typeof(GeneColorController))]
    public class GeneModelTransform : MonoBehaviour
    {
        // 适配后模型最大维度的目标世界尺寸(米)
        [SerializeField] float _targetWorldSize = 0.5f;
        [SerializeField] float _minScale = 0.0002f;
        [SerializeField] float _maxScale = 0.05f;

        GeneColorController _controller;
        Rigidbody _rigidbody;

        float _fitScale = 0.002f;
        Vector3 _homeLocalPosition;
        Quaternion _homeLocalRotation = Quaternion.identity;
        bool _hasHome;

        public float FitScale => _fitScale;
        public float MinScale => _minScale;
        public float MaxScale => _maxScale;
        public float CurrentScale => transform.localScale.x;

        // 模型当前世界呈现尺寸(米):局部包围盒(mm)乘缩放
        public Vector3 CurrentWorldSize
        {
            get
            {
                if (_controller == null) return Vector3.zero;
                return _controller.LocalBounds.size * transform.localScale.x;
            }
        }

        public event Action OnPoseChanged;

        void Awake()
        {
            _controller = GetComponent<GeneColorController>();
            _controller.OnLoaded += OnModelLoaded;
            _rigidbody = GetComponent<Rigidbody>();
        }

        void OnDestroy()
        {
            if (_controller != null) _controller.OnLoaded -= OnModelLoaded;
        }

        // 加载完成才知体积,据网格范围算适配缩放并记录 Home 位姿
        void OnModelLoaded(GeneModelData model)
        {
            if (_rigidbody == null) _rigidbody = GetComponent<Rigidbody>();

            // 与 GeneColorController 的 _cellSpacing 无关的最大维度以网格格数近似(spacing=1mm 时等价)
            float maxDim = Mathf.Max(
                model.GridMax.x - model.GridMin.x + 1,
                model.GridMax.y - model.GridMin.y + 1,
                model.GridMax.z - model.GridMin.z + 1);

            _fitScale = Mathf.Clamp(_targetWorldSize / Mathf.Max(maxDim, 1e-4f), _minScale, _maxScale);
            transform.localScale = Vector3.one * _fitScale;

            _homeLocalPosition = transform.localPosition;
            _homeLocalRotation = transform.localRotation;
            _hasHome = true;

            ClearVelocity();
        }

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

        public void SetScale(float scale)
        {
            transform.localScale = Vector3.one * Mathf.Clamp(scale, _minScale, _maxScale);
            OnPoseChanged?.Invoke();
        }

        public void TranslateWorld(Vector3 delta)
        {
            ApplyWorldPosition(transform.position + delta);
            OnPoseChanged?.Invoke();
        }

        public void RotateAroundWorld(Vector3 worldPivot, Quaternion delta)
        {
            Vector3 pos = worldPivot + delta * (transform.position - worldPivot);
            Quaternion rot = delta * transform.rotation;
            ApplyWorldPosition(pos);
            ApplyWorldRotation(rot);
            OnPoseChanged?.Invoke();
        }

        public void ScaleAroundWorld(Vector3 worldPivot, float targetScale)
        {
            float clamped = Mathf.Clamp(targetScale, _minScale, _maxScale);
            float ratio = clamped / Mathf.Max(transform.localScale.x, 1e-8f);
            Vector3 offset = transform.position - worldPivot;
            ApplyWorldPosition(worldPivot + offset * ratio);
            transform.localScale = Vector3.one * clamped;
            OnPoseChanged?.Invoke();
        }

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

        public void RaisePoseChanged() => OnPoseChanged?.Invoke();

        void ClearVelocity()
        {
            if (_rigidbody == null || _rigidbody.isKinematic) return;
            _rigidbody.velocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}
