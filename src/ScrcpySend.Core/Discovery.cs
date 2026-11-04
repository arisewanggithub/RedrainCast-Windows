using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using System.Globalization;

namespace ScrcpySend.Core;

public sealed record ReceiverEndpoint(IPAddress Address, int Port, string Name, bool Busy)
{
    public string Key => $"{Address}:{Port}";
    public override string ToString() => $"{Name} - {Address} : {Port}{(Busy ? " (busy)" : "")}";

    public static ReceiverEndpoint Manual(string address, int port)
    {
        var input = ManualReceiverInput.Parse(address, port.ToString(CultureInfo.InvariantCulture));
        return input.Endpoint ?? throw new ArgumentException(string.Join(" ",
            new[] { input.AddressError, input.PortError }.OfType<string>()));
    }
}

public sealed record ManualReceiverInput(ReceiverEndpoint? Endpoint, string? AddressError, string? PortError)
{
    public static ManualReceiverInput Parse(string address, string port)
    {
        string? addressError = null, portError = null;
        if (string.IsNullOrWhiteSpace(address)) addressError = "Enter the receiver's IP address.";
        else if (!IPAddress.TryParse(address.Trim(), out _)) addressError = "Enter a numeric IPv4 or IPv6 address.";
        if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var portNumber) ||
            portNumber is < 1 or > 65535)
            portError = "Enter a whole-number TCP port from 1 to 65535.";
        var endpoint = addressError == null && portError == null
            ? new ReceiverEndpoint(IPAddress.Parse(address.Trim()), portNumber, "Manual receiver", false) : null;
        return new(endpoint, addressError, portError);
    }

    public (string? Address, string? Port) Feedback(bool addressTouched, bool portTouched) =>
        (addressTouched ? AddressError : null, portTouched ? PortError : null);
}

public sealed record ReceiverChoice(ReceiverEndpoint? Endpoint, bool IsCurrent = true)
{
    public static ReceiverChoice Manual { get; } = new(Endpoint: null);
    public bool IsManual => Endpoint == null;
    public override string ToString() => IsManual ? "Manually specify..." :
        $"{Endpoint}{(IsCurrent ? "" : " (unavailable)")}";
}

public sealed class ReceiverSelection
{
    public IReadOnlyList<ReceiverChoice> Choices { get; private set; } = [ReceiverChoice.Manual];
    public ReceiverChoice? Selected { get; private set; }
    private bool canAutoSelect = true;

    public void Refresh(IReadOnlyList<ReceiverEndpoint> endpoints)
    {
        var choices = endpoints.Select(endpoint => new ReceiverChoice(endpoint)).ToList();
        if (Selected is { IsManual: false } previous)
        {
            var current = choices.FirstOrDefault(choice => choice.Endpoint!.Key == previous.Endpoint!.Key);
            if (current == null)
            {
                current = previous with { IsCurrent = false };
                choices.Add(current);
            }
            Selected = current;
        }
        choices.Add(ReceiverChoice.Manual);
        Choices = choices;
        if (Selected == null && canAutoSelect)
        {
            Selected = choices.FirstOrDefault(choice => choice.Endpoint is { Busy: false } && choice.IsCurrent);
            if (Selected != null) canAutoSelect = false;
        }
    }

    public void Select(ReceiverChoice choice)
    {
        if (!Choices.Contains(choice)) throw new ArgumentException("Choose a receiver from the current list.");
        Selected = choice;
        canAutoSelect = false;
    }

    public ReceiverEndpoint Resolve(string manualAddress, string manualPort)
    {
        var choice = Selected ?? throw new ArgumentException("Select a receiver or choose Manually specify.");
        if (choice.IsManual)
        {
            var input = ManualReceiverInput.Parse(manualAddress, manualPort);
            return input.Endpoint ?? throw new ArgumentException(string.Join(" ",
                new[] { input.AddressError, input.PortError }.OfType<string>()));
        }
        if (!choice.IsCurrent) throw new ArgumentException("The selected receiver is no longer advertised. Select another receiver or use manual entry.");
        var endpoint = choice.Endpoint!;
        if (endpoint.Busy) throw new ArgumentException("The selected receiver is busy. Its active cast will not be evicted.");
        return endpoint;
    }
}

public sealed class DiscoveryTable
{
    private readonly Dictionary<string, (ReceiverEndpoint Endpoint, DateTimeOffset Seen)> entries = [];

    public bool Observe(ReadOnlySpan<byte> payload, IPAddress source, DateTimeOffset now)
    {
        Expire(now);
        if (payload.Length > 1024) throw new ProtocolException("Discovery packet exceeds 1024 bytes.");
        var json = StrictJson.Parse(payload);
        if (StrictJson.String(json, "protocol", 32) != "screencopy" ||
            StrictJson.Integer(json, "version") != Wire.Version) return false;
        var name = StrictJson.String(json, "name", 80);
        var port = StrictJson.Integer(json, "port");
        var state = StrictJson.String(json, "state", 16);
        if (name.Length == 0 || port is < 1 or > 65535 || state is not ("available" or "busy"))
            throw new ProtocolException("Invalid discovery fields.");
        var endpoint = new ReceiverEndpoint(source, port, name, state == "busy");
        if (!entries.ContainsKey(endpoint.Key) && entries.Count >= 64) return false;
        entries[endpoint.Key] = (endpoint, now);
        return true;
    }

    public IReadOnlyList<ReceiverEndpoint> Snapshot(DateTimeOffset now)
    {
        Expire(now);
        return entries.Values.Select(item => item.Endpoint).OrderBy(item => item.Key).ToArray();
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var key in entries.Where(item => now - item.Value.Seen >= TimeSpan.FromSeconds(6))
                     .Select(item => item.Key).ToArray())
            entries.Remove(key);
    }
}

public sealed class ReceiverDiscovery
{
    public async Task RunAsync(Action<IReadOnlyList<ReceiverEndpoint>> changed,
        Action<string> diagnostic, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 27183));
        var table = new DiscoveryTable();
        var receive = socket.ReceiveAsync(cancellationToken).AsTask();
        var tick = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var lastWarning = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await Task.WhenAny(receive, tick) == receive)
            {
                var packet = await receive;
                try { table.Observe(packet.Buffer, packet.RemoteEndPoint.Address, DateTimeOffset.UtcNow); }
                catch (Exception error) when (error is ProtocolException or JsonException)
                {
                    if (lastWarning == 0 || Stopwatch.GetElapsedTime(lastWarning) >= TimeSpan.FromSeconds(1))
                    {
                        lastWarning = Stopwatch.GetTimestamp();
                        diagnostic($"Ignored invalid discovery from {packet.RemoteEndPoint.Address}: {error.Message} (diagnostics limited to once/second)");
                    }
                }
                receive = socket.ReceiveAsync(cancellationToken).AsTask();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (tick.IsCompleted)
            {
                changed(table.Snapshot(DateTimeOffset.UtcNow));
                tick = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }
}
