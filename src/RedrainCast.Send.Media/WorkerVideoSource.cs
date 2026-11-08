using System.Globalization;
using System.Text.Json;
using RedrainCast.Send.Core;

namespace RedrainCast.Send.Media;

public sealed class WorkerVideoSource : ProcessVideoSource, IHdrVideoSource
{
    public override bool SupportsStreamRecovery => true;
    private readonly CaptureSource source;
    private CaptureSettings settings;
    private bool hdrConfigured;
    private readonly Action<string> diagnostic;
    public override RedrainCast.Protocol.HdrVideoFormat ColorFormat => settings.VideoColor ?? RedrainCast.Protocol.HdrVideoFormat.Sdr;
    public bool NativeHdrEligible { get; }
    public ValueTask ConfigureHdrAsync(bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNotPrepared();
        if (hdrConfigured) throw new InvalidOperationException("HDR configuration cannot be changed. Restart sharing.");
        var format = HdrCaptureFormat.Configure(source, settings, enabled, diagnostic);
        settings = settings with { VideoColor = format.Hdr10 ? format : null };
        settings.Validate();
        hdrConfigured = true;
        return ValueTask.CompletedTask;
    }
    protected override string Kind => "Capture worker";
    protected override bool ForwardDiagnostics => true;
    protected override bool RequiresStartCommand => true;
    protected override CancellationToken SourceClosed => source.Closed;
    protected override void ValidateSource()
    {
        source.EnsureAvailable();
        if (ColorFormat.Hdr10) HdrCaptureFormat.ValidateActive(source, ColorFormat, source.ReadColor(diagnostic));
    }

    public WorkerVideoSource(string executable, CaptureSource source, CaptureSettings settings, Action<string> diagnostic)
        : base(ExecutablePath(executable), settings.OutputSize(source.Item.Size.Width, source.Item.Size.Height).Width,
            settings.OutputSize(source.Item.Size.Width, source.Item.Size.Height).Height, settings.VideoCodec,
            $"Isolated hardware {settings.VideoCodec}", diagnostic, 1024L * 1024 * 1024)
    {
        settings.Validate();
        source.EnsureAvailable();
        var color = source.ReadColor(diagnostic);
        EncoderCompatibility.ValidateCapture(0, color.Hdr, settings.VideoCodec);
        NativeHdrEligible = HdrCaptureFormat.Eligible(source, settings, color, diagnostic);
        this.source = source;
        this.settings = settings;
        this.diagnostic = diagnostic;
    }

    private static string ExecutablePath(string appExecutable) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(appExecutable))!, "capture-worker", "RedrainCast.CaptureWorker.exe");

    protected override IEnumerable<string> Arguments() =>
    [
        "--capture-worker", CaptureWorkerHandshake.PreparationArgument, source.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
        source.IsWindow ? "window" : "display", JsonSerializer.Serialize(settings, CaptureSettingsJsonContext.Default.CaptureSettings),
        Width.ToString(CultureInfo.InvariantCulture), Height.ToString(CultureInfo.InvariantCulture),
        source.WindowProcessId.ToString(CultureInfo.InvariantCulture), source.WindowThreadId.ToString(CultureInfo.InvariantCulture)
    ];
}
