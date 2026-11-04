using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RedrainCast.Core;
using ScrcpyReceive.Core;
using ScrcpySend.Core;
using ScrcpySend.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Capture;
using ReceiverDiscovery = ScrcpySend.Core.ReceiverDiscovery;
using LocalDiscovery = ScrcpyReceive.Core.Discovery;

namespace RedrainCast.App;

public sealed partial class MainWindow : Window
{
    private sealed record AddressRow(string Adapter, string DisplayAddress, string CanonicalAddress);

    private readonly SessionRoles roles = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly VideoHost video;
    private readonly WindowLayoutController layout;
    private readonly bool isolated;
    private readonly List<string> diagnostics = [];
    private readonly HashSet<IPAddress> localAddresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
    private ReceiverUiState receiverState = new();
    private ReceiverServer? receiver;
    private Task? startup;
    private Task discovery = Task.CompletedTask;
    private Task? sending;
    private CancellationTokenSource? sendStop;
    private OutgoingCast? outgoing;
    private IReadOnlyList<ReceiverEndpoint> discovered = [];
    private GraphicsCaptureItem? source;
    private CaptureSettings settings = new();
    private ContentDialog? currentDialog;
    private int receiverGeneration;
    private bool closing;
    private bool picking;
    private TaskCompletionSource? pickerFinished;
    private bool receiverFaulted;
    private bool discoveryFailed;
    private bool showingVideo;
    private const int ListeningPort = 27184;
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedrainCast", "settings.json");

    public MainWindow(bool isolated = false)
    {
        this.isolated = isolated;
        InitializeComponent();
        Title = "RedrainCast";
        DeviceName.Text = Environment.MachineName;
        VersionText.Text = $"RedrainCast {AppVersion.Current().Label}";
        video = new(VideoSurface, detail => OnReceiverNotice(new("Video", detail), receiverGeneration));
        layout = new(this, HomePage, Report);
        WindowRoot.ActualThemeChanged += (_, _) => UpdateTitleBar();
        UpdateTitleBar();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            video.Dispose();
            layout.Dispose();
            lifetime.Dispose();
        };
    }

    internal Task InitializeAsync() => startup ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (!isolated)
        {
            LoadSettings();
            try { SetSource(WindowsVideoSource.SelectDefaultDisplay()); }
            catch (Exception error) { Report(new IOException($"Choose a display or window before sending. {error.Message}", error)); }
        }
        await StartReceiverAsync();
        if (!isolated) discovery = DiscoverAsync();
    }

    private void Diagnostic(string detail)
    {
        void Apply()
        {
            diagnostics.Add($"{DateTimeOffset.Now:O} {detail}");
            while (diagnostics.Count > 100) diagnostics.RemoveAt(0);
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    private void Report(Exception error)
    {
        Diagnostic(error.ToString());
        Problem.Title = "Action failed";
        Problem.Message = error.Message;
        Problem.Severity = InfoBarSeverity.Error;
        Problem.IsOpen = true;
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
                localAddresses.Clear();
                localAddresses.UnionWith([IPAddress.Loopback, IPAddress.IPv6Loopback]);
                foreach (var endpoint in notice.ListeningEndpoints) localAddresses.Add(EndpointText.Parse(endpoint).Address);
            }
            if (notice.State is "Start failed" or "Listener failed" or "Cleanup failed") receiverFaulted = true;
            if (receiverState.ProblemCode is not null && notice.State == receiverState.ProblemCode)
            {
                Problem.Title = receiverState.ProblemTitle;
                Problem.Message = $"{receiverState.ProblemMessage}\n{notice.Detail}";
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

    private Task StartReceiverAsync(int? port = null)
    {
        if (closing || receiver is not null) return Task.CompletedTask;
        receiverState = new();
        receiverFaulted = false;
        var generation = ++receiverGeneration;
        ReceiverServer? candidate = null;
        try
        {
            candidate = new(Environment.MachineName, video, notice => OnReceiverNotice(notice, generation),
                port ?? (isolated ? 0 : ListeningPort), isolated ? IPAddress.Loopback : IPAddress.Any, AcquireReceiving);
            candidate.Start(advertise: !isolated);
            receiver = candidate;
            if (isolated)
                OnReceiverNotice(new("Listening", "Isolated loopback listener.",
                    [LocalDiscovery.DisplayEndpoint(IPAddress.Loopback, candidate.Port)]), generation);
        }
        catch (Exception error) when (error is SocketException or ArgumentException or NetworkInformationException)
        {
            return HandleStartFailureAsync(candidate, error, port ?? ListeningPort);
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
        OnReceiverNotice(new("Start failed", $"{error.Message} Close other receivers using TCP {port}, then reopen RedrainCast. Sending remains available."), generation);
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
                Problem.Title = "Invalid discovery advertisement ignored";
                Problem.Message = detail;
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
                Report(new IOException($"Nearby discovery stopped. Connect by IP : Port instead. {error.Message}", error));
                RenderDevices();
            });
        }
    }

    private bool IsSelf(ReceiverEndpoint endpoint) =>
        receiver is not null && localAddresses.Contains(endpoint.Address) && endpoint.Port == receiver.Port;

    private bool CanSend => !closing && !picking && receiverState.ProblemCode != "Cleanup failed" &&
        roles.Role == CastRole.Idle && source is not null;

    private void RenderDevices()
    {
        var nearby = discovered.Where(endpoint => !IsSelf(endpoint)).ToArray();
        var rows = ReceiverRows.Create(nearby, outgoing, CanSend);
        if (ReceiverList.ItemsSource is not ReceiverRow[] previous || !previous.SequenceEqual(rows))
            ReceiverList.ItemsSource = rows;
        DiscoveryStatus.Text = discoveryFailed
            ? outgoing is null ? "Discovery unavailable. Connect by IP : Port." : "Discovery unavailable. The active destination is retained above."
            : nearby.Length == 0
                ? outgoing is null ? "No nearby receivers yet. Both devices need a reachable local network." : "No nearby receivers advertised. The active destination is shown above."
                : $"{nearby.Length} receiver(s) discovered. Updated automatically.";
    }

    public static Visibility OptionalTextVisibility(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private void Render()
    {
        var role = roles.Role;
        var paused = role == CastRole.Sending;
        ReadinessTitle.Text = paused ? "PAUSED" : role == CastRole.Receiving ? "CONNECTING" :
            receiverState.Phase == ReceiverUiPhase.Ready ? "READY" : receiverState.Status.ToUpperInvariant();
        ReadinessDetail.Text = paused ? "Receiving pauses while you share your screen." :
            role == CastRole.Receiving ? "Preparing the incoming video..." :
            receiverState.Phase == ReceiverUiPhase.Ready ? "Listening for connections" :
            receiverState.Phase == ReceiverUiPhase.NoNetwork ? "Connect to Wi-Fi or Ethernet." :
            receiverState.Phase == ReceiverUiPhase.Starting ? "Opening the listener..." : receiverState.ProblemMessage;
        ReadyTint.Visibility = !paused && role == CastRole.Idle && receiverState.Phase == ReceiverUiPhase.Ready
            ? Visibility.Visible : Visibility.Collapsed;
        var showAddresses = !paused && receiverState.ListenerOpen && !receiverFaulted;
        AddressList.Visibility = showAddresses ? Visibility.Visible : Visibility.Collapsed;
        AddressHint.Text = paused ? "Local receiving addresses are unavailable while sending." :
            "Use an address reachable from the sender's network.";
        AddressList.ItemsSource = showAddresses ? receiverState.Addresses.Select(endpoint =>
        {
            var parsed = EndpointText.Parse(endpoint);
            return new AddressRow(receiverState.AdapterLabels.TryGetValue(endpoint, out var label) ? label : "Local network",
                EndpointText.Format(parsed.Address, parsed.Port), EndpointText.Format(parsed.Address, parsed.Port, false));
        }).ToArray() : Array.Empty<AddressRow>();
        SettingsButton.IsEnabled = ChooseSource.IsEnabled = !closing && !picking && role == CastRole.Idle;
        ManualConnect.IsEnabled = CanSend;
        RenderDevices();
        var playing = video.IsPlaying;
        if (role == CastRole.Receiving) currentDialog?.Hide();
        if (showingVideo == playing) return;
        showingVideo = playing;
        HomePage.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PlayerInput.IsTabStop = playing;
        if (playing) PlayerInput.Focus(FocusState.Programmatic);
        else
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            ChooseSource.Focus(FocusState.Programmatic);
        }
    }

    private void WindowRoot_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var stacked = args.NewSize.Width < 900;
        Cards.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(ReceiveCard, stacked ? 0 : 1);
        Grid.SetRow(ReceiveCard, stacked ? 1 : 0);
    }

    private void UpdateTitleBar() => AppWindow.TitleBar.PreferredTheme =
        new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? TitleBarTheme.UseDefaultAppMode :
        WindowRoot.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    private void SetSource(GraphicsCaptureItem selected)
    {
        source = selected;
        SourceLabel.Text = $"{selected.DisplayName} ({selected.Size.Width} x {selected.Size.Height})";
        RenderDevices();
        ManualConnect.IsEnabled = CanSend;
    }

    private async void ChooseSource_Click(object sender, RoutedEventArgs args)
    {
        if (picking || roles.Role != CastRole.Idle) return;
        picking = true;
        pickerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Render();
        try
        {
            var picker = new GraphicsCapturePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickSingleItemAsync();
            if (selected is not null && !closing) SetSource(selected);
        }
        catch (Exception error) { Report(error); }
        finally
        {
            picking = false;
            pickerFinished.TrySetResult();
            if (!closing) Render();
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
            Report(new ArgumentException(endpoint.Busy ? "That receiver is busy." : "Choose another device, not this PC's own receiver."));
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
            Report(new InvalidOperationException("An incoming cast has already started."));
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
            await Task.Run(async () =>
            {
                await using var outgoingVideo = new WindowsVideoSource(selected, selectedSettings);
                await using var audio = selectedSettings.EnableAudio ? new WindowsAudioSource() : null;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(sendStop, stop) || outgoing is null) return;
                    var codec = selectedSettings.VideoCodec switch
                    {
                        "h264" => "H.264", "h265" => "HEVC", "av1" => "AV1",
                        _ => throw new NotSupportedException("Unsupported video codec.")
                    };
                    outgoing = outgoing with
                    {
                        Parameters = $"{codec} - {outgoingVideo.Width} x {outgoingVideo.Height}",
                        Encoder = outgoingVideo.EncoderName
                    };
                    RenderDevices();
                });
                stop.Token.ThrowIfCancellationRequested();
                await new SenderSession().RunAsync(endpoint, selectedSettings, outgoingVideo,
                    text => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!closing && ReferenceEquals(sendStop, stop) && outgoing is not null)
                        {
                            outgoing = outgoing.ApplyStatus(text);
                            RenderDevices();
                        }
                    }), stop.Token, audio);
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { Report(error); }
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
        try { return await dialog.ShowAsync(); }
        finally { currentDialog = null; }
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs args)
    {
        var input = new TextBox { Header = "Receiver IP : Port", PlaceholderText = "192.168.1.42 : 27184" };
        var errorLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(input);
        content.Children.Add(errorLabel);
        ReceiverEndpoint? target = null;
        var dialog = new ContentDialog
        {
            Title = "Connect to a receiver", Content = content,
            PrimaryButtonText = "Send", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try
            {
                target = EndpointText.Parse(input.Text);
                if (IsSelf(target)) throw new ArgumentException("Enter another device's address, not this PC's receiver.");
            }
            catch (ArgumentException error) { errorLabel.Text = error.Message; click.Cancel = true; }
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
            AddressFeedback.Text = $"Copied {EndpointText.Parse(endpoint).Address} : {EndpointText.Parse(endpoint).Port}";
        }
        catch (Exception error) { Report(new IOException($"Could not copy the address. {error.Message}", error)); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs args)
    {
        var size = new TextBox { Header = "Maximum dimension (64-3840 pixels)", Text = settings.MaxSize.ToString(CultureInfo.CurrentCulture) };
        var fps = new TextBox { Header = "Maximum FPS (1-120; this receiver supports up to 60)", Text = settings.MaxFps.ToString(CultureInfo.CurrentCulture) };
        var bitrate = new TextBox { Header = "Video bitrate (0.1-100 Mbps)", Text = (settings.VideoBitrate / 1_000_000m).ToString(CultureInfo.CurrentCulture) };
        var codec = new ComboBox { Header = "Video codec", ItemsSource = new[] { "H.264", "HEVC", "AV1" },
            SelectedIndex = settings.VideoCodec switch { "h265" => 1, "av1" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var orientation = new ComboBox { Header = "Receiver orientation", ItemsSource = new[] { "Auto", "Landscape", "Portrait" },
            SelectedIndex = settings.ReceiverOrientation switch { "landscape" => 1, "portrait" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var audio = new CheckBox { Content = "Send audio to receiver (silences local playback)", IsChecked = settings.EnableAudio };
        var errorLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        foreach (var element in new FrameworkElement[] { size, fps, bitrate, codec, orientation, audio, errorLabel })
            content.Children.Add(element);
        var dialog = new ContentDialog
        {
            Title = "Sharing settings", Content = new ScrollViewer { Content = content },
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, click) =>
        {
            try
            {
                var input = CaptureSettings.ParseInput(size.Text, bitrate.Text,
                    int.TryParse(fps.Text, NumberStyles.None, CultureInfo.CurrentCulture, out var cap) ? cap : null,
                    new[] { "auto", "landscape", "portrait" }[orientation.SelectedIndex], CultureInfo.CurrentCulture,
                    new[] { "h264", "h265", "av1" }[codec.SelectedIndex], audio.IsChecked == true);
                var next = input.Settings ?? throw new ArgumentException(string.Join(" ",
                    new[] { input.DimensionError, input.BitrateError, input.FpsError }.OfType<string>()));
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(next));
                File.Move(SettingsPath + ".tmp", SettingsPath, true);
                settings = next;
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                errorLabel.Text = error.Message;
                Diagnostic(error.ToString());
                click.Cancel = true;
            }
        };
        try { await ShowDialogAsync(dialog); }
        catch (Exception error) { Report(error); }
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
            Report(new IOException($"Saved settings could not be loaded; default sharing settings are in use. {error.Message}", error));
        }
    }

    private async void Help_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = "Connect on a trusted local network",
                Content = new TextBlock
                {
                    Text = "Open RedrainCast on both PCs. Choose a nearby receiver and Send, or enter its IP : Port manually.\n\n" +
                        "Incoming casts are accepted automatically. While receiving, this window shows only video. " +
                        "Enter or F toggles fullscreen; Esc leaves fullscreen. End the cast from the sender to return home.\n\n" +
                        "While sending, receiving is paused. Use Stop in the active receiver's row to automatically resume receiving.\n\n" +
                        "Allow RedrainCast through Windows Firewall on trusted Private networks. TCP 27184 carries casts; UDP 27183 discovers receivers. " +
                        "Traffic is not authenticated or encrypted. Do not expose these ports to the internet.",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "Close"
            });
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
            AppWindow.SetPresenter(action == FullscreenAction.Enter ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
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

    internal async Task RunSelfTestAsync(string reportFile)
    {
        var checks = new List<string>();
        string? failure = null;
        try
        {
            await Task.Delay(250);
            if (receiver is null || ReadinessTitle.Text != "READY" || HomePage.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Startup did not show a ready listener on the home screen.");
            checks.Add("Automatic startup opens a real loopback listener and shows READY.");
            if (AddressList.ItemsSource is not AddressRow[] addresses || addresses.Length != 1 ||
                !addresses[0].DisplayAddress.Contains(" : ", StringComparison.Ordinal) ||
                addresses[0].CanonicalAddress.Contains(' '))
                throw new InvalidOperationException("Endpoint display/copy formatting is incorrect.");
            checks.Add("Displayed endpoints contain spaces; clipboard values are canonical.");
            var original = receiver.Port;
            await InitializeAsync();
            if (receiver.Port != original) throw new InvalidOperationException("Startup opened a second listener.");
            using (var lease = roles.TryAcquire(CastRole.Sending) ?? throw new InvalidOperationException("Could not reserve sending."))
            {
                await StopReceiverAsync();
                Render();
                if (ReadinessTitle.Text != "PAUSED" || AddressList.Visibility != Visibility.Collapsed || receiver is not null)
                    throw new InvalidOperationException("Sending did not pause the actual listener and hide its endpoints.");
                if (roles.TryAcquire(CastRole.Receiving) is not null)
                    throw new InvalidOperationException("Receiving was admitted while sending.");
            }
            await StartReceiverAsync();
            if (receiver is null || ReadinessTitle.Text != "READY")
                throw new InvalidOperationException("Receiving did not restart after the outgoing lease ended.");
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, receiver.Port);
            checks.Add("Outgoing role closes the listener; release restarts a responsive listener.");
            discovered = [new(IPAddress.Loopback, receiver.Port, "Self", false), new(IPAddress.Parse("192.0.2.1"), 27184, "Other", false)];
            RenderDevices();
            if (ReceiverList.ItemsSource is not ReceiverRow[] devices || devices.Length != 1 || devices[0].Name != "Other")
                throw new InvalidOperationException("Discovery listed this app as its own target.");
            checks.Add("Nearby list excludes this app's own listener.");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 740));
            await Task.Delay(150);
            if (Grid.GetRow(ReceiveCard) != 1) throw new InvalidOperationException("Narrow layout did not stack the cards.");
            checks.Add("Narrow windows stack sending and receiving panels.");
            SetSource(WindowsVideoSource.SelectDefaultDisplay());
            using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var unavailablePort = ((IPEndPoint)unavailable.LocalEndPoint!).Port;
            var beforeFailure = diagnostics.Count;
            await StartSendingAsync(new(IPAddress.Loopback, unavailablePort, "Unavailable test receiver", false));
            if (receiver is null || ReadinessTitle.Text != "READY" || !Problem.IsOpen || roles.Role != CastRole.Idle ||
                outgoing is not null || ReceiverList.ItemsSource is ReceiverRow[] failedRows && failedRows.Any(row => row.IsActive) ||
                !diagnostics.Skip(beforeFailure).Any(detail => detail.Contains("SocketException", StringComparison.Ordinal)))
                throw new InvalidOperationException("A real outgoing TCP connection failure did not report its error and restore receiving.");
            checks.Add("Hardware encoder preflight followed by a real outgoing TCP failure reports the error and restores READY.");
            await StopReceiverAsync();
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            try
            {
                var blockedPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
                await StartReceiverAsync(blockedPort);
                if (receiver is not null || receiverState.Phase != ReceiverUiPhase.Unavailable ||
                    ReadinessTitle.Text != "RECEIVER UNAVAILABLE" || !Problem.IsOpen || !CanSend ||
                    !Problem.Message.Contains($"TCP {blockedPort}", StringComparison.Ordinal))
                    throw new InvalidOperationException("A bound port did not retain an explicit unavailable state while leaving sending enabled.");
            }
            finally { blocker.Stop(); }
            await StartReceiverAsync();
            if (receiver is null || ReadinessTitle.Text != "READY")
                throw new InvalidOperationException("Receiving did not recover after the conflicting listener was released.");
            checks.Add("Real bind conflict retains its startup error, leaves sending enabled, and recovers after the port is released.");
            Settings_Click(SettingsButton, new RoutedEventArgs());
            await Task.Delay(150);
            if (currentDialog?.Content is not ScrollViewer { Content: StackPanel settingsPanel } ||
                !settingsPanel.Children.OfType<ComboBox>().Select(combo => combo.Header?.ToString())
                    .SequenceEqual(new[] { "Video codec", "Receiver orientation" }))
                throw new InvalidOperationException("Sharing settings still expose a transport selector or lost the codec/orientation options.");
            currentDialog.Hide();
            await Task.Delay(50);
            checks.Add("The actual Settings dialog offers codec/orientation options without a transport selector.");
            await RunInlineSendingSelfTestAsync(checks);
        }
        catch (Exception error) { failure = error.ToString(); }
        File.WriteAllText(reportFile, JsonSerializer.Serialize(new { passed = failure is null, checks, failure, diagnostics },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
