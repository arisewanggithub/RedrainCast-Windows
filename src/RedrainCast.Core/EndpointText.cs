using System.Globalization;
using System.Net;
using System.Net.Sockets;
using RedrainCast.Send.Core;

namespace RedrainCast.Core;

public static class EndpointText
{
    public static string Format(IPAddress address, int port, bool spaced = true)
    {
        var endpoint = ReceiverEndpoint.Manual(address.ToString(), port);
        var host = endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address}]" : endpoint.Address.ToString();
        return $"{host}{(spaced ? " : " : ":")}{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static ReceiverEndpoint Parse(string text)
    {
        var value = text.Trim();
        var separator = value.LastIndexOf(':');
        if (separator <= 0)
            throw new ArgumentException("Enter the receiver's current IP : Port, for example 192.168.1.42 : 50432.");
        var host = value[..separator].Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        else if (host.Contains(':'))
            throw new ArgumentException("Enclose IPv6 addresses in brackets, for example [fe80::1] : 50432.");
        var input = ManualReceiverInput.Parse(host, value[(separator + 1)..]);
        var endpoint = input.Endpoint ?? throw new ArgumentException(string.Join(" ",
            new[] { input.AddressError, input.PortError }.OfType<string>()));
        if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any) ||
            endpoint.Address.Equals(IPAddress.Broadcast) || endpoint.Address.IsIPv6Multicast ||
            endpoint.Address.AddressFamily == AddressFamily.InterNetwork && endpoint.Address.GetAddressBytes()[0] is >= 224 and <= 239)
            throw new ArgumentException("Enter a device's unicast IP address, not a wildcard, broadcast or multicast address.");
        return endpoint;
    }
}
