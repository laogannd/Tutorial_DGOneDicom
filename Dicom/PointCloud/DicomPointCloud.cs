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
        // 显色参数写入本实例的 property block,按 draw 覆盖全局/材质值,使每个点云实例
        // (如 DICOM 与基因)显色互不干扰;若走 Shader.SetGlobal 则全进程共享会互相污染
        MaterialPropertyBlock _props;
        // 局部空间可见点 AABB(中心可偏离原点),用于每帧算世界 bounds 做视锥剔除
        Bounds _localBounds = new Bounds(Vector3.zero, Vector3.one);

        static readonly int _PointsId = Shader.PropertyToID("_Points");
        static readonly int _PointCountId = Shader.PropertyToID("_PointCount");
        static readonly int _PointSizeId = Shader.PropertyToID("_PointSize");
        static readonly int _LocalToWorldId = Shader.PropertyToID("_DicomLocalToWorld");

        // 显色参数 id:全部写入本实例 _props 而非 Shader 全局,实现按实例隔离
        static readonly int _ColorModeId = Shader.PropertyToID("_DicomColorMode");
        static readonly int _ClassColorsId = Shader.PropertyToID("_DicomClassColors");
        static readonly int _LutTexId = Shader.PropertyToID("_DicomLut");
        static readonly int _BreakpointTexId = Shader.PropertyToID("_DicomBreakpointLut");
        static readonly int _BreakpointDomainId = Shader.PropertyToID("_DicomBreakpointDomain");
        static readonly int _NormalizeId = Shader.PropertyToID("_DicomNormalize");
        static readonly int _WindowId = Shader.PropertyToID("_DicomWindow");
        static readonly int _TintId = Shader.PropertyToID("_DicomTint");

        public int PointCount => _pointCount;
        public Material Material => _material;

        void Awake() => EnsureProps();

        // property block 承载本实例全部显色态,颜色 setter 可能早于 SetPoints 被调用(控制器 Awake),
        // 故在此建好并给窗宽窗位/色调默认值,避免未设属性回落为 0 导致全黑
        void EnsureProps()
        {
            if (_props != null) return;
            _props = new MaterialPropertyBlock();
            _props.SetVector(_WindowId, new Vector4(0.5f, 1f, 0f, 0f));
            _props.SetVector(_TintId, new Vector4(1f, 1f, 1f, 1f));
        }

        // 运行时指定渲染材质(使用 Dicom/PointCloud shader)
        public void SetMaterial(Material material) => _material = material;

        // 设置局部空间可见点 AABB(过滤后中心可偏离原点)，供 bounds 剔除
        public void SetLocalBounds(Bounds bounds) => _localBounds = bounds;

        // === 每实例显色接口:全部写入本实例 property block,不触碰 Shader 全局 ===
        public void SetColorMode(float mode) { EnsureProps(); _props.SetFloat(_ColorModeId, mode); }
        public void SetLutTexture(Texture tex) { if (tex == null) return; EnsureProps(); _props.SetTexture(_LutTexId, tex); }
        public void SetBreakpointTexture(Texture tex) { if (tex == null) return; EnsureProps(); _props.SetTexture(_BreakpointTexId, tex); }
        public void SetBreakpointDomain(float min, float max) { EnsureProps(); _props.SetVector(_BreakpointDomainId, new Vector4(min, max, 0f, 0f)); }
        public void SetNormalize(float min, float max) { EnsureProps(); _props.SetVector(_NormalizeId, new Vector4(min, max, 0f, 0f)); }
        public void SetWindow(float center, float width) { EnsureProps(); _props.SetVector(_WindowId, new Vector4(center, width, 0f, 0f)); }
        public void SetTint(float r, float g, float b, float gain) { EnsureProps(); _props.SetVector(_TintId, new Vector4(r, g, b, gain)); }
        public void SetClassColors(Vector4[] palette) { if (palette == null) return; EnsureProps(); _props.SetVectorArray(_ClassColorsId, palette); }

        // 把本实例当前显色态复制到另一个点云实例(供 overlay 高亮复用主点云 colormap)
        public void CopyColorStateTo(DicomPointCloud target)
        {
            if (target == null) return;
            EnsureProps();
            target.EnsureProps();
            target._props.SetFloat(_ColorModeId, _props.GetFloat(_ColorModeId));
            target._props.SetVector(_NormalizeId, _props.GetVector(_NormalizeId));
            target._props.SetVector(_WindowId, _props.GetVector(_WindowId));
            target._props.SetVector(_TintId, _props.GetVector(_TintId));
            target._props.SetVector(_BreakpointDomainId, _props.GetVector(_BreakpointDomainId));
            var lut = _props.GetTexture(_LutTexId);
            if (lut != null) target._props.SetTexture(_LutTexId, lut);
            var bp = _props.GetTexture(_BreakpointTexId);
            if (bp != null) target._props.SetTexture(_BreakpointTexId, bp);
        }

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

            EnsureProps();
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
