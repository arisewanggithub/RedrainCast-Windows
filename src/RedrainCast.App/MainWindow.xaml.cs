using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RedrainCast.Core;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using RedrainCast.Send.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Capture;
using ReceiverDiscovery = RedrainCast.Send.Core.ReceiverDiscovery;
using LocalDiscovery = RedrainCast.Receive.Core.Discovery;

namespace RedrainCast.App;

public sealed partial class MainWindow : Window
{
    private sealed record AddressRow(string Adapter, string DisplayAddress, string CanonicalAddress);

    private readonly SessionRoles roles = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly VideoHost video;
    private readonly WindowLayoutController layout;
    private readonly DispatcherTimer statisticsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly PlaybackRateSampler statisticsRates = new();
    private readonly bool isolated;
    private readonly List<string> diagnostics = [];
    private DiagnosticLog? diagnosticLog;
    private string? diagnosticLogPath;
    private string? diagnosticLogError;
    private readonly HashSet<IPAddress> localAddresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
    private ReceiverUiState receiverState = new();
    private ReceiverServer? receiver;
    private Task? startup;
    private Task discovery = Task.CompletedTask;
    private Task? sending;
    private CancellationTokenSource? sendStop;
    private OutgoingCast? outgoing;
    private IReadOnlyList<ReceiverEndpoint> discovered = [];
    private CaptureSource? source;
    private CancellationTokenRegistration sourceClosedNotice;
    private CaptureSettings settings = new();
    private ContentDialog? currentDialog;
    private readonly AppStrings strings = AppStrings.Current;
    private SystemLanguageNotifications? languageNotifications;
    private XamlRoot? observedRoot;
    private Func<string>? problemTitle;
    private Func<string>? problemMessage;
    private string? copiedEndpoint;
    private string L(string key, params object?[] values) => strings.Get(key, values);
    private TextBlock InputHeader(string key) => new()
    {
        Text = L(key), FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        TextWrapping = TextWrapping.Wrap
    };
    private int receiverGeneration;
    private bool closing;
    private bool picking;
    private TaskCompletionSource? pickerFinished;
    private CancellationTokenSource? pickerCancellation;
    private bool receiverFaulted;
    private bool discoveryFailed;
    private bool showingVideo;
    private AppWindowPresenter? presenterBeforePlayback;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedrainCast", "settings.json");
    private static string DiagnosticDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedrainCast", "Logs");

    public MainWindow(bool isolated = false, Func<IReadOnlyList<string>>? preferredLanguages = null)
    {
        this.isolated = isolated;
        InitializeComponent();
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RedrainCast.ico");
        if (!File.Exists(iconPath))
            throw new FileNotFoundException("The required RedrainCast window icon is missing.", iconPath);
        AppWindow.SetIcon(iconPath);
        Title = "RedrainCast";
        DeviceName.Text = Environment.MachineName;
        VersionText.Text = L("Version", AppVersion.Current().Label);
        if (!isolated)
        {
            try
            {
                diagnosticLog = new DiagnosticLog(DiagnosticDirectory);
                diagnosticLogPath = diagnosticLog.FilePath;
                Diagnostic($"App started: version={AppVersion.Current().Label}, pid={Environment.ProcessId}, " +
                    $"architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, OS={Environment.OSVersion}; log={diagnosticLogPath}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                diagnosticLogError = error.Message;
                Report(new IOException($"Diagnostic logging could not start in {DiagnosticDirectory}. {error.Message}", error));
            }
        }
        video = new(VideoSurface, detail => OnReceiverNotice(new("Video", detail), receiverGeneration));
        try { video.SetDisplayWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)); }
        catch (Exception error) { Report(error); }
        AppWindow.Changed += DisplayChanged;
        statisticsTimer.Tick += StatisticsTick;
        video.StatisticsAvailabilityChanged += UpdateStatisticsVisibility;
        WindowRoot.Loaded += ObserveRoot;
        layout = new(this, HomePage, Report);
        strings.Changed += RefreshLanguage;
        strings.Start(DispatcherQueue, error => Diagnostic($"Localization: {error}"), preferredLanguages);
        try { languageNotifications = new(this, strings.Reconcile); }
        catch (Exception error) { Report(error); }
        WindowRoot.ActualThemeChanged += (_, _) => UpdateTitleBar();
        UpdateTitleBar();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            statisticsTimer.Stop();
            statisticsTimer.Tick -= StatisticsTick;
            video.StatisticsAvailabilityChanged -= UpdateStatisticsVisibility;
            AppWindow.Changed -= DisplayChanged;
            ClearStatistics();
            video.Dispose();
            layout.Dispose();
            WindowRoot.Loaded -= ObserveRoot;
            if (observedRoot is not null) observedRoot.Changed -= RootPresentationChanged;
            languageNotifications?.Dispose();
            strings.Changed -= RefreshLanguage;
            strings.Stop();
            sourceClosedNotice.Dispose();
            source?.Dispose();
            var log = diagnosticLog;
            diagnosticLog = null;
            try { log?.Dispose(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                diagnosticLogError = error.Message;
                Report(new IOException($"Diagnostic log could not close. {error.Message}", error));
            }
            lifetime.Dispose();
        };
    }

    internal Task InitializeAsync() => startup ??= InitializeCoreAsync();

    private void DisplayChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (closing) return;
        try { video.RefreshDisplay(); }
        catch (Exception error) { Report(error); }
    }

    private void ObserveRoot(object sender, RoutedEventArgs args)
    {
        if (observedRoot is not null) observedRoot.Changed -= RootPresentationChanged;
        observedRoot = WindowRoot.XamlRoot;
        observedRoot.Changed += RootPresentationChanged;
        strings.Reconcile();
    }

    private void RootPresentationChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateStatisticsVisibility();
        if (sender.IsHostVisible) strings.Reconcile();
    }

    private async Task InitializeCoreAsync()
    {
        if (!isolated)
        {
            LoadSettings();
            try { SetSource(WindowsVideoSource.SelectDefaultDisplay()); }
            catch (Exception error) { ReportAction(error, "ChooseSourceFirst"); }
        }
        await StartReceiverAsync();
        if (!isolated) discovery = DiscoverAsync();
    }

    private void Diagnostic(string detail)
    {
        void Apply()
        {
            var line = $"{DateTimeOffset.Now:O} {detail}";
            diagnostics.Add(line);
            while (diagnostics.Count > 100) diagnostics.RemoveAt(0);
            try { diagnosticLog?.Write(line); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var failed = diagnosticLog;
                diagnosticLog = null;
                try { failed?.Dispose(); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    error = new AggregateException("Diagnostic write and cleanup failed.", error, cleanupError);
                }
                diagnosticLogError = error.Message;
                Report(new IOException($"Diagnostic logging stopped. {error.Message}", error));
            }
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void Report(Exception error) => ReportAction(error, "RetryAction");

    private void ReportAction(Exception error, string messageKey, params object?[] values)
    {
        Diagnostic(error.ToString());
        problemTitle = () => L("ActionFailed");
        var key = error is UiActionException action ? action.Key : messageKey;
        problemMessage = () => L(key, values) + "\n" + L("TechnicalDetails", strings.LeftToRight(error.Message));
        RefreshProblem();
        Problem.Severity = InfoBarSeverity.Error;
        Problem.IsOpen = true;
    }

    private void RefreshProblem()
    {
        if (problemTitle is not null) Problem.Title = problemTitle();
        if (problemMessage is not null) Problem.Message = problemMessage();
    }

    private void RefreshSourceLabel()
    {
        SourceLabel.Text = source is null ? L("ChooseSource") :
            source.Closed.IsCancellationRequested ? L("SourceClosed") :
            L("SourceDimensions", strings.LeftToRight(source.Item.DisplayName),
                strings.LeftToRight(source.Item.Size.Width.ToString(CultureInfo.InvariantCulture)),
                strings.LeftToRight(source.Item.Size.Height.ToString(CultureInfo.InvariantCulture)));
    }

    private void RefreshLanguage()
    {
        HomePage.FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        PlaybackStatsPanel.FlowDirection = HomePage.FlowDirection;
        if (currentDialog is not null) currentDialog.FlowDirection = HomePage.FlowDirection;
        VersionText.Text = L("Version", AppVersion.Current().Label);
        RefreshSourceLabel();
        if (copiedEndpoint is not null) AddressFeedback.Text = L("CopiedAddress", strings.LeftToRight(copiedEndpoint));
        RefreshProblem();
        RefreshStatisticsLanguage();
        Render();
    }

    private void OnReceiverNotice(ReceiverNotice notice, int generation)
    {
        void Apply()
        {
            if (closing || generation != receiverGeneration) return;
            Diagnostic($"{notice.State}: {notice.Detail}");
            receiverState = receiverState.Apply(notice);
            if (notice.State == "Listening" && notice.Detail.StartsWith("Cast resources released", StringComparison.Ordinal))
                receiverState = receiverState with { Connecting = false, ProblemCode = receiverFaulted ? receiverState.ProblemCode : null };
            if (notice.ListeningEndpoints is not null)
            {
                AddressFeedback.Text = "";
                copiedEndpoint = null;
                localAddresses.Clear();
                localAddresses.UnionWith([IPAddress.Loopback, IPAddress.IPv6Loopback]);
                foreach (var endpoint in notice.ListeningEndpoints) localAddresses.Add(EndpointText.Parse(endpoint).Address);
            }
            if (notice.State is "Start failed" or "Listener failed" or "Cleanup failed") receiverFaulted = true;
            if (receiverState.ProblemCode is not null && notice.State == receiverState.ProblemCode)
            {
                var problemState = receiverState;
                problemTitle = () => L(ReceiverPresentation.TitleKey(problemState));
                problemMessage = () => L(ReceiverPresentation.ProblemKey(problemState)) + "\n" + L("TechnicalDetails", strings.LeftToRight(notice.Detail));
                RefreshProblem();
                Problem.Severity = receiverState.IsWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
                Problem.IsOpen = true;
            }
            Render();
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private IDisposable? AcquireReceiving()
    {
        var lease = roles.TryAcquire(CastRole.Receiving);
        if (lease is not null) DispatcherQueue.TryEnqueue(Render);
        return lease;
    }

    private Task StartReceiverAsync(int port = 0, IPAddress? bindAddress = null)
    {
        if (closing || receiver is not null) return Task.CompletedTask;
        receiverState = new();
        AddressFeedback.Text = "";
        copiedEndpoint = null;
        receiverFaulted = false;
        var generation = ++receiverGeneration;
        ReceiverServer? candidate = null;
        var address = bindAddress ?? (isolated ? IPAddress.Loopback : IPAddress.Any);
        try
        {
            candidate = new(Environment.MachineName, video, notice => OnReceiverNotice(notice, generation),
                port, address, AcquireReceiving, instanceId);
            candidate.Start(advertise: !isolated);
            receiver = candidate;
            if (isolated)
                OnReceiverNotice(new("Listening", IPAddress.IsLoopback(address) ? "Isolated loopback listener." : "Owned LAN test listener.",
                    [LocalDiscovery.DisplayEndpoint(address, candidate.Port)]), generation);
        }
        catch (Exception error) when (error is SocketException or ArgumentException or NetworkInformationException)
        {
            return HandleStartFailureAsync(candidate, error, port);
        }
        Render();
        return Task.CompletedTask;
    }

    private async Task HandleStartFailureAsync(ReceiverServer? candidate, Exception error, int port)
    {
        var generation = ++receiverGeneration;
        try
        {
            if (candidate is not null) await candidate.DisposeAsync();
        }
        catch (Exception cleanupError)
        {
            OnReceiverNotice(new("Cleanup failed", $"Listener startup failed: {error.Message}. Cleanup also failed: {cleanupError}"), generation);
            return;
        }
        var guidance = port == 0 ? "Windows could not allocate a TCP listener. Check network permissions, then reopen RedrainCast." :
            $"Close other receivers using TCP {port}, then reopen RedrainCast.";
        OnReceiverNotice(new("Start failed", $"{error.Message} {guidance} Sending remains available."), generation);
    }

    private async Task StopReceiverAsync()
    {
        ++receiverGeneration;
        var previous = receiver;
        receiver = null;
        if (previous is not null) await previous.DisposeAsync();
        receiverState = new() { Stopped = true };
        Render();
    }

    private async Task DiscoverAsync()
    {
        try
        {
            await new ReceiverDiscovery().RunAsync(items => DispatcherQueue.TryEnqueue(() =>
            {
                discovered = items;
                RenderDevices();
            }), detail => DispatcherQueue.TryEnqueue(() =>
            {
                Diagnostic(detail);
                problemTitle = () => L("InvalidAdvertisement");
                problemMessage = () => L("TechnicalDetails", strings.LeftToRight(detail));
                RefreshProblem();
                Problem.Severity = InfoBarSeverity.Warning;
                Problem.IsOpen = true;
            }), lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            discoveryFailed = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                ReportAction(error, "DiscoveryUnavailable");
                RenderDevices();
            });
        }
    }

    private bool IsSelf(ReceiverEndpoint endpoint) =>
        endpoint.InstanceId == instanceId ||
        receiver is not null && localAddresses.Contains(endpoint.Address) && endpoint.Port == receiver.Port;

    private bool CanSend => !closing && !picking && receiverState.ProblemCode != "Cleanup failed" &&
        roles.Role == CastRole.Idle && source is not null && !source.Closed.IsCancellationRequested;

    private void RenderDevices()
    {
        var nearby = discovered.Where(endpoint => !IsSelf(endpoint)).ToArray();
        var presented = outgoing is null ? null : outgoing with
        {
            Notice = outgoing.NoticeKey is not null ? L(outgoing.NoticeKey) : string.IsNullOrEmpty(outgoing.Notice) ? "" :
                L("OperationalNotice") + "\n" + L("TechnicalDetails", strings.LeftToRight(outgoing.Notice))
        };
        var rows = ReceiverRows.Create(nearby, presented, CanSend, key => L(key),
            (action, name) => L("ActionHelp", action, name));
        if (ReceiverList.ItemsSource is not ReceiverRow[] previous || !previous.SequenceEqual(rows))
            ReceiverList.ItemsSource = rows;
        DiscoveryStatus.Text = discoveryFailed
            ? L(outgoing is null ? "DiscoveryUnavailable" : "DiscoveryUnavailableActive")
            : nearby.Length == 0
                ? L(outgoing is null ? "NoReceivers" : "NoReceiversActive")
                : L("ReceiverCount", nearby.Length);
    }

    public static Visibility OptionalTextVisibility(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void Render()
    {
        var role = roles.Role;
        var paused = role == CastRole.Sending;
        ReadinessTitle.Text = L(paused ? "Paused" : role == CastRole.Receiving ? "ConnectingBanner" :
            receiverState.Phase == ReceiverUiPhase.Ready ? "Ready" : "ReceiverPhase" + receiverState.Phase);
        ReadinessDetail.Text = L(paused ? "PauseReceiveHint" :
            role == CastRole.Receiving ? "PreparingVideo" :
            receiverState.Phase == ReceiverUiPhase.Ready ? "Listening" :
            receiverState.Phase == ReceiverUiPhase.NoNetwork ? "ConnectNetwork" :
            receiverState.Phase == ReceiverUiPhase.Starting ? "OpeningListener" : ReceiverPresentation.ProblemKey(receiverState));
        ReadyTint.Visibility = !paused && role == CastRole.Idle && receiverState.Phase == ReceiverUiPhase.Ready
            ? Visibility.Visible : Visibility.Collapsed;
        var showAddresses = !paused && receiverState.ListenerOpen && !receiverFaulted;
        AddressList.Visibility = showAddresses ? Visibility.Visible : Visibility.Collapsed;
        AddressHint.Text = L(paused ? "AddressUnavailableSending" : "AddressHint");
        AddressList.ItemsSource = showAddresses ? receiverState.Addresses.Select(endpoint =>
        {
            var parsed = EndpointText.Parse(endpoint);
            return new AddressRow(receiverState.AdapterLabels.TryGetValue(endpoint, out var label) ? label : L("LocalNetwork"),
                EndpointText.Format(parsed.Address, parsed.Port), EndpointText.Format(parsed.Address, parsed.Port, false));
        }).ToArray() : Array.Empty<AddressRow>();
        SettingsButton.IsEnabled = ChooseSource.IsEnabled = !closing && !picking && role == CastRole.Idle;
        ManualConnect.IsEnabled = CanSend;
        RenderDevices();
        var playing = video.HasDecodedVideo || showingVideo && role == CastRole.Receiving;
        UpdateStatisticsVisibility();
        if (role == CastRole.Receiving)
        {
            currentDialog?.Hide();
            pickerCancellation?.Cancel();
        }
        if (showingVideo == playing) return;
        showingVideo = playing;
        HomePage.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PlayerInput.IsTabStop = playing;
        if (playing)
        {
            Diagnostic($"Receiver video surface exposed: decodedFrames={video.DecodedFrames}, presentedFrames={video.PresentedFrames}");
            presenterBeforePlayback = AppWindow.Presenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            Activate();
            PlayerInput.Focus(FocusState.Programmatic);
        }
        else
        {
            if (presenterBeforePlayback is not null)
                AppWindow.SetPresenter(presenterBeforePlayback);
            presenterBeforePlayback = null;
            ChooseSource.Focus(FocusState.Programmatic);
        }
    }

    private void WindowRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        FitStatisticsPanel();
        var stacked = args.NewSize.Width < 900;
        Cards.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(ReceiveCard, stacked ? 0 : 1);
        Grid.SetRow(ReceiveCard, stacked ? 1 : 0);
    }

    private void UpdateTitleBar() => AppWindow.TitleBar.PreferredTheme =
        new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? TitleBarTheme.UseDefaultAppMode :
        WindowRoot.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    private void SetSource(CaptureSource selected)
    {
        selected.EnsureAvailable();
        sourceClosedNotice.Dispose();
        var previous = source;
        source = selected;
        if (!ReferenceEquals(previous, selected)) previous?.Dispose();
        sourceClosedNotice = selected.Closed.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(source, selected)) return;
            RefreshSourceLabel();
            RenderDevices();
            ManualConnect.IsEnabled = CanSend;
        }));
        RefreshSourceLabel();
        RenderDevices();
        ManualConnect.IsEnabled = CanSend;
    }

    private async void ChooseSource_Click(object sender, RoutedEventArgs args)
    {
        if (closing || picking || roles.Role != CastRole.Idle) return;
        picking = true;
        pickerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pickerCancellation = cancelled;
        Render();
        try
        {
            var owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
            CaptureSource? selected;
            if (CaptureSource.SupportsNativePicker(owner))
                selected = await NativeSourcePicker.PickAsync(owner, cancelled.Token);
            else
            {
                Diagnostic("Native capture-item handle interop is unavailable; using the compatibility visual chooser.");
                var targets = CaptureSource.Enumerate(owner);
                await using var picker = new SourcePicker(targets, source,
                    () => SourcePickerPolicy.CanSelect(cancelled.IsCancellationRequested, closing, roles.Role),
                    Diagnostic, WindowRoot.ActualHeight, WindowRoot.ActualWidth);
                await ShowDialogAsync(picker.Dialog);
                selected = picker.Selected;
            }
            if (selected is not null)
            {
                try
                {
                    if (SourcePickerPolicy.CanSelect(cancelled.IsCancellationRequested, closing, roles.Role))
                        SetSource(selected);
                }
                finally { if (!ReferenceEquals(source, selected)) selected.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        catch (Exception error) { ReportAction(error, "ChooseSourceFailed"); }
        finally
        {
            picking = false;
            pickerCancellation = null;
            pickerFinished.TrySetResult();
            if (!closing) Render();
            if (!closing && roles.Role == CastRole.Idle) ChooseSource.Focus(FocusState.Programmatic);
        }
    }

    private async void DeviceAction_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (sender is not Button { Tag: ReceiverRow row } || !row.CanAct) return;
            if (row.IsActive)
            {
                if (outgoing?.Target.Key == row.Endpoint.Key) StopSending();
            }
            else await StartSendingAsync(row.Endpoint);
        }
        catch (Exception error) { Report(error); }
    }

    private async Task StartSendingAsync(ReceiverEndpoint endpoint)
    {
        if (sending is not null || !CanSend) return;
        if (endpoint.Busy || IsSelf(endpoint))
        {
            ReportAction(new ArgumentException("Destination is busy or is this receiver."), endpoint.Busy ? "ReceiverBusy" : "ChooseOtherDevice");
            return;
        }
        sending = SendAsync(endpoint);
        try { await sending; }
        finally { sending = null; }
    }

    private async Task SendAsync(ReceiverEndpoint endpoint)
    {
        var lease = roles.TryAcquire(CastRole.Sending);
        if (lease is null)
        {
            ReportAction(new InvalidOperationException("An incoming cast has already started."), "IncomingAlreadyStarted");
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        sendStop = stop;
        outgoing = new(endpoint);
        Problem.IsOpen = false;
        Render();
        ReceiverList.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        try
        {
            await StopReceiverAsync();
            var selected = source ?? throw new InvalidOperationException("Choose a display or window first.");
            var selectedSettings = settings;
            Diagnostic($"Sender starting: target={endpoint.Key}; source={(selected.IsWindow ? "window" : "display")}, " +
                $"captureSize={selected.Item.Size.Width}x{selected.Item.Size.Height}; settings={JsonSerializer.Serialize(selectedSettings, CaptureSettingsJsonContext.Default.CaptureSettings)}");
            await Task.Run(async () =>
            {
                await using var outgoingVideo = new WorkerVideoSource(Environment.ProcessPath ??
                    throw new IOException("Cannot locate the capture worker executable."), selected, selectedSettings, Diagnostic);
                if (peerSelfTestAudioFactory is not null && !isolated)
                    throw new InvalidOperationException("Synthetic peer audio is restricted to isolated self-tests.");
                await using IAudioSource? audio = selectedSettings.EnableAudio
                    ? peerSelfTestAudioFactory?.Invoke() ?? new WindowsAudioSource() : null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(sendStop, stop) || outgoing is null) return;
                    var codec = selectedSettings.VideoCodec switch
                    {
                        "h264" => "H.264", "h265" => "H.265 (HEVC)", "av1" => "AV1",
                        _ => throw new NotSupportedException("Unsupported video codec.")
                    };
                    outgoing = outgoing with
                    {
                        Parameters = $"{codec} - {outgoingVideo.Width.ToString(CultureInfo.InvariantCulture)} x {outgoingVideo.Height.ToString(CultureInfo.InvariantCulture)}",
                        Encoder = outgoingVideo.EncoderName
                    };
                    RenderDevices();
                });
                stop.Token.ThrowIfCancellationRequested();
                try
                {
                    await new SenderSession().RunAsync(endpoint, selectedSettings, outgoingVideo,
                    text =>
                    {
                        Diagnostic($"Sender state: {text}");
                        if (text.StartsWith("Recovery transport timing:", StringComparison.Ordinal) ||
                            text.StartsWith("Recovery pressure timing:", StringComparison.Ordinal)) return;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!closing && ReferenceEquals(sendStop, stop) && outgoing is not null)
                            {
                                var parameters = outgoing.Parameters;
                                if (outgoingVideo.ColorFormat.Hdr10 && !parameters.EndsWith(" - HDR10", StringComparison.Ordinal))
                                    parameters += " - HDR10";
                                outgoing = outgoing.ApplyStatus(text) with
                                {
                                    Encoder = outgoingVideo.EncoderName,
                                    Parameters = parameters
                                };
                                RenderDevices();
                            }
                        });
                    }, stop.Token, audio);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new IOException($"{error.Message}\nSender video: {outgoingVideo.Statistics}\n" +
                        $"Diagnostic log: {diagnosticLogPath ?? "unavailable in this isolated test"}", error);
                }
                finally { Diagnostic($"Sender session ended: {outgoingVideo.Statistics}"); }
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            ReportAction(error, error is UncappedFpsUnavailableException ||
                error.InnerException is UncappedFpsUnavailableException ? "UncappedFpsUnavailable" : "SendingFailed");
        }
        finally
        {
            sendStop = null;
            outgoing = null;
            lease.Dispose();
            if (!closing) await StartReceiverAsync();
        }
    }

    private void StopSending()
    {
        if (outgoing is null) return;
        outgoing = outgoing.ApplyStatus("Stopping");
        RenderDevices();
        sendStop?.Cancel();
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (currentDialog is not null || closing || roles.Role != CastRole.Idle) return ContentDialogResult.None;
        currentDialog = dialog;
        dialog.XamlRoot = WindowRoot.XamlRoot;
        dialog.FlowDirection = HomePage.FlowDirection;
        try { return await dialog.ShowAsync(); }
        finally { currentDialog = null; }
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { FlowDirection = FlowDirection.LeftToRight };
        var errorLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(input);
        content.Children.Add(errorLabel);
        ReceiverEndpoint? target = null;
        var dialog = new ContentDialog
        {
            Content = content, DefaultButton = ContentDialogButton.Primary
        };
        string? validationKey = null;
        using var labels = strings.Bind(() =>
        {
            input.Header = InputHeader("ReceiverAddress");
            input.PlaceholderText = L("AddressExample");
            dialog.Title = L("ConnectReceiver");
            dialog.PrimaryButtonText = L("Send");
            dialog.CloseButtonText = L("Cancel");
            errorLabel.Text = validationKey is null ? "" : L(validationKey);
        });
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try
            {
                validationKey = "AddressValidation";
                target = EndpointText.Parse(input.Text);
                if (IsSelf(target))
                {
                    validationKey = "OwnAddress";
                    throw new ArgumentException("Self-receiver endpoint rejected.");
                }
            }
            catch (ArgumentException) { errorLabel.Text = L(validationKey!); click.Cancel = true; }
        };
        try
        {
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && target is not null)
                await StartSendingAsync(target);
        }
        catch (Exception error) { Report(error); }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string endpoint }) return;
        try
        {
            var data = new DataPackage();
            data.SetText(endpoint);
            Clipboard.SetContent(data);
            var parsed = EndpointText.Parse(endpoint);
            copiedEndpoint = EndpointText.Format(parsed.Address, parsed.Port);
            AddressFeedback.Text = L("CopiedAddress", strings.LeftToRight(copiedEndpoint));
        }
        catch (Exception error) { ReportAction(error, "CopyFailed"); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs args)
    {
        try { await ShowSharingSettingsAsync(); }
        catch (Exception error) { ReportAction(error, "SettingsUnavailable"); }
    }

    private async Task<CodecSelection> ReadCodecSelectionAsync()
    {
        var selectedSource = source;
        CodecSelection selection;
        try
        {
            selection = await Task.Run(() => new CodecSelection(
                WindowsVideoSource.ReadCaptureAdapterVendorId(), selectedSource?.ReadColor(Diagnostic).Hdr));
        }
        catch (Exception error)
        {
            throw new IOException($"Could not read capture adapter/display metadata for codec choices. No compatibility assumption was made. {error.Message}", error);
        }
        if (!ReferenceEquals(selectedSource, source))
            throw new UiActionException("SourceChanged");
        return selection;
    }

    private async Task ShowSharingSettingsAsync()
    {
        var codecSelection = await ReadCodecSelectionAsync();
        await ShowSharingSettingsDialogAsync(codecSelection, ReadCodecSelectionAsync);
    }

    private async Task ShowSharingSettingsDialogAsync(CodecSelection codecSelection, Func<Task<CodecSelection>> refreshSelection)
    {
        var editCulture = strings.Culture;
        var size = new TextBox { Text = settings.MaxSize.ToString(editCulture), FlowDirection = FlowDirection.LeftToRight };
        var fps = new ComboBox
        {
            ItemsSource = CaptureSettings.FpsPresets.Append(settings.MaxFps).Distinct().Order()
                .Select(cap => new ComboBoxItem { Tag = cap }).ToArray(),
            SelectedValuePath = nameof(ComboBoxItem.Tag), SelectedValue = settings.MaxFps,
            HorizontalAlignment = HorizontalAlignment.Stretch, FlowDirection = FlowDirection.LeftToRight
        };
        var bitrate = new TextBox { Text = (settings.VideoBitrate / 1_000_000m).ToString(editCulture), FlowDirection = FlowDirection.LeftToRight };
        var codec = new ComboBox { ItemsSource = codecSelection.Choices, FlowDirection = FlowDirection.LeftToRight,
            DisplayMemberPath = nameof(CodecChoice.Label), SelectedItem = codecSelection.Find(settings.VideoCodec),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var orientation = new ComboBox { ItemsSource = new[] { L("Auto"), L("Landscape"), L("Portrait") },
            SelectedIndex = settings.ReceiverOrientation switch { "landscape" => 1, "portrait" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var audio = new CheckBox { IsChecked = settings.EnableAudio };
        var playbackStats = new CheckBox { IsChecked = settings.ShowPlaybackStatistics };
        string? settingsErrorKey = string.IsNullOrEmpty(codecSelection.SavedSelectionMessage(settings.VideoCodec)) ? null : "SavedCodecUnavailable";
        string? settingsErrorDetail = null;
        var errorLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        foreach (var element in new FrameworkElement[] { size, fps, bitrate, codec, orientation, audio, playbackStats, errorLabel })
            content.Children.Add(element);
        var dialog = new ContentDialog
        {
            Content = new ScrollViewer { Content = content }, DefaultButton = ContentDialogButton.Primary
        };
        void RefreshSettingsLabels()
        {
            size.Header = InputHeader("MaximumDimension");
            fps.Header = InputHeader("MaximumFPS");
            foreach (var item in fps.Items.Cast<ComboBoxItem>())
                item.Content = item.Tag is 0 ? L("UncappedDefault") :
                    $"{((int)item.Tag).ToString(strings.Culture)} {L("UnitFrames")}";
            bitrate.Header = InputHeader("VideoBitrate");
            codec.Header = InputHeader("VideoCodec");
            orientation.Header = InputHeader("ReceiverOrientation");
            var selectedIndex = orientation.SelectedIndex;
            orientation.ItemsSource = new[] { L("Auto"), L("Landscape"), L("Portrait") };
            orientation.SelectedIndex = selectedIndex;
            audio.Content = L("SendAudio");
            playbackStats.Content = L("ShowStatistics");
            dialog.Title = L("SharingSettings");
            dialog.PrimaryButtonText = L("Save");
            dialog.CloseButtonText = L("Cancel");
            errorLabel.Text = settingsErrorKey is null ? "" :
                L(settingsErrorKey, 0.1m.ToString(editCulture)) +
                (settingsErrorDetail is null ? "" : "\n" + L("TechnicalDetails", strings.LeftToRight(settingsErrorDetail)));
        }
        using var labels = strings.Bind(RefreshSettingsLabels);
        var cancelled = false;
        dialog.CloseButtonClick += (_, _) => cancelled = true;
        dialog.Closing += (_, closingArgs) =>
        {
            if (closingArgs.Result != ContentDialogResult.Primary) cancelled = true;
        };
        dialog.PrimaryButtonClick += async (_, click) =>
        {
            var deferral = click.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                var requested = codec.SelectedItem as CodecChoice;
                codecSelection = await refreshSelection();
                if (cancelled)
                {
                    click.Cancel = true;
                    return;
                }
                codec.ItemsSource = codecSelection.Choices;
                codec.SelectedItem = requested is null ? null : codecSelection.Find(requested.Codec);
                var videoCodec = codecSelection.ValidateSelection(requested);
                var receiverOrientation = orientation.SelectedIndex switch
                {
                    0 => "auto", 1 => "landscape", 2 => "portrait",
                    _ => throw new ArgumentException("Choose a receiver orientation before saving.")
                };
                var input = CaptureSettings.ParseInput(size.Text, bitrate.Text,
                    fps.SelectedValue is int cap ? cap : null,
                    receiverOrientation, editCulture, videoCodec, audio.IsChecked == true, playbackStats.IsChecked == true);
                var next = input.Settings ?? throw new ArgumentException(string.Join(" ",
                    new[] { input.DimensionError, input.BitrateError, input.FpsError }.OfType<string>()));
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(next, CaptureSettingsJsonContext.Default.CaptureSettings));
                File.Move(SettingsPath + ".tmp", SettingsPath, true);
                settings = next;
            }
            catch (Exception error)
            {
                settingsErrorKey = error is ArgumentException ? "SettingsValidation" : "RetryAction";
                settingsErrorDetail = error is ArgumentException ? null : error.Message;
                RefreshSettingsLabels();
                ReportAction(error, settingsErrorKey!, 0.1m.ToString(editCulture));
                click.Cancel = true;
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };
        await ShowDialogAsync(dialog);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            settings = CaptureSettings.ReadSaved(File.ReadAllText(SettingsPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            ReportAction(error, "SavedSettingsFailed");
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var content = new StackPanel { Spacing = 12 };
            var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
            content.Children.Add(body);
            var support = new HyperlinkButton
            {
                NavigateUri = new Uri("https://redraincast.redrain.ccwu.cc/support/")
            };
            content.Children.Add(support);
            var privacy = new HyperlinkButton
            {
                NavigateUri = new Uri("https://redraincast.redrain.ccwu.cc/privacy/")
            };
            content.Children.Add(privacy);
            var dialog = new ContentDialog
            {
                Content = new ScrollViewer { Content = content }
            };
            using var labels = strings.Bind(() =>
            {
                body.Text = L("HelpBody");
                support.Content = L("HelpSupport");
                privacy.Content = L("PrivacyPolicy");
                dialog.Title = L("TrustedNetworkTitle");
                dialog.CloseButtonText = L("Close");
            });
            await ShowDialogAsync(dialog);
        }
        catch (Exception error) { Report(error); }
    }

    private void Root_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        bool Down(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var editing = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is TextBox or PasswordBox or RichEditBox;
        var modified = Down(Windows.System.VirtualKey.Control) || Down(Windows.System.VirtualKey.Menu) ||
            Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows);
        var key = args.Key switch
        {
            Windows.System.VirtualKey.Enter => ConsoleKey.Enter,
            Windows.System.VirtualKey.F => ConsoleKey.F,
            Windows.System.VirtualKey.Escape => ConsoleKey.Escape,
            _ => (ConsoleKey?)null
        };
        var action = PlaybackShortcuts.Evaluate(key, video.IsPlaying,
            AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, editing, modified);
        if (action == FullscreenAction.None) return;
        args.Handled = true;
        if (!args.KeyStatus.WasKeyDown)
        {
            if (action == FullscreenAction.Exit && presenterBeforePlayback is { Kind: not AppWindowPresenterKind.FullScreen })
                AppWindow.SetPresenter(presenterBeforePlayback);
            else
                AppWindow.SetPresenter(action == FullscreenAction.Enter ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        closing = true;
        currentDialog?.Hide();
        lifetime.Cancel();
        StopSending();
        try
        {
            if (startup is not null) await startup;
            if (pickerFinished is not null) await pickerFinished.Task;
            if (sending is not null) await sending;
            await StopReceiverAsync();
            await discovery;
            Close();
        }
        catch (Exception error) { closing = false; Report(error); }
    }

    private async Task<string> VerifySharingSettingsAsync(CancellationToken token)
    {
        if (!isolated) throw new InvalidOperationException("Sharing settings verification requires an isolated app.");
        var originalSettings = settings;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await OpenDialogAsync();
            if (currentDialog?.Content is not ScrollViewer { Content: StackPanel settingsPanel } ||
                !settingsPanel.Children.OfType<ComboBox>().Select(combo => combo.Header is TextBlock header ? header.Text : combo.Header?.ToString())
                    .SequenceEqual(new[] { L("MaximumFPS"), L("VideoCodec"), L("ReceiverOrientation") }))
                throw new InvalidOperationException("Sharing settings lost the FPS/codec/orientation options or expose a transport selector.");
            var fpsChoice = settingsPanel.Children.OfType<ComboBox>().First();
            if (fpsChoice.IsEditable ||
                !fpsChoice.Items.Cast<ComboBoxItem>().Select(item => (int)item.Tag).SequenceEqual(CaptureSettings.FpsPresets) ||
                fpsChoice.SelectedValue is not int defaultFps || defaultFps != 0 ||
                fpsChoice.Items.Cast<ComboBoxItem>().First().Content?.ToString() != L("UncappedDefault"))
                throw new InvalidOperationException("The FPS dropdown must offer Uncapped by default and positive presets up to 120.");
            fpsChoice.SelectedValue = 120;
            if (fpsChoice.SelectedValue is not int selectedFps || selectedFps != 120)
                throw new InvalidOperationException("The FPS dropdown could not select 120 FPS.");
            await CloseDialogAsync();
            settings = settings with { MaxFps = 47 };
            await OpenDialogAsync();
            if (currentDialog?.Content is not ScrollViewer { Content: StackPanel legacySettingsPanel } ||
                legacySettingsPanel.Children.OfType<ComboBox>().First() is not { IsEditable: false, SelectedValue: int legacyFps } legacyFpsChoice ||
                legacyFps != 47 || !legacyFpsChoice.Items.Cast<ComboBoxItem>().Select(item => (int)item.Tag)
                    .SequenceEqual(CaptureSettings.FpsPresets.Append(47).Order()))
                throw new InvalidOperationException("The FPS dropdown did not preserve the saved custom cap alongside the common presets.");
            await CloseDialogAsync();
            return "Settings offer a non-editable FPS dropdown defaulting to Uncapped, allow 120, preserve saved custom caps, and retain codec/orientation without a transport selector.";
        }
        finally
        {
            currentDialog?.Hide();
            settings = originalSettings;
        }

        async Task OpenDialogAsync()
        {
            if (currentDialog is not null) throw new InvalidOperationException("An unexpected dialog is already open.");
            Settings_Click(SettingsButton, new RoutedEventArgs());
            while (currentDialog is null) await Task.Delay(25, deadline.Token);
        }

        async Task CloseDialogAsync()
        {
            currentDialog!.Hide();
            while (currentDialog is not null) await Task.Delay(25, deadline.Token);
        }
    }

    internal async Task RunSharingSettingsSelfTestAsync(string reportFile)
    {
        var checks = new List<string>();
        string? failure = null;
        try { checks.Add(await VerifySharingSettingsAsync(CancellationToken.None)); }
        catch (Exception error) { failure = error.ToString(); }
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null), ("checks", JsonData.Value(checks)), ("failure", failure),
            ("inputInjected", false), ("captureUsed", false), ("audioUsed", false), ("settingsPersisted", false)),
            indented: true));
    }

    internal async Task RunSelfTestAsync(string reportFile, DiagnosticLog? journal = null)
    {
        var checks = new List<string>();
        string? failure = null;
        var previousLog = diagnosticLog;
        var previousLogPath = diagnosticLogPath;
        diagnosticLog = journal;
        diagnosticLogPath = journal?.FilePath;
        var phase = "checking startup";
        void Phase(string next) { phase = next; Diagnostic($"UI self-test phase: {phase}"); }
        try
        {
            Phase("checking startup");
            await Task.Delay(250);
            if (receiver is null || ReadinessTitle.Text != L("Ready") || HomePage.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Startup did not show a ready listener on the home screen.");
            checks.Add("Automatic startup opens a real loopback listener and shows READY.");
            var displayedVersion = L("Version", AppVersion.Current().Label);
            if (VersionText.Text != displayedVersion)
                throw new InvalidOperationException($"Displayed version does not match the running app: expected {displayedVersion}.");
            checks.Add($"The displayed version matches the running app: {displayedVersion}.");
            if (AddressList.ItemsSource is not AddressRow[] addresses || addresses.Length != 1 ||
                !addresses[0].DisplayAddress.Contains(" : ", StringComparison.Ordinal) ||
                addresses[0].CanonicalAddress.Contains(' '))
                throw new InvalidOperationException("Endpoint display/copy formatting is incorrect.");
            checks.Add("Displayed endpoints contain spaces; clipboard values are canonical.");
            var original = receiver.Port;
            var retiredSelf = new ReceiverEndpoint(IPAddress.Loopback, original, "Retired self", false, instanceId);
            await InitializeAsync();
            if (receiver.Port != original) throw new InvalidOperationException("Startup opened a second listener.");
            using (var lease = roles.TryAcquire(CastRole.Sending) ?? throw new InvalidOperationException("Could not reserve sending."))
            {
                Phase("pausing receiver for outgoing role");
                await StopReceiverAsync();
                Render();
                if (ReadinessTitle.Text != L("Paused") || AddressList.Visibility != Visibility.Collapsed || receiver is not null)
                    throw new InvalidOperationException("Sending did not pause the actual listener and hide its endpoints.");
                if (roles.TryAcquire(CastRole.Receiving) is not null)
                    throw new InvalidOperationException("Receiving was admitted while sending.");
            }
            await StartReceiverAsync();
            Phase("checking receiver restart and discovery identity");
            if (receiver is null || ReadinessTitle.Text != L("Ready"))
                throw new InvalidOperationException("Receiving did not restart after the outgoing lease ended.");
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, receiver.Port);
            checks.Add("Outgoing role closes the listener; release restarts a responsive listener.");
            discovered = [retiredSelf, new(IPAddress.Loopback, receiver.Port, "Self", false, instanceId),
                new(IPAddress.Loopback, receiver.Port == 65535 ? 65534 : receiver.Port + 1, "Other", false, Guid.NewGuid().ToString("N"))];
            RenderDevices();
            if (ReceiverList.ItemsSource is not ReceiverRow[] devices || devices.Length != 1 || devices[0].Name != "Other")
                throw new InvalidOperationException("Discovery listed this app as its own target.");
            checks.Add("Nearby list excludes current/retired self identities but retains a different local app instance.");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 740));
            Phase("checking responsive layout");
            await Task.Delay(150);
            if (Grid.GetRow(ReceiveCard) != 1) throw new InvalidOperationException("Narrow layout did not stack the cards.");
            checks.Add("Narrow windows stack sending and receiving panels.");
            Phase("checking whole-home scrolling with and without a long error");
            using (var layoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                var layoutMeasurements = new List<JsonNode>();
                await CheckHomeLayoutAsync(layoutMeasurements, journal, layoutTimeout.Token);
                checks.Add($"Whole-home scrolling and Tab bring-into-view pass in {layoutMeasurements.Count} short-window layouts.");
            }
            SetSource(WindowsVideoSource.SelectDefaultDisplay());
            Phase("checking failed destination and worker retirement");
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var unavailablePort = ((IPEndPoint)unavailable.LocalEndPoint!).Port;
            var beforeFailure = diagnostics.Count;
            await StartSendingAsync(new(IPAddress.Loopback, unavailablePort, "Unavailable test receiver", false));
            if (receiver is null || ReadinessTitle.Text != L("Ready") || !Problem.IsOpen || roles.Role != CastRole.Idle ||
                outgoing is not null || ReceiverList.ItemsSource is ReceiverRow[] failedRows && failedRows.Any(row => row.IsActive) ||
                !diagnostics.Skip(beforeFailure).Any(detail => detail.Contains("SocketException", StringComparison.Ordinal)))
                throw new InvalidOperationException("A real outgoing TCP connection failure did not report its error and restore receiving.");
            checks.Add("Capture source preflight followed by a real outgoing TCP failure reports the error and restores READY.");
            Phase("checking bind conflict and recovery");
            await StopReceiverAsync();
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            try
            {
                var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
                await StartReceiverAsync(blockedPort);
                if (receiver is not null || receiverState.Phase != ReceiverUiPhase.Unavailable ||
                    ReadinessTitle.Text != L("ReceiverPhaseUnavailable") || !Problem.IsOpen || !CanSend ||
                    !Problem.Message.Contains($"TCP {blockedPort}", StringComparison.Ordinal))
                    throw new InvalidOperationException("A bound port did not retain an explicit unavailable state while leaving sending enabled.");
            }
            finally { blocker.Stop(); }
            await StartReceiverAsync();
            if (receiver is null || ReadinessTitle.Text != L("Ready"))
                throw new InvalidOperationException("Receiving did not recover after the conflicting listener was released.");
            checks.Add("Real bind conflict retains its startup error, leaves sending enabled, and recovers after the port is released.");
            Phase("checking sharing settings");
            checks.Add(await VerifySharingSettingsAsync(CancellationToken.None));
            var captureTargets = CaptureSource.Enumerate();
            if (!captureTargets.Any(target => !target.IsWindow) ||
                !captureTargets.Any(target => target.IsWindow && target.Handle == WinRT.Interop.WindowNative.GetWindowHandle(this)))
                throw new InvalidOperationException("The target-aware source selector did not enumerate displays and this window.");
            (source ?? throw new InvalidOperationException("Capture source was not selected.")).ReadColor().Validate();
            checks.Add("Target-aware capture enumerates displays/windows and preserves per-display HDR mode with explicit known/unknown luminance metadata.");
            Phase("checking inline send negotiation, capture and cleanup");
            await RunInlineSendingSelfTestAsync(checks);
            Phase("all checks completed");
        }
        catch (Exception error) { failure = $"{phase}: {error}"; Diagnostic($"UI self-test failed: {failure}"); }
        try
        {
            journal?.Write($"UI self-test phase: writing result; passed={failure is null}, checks={checks.Count}, lastPhase={phase}");
            File.WriteAllText(reportFile, JsonData.Text(JsonData.Object(
                ("passed", failure is null), ("checks", JsonData.Value(checks)), ("failure", failure), ("phase", phase),
                ("diagnosticLogPath", diagnosticLogPath), ("version", AppVersion.Current().Label), ("diagnostics", JsonData.Value(diagnostics))),
                indented: true));
        }
        finally { diagnosticLog = previousLog; diagnosticLogPath = previousLogPath; }
    }
}
