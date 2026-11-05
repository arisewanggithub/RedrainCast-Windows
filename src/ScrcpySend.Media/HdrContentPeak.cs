using System.Runtime.InteropServices;
using ScrcpySend.Core;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace ScrcpySend.Media;

internal sealed class HdrContentPeak : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Input : register(t0);
        RWStructuredBuffer<uint> Histogram : register(u0);
        float PQ(float nits) {
            float p = pow(saturate(nits / 10000), 2610.0 / 16384);
            return pow((3424.0/4096 + 2413.0/128*p)/(1+2392.0/128*p),2523.0/32);
        }
        [numthreads(8,8,1)]
        void CS(uint3 id : SV_DispatchThreadID) {
            uint width, height; Input.GetDimensions(width, height);
            uint2 position = id.xy * 8;
            if (position.x >= width || position.y >= height) return;
            float3 rgb = Input.Load(int3(position, 0)).rgb;
            float3 wide = float3(dot(rgb,float3(.627404,.329283,.0433136)),
                                dot(rgb,float3(.069097,.919540,.0113612)),
                                dot(rgb,float3(.0163916,.0880132,.895595)));
            float peak = max(0,max(wide.r,max(wide.g,wide.b))) * 80;
            if (!isfinite(peak) || peak > 10000) return;
            uint bin = min(255, (uint)(PQ(peak) * 255));
            InterlockedAdd(Histogram[bin], 1);
        }
        """;
    private static readonly Lazy<ReadOnlyMemory<byte>> Code = new(() => Compiler.Compile(Shader, "CS", "HdrContentPeak", "cs_5_0"));
    private static readonly ID3D11ShaderResourceView[] NoSource = new ID3D11ShaderResourceView[1];
    private static readonly ID3D11UnorderedAccessView[] NoOutput = new ID3D11UnorderedAccessView[1];
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11ComputeShader compute;
    private readonly ID3D11Buffer histogram;
    private readonly ID3D11Buffer readback;
    private readonly ID3D11UnorderedAccessView view;
    private readonly HdrLuminance analysis = new();
    private readonly int[] values = new int[HdrLuminance.HistogramBins];
    private bool pending;
    private int frame;

    public HdrContentPeak(ID3D11Device device)
    {
        context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        try
        {
            compute = device.CreateComputeShader(Code.Value.Span);
            histogram = device.CreateBuffer(1024, BindFlags.UnorderedAccess, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
            readback = device.CreateBuffer(1024, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            view = device.CreateUnorderedAccessView(histogram);
        }
        catch
        {
            view?.Dispose(); readback?.Dispose(); histogram?.Dispose(); compute?.Dispose(); context.Dispose();
            throw;
        }
    }

    public float Measure(ID3D11Texture2D input, ID3D11ShaderResourceView source)
    {
        if (pending) Read(wait: false);
        if (!pending && frame++ % 4 == 0)
        {
            context.ClearUnorderedAccessView(view, new Vortice.Mathematics.Int4(0));
            context.CSSetShader(compute);
            context.CSSetShaderResource(0, source);
            context.CSSetUnorderedAccessView(0, view);
            var size = input.Description;
            context.Dispatch((size.Width + 63) / 64, (size.Height + 63) / 64, 1);
            context.CSSetShaderResources(0, 1, NoSource);
            context.CSSetUnorderedAccessViews(0, 1, NoOutput);
            context.CopyResource(readback, histogram);
            pending = true;
        }
        if (analysis.PeakNits == 0) Read(wait: true);
        return analysis.PeakNits;
    }

    private void Read(bool wait)
    {
        MappedSubresource mapped;
        try { mapped = context.Map(readback, MapMode.Read, wait ? MapFlags.None : MapFlags.DoNotWait); }
        catch (SharpGenException error) when (!wait && error.ResultCode == Vortice.DXGI.ResultCode.WasStillDrawing) { return; }
        try
        {
            Marshal.Copy(mapped.DataPointer, values, 0, values.Length);
            analysis.Observe(MemoryMarshal.Cast<int, uint>(values));
            pending = false;
        }
        finally { context.Unmap(readback, 0); }
    }

    public void Dispose()
    {
        view.Dispose(); readback.Dispose(); histogram.Dispose(); compute.Dispose(); context.Dispose();
    }
}
