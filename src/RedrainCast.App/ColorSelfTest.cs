using System.Text.Json;
using ScrcpySend.Media;
using ScrcpyReceive.Core;
using ScrcpyReceive.Media;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunColorSelfTestAsync(string reportFile)
    {
        string? failure = null;
        ColorProbeReport? measurements = null;
        var encoders = new List<string>();
        try
        {
            measurements = await Task.Run(ColorDiagnostics.Run);
            foreach (var codec in new[] { "h264", "h265", "av1" })
            {
                if (HardwareEncoder.EnumerateNames(codec).Count == 0)
                {
                    encoders.Add($"{codec}: unavailable on this device");
                    continue;
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                encoders.Add(await Task.Run(() => MediaDiagnostics.SelfTestAsync(timeout.Token, codec: codec)));
                var encoded = await Task.Run(() => MediaDiagnostics.EncodeColorProbeAsync(codec, timeout.Token));
                using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                using var decoder = new HardwareDecoder(device, codec);
                decoder.Configure(encoded.Configuration, 640, 360);
                var frames = decoder.Decode(new(PacketType.VideoFrame, 1, 1, 1, encoded.PtsUs, 1, encoded.Data));
                try
                {
                    if (frames.Count == 0 || frames.Any(frame =>
                        frame.PrimariesCode != 1 || frame.TransferCode != 1 || frame.MatrixCode != 1 || frame.RangeCode != 1))
                        throw new IOException($"{codec} bitstream did not decode as explicit BT.709 primaries/transfer/matrix and limited range.");
                    encoders.Add($"{codec}: encoded bitstream hardware-decoded with primaries=1, transfer=1, matrix=1, range=1 (BT.709 limited SDR).");
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, measurements, encoders,
            syntheticOnly = true, cpuReadbackForDiagnosticsOnly = true, windowsHdrSettingChanged = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
