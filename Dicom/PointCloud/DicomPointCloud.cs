using Unity.Collections;
using UnityEngine;

namespace Dicom.PointCloud
{
    // 持有点云 ComputeBuffer，用 Graphics.DrawProcedural 单 DrawCall 渲染点
    // 不依赖 SRP 专属回调，Built-in 与 URP 下行为一致；50万-100万点用一个 StructuredBuffer 承载
    [DisallowMultipleComponent]
    public class DicomPointCloud : MonoBehaviour
    {
        [SerializeField] Material _material;
        [SerializeField, Range(0.0001f, 0.02f)] float _pointSize = 0.002f;
        [SerializeField] bool _useBillboardQuads = true;

        ComputeBuffer _pointBuffer;
        int _pointCount;
        MaterialPropertyBlock _props;
        // 局部空间可见点 AABB(中心可偏离原点),用于每帧算世界 bounds 做视锥剔除
        Bounds _localBounds = new Bounds(Vector3.zero, Vector3.one);

        static readonly int _PointsId = Shader.PropertyToID("_Points");
        static readonly int _PointCountId = Shader.PropertyToID("_PointCount");
        static readonly int _PointSizeId = Shader.PropertyToID("_PointSize");
        static readonly int _LocalToWorldId = Shader.PropertyToID("_DicomLocalToWorld");

        public int PointCount => _pointCount;
        public Material Material => _material;

        // 运行时指定渲染材质(使用 Dicom/PointCloud shader)
        public void SetMaterial(Material material) => _material = material;

        // 设置局部空间可见点 AABB(过滤后中心可偏离原点)，供 bounds 剔除
        public void SetLocalBounds(Bounds bounds) => _localBounds = bounds;

        // 用 Job 产出的点填充 GPU buffer，count 为有效点数
        public void SetPoints(NativeArray<DicomPoint> points, int count)
        {
            ReleaseBuffer();
            if (count <= 0)
                return;

            // stride 20B = float3 + float + float，与 DicomPoint / shader 一致
            _pointBuffer = new ComputeBuffer(count, 20, ComputeBufferType.Structured);
            _pointBuffer.SetData(points, 0, 0, count);
            _pointCount = count;

            if (_props == null) _props = new MaterialPropertyBlock();
        }

        // 每帧提交一次 DrawProcedural，所有相机自动可见；进入正常渲染队列不被清屏覆盖
        void LateUpdate()
        {
            if (_material == null || _pointBuffer == null || _pointCount <= 0)
                return;

            _props.SetBuffer(_PointsId, _pointBuffer);
            _props.SetInt(_PointCountId, _pointCount);
            _props.SetFloat(_PointSizeId, _pointSize);
            _props.SetMatrix(_LocalToWorldId, transform.localToWorldMatrix);

            // billboard quad 每点 6 顶点(两三角)，否则点图元每点 1 顶点
            int vertsPerPoint = _useBillboardQuads ? 6 : 1;
            var topology = _useBillboardQuads ? MeshTopology.Triangles : MeshTopology.Points;

            // 用变换后的世界 bounds 做剔除，billboard 在世界空间展开故用 identity 矩阵
            Bounds worldBounds = ComputeWorldBounds();
            Graphics.DrawProcedural(_material, worldBounds, topology, vertsPerPoint * _pointCount, 1,
                null, _props, UnityEngine.Rendering.ShadowCastingMode.Off, false, gameObject.layer);
        }

        // 局部 AABB 经 transform 变换为世界 AABB
        Bounds ComputeWorldBounds()
        {
            Vector3 c = transform.TransformPoint(_localBounds.center);
            Vector3 e = _localBounds.extents;
            Vector3 axisX = transform.TransformVector(new Vector3(e.x, 0f, 0f));
            Vector3 axisY = transform.TransformVector(new Vector3(0f, e.y, 0f));
            Vector3 axisZ = transform.TransformVector(new Vector3(0f, 0f, e.z));
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(c, worldExtents * 2f);
        }

        public void SetPointSize(float size) => _pointSize = Mathf.Max(0.0001f, size);

        void ReleaseBuffer()
        {
            if (_pointBuffer != null)
            {
                _pointBuffer.Release();
                _pointBuffer = null;
            }
            _pointCount = 0;
        }

        void OnDestroy() => ReleaseBuffer();
    }
}
