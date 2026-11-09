using System.Net;
using Microsoft.UI.Xaml;
using RedrainCast.Core;

namespace RedrainCast.App;

public partial class App : Application
{
    private MainWindow? window;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        string[] supportedFlags = ["--self-test", "--sharing-settings-self-test", "--layout-self-test", "--package-startup-self-test", "--playback-stats-self-test",
            "--media-self-test", "--occlusion-self-test", "--color-self-test", "--encoder-self-test", "--hdr-self-test",
            "--peer-self-test", "--peer-timeline", "--peer-static-source", "--peer-duration-seconds",
            "--peer-sample-seconds", "--peer-silent-pcm", "--rotation-self-test",
            "--recovery-self-test", "--high-fps-self-test"];
        var unsupported = arguments.Skip(1).FirstOrDefault(argument =>
            argument.StartsWith("--", StringComparison.Ordinal) && !supportedFlags.Contains(argument, StringComparer.Ordinal));
        if (unsupported is not null)
        {
            MessageBoxW(0, $"Unsupported option: {unsupported}. No work was started.",
                "RedrainCast — unsupported option", 0x10);
            Environment.ExitCode = 2;
            Exit();
            return;
        }
        var testIndex = Array.IndexOf(arguments, "--self-test");
        var sharingSettingsIndex = Array.IndexOf(arguments, "--sharing-settings-self-test");
        var layoutIndex = Array.IndexOf(arguments, "--layout-self-test");
        var packageStartupIndex = Array.IndexOf(arguments, "--package-startup-self-test");
        var statisticsIndex = Array.IndexOf(arguments, "--playback-stats-self-test");
        var mediaIndex = Array.IndexOf(arguments, "--media-self-test");
        var occlusionIndex = Array.IndexOf(arguments, "--occlusion-self-test");
        var colorIndex = Array.IndexOf(arguments, "--color-self-test");
        var encoderIndex = Array.IndexOf(arguments, "--encoder-self-test");
        var hdrIndex = Array.IndexOf(arguments, "--hdr-self-test");
        var peerIndex = Array.IndexOf(arguments, "--peer-self-test");
        if (arguments.Contains("--peer-timeline", StringComparer.Ordinal) && peerIndex < 0)
            throw new ArgumentException("--peer-timeline is restricted to the isolated peer self-test.");
        if (arguments.Contains("--peer-static-source", StringComparer.Ordinal) && peerIndex < 0)
            throw new ArgumentException("--peer-static-source is restricted to the isolated peer self-test.");
        var rotationIndex = Array.IndexOf(arguments, "--rotation-self-test");
        var recoveryIndex = Array.IndexOf(arguments, "--recovery-self-test");
        var highFpsIndex = Array.IndexOf(arguments, "--high-fps-self-test");
        if (highFpsIndex >= 0 && highFpsIndex + 1 >= arguments.Length)
            throw new ArgumentException("--high-fps-self-test requires a report path.");
        if (hdrIndex >= 0)
        {
            if (hdrIndex + 1 >= arguments.Length || arguments[hdrIndex + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--hdr-self-test requires a report path.");
            Environment.ExitCode = await MainWindow.RunHdrSelfTestAsync(arguments[hdrIndex + 1]) ? 0 : 1;
            Exit();
            return;
        }
        if (colorIndex >= 0 || encoderIndex >= 0)
        {
            var index = colorIndex >= 0 ? colorIndex : encoderIndex;
            if (index + 1 >= arguments.Length) throw new ArgumentException($"{arguments[index]} requires a report path.");
            await MainWindow.RunColorSelfTestAsync(arguments[index + 1], includeDisplayColorProbe: colorIndex >= 0);
            Exit();
            return;
        }
        var uiIndex = packageStartupIndex >= 0 ? packageStartupIndex : statisticsIndex >= 0 ? statisticsIndex : layoutIndex >= 0 ? layoutIndex :
            sharingSettingsIndex >= 0 ? sharingSettingsIndex : testIndex;
        if (uiIndex >= 0 && uiIndex + 1 >= arguments.Length)
            throw new ArgumentException($"{arguments[uiIndex]} requires a report path.");
        var diagnosticIndex = uiIndex >= 0 ? uiIndex : highFpsIndex;
        using var uiTestJournal = diagnosticIndex >= 0 ? new DiagnosticLog(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(arguments[diagnosticIndex + 1]))!,
            Path.GetFileNameWithoutExtension(arguments[diagnosticIndex + 1]) + "-diagnostics")) : null;
        if (uiTestJournal is not null)
            UnhandledException += (_, failure) => uiTestJournal.Write($"Isolated UI unhandled exception: {failure.Exception}");
        uiTestJournal?.Write($"UI self-test phase: constructing window; version={AppVersion.Current().Label}, pid={Environment.ProcessId}");
        window = new MainWindow(uiIndex >= 0 || mediaIndex >= 0 || occlusionIndex >= 0 || peerIndex >= 0 || rotationIndex >= 0 || recoveryIndex >= 0 || highFpsIndex >= 0);
        if (highFpsIndex >= 0) window.Title = "RedrainCast — isolated synthetic high-FPS receiver test";
        if (layoutIndex >= 0) window.Title = "RedrainCast — isolated synthetic layout test";
        window.Activate();
        uiTestJournal?.Write("UI self-test phase: initializing receiver");
        await window.InitializeAsync();
        uiTestJournal?.Write("UI self-test phase: receiver initialization completed");
        if (packageStartupIndex >= 0)
        {
            try { await window.RunPackageStartupSelfTestAsync(arguments[packageStartupIndex + 1], uiTestJournal); }
            finally { window.Close(); }
        }
        else if (sharingSettingsIndex >= 0)
        {
            try { await window.RunSharingSettingsSelfTestAsync(arguments[sharingSettingsIndex + 1]); }
            finally { window.Close(); }
        }
        else if (statisticsIndex >= 0)
        {
            try { await window.RunPlaybackStatisticsSelfTestAsync(arguments[statisticsIndex + 1]); }
            finally { window.Close(); }
        }
        else if (highFpsIndex >= 0)
        {
            try { await window.RunHighFpsSelfTestAsync(arguments[highFpsIndex + 1]); }
            finally { window.Close(); }
        }
        else if (layoutIndex >= 0)
        {
            try { await window.RunLayoutSelfTestAsync(arguments[layoutIndex + 1], uiTestJournal); }
            finally { window.Close(); }
        }
        else if (recoveryIndex >= 0)
        {
            if (recoveryIndex + 1 >= arguments.Length) throw new ArgumentException("--recovery-self-test requires a report path.");
            await window.RunRecoverySelfTestAsync(arguments[recoveryIndex + 1]);
            window.Close();
        }
        else if (rotationIndex >= 0)
        {
            if (rotationIndex + 1 >= arguments.Length) throw new ArgumentException("--rotation-self-test requires a report path.");
            await window.RunRotationSelfTestAsync(arguments[rotationIndex + 1]);
            window.Close();
        }
        else if (peerIndex >= 0)
        {
            if (peerIndex + 3 >= arguments.Length || arguments[peerIndex + 3] is not ("sender-stop" or "receiver-stop"))
                throw new ArgumentException("--peer-self-test requires role, run directory and sender-stop/receiver-stop.");
            var address = peerIndex + 4 < arguments.Length && !arguments[peerIndex + 4].StartsWith("--", StringComparison.Ordinal)
                ? IPAddress.Parse(arguments[peerIndex + 4]) : IPAddress.Loopback;
            int PeerNumber(string name, int fallback)
            {
                var index = Array.IndexOf(arguments, name);
                if (index < 0) return fallback;
                if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out var value))
                    throw new ArgumentException($"{name} requires an integer.");
                return value;
            }
            var duration = PeerNumber("--peer-duration-seconds", 0);
            var sample = PeerNumber("--peer-sample-seconds", 5);
            if (duration != 0 && duration is < 60 or > 600 || sample != 5)
                throw new ArgumentException("Peer duration must be zero or 60..600 seconds; samples must be five seconds.");
            await window.RunPeerSelfTestAsync(arguments[peerIndex + 1], arguments[peerIndex + 2],
                arguments[peerIndex + 3] == "receiver-stop", address, duration,
                arguments.Contains("--peer-silent-pcm", StringComparer.Ordinal), sample,
                arguments.Contains("--peer-timeline", StringComparer.Ordinal),
                arguments.Contains("--peer-static-source", StringComparer.Ordinal));
            window.Close();
        }
        else if (mediaIndex >= 0 || occlusionIndex >= 0)
        {
            var index = mediaIndex >= 0 ? mediaIndex : occlusionIndex;
            if (index + 2 >= arguments.Length) throw new ArgumentException($"{arguments[index]} requires a synthetic fixture and a report path.");
            await window.RunMediaSelfTestAsync(arguments[index + 1], arguments[index + 2], verifyOcclusion: occlusionIndex >= 0);
            window.Close();
        }
        else if (testIndex >= 0)
        {
            if (testIndex + 1 >= arguments.Length) throw new ArgumentException("--self-test requires a report path.");
            await window.RunSelfTestAsync(arguments[testIndex + 1], uiTestJournal);
            uiTestJournal?.Write("UI self-test phase: closing window");
            window.Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
