using System.Globalization;
using System.Text.Json;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

public sealed class WorkerVideoSource : ProcessVideoSource
{
    public override bool SupportsStreamRecovery => true;
    private readonly CaptureSource source;
    private readonly CaptureSettings settings;
    protected override string Kind => "Capture worker";
    protected override bool ForwardDiagnostics => true;
    protected override bool RequiresStartCommand => true;
    protected override CancellationToken SourceClosed => source.Closed;
    protected override void ValidateSource() => source.EnsureAvailable();

    public WorkerVideoSource(string executable, CaptureSource source, CaptureSettings settings, Action<string> diagnostic)
        : base(ExecutablePath(executable), settings.OutputSize(source.Item.Size.Width, source.Item.Size.Height).Width,
            settings.OutputSize(source.Item.Size.Width, source.Item.Size.Height).Height, settings.VideoCodec,
            $"Isolated hardware {settings.VideoCodec}", diagnostic, 1024L * 1024 * 1024)
    {
        settings.Validate();
        source.EnsureAvailable();
        EncoderCompatibility.ValidateCapture(0, source.ReadColor(diagnostic).Hdr, settings.VideoCodec);
        this.source = source;
        this.settings = settings;
    }

    private static string ExecutablePath(string appExecutable) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(appExecutable))!, "capture-worker", "RedrainCast.CaptureWorker.exe");

    protected override IEnumerable<string> Arguments() =>
    [
        "--capture-worker", CaptureWorkerHandshake.PreparationArgument, source.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
        source.IsWindow ? "window" : "display", JsonSerializer.Serialize(settings),
        Width.ToString(CultureInfo.InvariantCulture), Height.ToString(CultureInfo.InvariantCulture),
        source.WindowProcessId.ToString(CultureInfo.InvariantCulture), source.WindowThreadId.ToString(CultureInfo.InvariantCulture)
    ];
}
