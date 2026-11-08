using System.Numerics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace RedrainCast.Receive.Media;

public sealed record HdrDecodedColorProbeReport(int Width, int Height, int Pictures, int Rotations,
    int OutputPolicyTransitions, float MaximumSdrCodeError, float MaximumNativeScRgbError,
    float MaximumNativeNitsError, bool PhysicalHdrPanelVerified = false)
{
    public int DecoderReconfigurations { get; init; }
    public int Generations { get; init; }
    public bool ScalingVerified { get; init; }
    public bool Chroma420Verified { get; init; }
}

public sealed record HdrRenderDiagnosticReport(bool Main10P0101080Supported, bool Main10P0104KSupported,
    float MaximumSdrCodeError, float MaximumNativeScRgbError, float PeakMappingErrorNits,
    int Rotations, int OutputPolicyTransitions, bool PhysicalHdrPanelVerified = false)
{
    public bool ScalingVerified { get; init; }
    public bool Chroma420Verified { get; init; }
    public bool DitherVerified { get; init; }
    public float DitherMeanErrorCodes { get; init; }
    public int DitherCodeSpan { get; init; }
    public int DitherDistinctCodes { get; init; }
    public bool UnknownMonitorPeakFallbackVerified { get; init; }
    public bool MetadataQueryRecoveryVerified { get; init; }
    public bool FatalDisplayFailureVerified { get; init; }
    public string Summary => $"HDR receive hardware verified: Main10/P010 1080p and 4K decoder creation; " +
        $"GPU black/white/highlights/saturated colors, SDR error {MaximumSdrCodeError:F3} codes, " +
        $"absolute FP16 scRGB error {MaximumNativeScRgbError:F5}, 250/400-nit peak error {PeakMappingErrorNits:F3} nits, " +
        $"{Rotations} rotations and {OutputPolicyTransitions} live SDR/HDR swapchain transitions; " +
        "unknown DXGI monitor peak selects explicit SDR tone-mapping; " +
        "shared GPU histogram percentile/outlier/temporal policy verified; point-sampled P010 up/downscaling and 2x2 chroma verified, " +
        $"dither span {DitherCodeSpan} code, mean error {DitherMeanErrorCodes:F4} codes. Physical HDR panel appearance remains unverified.";
}

/// <summary>Explicit hardware self-test. GPU readback is restricted to this diagnostic.</summary>
public static unsafe class HdrDiagnostics
{
    public static HdrDecodedColorProbeReport ValidateHdrColorProbe(EncodedVideo encoded, HdrVideoFormat color, int width, int height)
    {
        if (!color.Hdr10) throw new ArgumentException("The coded color probe requires HDR10.", nameof(color));
        using var presenter = new GpuPresenter(60, color);
        using var decoder = new HardwareDecoder(presenter.Device, "h265", color);
        decoder.Configure(encoded.Configuration, width, height);
        presenter.SetViewport(width, height, 1, 1);
        var sdrError = 0f;
        var nativeError = 0f;
        uint generation = 1;
        var reconfigurations = 0;
        var quadrants = new Vector3[] { new(12.5f), new(12.5f, 0, 0), new(0, 12.5f, 0), new(0, 0, 12.5f) };
        for (var native = 0; native < 2; native++)
            for (var rotation = 0; rotation < 4; rotation++)
            {
                if (native != 0 && rotation == 0 || rotation == 2)
                {
                    decoder.Configure(encoded.Configuration, width, height);
                    generation++;
                    reconfigurations++;
                }
                presenter.SetDisplayPolicy(new(native == 1, 2000, "coded color/rotation diagnostic"));
                var pts = encoded.PtsUs + (native * 4 + rotation) * 16666;
                var frames = decoder.Decode(new(RedrainCast.Receive.Core.PacketType.VideoFrame, 1, 1, generation,
                    pts, (uint)(1 | rotation << 1), encoded.Data));
                try
                {
                    if (frames.Count != 1) throw new InvalidDataException("HDR color probe must produce exactly one hardware picture.");
                    var frame = frames[0];
                    if (frame.PtsUs != pts || frame.RotationDegrees != rotation * 90 || frame.ColorFormat != color ||
                        decoder.HardwareSurfaceCount < decoder.MinimumHardwareSurfaces || decoder.HardwareSurfaceCount > 64)
                        throw new InvalidDataException("Reconfigured HDR decoder lost timestamp/rotation/color or bounded surfaces.");
                    var fit = RedrainCast.Receive.Core.VideoLayout.Fit(width, height, rotation * 90, width, height);
                    presenter.InspectForDiagnostics(frame, texture => Read(presenter.Device, texture, mapped =>
                    {
                        for (var corner = 0; corner < 4; corner++)
                        {
                            var u = corner % 2 == 0 ? .25f : .75f;
                            var v = corner < 2 ? .25f : .75f;
                            var source = rotation switch
                            {
                                0 => new Vector2(u, v),
                                1 => new Vector2(v, 1 - u),
                                2 => new Vector2(1 - u, 1 - v),
                                _ => new Vector2(1 - v, u)
                            };
                            var expected = quadrants[(source.Y < .5 ? 0 : 2) + (source.X < .5 ? 0 : 1)];
                            var x = fit.X + (int)(fit.Width * u);
                            var y = fit.Y + (int)(fit.Height * v);
                            if (native == 1)
                            {
                                var pixel = (Half*)((byte*)mapped.DataPointer + y * mapped.RowPitch) + x * 4;
                                for (var c = 0; c < 3; c++) nativeError = Math.Max(nativeError, Math.Abs((float)pixel[c] - expected[c]));
                            }
                            else
                            {
                                expected = OutputSdr(ToneMapTables.Sample(expected, presenter.SdrPeakNits)) * 255;
                                var pixel = (byte*)mapped.DataPointer + y * mapped.RowPitch + x * 4;
                                for (var c = 0; c < 3; c++) sdrError = Math.Max(sdrError, Math.Abs(pixel[2 - c] - expected[c]));
                            }
                        }
                    }));
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
        if (sdrError > 6 || nativeError > .25)
            throw new InvalidDataException($"Actual coded HDR quadrant/rotation mismatch: SDR error {sdrError:F3} codes, native error {nativeError:F4} scRGB.");
        ValidateChromaScaling(presenter.Device);
        return new(width, height, 8, 4, 1, sdrError, nativeError, nativeError * 80)
        {
            DecoderReconfigurations = reconfigurations,
            Generations = (int)generation,
            ScalingVerified = true,
            Chroma420Verified = true
        };
    }

    public static string ReplayHdrStream(string streamPath, string configurationPath, int width, int height,
        HdrVideoFormat color)
    {
        if (!color.Hdr10) throw new ArgumentException("This diagnostic requires HDR10.", nameof(color));
        color.Validate("h265");
        var configuration = Hevc.ParameterSets(File.ReadAllBytes(configurationPath));
        RedrainCast.Receive.Core.CodecConfiguration.ValidateVideo("h265", configuration, width, height, color);
        using var presenter = new GpuPresenter(60, color);
        using var decoder = new HardwareDecoder(presenter.Device, "h265", color);
        decoder.Configure(configuration, width, height);
        var picture = new List<byte[]>();
        var count = 0;
        foreach (var unit in AnnexB.Units(File.ReadAllBytes(streamPath)))
        {
            picture.Add(unit);
            if ((unit[0] >> 1 & 63) > 31) continue;
            var bytes = AnnexB.Join(picture);
            picture.Clear();
            var rotation = count % 4;
            var pts = count * 16666L;
            var frames = decoder.Decode(new(RedrainCast.Receive.Core.PacketType.VideoFrame, 1, 1, 1, pts,
                (uint)((Hevc.IsKeyFrame(bytes) ? 1 : 0) | rotation << 1), bytes));
            try
            {
                if (frames.Count != 1 || frames[0].PtsUs != pts || frames[0].RotationDegrees != rotation * 90 ||
                    frames[0].ColorFormat != color)
                    throw new InvalidDataException("Actual HDR decode did not preserve the assigned picture timestamp/rotation/color.");
                presenter.Present(frames[0]);
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
            count++;
        }
        if (count == 0) throw new InvalidDataException("HDR diagnostic stream has no pictures.");
        decoder.Configure(configuration, width, height);
        return $"Production HDR replay {width}x{height}: {count} pictures, exact assigned timestamps/rotation/color, " +
            $"bounded hardware surfaces={decoder.HardwareSurfaceCount}; no delayed drain. Physical presentation was not measured.";
    }

    public static string Run() => RunColorDiagnostics().Summary;

    public static HdrRenderDiagnosticReport RunColorDiagnostics()
    {
        ValidateMetadataPlaybackLifecycle();
        var color = new HdrVideoFormat(true);
        using var presenter = new GpuPresenter(60, color);
        using var decoder = new HardwareDecoder(presenter.Device, "h265", color);
        decoder.ProbeSize(1920, 1080);
        decoder.ProbeSize(3840, 2160);
        using var renderer = new HdrRenderer(presenter.Device);
        var width = 64;
        var height = 64;
        var values = new ushort[width * height * 3 / 2];
        var levels = new Vector3[] { new(0), new(100), new(400), new(1000),
            new(400,0,0), new(0,400,0), new(0,0,400), new(50,200,400) };
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var n = levels[x / 8];
                var pq = new Vector3(HdrLuminance.ToPq(n.X), HdrLuminance.ToPq(n.Y), HdrLuminance.ToPq(n.Z));
                var luma = Vector3.Dot(pq, new(.2627f, .6780f, .0593f));
                values[y * width + x] = (ushort)((64 + MathF.Round(luma * 876)) * 64);
                if (y % 2 == 0 && x % 2 == 0)
                {
                    var offset = width * height + y / 2 * width + x;
                    values[offset] = (ushort)((512 + MathF.Round((pq.Z - luma) / 1.8814f * 896)) * 64);
                    values[offset + 1] = (ushort)((512 + MathF.Round((pq.X - luma) / 1.4746f * 896)) * 64);
                }
            }
        Vector3 Linear(int x)
        {
            var y = (values[x] / 64f - 64) / 876;
            var cb = (values[width * height + x / 2 * 2] / 64f - 512) / 896;
            var cr = (values[width * height + x / 2 * 2 + 1] / 64f - 512) / 896;
            return new(HdrLuminance.FromPq(y + 1.4746f * cr),
                HdrLuminance.FromPq(y - .164553f * cb - .571353f * cr), HdrLuminance.FromPq(y + 1.8814f * cb));
        }
        var histogram = new uint[HdrLuminance.HistogramBins];
        for (var y = 0; y < height; y += 8)
            for (var x = 0; x < width; x += 8)
            {
                var n = Linear(x);
                var bin = Math.Min(255, (int)(HdrLuminance.ToPq(Math.Max(n.X, Math.Max(n.Y, n.Z))) * 255));
                histogram[bin]++;
            }
        var expectedSdrPeak = new HdrLuminance().Observe(histogram);
        var maximumSdrError = 0f;
        var maximumNativeError = 0f;
        var peakError = 0f;
        var ditherMeanError = 0f;
        var ditherSpan = 0;
        var ditherDistinct = 0;
        fixed (ushort* data = values)
        {
            using var texture = presenter.Device.CreateTexture2D(new Texture2DDescription(Format.P010,
                (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)data, (uint)(width * 2)));
            var native = ffmpeg.av_frame_alloc();
            if (native == null) throw new OutOfMemoryException();
            native->data[0] = (byte*)texture.NativePointer;
            native->width = width; native->height = height; native->opaque = (void*)1;
            using var frame = new HardwareFrame(native, color);
            using var sdr = presenter.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
            renderer.Draw(frame, sdr, HdrDisplayPolicy.Sdr);
            if (Math.Abs(renderer.SdrPeakNits - expectedSdrPeak) > .1)
                throw new InvalidDataException("GPU HDR histogram differs from shared HdrLuminance percentile measurement.");
            Read(presenter.Device, sdr, mapped =>
            {
                for (var i = 0; i < 8; i++)
                {
                    var nits = Linear(i * 8 + 4);
                    var expected = OutputSdr(ToneMapTables.Sample(ScRgb(nits), expectedSdrPeak)) * 255;
                    var pixel = (byte*)mapped.DataPointer + 32 * mapped.RowPitch + (i * 8 + 4) * 4;
                    maximumSdrError = Math.Max(maximumSdrError, Math.Max(Math.Abs(pixel[2] - expected.X),
                        Math.Max(Math.Abs(pixel[1] - expected.Y), Math.Abs(pixel[0] - expected.Z))));
                    if (Math.Abs(pixel[2] - expected.X) > 3 || Math.Abs(pixel[1] - expected.Y) > 3 ||
                        Math.Abs(pixel[0] - expected.Z) > 3)
                        throw new InvalidDataException($"GPU P010->SDR mismatch at {nits} nits: RGB {pixel[2]}/{pixel[1]}/{pixel[0]}, expected {expected}.");
                }
                var ditherCodes = new HashSet<byte>();
                var total = 0d;
                for (var y = 0; y < height; y++)
                    for (var x = 8; x < 16; x++)
                    {
                        var code = ((byte*)mapped.DataPointer)[y * mapped.RowPitch + x * 4 + 2];
                        ditherCodes.Add(code); total += code;
                    }
                var ideal = OutputSdr(ToneMapTables.Sample(ScRgb(Linear(12)), expectedSdrPeak)).X * 255;
                ditherMeanError = (float)Math.Abs(total / (height * 8) - ideal);
                ditherSpan = ditherCodes.Max() - ditherCodes.Min();
                ditherDistinct = ditherCodes.Count;
                if (ditherDistinct != 2 || ditherSpan != 1 || ditherMeanError > .2)
                    throw new InvalidDataException("8-bit SDR dither is absent, exceeds one code, or has excessive mean bias.");
            });
            using var hdr = presenter.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,
                (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
            renderer.Draw(frame, hdr, new(true, 2000, "diagnostic"));
            Read(presenter.Device, hdr, mapped =>
            {
                for (var i = 0; i < 8; i++)
                {
                    maximumNativeError = Math.Max(maximumNativeError, CheckFp16(mapped, i * 8 + 4, 32, ScRgb(Linear(i * 8 + 4))));
                }
            });
            foreach (var peak in new[] { 250f, 400f })
            {
                renderer.Draw(frame, hdr, HdrDisplayPolicy.FromMonitor(true, peak));
                Read(presenter.Device, hdr, mapped =>
                {
                    var pixel = (Half*)((byte*)mapped.DataPointer + 32 * mapped.RowPitch) + 28 * 4;
                    var actual = (float)pixel[0] * 80;
                    peakError = Math.Max(peakError, Math.Abs(actual - peak));
                    if (actual > peak || Math.Abs(actual - peak) > 1)
                        throw new InvalidDataException($"Native HDR peak mapping did not map measured content to {peak} nits: {actual}.");
                });
            }
            for (var rotation = 0; rotation < 4; rotation++)
            {
                native->opaque = (void*)(nuint)(rotation + 1);
                renderer.Draw(frame, hdr, new(true, 2000, "rotation diagnostic"));
                var sourceX = rotation switch { 0 => 32, 1 => 8, 2 => 31, _ => 55 };
                Read(presenter.Device, hdr, mapped => maximumNativeError = Math.Max(maximumNativeError,
                    CheckFp16(mapped, 32, 8, ScRgb(Linear(sourceX)))));
            }
            presenter.SetViewport(64, 64, 1, 1);
            native->opaque = (void*)1;
            var policies = new[]
            {
                HdrDisplayPolicy.FromMonitor(true, 250),
                HdrDisplayPolicy.FromMonitor(true, 0),
                HdrDisplayPolicy.FromMonitor(true, 400),
                HdrDisplayPolicy.Sdr
            };
            foreach (var policy in policies)
            {
                presenter.SetDisplayPolicy(policy);
                presenter.Prepare(width, height);
                using var backBuffer = presenter.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                if (backBuffer.Description.Format != (policy.NativeHdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm))
                    throw new InvalidDataException("Local output transition did not safely reconfigure the composition swapchain.");
                renderer.Draw(frame, backBuffer, policy);
                if (!policy.NativeHdr)
                {
                    if (policy.Capabilities(true).NativeHdrAvailable != false)
                        throw new InvalidDataException("Unknown monitor peak incorrectly advertises native HDR.");
                    Read(presenter.Device, backBuffer, mapped =>
                    {
                        var expected = OutputSdr(ToneMapTables.Sample(ScRgb(Linear(28)), renderer.SdrPeakNits)).X * 255;
                        var actual = ((byte*)mapped.DataPointer)[32 * mapped.RowPitch + 28 * 4 + 2];
                        if (Math.Abs(actual - expected) > 3)
                            throw new InvalidDataException("Unknown monitor peak did not use the SDR tone-mapping shader.");
                    });
                }
            }
        }
        ValidateHistogramTemporalPolicy(presenter.Device);
        ValidateChromaScaling(presenter.Device);
        return new(true, true, maximumSdrError, maximumNativeError, peakError, 4, 4)
        {
            ScalingVerified = true,
            Chroma420Verified = true,
            DitherVerified = true,
            UnknownMonitorPeakFallbackVerified = true,
            MetadataQueryRecoveryVerified = true,
            FatalDisplayFailureVerified = true,
            DitherMeanErrorCodes = ditherMeanError,
            DitherCodeSpan = ditherSpan,
            DitherDistinctCodes = ditherDistinct
        };
    }

    private static void ValidateMetadataPlaybackLifecycle()
    {
        var color = new HdrVideoFormat(true);
        var presenter = new GpuPresenter(60, color);
        HardwareDecoder decoder;
        try { decoder = new HardwareDecoder(presenter.Device, "h265", color); }
        catch { presenter.Dispose(); throw; }
        var playback = new VideoPlayback(presenter, decoder, () => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask, null, "h265", color);
        try
        {
            presenter.SetViewport(64, 64, 1, 1);
            var failures = new Exception[]
            {
                new HdrDisplayMetadataException("The window's current DXGI display could not be identified."),
                new COMException("Injected factory query failure", unchecked((int)0x80004005)),
                new COMException("Injected GetDesc1 failure", unchecked((int)0x80004002))
            };
            foreach (var failure in failures)
            {
                var policies = new[]
                {
                    HdrDisplayPolicy.FromMonitor(true, 250),
                    HdrDisplayPolicy.ReadMetadata(() => throw failure),
                    HdrDisplayPolicy.FromMonitor(true, 400)
                };
                foreach (var policy in policies)
                {
                    playback.RefreshDisplay(policy);
                    playback.CheckHealth();
                    presenter.Prepare(64, 64);
                    using var buffer = presenter.SwapChain.GetBuffer<ID3D11Texture2D>(0);
                    if (buffer.Description.Format != (policy.NativeHdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm) ||
                        !policy.Capabilities(true).CanReceiveHdr10 ||
                        policy.Capabilities(false).CanReceiveHdr10)
                        throw new InvalidDataException("Metadata query fallback/recovery lost HDR playback capability or swapchain format.");
                    playback.CheckHealth();
                }
            }
            var gpuFailure = new COMException("Injected swapchain device removal", unchecked((int)0x887A0005));
            playback.FailDisplay(gpuFailure);
            playback.RefreshDisplay(HdrDisplayPolicy.FromMonitor(true, 250));
            try { playback.CheckHealth(); }
            catch (IOException error) when (ReferenceEquals(error.InnerException, gpuFailure)) { return; }
            throw new InvalidDataException("Actual GPU display failure was incorrectly treated as recoverable metadata.");
        }
        finally { playback.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void ValidateChromaScaling(ID3D11Device device)
    {
        const int width = 8, height = 4;
        var pixels = new ushort[width * height * 3 / 2];
        pixels.AsSpan(0, width * height).Fill(500 * 64);
        for (var cy = 0; cy < height / 2; cy++)
            for (var cx = 0; cx < width / 2; cx++)
            {
                var offset = width * height + cy * width + cx * 2;
                pixels[offset] = (ushort)((464 + cx * 32) * 64);
                pixels[offset + 1] = (ushort)((560 - cx * 32 - cy * 16) * 64);
            }
        Vector3 Expected(int x, int y)
        {
            var luma = (pixels[y * width + x] / 64f - 64) / 876;
            var offset = width * height + y / 2 * width + x / 2 * 2;
            var cb = (pixels[offset] / 64f - 512) / 896;
            var cr = (pixels[offset + 1] / 64f - 512) / 896;
            return ScRgb(new(HdrLuminance.FromPq(luma + 1.4746f * cr),
                HdrLuminance.FromPq(luma - .164553f * cb - .571353f * cr), HdrLuminance.FromPq(luma + 1.8814f * cb)));
        }
        using var renderer = new HdrRenderer(device);
        fixed (ushort* data = pixels)
        {
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.P010, width, height, 1, 1, BindFlags.ShaderResource),
                new SubresourceData((nint)data, width * 2));
            var native = ffmpeg.av_frame_alloc();
            if (native == null) throw new OutOfMemoryException();
            native->data[0] = (byte*)texture.NativePointer; native->width = width; native->height = height;
            using var frame = new HardwareFrame(native, new(true));
            foreach (var size in new[] { (17, 11), (5, 9) })
                for (var rotation = 0; rotation < 4; rotation++)
                {
                    native->opaque = (void*)(nuint)(rotation + 1);
                    using var output = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,
                        (uint)size.Item1, (uint)size.Item2, 1, 1, BindFlags.RenderTarget));
                    renderer.Draw(frame, output, new(true, 2000, "P010 chroma/scaling diagnostic"));
                    var fit = RedrainCast.Receive.Core.VideoLayout.Fit(width, height, rotation * 90, size.Item1, size.Item2);
                    Read(device, output, mapped =>
                    {
                        for (var y = 0; y < size.Item2; y++)
                            for (var x = 0; x < size.Item1; x++)
                            {
                                var possibilities = new List<Vector3> { Vector3.Zero };
                                if (x >= fit.X && x < fit.X + fit.Width && y >= fit.Y && y < fit.Y + fit.Height)
                                {
                                    var u = (x - fit.X + .5f) / fit.Width;
                                    var v = (y - fit.Y + .5f) / fit.Height;
                                    var source = rotation switch
                                    {
                                        0 => new Vector2(u, v),
                                        1 => new Vector2(v, 1 - u),
                                        2 => new Vector2(1 - u, 1 - v),
                                        _ => new Vector2(1 - v, u)
                                    };
                                    var sx = Math.Clamp((int)(source.X * width), 0, width - 1);
                                    var sy = Math.Clamp((int)(source.Y * height), 0, height - 1);
                                    // Interpolation rounding can select either adjacent point at an exact texel boundary.
                                    var rx = (int)MathF.Round(source.X * width);
                                    var ry = (int)MathF.Round(source.Y * height);
                                    var boundaryX = rx > 0 && rx < width && Math.Abs(source.X * width - rx) < .00001f;
                                    var boundaryY = ry > 0 && ry < height && Math.Abs(source.Y * height - ry) < .00001f;
                                    if (boundaryX) sx = rx;
                                    if (boundaryY) sy = ry;
                                    possibilities.Clear();
                                    possibilities.Add(Expected(sx, sy));
                                    if (boundaryX) possibilities.Add(Expected(sx - 1, sy));
                                    if (boundaryY) possibilities.Add(Expected(sx, sy - 1));
                                    if (boundaryX && boundaryY) possibilities.Add(Expected(sx - 1, sy - 1));
                                }
                                var verified = false;
                                foreach (var expected in possibilities)
                                {
                                    try { CheckFp16(mapped, x, y, expected); verified = true; break; }
                                    catch (InvalidDataException) { }
                                }
                                if (!verified) throw new InvalidDataException($"P010 point-sampling/chroma mismatch {size}, rotation {rotation}, pixel ({x},{y}).");
                            }
                    });
                }
        }
    }

    private static void ValidateHistogramTemporalPolicy(ID3D11Device device)
    {
        const int width = 1920, height = 1080;
        using var renderer = new HdrRenderer(device);
        using var output = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 64, 1, 1, BindFlags.RenderTarget));
        var analysis = new HdrLuminance();
        var previous = 0f;
        for (var step = 0; step < 4; step++)
        {
            var pq = step == 3 ? HdrLuminance.ToPq(100) : ((step == 2 ? 189 : 190) + .5f) / 255;
            var code = (ushort)((64 + MathF.Round(pq * 876)) * 64);
            var pixels = new ushort[width * height * 3 / 2];
            pixels.AsSpan(0, width * height).Fill(code);
            pixels.AsSpan(width * height).Fill(512 * 64);
            var bins = new uint[HdrLuminance.HistogramBins];
            var bin = (int)((code / 64f - 64) / 876 * 255);
            bins[bin] = (width / 8) * (height / 8);
            if (step == 1)
            {
                pixels[0] = 940 * 64;
                bins[bin]--; bins[255]++;
            }
            var expected = analysis.Observe(bins);
            fixed (ushort* data = pixels)
            {
                using var texture = device.CreateTexture2D(new Texture2DDescription(Format.P010, width, height, 1, 1, BindFlags.ShaderResource),
                    new SubresourceData((nint)data, width * 2));
                var native = ffmpeg.av_frame_alloc();
                if (native == null) throw new OutOfMemoryException();
                native->data[0] = (byte*)texture.NativePointer;
                native->width = width; native->height = height; native->opaque = (void*)1;
                using var frame = new HardwareFrame(native, new(true));
                renderer.ScheduleSdrMeasurementForDiagnostics();
                renderer.Draw(frame, output, HdrDisplayPolicy.Sdr);
                var measured = renderer.CompleteSdrMeasurementForDiagnostics();
                if (Math.Abs(measured - expected) > .1 ||
                    step == 1 && Math.Abs(measured - previous) > .1 ||
                    step == 2 && !(measured < previous && measured > HdrLuminance.FromPq((bin + 1f) / 255)))
                    throw new InvalidDataException("GPU percentile/outlier/temporal HDR policy differs from shared HdrLuminance.Observe.");
                previous = measured;
            }
        }
    }

    private static Vector3 ScRgb(Vector3 n) => new Vector3(
        Vector3.Dot(n, new(1.660491f, -.587641f, -.072850f)),
        Vector3.Dot(n, new(-.124550f, 1.132900f, -.008349f)),
        Vector3.Dot(n, new(-.018151f, -.100579f, 1.118730f))) / 80;

    private static Vector3 OutputSdr(Vector3 encoded)
    {
        static float G22(float value) => MathF.Pow(value < .081f ? value / 4.5f
            : MathF.Pow((value + .099f) / 1.099f, 1 / .45f), 1 / 2.2f);
        return new(G22(encoded.X), G22(encoded.Y), G22(encoded.Z));
    }

    private static float CheckFp16(MappedSubresource mapped, int x, int y, Vector3 expected)
    {
        var pixel = (Half*)((byte*)mapped.DataPointer + y * mapped.RowPitch) + x * 4;
        var maximum = 0f;
        for (var channel = 0; channel < 3; channel++)
        {
            maximum = Math.Max(maximum, Math.Abs((float)pixel[channel] - expected[channel]));
            if (Math.Abs((float)pixel[channel] - expected[channel]) > Math.Max(.003, Math.Abs(expected[channel]) * .003))
                throw new InvalidDataException($"P010->FP16/rotation failed: channel {channel}, actual {(float)pixel[channel]}, expected {expected[channel]}.");
        }
        return maximum;
    }

    private static void Read(ID3D11Device device, ID3D11Texture2D texture, Action<MappedSubresource> inspect)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(description);
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try { inspect(mapped); }
        finally { context.Unmap(staging, 0); }
    }
}
