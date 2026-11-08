using System.Numerics;
using System.Runtime.InteropServices;
using RedrainCast.Send.Core;
using SharpGen.Runtime;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RedrainCast.Receive.Media;

internal sealed class HdrRenderer : IDisposable
{
    private const string Header = """
        Texture2D<float> Y : register(t0);
        Texture2D<float2> UV : register(t1);
        Texture3D<float4> Table0 : register(t2);
        Texture3D<float4> Table1 : register(t3);
        Texture3D<float4> Table2 : register(t4);
        Texture3D<float4> Table3 : register(t5);
        Texture3D<float4> Table4 : register(t6);
        Texture3D<float4> Table5 : register(t7);
        Texture3D<float4> Table6 : register(t8);
        Texture3D<float4> Table7 : register(t9);
        Buffer<uint> Measured : register(t10);
        RWBuffer<uint> Peak : register(u0);
        RWStructuredBuffer<uint> Histogram : register(u1);
        groupshared uint GroupPeak;
        cbuffer Color : register(b0) {
            float Weight; float Scale; float Native; float OutputPeak;
            float Width; float Height; float Rotation; float Reserved;
        };
        float Eotf(float x) {
            float p=pow(saturate(x),32.0/2523);
            return 10000*pow(max(p-3424.0/4096,0)/max(2413.0/128-2392.0/128*p,1e-7),16384.0/2610);
        }
        float Oetf(float n) {
            float p=pow(saturate(n/10000),2610.0/16384);
            return pow((3424.0/4096+2413.0/128*p)/(1+2392.0/128*p),2523.0/32);
        }
        float3 ReadPq(int2 p) {
            float y=(Y.Load(int3(p,0))*65535/64-64)/876;
            float2 c=(UV.Load(int3(p/2,0))*65535/64-512)/896;
            return saturate(float3(y+1.4746*c.y,y-.164553*c.x-.571353*c.y,y+1.8814*c.x));
        }
        float3 Linear(float3 pq) { return float3(Eotf(pq.r),Eotf(pq.g),Eotf(pq.b)); }
        [numthreads(16,16,1)]
        void CS(uint3 p : SV_DispatchThreadID,uint index:SV_GroupIndex) {
            if(index==0) GroupPeak=0;
            GroupMemoryBarrierWithGroupSync();
            if(p.x<Width && p.y<Height) {
                float3 n=Linear(ReadPq(p.xy));
                InterlockedMax(GroupPeak,asuint(max(n.r,max(n.g,n.b))));
            }
            GroupMemoryBarrierWithGroupSync();
            if(index==0) InterlockedMax(Peak[0],GroupPeak);
        }
        [numthreads(8,8,1)]
        void HistogramCS(uint3 id:SV_DispatchThreadID) {
            uint2 p=id.xy*8;
            if(p.x>=(uint)Width || p.y>=(uint)Height) return;
            float3 pq=ReadPq(int2(p));
            uint bin=min(255u,(uint)(max(pq.r,max(pq.g,pq.b))*255));
            InterlockedAdd(Histogram[bin],1);
        }
        struct Vertex { float4 position:SV_Position; float2 uv:TEXCOORD; };
        Vertex VS(uint id:SV_VertexID) {
            Vertex v; v.uv=float2((id<<1)&2,id&2);
            v.position=float4(v.uv*float2(2,-2)+float2(-1,1),0,1); return v;
        }
        """;
    private const string Body = """
        float3 MapTables(float3 pq,float peak) {
            if(peak<=100) return SampleTable(Table0,pq);
            if(peak<=200) return lerp(SampleTable(Table0,pq),SampleTable(Table1,pq),log(peak/100)/log(2));
            if(peak<=400) return lerp(SampleTable(Table1,pq),SampleTable(Table2,pq),log(peak/200)/log(2));
            if(peak<=800) return lerp(SampleTable(Table2,pq),SampleTable(Table3,pq),log(peak/400)/log(2));
            if(peak<=1600) return lerp(SampleTable(Table3,pq),SampleTable(Table4,pq),log(peak/800)/log(2));
            if(peak<=3200) return lerp(SampleTable(Table4,pq),SampleTable(Table5,pq),log(peak/1600)/log(2));
            if(peak<=6400) return lerp(SampleTable(Table5,pq),SampleTable(Table6,pq),log(peak/3200)/log(2));
            return lerp(SampleTable(Table6,pq),SampleTable(Table7,pq),saturate(log(peak/6400)/log(10000.0/6400)));
        }
        float G22(float encoded709) {
            float light=encoded709<.081 ? encoded709/4.5 : pow((encoded709+.099)/1.099,1/.45);
            return pow(saturate(light),1/2.2);
        }
        float4 PS(Vertex v):SV_Target {
            float2 uv=v.uv;
            if(Rotation==1) uv=float2(uv.y,1-uv.x);
            else if(Rotation==2) uv=1-uv;
            else if(Rotation==3) uv=float2(1-uv.y,uv.x);
            float3 pq=ReadPq(min(int2(uv*float2(Width,Height)),int2(Width-1,Height-1)));
            float peak=Native<.5 ? Weight : max(100,asfloat(Measured[0]));
            if(Native<.5) {
                float3 rgb=MapTables(pq,peak);
                // LUT output is encoded BT.709; the BGRA swapchain explicitly declares G22.
                rgb=float3(G22(rgb.r),G22(rgb.g),G22(rgb.b));
                float noise=frac(sin(dot(v.position.xy,float2(12.9898,78.233)))*43758.5453)-.5;
                return float4(saturate(rgb+noise/255),1);
            }
            float3 n=Linear(pq);
            // BT.2390 EETF in normalized PQ; preserve hue and absolute scRGB's 80-nit unit.
            float source=Oetf(peak), target=Oetf(min(OutputPeak,peak))/source;
            float knee=1.5*target-.5;
            float l=max(n.r,max(n.g,n.b));
            float e=Oetf(l)/source;
            if(e>knee && target<1) {
                float t=saturate((e-knee)/(1-knee));
                float t2=t*t,t3=t2*t;
                float mapped=(2*t3-3*t2+1)*knee+(t3-2*t2+t)*(1-knee)+(-2*t3+3*t2)*target;
                n*=Eotf(mapped*source)/max(l,1e-6);
            }
            // Bound PQ roundoff at the validated monitor peak without changing hue.
            n*=min(1,OutputPeak/max(max(n.r,max(n.g,n.b)),1e-6));
            float3 rgb=float3(dot(n,float3(1.660491,-.587641,-.072850)),
                             dot(n,float3(-.124550,1.132900,-.008349)),
                             dot(n,float3(-.018151,-.100579,1.118730)));
            return float4(rgb/80,1);
        }
        """;
    private static readonly string Shader = Header + ToneMapShader.Lookup + Body;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11ComputeShader measure;
    private readonly ID3D11ComputeShader histogramShader;
    private readonly ID3D11Buffer histogram, histogramReadback;
    private readonly ID3D11UnorderedAccessView histogramView;
    private HdrLuminance luminance = new();
    private readonly int[] histogramValues = new int[HdrLuminance.HistogramBins];
    private bool histogramPending;
    private int histogramFrame;
    internal float SdrPeakNits => luminance.PeakNits;
    private readonly ID3D11Buffer constants, peak;
    private readonly ID3D11UnorderedAccessView peakUav;
    private readonly ID3D11ShaderResourceView peakView;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11ShaderResourceView[] tables = new ID3D11ShaderResourceView[ToneMapTables.Peaks.Count];
    private ID3D11Texture2D? sample;
    private ID3D11ShaderResourceView? y, uv;
    private readonly ID3D11ShaderResourceView[] empty = new ID3D11ShaderResourceView[11];

    public HdrRenderer(ID3D11Device device)
    {
        this.device = device;
        context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        try
        {
            vertex = device.CreateVertexShader(Compiler.Compile(Shader, "VS", "ReceiveHdr", "vs_5_0").Span);
            pixel = device.CreatePixelShader(Compiler.Compile(Shader, "PS", "ReceiveHdr", "ps_5_0").Span);
            measure = device.CreateComputeShader(Compiler.Compile(Shader, "CS", "ReceiveHdr", "cs_5_0").Span);
            histogramShader = device.CreateComputeShader(Compiler.Compile(Shader, "HistogramCS", "ReceiveHdr", "cs_5_0").Span);
            histogram = device.CreateBuffer(1024, BindFlags.UnorderedAccess, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4);
            histogramReadback = device.CreateBuffer(1024, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            histogramView = device.CreateUnorderedAccessView(histogram);
            constants = device.CreateBuffer(32, BindFlags.ConstantBuffer);
            peak = device.CreateBuffer(4, BindFlags.UnorderedAccess | BindFlags.ShaderResource);
            peakUav = device.CreateUnorderedAccessView(peak, new UnorderedAccessViewDescription
            {
                Format = Format.R32_UInt,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new() { NumElements = 1 }
            });
            peakView = device.CreateShaderResourceView(peak, new ShaderResourceViewDescription
            {
                Format = Format.R32_UInt,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new() { NumElements = 1 }
            });
            rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            for (var i = 0; i < tables.Length; i++) tables[i] = Table(i);
            EnsureSample(64, 64);
            using var output = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 64, 64, 1, 1, BindFlags.RenderTarget));
            using var target = device.CreateRenderTargetView(output);
        }
        catch { Dispose(); throw; }
    }

    private unsafe ID3D11ShaderResourceView Table(int index)
    {
        fixed (Half* data = ToneMapTables.Data(index).Span)
        {
            using var texture = device.CreateTexture3D(new Texture3DDescription(Format.R16G16B16A16_Float,
                ToneMapTables.Size, ToneMapTables.Size, ToneMapTables.Size, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)data, ToneMapTables.Size * 8, ToneMapTables.Size * ToneMapTables.Size * 8));
            return device.CreateShaderResourceView(texture);
        }
    }

    private void EnsureSample(int width, int height)
    {
        if (sample?.Description.Width == width && sample.Description.Height == height) return;
        y?.Dispose(); uv?.Dispose(); sample?.Dispose();
        sample = device.CreateTexture2D(new Texture2DDescription(Format.P010, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
        y = device.CreateShaderResourceView(sample, new ShaderResourceViewDescription
        {
            Format = Format.R16_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new() { MipLevels = 1 }
        });
        uv = device.CreateShaderResourceView(sample, new ShaderResourceViewDescription
        {
            Format = Format.R16G16_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new() { MipLevels = 1 }
        });
    }

    public void Draw(HardwareFrame frame, ID3D11Texture2D output, HdrDisplayPolicy policy)
    {
        policy.Validate();
        var expectedOutput = policy.NativeHdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        if (output.Description.Format != expectedOutput)
            throw new NotSupportedException($"HDR rendering policy requires a {expectedOutput} output texture.");
        System.Runtime.InteropServices.Marshal.AddRef(frame.Texture);
        using var source = new ID3D11Texture2D(frame.Texture);
        if (source.Description.Format != Format.P010) throw new NotSupportedException("HDR requires a P010 GPU surface.");
        EnsureSample((int)source.Description.Width, (int)source.Description.Height);
        context.CopySubresourceRegion(sample!, 0, 0, 0, 0, source, frame.ArraySlice);
        var values = new Constants
        {
            Weight = 0,
            Reserved = 0,
            Scale = ToneMapTables.Size - 1,
            Native = policy.NativeHdr ? 1 : 0,
            OutputPeak = policy.PeakNits,
            Width = frame.Width,
            Height = frame.Height,
            Rotation = frame.RotationDegrees / 90
        };
        context.UpdateSubresource(in values, constants);
        if (policy.NativeHdr)
        {
            context.ClearUnorderedAccessView(peakUav, new Vortice.Mathematics.Int4(0));
            context.CSSetShader(measure);
            context.CSSetConstantBuffer(0, constants);
            context.CSSetShaderResources(0, [y!, uv!]);
            context.CSSetUnorderedAccessView(0, peakUav);
            context.Dispatch((uint)(frame.Width + 15) / 16, (uint)(frame.Height + 15) / 16, 1);
            context.CSSetUnorderedAccessView(0, null);
            context.CSSetShaderResources(0, 2, empty);
        }
        else
        {
            values.Weight = MeasureSdrPeak(frame.Width, frame.Height);
            context.UpdateSubresource(in values, constants);
        }
        using var target = device.CreateRenderTargetView(output);
        context.ClearRenderTargetView(target, new(0, 0, 0, 1));
        var fit = RedrainCast.Receive.Core.VideoLayout.Fit(frame.Width, frame.Height, frame.RotationDegrees,
            (int)output.Description.Width, (int)output.Description.Height);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex); context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResource(0, y!); context.PSSetShaderResource(1, uv!);
        context.PSSetShaderResource(10, peakView);
        for (var i = 0; i < tables.Length; i++) context.PSSetShaderResource((uint)i + 2, tables[i]);
        context.RSSetState(rasterizer);
        context.RSSetViewport(fit.X, fit.Y, fit.Width, fit.Height);
        context.OMSetBlendState(null); context.OMSetRenderTargets(target);
        try
        {
            context.Draw(3, 0);
        }
        finally
        {
            context.PSSetShaderResources(0, 11, empty);
            context.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty);
        }
    }

    private struct Constants
    {
        public float Weight, Scale, Native, OutputPeak, Width, Height, Rotation, Reserved;
    }

    internal void ResetLuminance()
    {
        luminance = new();
        histogramPending = false;
        histogramFrame = 0;
    }

    internal void ScheduleSdrMeasurementForDiagnostics() => histogramFrame = 0;

    internal float CompleteSdrMeasurementForDiagnostics()
    {
        if (histogramPending) ReadHistogram(true);
        return luminance.PeakNits;
    }

    private float MeasureSdrPeak(int width, int height)
    {
        if (histogramPending) ReadHistogram(false);
        if (!histogramPending && histogramFrame++ % 4 == 0)
        {
            context.ClearUnorderedAccessView(histogramView, new Vortice.Mathematics.Int4(0));
            context.CSSetShader(histogramShader);
            context.CSSetConstantBuffer(0, constants);
            context.CSSetShaderResources(0, [y!, uv!]);
            context.CSSetUnorderedAccessView(1, histogramView);
            context.Dispatch((uint)(width + 63) / 64, (uint)(height + 63) / 64, 1);
            context.CSSetUnorderedAccessView(1, null);
            context.CSSetShaderResources(0, 2, empty);
            context.CopyResource(histogramReadback, histogram);
            histogramPending = true;
        }
        if (luminance.PeakNits == 0) ReadHistogram(true);
        return luminance.PeakNits;
    }

    private void ReadHistogram(bool wait)
    {
        MappedSubresource mapped;
        try
        {
            mapped = context.Map(histogramReadback, MapMode.Read,
            wait ? Vortice.Direct3D11.MapFlags.None : Vortice.Direct3D11.MapFlags.DoNotWait);
        }
        catch (SharpGenException error) when (!wait && error.ResultCode == Vortice.DXGI.ResultCode.WasStillDrawing) { return; }
        try
        {
            Marshal.Copy(mapped.DataPointer, histogramValues, 0, histogramValues.Length);
            luminance.Observe(MemoryMarshal.Cast<int, uint>(histogramValues));
            histogramPending = false;
        }
        finally { context.Unmap(histogramReadback, 0); }
    }

    public void Dispose()
    {
        y?.Dispose(); uv?.Dispose(); sample?.Dispose();
        foreach (var table in tables) table?.Dispose();
        rasterizer?.Dispose(); peakView?.Dispose(); peakUav?.Dispose(); peak?.Dispose(); constants?.Dispose();
        histogramView?.Dispose(); histogramReadback?.Dispose(); histogram?.Dispose(); histogramShader?.Dispose();
        measure?.Dispose(); pixel?.Dispose(); vertex?.Dispose(); context.Dispose();
    }
}
