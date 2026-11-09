using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace RedrainCast.Tests;

public sealed class ShippingBoundaryTests
{
    [Fact]
    public void StartupRetainsProductionWorkerAndReusableTestsButNoDisposableDispatch()
    {
        var source = Fixture("App.xaml.cs.txt");
        var entryPoint = Fixture("Program.cs.txt");
        Assert.Contains("\"--capture-worker\"", entryPoint);
        Assert.Contains("CaptureWorker.RunAsync", entryPoint);
        Assert.Contains("ComWrappersSupport.InitializeComWrappers();", entryPoint);
        Assert.Contains("Application.Start(", entryPoint);
        Assert.Contains("new RedrainCast.App.App();", entryPoint);
        Assert.DoesNotContain("\"--capture-worker\"", source);
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", Fixture("App.csproj"));
        Assert.DoesNotContain("CaptureWorker.csproj", Fixture("App.csproj"));
        var worker = Fixture("WorkerVideoSource.cs.txt");
        Assert.Contains("Path.GetFullPath(appExecutable)", worker);
        Assert.DoesNotContain("RedrainCast.CaptureWorker.exe", worker);
        foreach (var feature in new[] { "RecordingLaunch", "recordingIndex", "cadenceIndex", "ffmpegIndex" })
            Assert.DoesNotContain(feature, source);
        foreach (var flag in new[] { "--layout-self-test", "--playback-stats-self-test",
            "--recovery-self-test", "--rotation-self-test", "--high-fps-self-test", "--peer-self-test" })
            Assert.Contains($"\"{flag}\"", source);
        Assert.Contains("Unsupported option:", source);
        Assert.Contains("Environment.ExitCode = 2;", source);
        Assert.True(source.IndexOf("if (unsupported is not null)", StringComparison.Ordinal) <
            source.IndexOf("window = new MainWindow(", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildAndPublicationHaveNoDiagnosticFlightOrValidationOverride()
    {
        foreach (var file in new[] { "Directory.Build.props", "App.csproj", "Publish.ps1", "Package.ps1" })
        {
            Assert.DoesNotContain("EnableDiagnostics", Fixture(file));
            Assert.DoesNotContain("AllowDiagnostics", Fixture(file));
        }
    }

    [Fact]
    public void ReceiverHasNoArchiveTapAndKeepsRecoveryAndRetirement()
    {
        var source = Fixture("ReceiverServer.cs.txt");
        Assert.DoesNotContain("ReceivedRecording", source);
        Assert.DoesNotContain("recordingOptions", source);
        Assert.DoesNotContain("RecordRecovery", source);
        Assert.Contains("new RecoveryPlayback(", source);
        Assert.Contains("(packet, token) => SendAsync(packet, token, playbackForMedia as RecoveryPlayback)", source);
        Assert.Contains("playbackRetirement ?? RetirePlaybackAsync(playback)", source);
    }

    [Theory]
    [InlineData("recording-helper\\helper.dll")]
    [InlineData("avformat-62.dll")]
    [InlineData("ffmpeg.exe")]
    [InlineData("ffmpeg-gpl.exe")]
    [InlineData("RedrainCast.TestHarness.exe")]
    [InlineData("debug.pdb")]
    [InlineData("Start-Recording.cmd")]
    [InlineData("avcodec-62.dll")]
    [InlineData("ScrcpySend.Core.dll")]
    [InlineData("capture-worker\\ScrcpySend.Media.dll")]
    [InlineData("ScrcpyReceive.Core.deps.json")]
    [InlineData("onnxruntime.dll")]
    [InlineData("DirectML.dll")]
    [InlineData("Microsoft.Windows.AI.MachineLearning.dll")]
    [InlineData("Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll")]
    [InlineData("Microsoft.Windows.Search.dll")]
    [InlineData("Microsoft.Windows.Widgets.dll")]
    public void ShippingGuardRejectsContaminatedDirectory(string relativePath) =>
        CheckGuard(relativePath, accepted: false);

    [Fact]
    public void ShippingGuardAcceptsCleanDirectoryWithoutFlightMarker() =>
        CheckGuard("avcodec-61.dll", accepted: true);

    [Fact]
    public void AppReferencesWinUIInsteadOfTheUmbrellaWindowsAppSdk()
    {
        var project = System.Xml.Linq.XDocument.Parse(Fixture("App.csproj"));
        var packages = project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include")).ToArray();
        Assert.Contains("Microsoft.WindowsAppSDK.WinUI", packages);
        Assert.DoesNotContain("Microsoft.WindowsAppSDK", packages);
        Assert.Contains("Microsoft.WindowsAppSDK.Runtime", packages);
        Assert.Equal("false", project.Descendants("WindowsAppSDKSelfContained").Single().Value);
        Assert.Contains("--self-contained true", Fixture("Publish.ps1"));
        Assert.Contains("Microsoft.WindowsAppRuntime.2", Fixture("Package.ps1"));
        Assert.Contains("Microsoft.WindowsAppRuntime.Bootstrap.dll", Fixture("Publish.ps1"));
        Assert.Contains("Dependencies\\\\", Fixture("Package.ps1"));
    }

    [Fact]
    public void ShippingGuardRetainsGenuineLegacyLicenseNotice() =>
        CheckGuard("licenses\\ScrcpyReceive-Windows-LICENSE.txt", accepted: true);

    [Fact]
    public void ShippingGuardRejectsObsoleteProjectDependency() =>
        CheckGuard("RedrainCast.App.deps.json", accepted: false,
            contents: """{"libraries":{"ScrcpySend.Core/0.1.20":{}}}""");

    [Theory]
    [InlineData("RedrainCast.App.dll", "RecordingLaunch")]
    [InlineData("RedrainCast.Receive.Core.dll", "ReceivedRecording")]
    [InlineData("RedrainCast.Send.Media.dll", "VerifyEmptyOutputModel")]
    public void ShippingGuardRejectsRemovedCodeEvenWithoutAnExternalHelper(string assembly, string member) =>
        CheckGuard(assembly, accepted: false, contents: member + '\0');

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static void CheckGuard(string relativePath, bool accepted, string contents = "")
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "shipping-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, contents);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(AppContext.BaseDirectory, "fixtures", "Assert-ShippingBoundary.ps1"),
                "-PublishedDirectory", directory })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(30000), "Shipping guard timed out.");
            Assert.True((process.ExitCode == 0) == accepted, output.Result + errors.Result);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PublishedTypesExcludeRemovedFeaturesAndRetainReusableAcceptanceHarnesses()
    {
        var directory = Environment.GetEnvironmentVariable("REDRAINCAST_BOUNDARY_PUBLICATION");
        if (directory is null) return;
        CheckTypes("RedrainCast.App.dll",
            ["RecordingLaunch", "CaptureCadenceGate", "DiagnosticsLaunch"],
            ["HighFpsProbeControl", "RecoveryPcmSource"]);
        CheckTypes("RedrainCast.Receive.Core.dll", ["ReceivedRecording", "ReceivedRecordingOptions", "RecordingArchive"], []);
        CheckTypes("RedrainCast.Send.Core.dll", ["CaptureCadenceAnalysis"], ["EncoderSampleAccounting"]);
        CheckTypes("RedrainCast.Send.Media.dll", ["FfmpegVideoSource"], ["WorkerVideoSource", "ProcessVideoSource"]);
        CheckTypes("RedrainCast.Receive.Media.dll", ["OutputThreadProbe"], ["VideoPlayback", "AudioPlayback"]);
        CheckTypes("RedrainCast.Protocol.dll", ["ThreadTiming", "IThreadTimingSource"], ["MediaTiming"]);

        void CheckTypes(string file, string[] absent, string[] retained)
        {
            using var stream = File.OpenRead(Path.Combine(directory, file));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var definitions = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle)).ToArray();
            var names = definitions.Select(type => metadata.GetString(type.Name)).ToArray();
            foreach (var name in absent) Assert.DoesNotContain(name, names);
            foreach (var name in retained) Assert.Contains(name, names);
            var methods = definitions.SelectMany(type => type.GetMethods()).Select(handle =>
                metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToArray();
            foreach (var name in new[] { "RunRecordingSelfTestAsync", "RunSenderCadenceSelfTestAsync",
                "RunFfmpegSelfTestAsync", "CreateCadenceSource", "VerifyEmptyOutputModel" })
                Assert.DoesNotContain(name, methods);
            if (file != "RedrainCast.App.dll") return;
            foreach (var name in new[] { "RunLayoutSelfTestAsync", "RunPlaybackStatisticsSelfTestAsync",
                "RunRecoverySelfTestAsync", "RunRotationSelfTestAsync", "RunHighFpsSelfTestAsync", "RunPeerSelfTestAsync" })
                Assert.Contains(name, methods);
        }
    }
}
