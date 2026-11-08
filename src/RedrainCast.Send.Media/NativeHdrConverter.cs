using System.Numerics;
using RedrainCast.Send.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RedrainCast.Send.Media;

internal sealed class NativeHdrConverter : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Input : register(t0);
        SamplerState Linear : register(s0);
        cbuffer Raster : register(b0) { float4 Fit; };
        float4 VS(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2,-2) + float2(-1,1), 0, 1);
        }
        float3 PQ(float3 nits) {
            float3 p = pow(saturate(nits / 10000), 2610.0/16384);
            return pow((3424.0/4096 + 2413.0/128*p)/(1+2392.0/128*p),2523.0/32);
        }
        float3 Rgb(float2 pixel) {
            float2 uv = (pixel - Fit.xy) / Fit.zw;
            if (any(uv < 0) || any(uv > 1)) return 0;
            float3 rgb = Input.SampleLevel(Linear, uv, 0).rgb;
            float3 wide = float3(dot(rgb,float3(.627404,.329283,.0433136)),
                                dot(rgb,float3(.069097,.919540,.0113612)),
                                dot(rgb,float3(.0163916,.0880132,.895595)));
            // WGC scRGB is linear Rec.709, with 1.0 representing 80 nits.
            return PQ(wide * 80);
        }
        float Luma(float3 rgb) { return dot(rgb, float3(.2627,.6780,.0593)); }
        float Y(float4 pixel : SV_Position) : SV_Target {
            return round(64 + 876 * saturate(Luma(Rgb(pixel.xy)))) * 64 / 65535;
        }
        float2 UV(float4 pixel : SV_Position) : SV_Target {
            float2 p = floor(pixel.xy) * 2 + .5;
            float3 rgb = (Rgb(p) + Rgb(p+float2(1,0)) + Rgb(p+float2(0,1)) + Rgb(p+1)) / 4;
            float y = Luma(rgb);
            float2 chroma = float2((rgb.b-y)/1.8814, (rgb.r-y)/1.4746);
            return round(clamp(512 + 896*chroma, 64, 960)) * 64 / 65535;
        }
        """;
    private static readonly Lazy<ReadOnlyMemory<byte>> VertexCode = new(() => Compiler.Compile(Shader, "VS", "NativeHdrConverter", "vs_5_0"));
    private static readonly Lazy<ReadOnlyMemory<byte>> LumaCode = new(() => Compiler.Compile(Shader, "Y", "NativeHdrConverter", "ps_5_0"));
    private static readonly Lazy<ReadOnlyMemory<byte>> ChromaCode = new(() => Compiler.Compile(Shader, "UV", "NativeHdrConverter", "ps_5_0"));
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader luma, chroma;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly Dictionary<nint, (ID3D11RenderTargetView Y, ID3D11RenderTargetView UV)> targets = [];
    private readonly int width, height;

    internal NativeHdrConverter(ID3D11Device device, int width, int height)
    {
        this.device = device;
        this.width = width;
        this.height = height;
        context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        try
        {
            if (!device.CheckFormatSupport(Format.P010).HasFlag(FormatSupport.RenderTarget))
                throw new NotSupportedException("GPU cannot render native HDR into P010 surfaces. CPU conversion is not allowed.");
            vertex = device.CreateVertexShader(VertexCode.Value.Span);
            luma = device.CreatePixelShader(LumaCode.Value.Span);
            chroma = device.CreatePixelShader(ChromaCode.Value.Span);
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
            sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        }
        catch
        {
            rasterizer?.Dispose(); sampler?.Dispose(); constants?.Dispose(); chroma?.Dispose(); luma?.Dispose();
            vertex?.Dispose(); context.Dispose();
            throw;
        }
    }

    internal void ConvertInto(ID3D11Texture2D input, int sourceWidth, int sourceHeight,
        ID3D11Texture2D output, CaptureColor color, bool cacheViews = true)
    {
        color.Validate();
        if (!color.Hdr || input.Description.Format != Format.R16G16B16A16_Float)
            throw new NotSupportedException("Native HDR10 requires HDR-active linear FP16 scRGB capture. Restart sharing.");
        var description = output.Description;
        if (description.Format != Format.P010 || description.Width != width || description.Height != height ||
            sourceWidth <= 0 || sourceHeight <= 0)
            throw new NotSupportedException("Native HDR10 conversion requires the fixed negotiated P010 output raster.");
        if (!cacheViews || !targets.TryGetValue(output.NativePointer, out var planes))
        {
            var y = device.CreateRenderTargetView(output, new RenderTargetViewDescription
            {
                Format = Format.R16_UNorm, ViewDimension = RenderTargetViewDimension.Texture2D
            });
            try
            {
                var uv = device.CreateRenderTargetView(output, new RenderTargetViewDescription
                {
                    Format = Format.R16G16_UNorm, ViewDimension = RenderTargetViewDimension.Texture2D
                });
                planes = (y, uv);
                if (cacheViews) targets.Add(output.NativePointer, planes);
            }
            catch { y.Dispose(); throw; }
        }
        try
        {
            var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
            var fitWidth = Math.Max(2, (int)(sourceWidth * scale) & ~1);
            var fitHeight = Math.Max(2, (int)(sourceHeight * scale) & ~1);
            var fit = new Vector4((width - fitWidth) / 2 & ~1, (height - fitHeight) / 2 & ~1, fitWidth, fitHeight);
            using var resource = device.CreateShaderResourceView(input);
            context.UpdateSubresource(in fit, constants);
            context.IASetInputLayout(null);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.PSSetConstantBuffer(0, constants);
            context.PSSetSampler(0, sampler);
            context.PSSetShaderResource(0, resource);
            context.RSSetState(rasterizer);
            context.OMSetBlendState(null);
            context.RSSetViewport(0, 0, width, height);
            context.OMSetRenderTargets(planes.Y);
            context.PSSetShader(luma);
            context.Draw(3, 0);
            context.RSSetViewport(0, 0, width / 2, height / 2);
            context.OMSetRenderTargets(planes.UV);
            context.PSSetShader(chroma);
            context.Draw(3, 0);
            context.Flush();
        }
        finally
        {
            context.PSSetShaderResource(0, null!);
            context.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty);
            if (!cacheViews) { planes.Y.Dispose(); planes.UV.Dispose(); }
        }
    }

    public void Dispose()
    {
        foreach (var planes in targets.Values) { planes.Y.Dispose(); planes.UV.Dispose(); }
        rasterizer.Dispose(); sampler.Dispose(); constants.Dispose(); chroma.Dispose(); luma.Dispose();
        vertex.Dispose(); context.Dispose();
    }
}
