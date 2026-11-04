using System.Numerics;
using ScrcpySend.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScrcpySend.Media;

internal sealed class HdrToneMapper : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Input : register(t0);
        cbuffer Color : register(b0) { float InverseWhite; float Peak; float2 Padding; };
        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float Encode709(float value)
        {
            value = saturate(value);
            return value < 0.018 ? 4.5 * value : 1.099 * pow(value, 0.45) - 0.099;
        }
        float4 PS(float4 position : SV_Position) : SV_Target
        {
            float3 rgb = Input.Load(int3(position.xy, 0)).rgb * InverseWhite;
            float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            if (luminance <= 0) return float4(0, 0, 0, 1);
            float delta = max(0, luminance - 0.75);
            float mapped = luminance <= 0.75 ? luminance :
                0.75 + delta / (1 + (4 - 1 / (Peak - 0.75)) * delta);
            mapped = saturate(mapped);
            rgb *= mapped / luminance;
            float minimum = min(rgb.r, min(rgb.g, rgb.b));
            if (minimum < 0) rgb = mapped + (rgb - mapped) * (mapped / (mapped - minimum));
            float maximum = max(rgb.r, max(rgb.g, rgb.b));
            if (maximum > 1) rgb = mapped + (rgb - mapped) * ((1 - mapped) / (maximum - mapped));
            return float4(Encode709(rgb.r), Encode709(rgb.g), Encode709(rgb.b), 1);
        }
        """;
    private static readonly Lazy<ReadOnlyMemory<byte>> VertexCode = new(() => Compiler.Compile(Shader, "VS", "HdrToneMapper", "vs_5_0"));
    private static readonly Lazy<ReadOnlyMemory<byte>> PixelCode = new(() => Compiler.Compile(Shader, "PS", "HdrToneMapper", "ps_5_0"));
    private static readonly ID3D11ShaderResourceView[] NoSource = new ID3D11ShaderResourceView[1];
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11Texture2D output;
    private readonly ID3D11RenderTargetView target;
    private readonly int width, height;

    public HdrToneMapper(ID3D11Device device, int width, int height)
    {
        this.device = device;
        this.width = width;
        this.height = height;
        context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        try
        {
            vertex = device.CreateVertexShader(VertexCode.Value.Span);
            pixel = device.CreatePixelShader(PixelCode.Value.Span);
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
            rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            output = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height,
                1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            target = device.CreateRenderTargetView(output);
        }
        catch
        {
            target?.Dispose(); output?.Dispose(); rasterizer?.Dispose(); constants?.Dispose();
            pixel?.Dispose(); vertex?.Dispose(); context.Dispose();
            throw;
        }
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D input, CaptureColor color)
    {
        color.Validate();
        if (!color.Hdr || input.Description.Format != Format.R16G16B16A16_Float)
            throw new ArgumentException("HDR tone mapping requires linear floating-point scRGB capture.");
        using var resource = device.CreateShaderResourceView(input);
        var values = new Vector4(1 / color.WhiteScale, color.PeakScale, 0, 0);
        context.UpdateSubresource(in values, constants);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResource(0, resource);
        context.RSSetState(rasterizer);
        context.RSSetViewport(0, 0, width, height);
        context.OMSetBlendState(null);
        context.OMSetRenderTargets(target);
        try { context.Draw(3, 0); }
        finally
        {
            context.PSSetShaderResources(0, 1, NoSource);
            context.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty);
        }
        return output;
    }

    public void Dispose()
    {
        target.Dispose(); output.Dispose(); rasterizer.Dispose(); constants.Dispose();
        pixel.Dispose(); vertex.Dispose(); context.Dispose();
    }
}
