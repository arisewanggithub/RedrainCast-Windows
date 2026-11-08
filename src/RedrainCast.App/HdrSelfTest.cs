using System.Text.Json.Nodes;
using RedrainCast.Core;
using RedrainCast.Protocol;
using RedrainCast.Receive.Media;
using RedrainCast.Send.Core;
using RedrainCast.Send.Media;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal static async Task<bool> RunHdrSelfTestAsync(string reportFile)
    {
        using var journal = new DiagnosticLog(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(reportFile))!,
            Path.GetFileNameWithoutExtension(reportFile) + "-diagnostics"));
        string? failure = null;
        JsonObject? sender = null;
        ColorProbeReport? captureConversion = null;
        HdrRenderDiagnosticReport? rendering = null;
        HdrDecodedColorProbeReport? roundtrip = null;
        var loopbacks = new List<HdrLoopbackReport>();
        var exactWireColor = false;
        try
        {
            journal.Write("HDR self-test phase: production capture GPU HDR-to-SDR/SDR conversion");
            captureConversion = await Task.Run(ColorDiagnostics.Run);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            journal.Write("HDR self-test phase: synthetic FP16/P010 hardware encoding");
            var probe = await Task.Run(() => MediaDiagnostics.EncodeHdrColorProbeAsync(timeout.Token));
            journal.Write("HDR self-test phase: exact wire color/metadata and coded Main10/VUI validation");
            var wireColor = JsonData.Object(("videoColor", probe.Color.WireValue));
            var receivedColor = HdrVideoProtocol.Format(StrictJson.Parse(JsonData.Bytes(wireColor)));
            HdrVideoProtocol.RequireAcknowledgement(StrictJson.Parse(JsonData.Bytes(wireColor)), probe.Color);
            if (receivedColor != probe.Color)
                throw new IOException("HDR diagnostic wire roundtrip changed the negotiated color format or static metadata.");
            exactWireColor = true;
            VideoBitstream.ValidateConfiguration("h265", probe.Frame.Configuration, probe.Width, probe.Height, receivedColor);
            RedrainCast.Receive.Core.CodecConfiguration.ValidateVideo("h265", probe.Frame.Configuration,
                probe.Width, probe.Height, receivedColor);
            sender = JsonData.Object(("encoder", probe.Encoder), ("width", probe.Width), ("height", probe.Height),
                ("maximumP010CodeError", probe.MaximumCodeError), ("encodedBytes", probe.Frame.Data.Length),
                ("videoColor", probe.Color.WireValue));
            journal.Write("HDR self-test phase: coded hardware color/rotation roundtrip");
            roundtrip = await Task.Run(() => HdrDiagnostics.ValidateHdrColorProbe(probe.Frame, receivedColor,
                probe.Width, probe.Height));
            journal.Write("HDR self-test phase: synthetic HDR/SDR rendering and display policy transitions");
            rendering = await Task.Run(HdrDiagnostics.RunColorDiagnostics);
            foreach (var recovery in new[] { false, true })
            {
                journal.Write($"HDR self-test phase: production TCP loopback; recovery={recovery}");
                loopbacks.Add(await RunHdrLoopbackAsync(probe, recovery, journal));
            }
        }
        catch (Exception error) { failure = error.ToString(); journal.Write(failure); }
        journal.Write($"HDR self-test phase: writing report; passed={failure is null}");
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null), ("failure", failure), ("sender", sender),
            ("captureConversion", ReportJson.Value(captureConversion)),
            ("decodedRoundtrip", ReportJson.Value(roundtrip)), ("rendering", ReportJson.Value(rendering)),
            ("loopbacks", ReportJson.Value(loopbacks.ToArray())),
            ("exactWireColorAndMetadata", exactWireColor), ("syntheticOnly", true),
            ("videoPixelReadbackForDiagnosticsOnly", true), ("windowsHdrSettingChanged", false),
            ("physicalHdrPanelVerified", false), ("androidInteropVerified", false)), indented: true));
        return failure is null;
    }
}
