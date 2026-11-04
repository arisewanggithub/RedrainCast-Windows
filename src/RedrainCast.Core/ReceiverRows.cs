using ScrcpySend.Core;

namespace RedrainCast.Core;

public enum OutgoingPhase { Connecting, Casting, Stopping }

public sealed record OutgoingCast(ReceiverEndpoint Target, OutgoingPhase Phase = OutgoingPhase.Connecting,
    string Parameters = "", string Encoder = "", string Notice = "")
{
    public OutgoingCast ApplyStatus(string status)
    {
        if (status == "Stopping") return this with { Phase = OutgoingPhase.Stopping, Notice = "" };
        if (Phase == OutgoingPhase.Stopping) return this;
        return status == "Streaming" ? this with { Phase = OutgoingPhase.Casting, Notice = "" } :
            this with { Notice = status == "Connecting" ? "" : status };
    }
}

public sealed record ReceiverRow(ReceiverEndpoint Endpoint, bool IsActive, bool CanAct,
    string Status, string ActionLabel, string Parameters = "", string Encoder = "", string Notice = "")
{
    public string Name => Endpoint.Name;
    public string DisplayAddress => EndpointText.Format(Endpoint.Address, Endpoint.Port);
    public string ActionHelp => $"{ActionLabel}: {Name}";
}

public static class ReceiverRows
{
    public static ReceiverRow[] Create(IEnumerable<ReceiverEndpoint> nearby, OutgoingCast? active, bool canSend)
    {
        var rows = new List<ReceiverRow>();
        if (active is not null)
        {
            var status = active.Phase switch
            {
                OutgoingPhase.Connecting => "Connecting...",
                OutgoingPhase.Casting => "Casting",
                OutgoingPhase.Stopping => "Stopping...",
                _ => throw new ArgumentOutOfRangeException(nameof(active))
            };
            rows.Add(new(active.Target, true, active.Phase != OutgoingPhase.Stopping, status,
                active.Phase == OutgoingPhase.Connecting ? "Cancel" : "Stop",
                active.Parameters, active.Encoder, active.Notice));
        }
        rows.AddRange(nearby.DistinctBy(endpoint => endpoint.Key)
            .Where(endpoint => endpoint.Key != active?.Target.Key)
            .Select(endpoint => new ReceiverRow(endpoint, false, active is null && canSend && !endpoint.Busy,
                endpoint.Busy ? "Busy" : "Available", "Send")));
        return rows.ToArray();
    }
}
