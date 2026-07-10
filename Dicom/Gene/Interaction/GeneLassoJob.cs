using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dicom.Gene
{
    // 套索命中测试:136k cell 以手部射线视锥投影到 f=1 平面,落在闭合多边形内则置位
    // apex 为锥顶(手部射线原点),Forward/Right/Up 为正交基;背向 apex(f<=eps)的 cell 排除
    // Mask 累积不清零:多次画圈持续 OR 置位;清除选择由外部填 0
    [BurstCompile]
    public struct GeneLassoJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> CellPos;
        // cell local -> world,主线程一次算好传入,避免每 cell 取 transform
        public float4x4 LocalToWorld;
        public float3 Apex;
        public float3 Forward;
        public float3 Right;
        public float3 Up;
        // 投影到 f=1 平面的闭合多边形顶点(uv),ReadOnly 各线程共享
        [ReadOnly] public NativeArray<float2> Polygon;
        public NativeArray<byte> Mask;

        public void Execute(int i)
        {
            float3 world = math.mul(LocalToWorld, new float4(CellPos[i], 1f)).xyz;
            float3 dir = world - Apex;

            float f = math.dot(dir, Forward);
            // 背向锥顶或几乎在锥顶平面上:排除,防除零与背面误选
            if (f <= 1e-4f) return;

            float2 uv = new float2(math.dot(dir, Right) / f, math.dot(dir, Up) / f);
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
