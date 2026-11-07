using RedrainCast.Protocol;
using System.Text.Json;
using RedrainCast.Send.Media;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal static async Task RunColorSelfTestAsync(string reportFile, bool includeDisplayColorProbe = true)
    {
        string? failure = null;
        ColorProbeReport? measurements = null;
        var encoders = new List<string>();
        string? rotation = null;
        try
        {
            if (includeDisplayColorProbe)
            {
                measurements = await Task.Run(ColorDiagnostics.Run);
                rotation = await Task.Run(RotationDiagnostics.Run);
            }
            foreach (var codec in new[] { "h264", "h265", "av1" })
            {
                if (HardwareEncoder.EnumerateNames(codec).Count == 0)
                {
                    encoders.Add($"{codec}: unavailable on this device");
                    continue;
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                encoders.Add(await Task.Run(() => MediaDiagnostics.SelfTestAsync(timeout.Token, codec: codec,
                    frameCount: includeDisplayColorProbe ? 30 : 600)));
                var encoded = await Task.Run(() => MediaDiagnostics.EncodeColorProbeAsync(codec, timeout.Token));
                using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                using var decoder = new HardwareDecoder(device, codec);
                foreach (var turns in new[] { 0, 1, 2, 3, 0 })
                {
                    decoder.Configure(encoded.Configuration, 640, 360);
                    var frames = decoder.Decode(new(PacketType.VideoFrame, 1, 1, 1, encoded.PtsUs,
                        (uint)(1 | turns << 1), encoded.Data));
                    try
                    {
                        if (frames.Count == 0 || frames.Any(frame => frame.RotationDegrees != turns * 90 ||
                            frame.PtsUs != encoded.PtsUs ||
                            frame.PrimariesCode != 1 || frame.TransferCode != 1 || frame.MatrixCode != 1 || frame.RangeCode != 1))
                            throw new IOException($"{codec} hardware decoding lost per-picture rotation/PTS or BT.709 limited SDR metadata.");
                    }
                    finally { foreach (var frame in frames) frame.Dispose(); }
                }
                encoders.Add($"{codec}: hardware-decoded BT.709 limited SDR, unchanged PTS and all four per-picture rotations survived reconfiguration.");
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null), ("failure", failure), ("measurements", ReportJson.Value(measurements)),
            ("rotation", rotation), ("encoders", JsonData.Value(encoders)), ("includeDisplayColorProbe", includeDisplayColorProbe),
            ("syntheticOnly", true), ("videoPixelReadbackForDiagnosticsOnly", true), ("windowsHdrSettingChanged", false)),
            indented: true));
    }
}
