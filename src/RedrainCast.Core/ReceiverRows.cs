using ScrcpySend.Core;

namespace RedrainCast.Core;

public enum OutgoingPhase { Connecting, Casting, Stopping }

public sealed record OutgoingCast(ReceiverEndpoint Target, OutgoingPhase Phase = OutgoingPhase.Connecting,
    string Parameters = "", string Encoder = "", string Notice = "", string ReceiverOptionNotice = "")
{
    public string? NoticeKey { get; init; }
    public string? ReceiverOptionKey { get; init; }
    public OutgoingCast ApplyStatus(string status)
    {
        if (status == "Stopping") return this with { Phase = OutgoingPhase.Stopping, Notice = "", NoticeKey = null };
        if (Phase == OutgoingPhase.Stopping) return this;
        if (status.StartsWith("Receiver option unavailable: ", StringComparison.Ordinal))
        {
            var key = status == "Receiver option unavailable: Playback statistics require a compatible receiver; casting continues without the overlay."
                ? "PlaybackOptionUnavailable" : null;
            return this with { Notice = status, ReceiverOptionNotice = status, NoticeKey = key, ReceiverOptionKey = key };
        }
        return status == "Streaming" ? this with { Phase = OutgoingPhase.Casting, Notice = ReceiverOptionNotice,
            NoticeKey = ReceiverOptionKey } :
            this with { Notice = status == "Connecting" ? "" : status, NoticeKey = status switch
            {
                "Preparing capture" => "PreparingCapture", "Initializing capture" => "InitializingCapture",
                "Negotiating" => "Negotiating", _ => null
            }};
    }
}

public sealed record ReceiverRow(ReceiverEndpoint Endpoint, bool IsActive, bool CanAct,
    string Status, string ActionLabel, string Parameters = "", string Encoder = "", string Notice = "",
    string? AccessibleAction = null)
{
    public string Name => Endpoint.Name;
    public string DisplayAddress => EndpointText.Format(Endpoint.Address, Endpoint.Port);
    public string ActionHelp => AccessibleAction ?? $"{ActionLabel}: {Name}";
}

public static class ReceiverRows
{
    public static ReceiverRow[] Create(IEnumerable<ReceiverEndpoint> nearby, OutgoingCast? active, bool canSend,
        Func<string, string>? localize = null, Func<string, string, string>? actionHelp = null)
    {
        string L(string key) => (localize ?? EnglishCatalog.Get)(key);
        ReceiverRow Accessible(ReceiverRow row) => row with
        {
            AccessibleAction = actionHelp?.Invoke(row.ActionLabel, row.Name)
        };
        var rows = new List<ReceiverRow>();
        if (active is not null)
        {
            var status = active.Phase switch
            {
                OutgoingPhase.Connecting => L("Connecting"),
                OutgoingPhase.Casting => L("Casting"),
                OutgoingPhase.Stopping => L("Stopping"),
                _ => throw new ArgumentOutOfRangeException(nameof(active))
            };
            rows.Add(Accessible(new(active.Target, true, active.Phase != OutgoingPhase.Stopping, status,
                L(active.Phase == OutgoingPhase.Connecting ? "Cancel" : "Stop"),
                active.Parameters, active.Encoder, active.Notice)));
        }
        rows.AddRange(nearby.DistinctBy(endpoint => endpoint.Key)
            .Where(endpoint => endpoint.Key != active?.Target.Key)
            .Select(endpoint => Accessible(new ReceiverRow(endpoint, false, active is null && canSend && !endpoint.Busy,
                L(endpoint.Busy ? "Busy" : "Available"), L("Send")))));
        return rows.ToArray();
    }
}
