using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // 相机视角套索命中测试:136k cell 投到相机视口空间,落在闭合多边形内且视深度在切片范围内则置位
    // ViewProj 为相机 projection*worldToCamera(主线程算好);Polygon 是视口空间(0..1)闭合多边形
    // 深度切片:cell 到相机的视空间深度(clip.w)须落在 [DepthMin, DepthMax],把整条穿透收窄成薄片
    // Mask 累积不清零:多次画圈持续 OR 置位;清除选择由外部填 0
    [BurstCompile]
    public struct GeneLassoJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        // cell local -> world,主线程一次算好传入
        public float4x4 LocalToWorld;
        // 相机 projection * worldToCamera,把世界点投到裁剪空间
        public float4x4 ViewProj;
        // 视口空间(0..1)闭合多边形顶点,ReadOnly 各线程共享
        [ReadOnly] public NativeArray<float2> Polygon;
        // 深度切片范围(视空间深度,即 clip.w):范围外的 cell 排除
        public float DepthMin;
        public float DepthMax;
        public NativeArray<byte> Mask;

        public void Execute(int i)
        {
            float3 world = math.mul(LocalToWorld, new float4(CellPos[i], 1f)).xyz;
            float4 clip = math.mul(ViewProj, new float4(world, 1f));

            // 相机背后或退化:排除(透视下 w 即视空间深度)
            if (clip.w <= 1e-5f) return;
            // 深度切片外:排除
            if (clip.w < DepthMin || clip.w > DepthMax) return;

            // 裁剪空间 -> NDC(-1..1)-> 视口(0..1)
            float2 ndc = clip.xy / clip.w;
            float2 uv = ndc * 0.5f + 0.5f;
            if (PointInPolygon(uv)) Mask[i] = 1;
        }

        // 射线交叉法:从 uv 向 +x 引射线,统计与多边形边的交点奇偶
        bool PointInPolygon(float2 uv)
        {
            bool inside = false;
            int n = Polygon.Length;
            for (int a = 0, b = n - 1; a < n; b = a++)
            {
                float2 pa = Polygon[a];
                float2 pb = Polygon[b];
                if ((pa.y > uv.y) != (pb.y > uv.y) &&
                    uv.x < (pb.x - pa.x) * (uv.y - pa.y) / (pb.y - pa.y) + pa.x)
                    inside = !inside;
            }
            return inside;
        }
    }
}
