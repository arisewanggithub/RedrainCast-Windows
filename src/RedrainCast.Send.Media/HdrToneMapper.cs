using System.Numerics;
using RedrainCast.Send.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RedrainCast.Send.Media;

internal sealed class HdrToneMapper : IDisposable
{
    private const string Shader = """
        Texture2D<float4> Input : register(t0);
        Texture3D<float4> Low : register(t1);
        Texture3D<float4> High : register(t2);
        cbuffer Color : register(b0) { float Weight; float Scale; float Offset; float Padding; };
        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }
        float PQ(float nits) {
            float p = pow(saturate(nits / 10000), 2610.0 / 16384);
            return pow((3424.0/4096 + 2413.0/128*p)/(1+2392.0/128*p),2523.0/32);
        }
        """ + ToneMapShader.Lookup + """
        float4 PS(float4 position : SV_Position) : SV_Target
        {
            float3 rgb = Input.Load(int3(position.xy, 0)).rgb;
            float3 wide = float3(dot(rgb,float3(.627404,.329283,.0433136)),
                                dot(rgb,float3(.069097,.919540,.0113612)),
                                dot(rgb,float3(.0163916,.0880132,.895595)));
            float3 pq = float3(PQ(wide.r*80),PQ(wide.g*80),PQ(wide.b*80));
            return float4(lerp(SampleTable(Low,pq),SampleTable(High,pq),Weight),1);
        }
        """;
    private static readonly Lazy<ReadOnlyMemory<byte>> VertexCode = new(() => Compiler.Compile(Shader, "VS", "HdrToneMapper", "vs_5_0"));
    private static readonly Lazy<ReadOnlyMemory<byte>> PixelCode = new(() => Compiler.Compile(Shader, "PS", "HdrToneMapper", "ps_5_0"));
    private static readonly ID3D11ShaderResourceView[] NoSource = new ID3D11ShaderResourceView[3];
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertex;
    private readonly ID3D11PixelShader pixel;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11Texture2D output;
    private readonly ID3D11RenderTargetView target;
    private readonly HdrContentPeak luminance;
    private readonly ID3D11Texture3D?[] tables = new ID3D11Texture3D?[ToneMapTables.Peaks.Count];
    private readonly ID3D11ShaderResourceView?[] views = new ID3D11ShaderResourceView?[ToneMapTables.Peaks.Count];
    private readonly int width, height;
    public float PeakNits { get; private set; }

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
            luminance = new(device);
        }
        catch
        {
            luminance?.Dispose(); target?.Dispose(); output?.Dispose(); rasterizer?.Dispose(); constants?.Dispose();
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
        PeakNits = luminance.Measure(input, resource);
        var selection = ToneMapTables.Select(PeakNits);
        var values = new Vector4(selection.Weight, ToneMapTables.Size - 1, 0, 0);
        context.UpdateSubresource(in values, constants);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetShader(pixel);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetShaderResource(0, resource);
        context.PSSetShaderResource(1, Table(selection.Low));
        context.PSSetShaderResource(2, Table(selection.High));
        context.RSSetState(rasterizer);
        context.RSSetViewport(0, 0, width, height);
        context.OMSetBlendState(null);
        context.OMSetRenderTargets(target);
        try { context.Draw(3, 0); }
        finally
        {
            context.PSSetShaderResources(0, 3, NoSource);
            context.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty);
        }
        return output;
    }

    private unsafe ID3D11ShaderResourceView Table(int index)
    {
        if (views[index] is { } cached) return cached;
        var data = ToneMapTables.Data(index).Span;
        fixed (Half* pointer = data)
        {
            var texture = device.CreateTexture3D(new Texture3DDescription(Format.R16G16B16A16_Float,
                ToneMapTables.Size, ToneMapTables.Size, ToneMapTables.Size, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)pointer, ToneMapTables.Size * 8, ToneMapTables.Size * ToneMapTables.Size * 8));
            try
            {
                var view = device.CreateShaderResourceView(texture);
                tables[index] = texture;
                views[index] = view;
                return view;
            }
            catch { texture.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        foreach (var view in views) view?.Dispose();
        foreach (var table in tables) table?.Dispose();
        luminance.Dispose(); target.Dispose(); output.Dispose(); rasterizer.Dispose(); constants.Dispose();
        pixel.Dispose(); vertex.Dispose(); context.Dispose();
    }
}
