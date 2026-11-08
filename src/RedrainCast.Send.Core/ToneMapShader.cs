namespace RedrainCast.Send.Core;

public static class ToneMapShader
{
    public const string Lookup = """
        float3 SampleTable(Texture3D<float4> table, float3 pq) {
            float3 position = saturate(pq) * Scale;
            int3 p = min((int3)position, (int)Scale-1);
            float3 f = position - p;
            int3 a, b; float3 w;
            if (f.x >= f.y) {
                if (f.y >= f.z) { a=int3(1,0,0); b=int3(1,1,0); w=f; }
                else if (f.x >= f.z) { a=int3(1,0,0); b=int3(1,0,1); w=f.xzy; }
                else { a=int3(0,0,1); b=int3(1,0,1); w=f.zxy; }
            } else {
                if (f.x >= f.z) { a=int3(0,1,0); b=int3(1,1,0); w=f.yxz; }
                else if (f.y >= f.z) { a=int3(0,1,0); b=int3(0,1,1); w=f.yzx; }
                else { a=int3(0,0,1); b=int3(0,1,1); w=f.zyx; }
            }
            return table.Load(int4(p,0)).rgb*(1-w.x) + table.Load(int4(p+a,0)).rgb*(w.x-w.y)
                + table.Load(int4(p+b,0)).rgb*(w.y-w.z) + table.Load(int4(p+1,0)).rgb*w.z;
        }
        """;
}
