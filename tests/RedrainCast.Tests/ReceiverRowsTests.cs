using System.Net;
using RedrainCast.Core;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class ReceiverRowsTests
{
    private static ReceiverEndpoint Device(int last, bool busy = false) =>
        new(IPAddress.Parse($"192.0.2.{last}"), 27184, "Receiver", busy);

    [Fact]
    public void IdleRowsKeepAvailableAndBusySemantics()
    {
        var rows = ReceiverRows.Create([Device(1), Device(2, true)], null, true);
        Assert.Equal("Available", rows[0].Status);
        Assert.True(rows[0].CanAct);
        Assert.Equal("Busy", rows[1].Status);
        Assert.False(rows[1].CanAct);
        Assert.All(rows, row => Assert.Equal("Send", row.ActionLabel));
    }

    [Theory]
    [InlineData(OutgoingPhase.Connecting, "Connecting...", "Cancel", true)]
    [InlineData(OutgoingPhase.Casting, "Casting", "Stop", true)]
    [InlineData(OutgoingPhase.Stopping, "Stopping...", "Stop", false)]
    public void ActiveDestinationOwnsItsStatusActionAndParameters(OutgoingPhase phase, string status, string action, bool enabled)
    {
        var active = new OutgoingCast(Device(2), phase, "AV1 - 1920 x 1200", "Hardware encoder");
        var rows = ReceiverRows.Create([Device(1, true), Device(2, true)], active, false);
        Assert.Equal(active.Target, rows[0].Endpoint);
        Assert.True(rows[0].IsActive);
        Assert.Equal(status, rows[0].Status);
        Assert.Equal(action, rows[0].ActionLabel);
        Assert.Equal(enabled, rows[0].CanAct);
        Assert.Equal(active.Parameters, rows[0].Parameters);
        Assert.Equal(active.Encoder, rows[0].Encoder);
        Assert.Equal("Busy", rows[1].Status);
        Assert.False(rows[1].CanAct);
    }

    [Fact]
    public void DiscoveryExpiryAndManualEntryCannotRemoveTheActiveControl()
    {
        var active = new OutgoingCast(Device(2), OutgoingPhase.Casting);
        var first = ReceiverRows.Create([Device(1), Device(2, true)], active, false);
        var expired = ReceiverRows.Create([Device(1)], active, false);
        Assert.Equal(first[0], expired[0]);
        var manual = Assert.Single(ReceiverRows.Create([], active, false));
        Assert.Equal("Stop", manual.ActionLabel);
        Assert.True(manual.CanAct);
        Assert.Empty(ReceiverRows.Create([], null, true));
    }

    [Fact]
    public void SameNameDoesNotConfuseDifferentTargetsAndActiveAdvertisementsAreNotDuplicated()
    {
        var active = new OutgoingCast(Device(2), OutgoingPhase.Casting);
        var rows = ReceiverRows.Create([Device(1, true), Device(2, true), Device(2, true)], active, false);
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.IsActive);
        Assert.Equal(Device(1, true), rows[1].Endpoint);
        Assert.Equal("Busy", rows[1].Status);
    }

    [Fact]
    public void StopIsTerminalUntilCleanupEvenIfLateStreamingStatusArrives()
    {
        var active = new OutgoingCast(Device(2)).ApplyStatus("Negotiating").ApplyStatus("Streaming");
        Assert.Equal(OutgoingPhase.Casting, active.Phase);
        var stopping = active.ApplyStatus("Stopping");
        Assert.Equal(stopping, stopping.ApplyStatus("Streaming"));
        Assert.Equal(stopping, stopping.ApplyStatus("Receiver warning: late callback"));
    }

    [Fact]
    public void WarningsRemainVisibleWithoutChangingCastingToBusy()
    {
        var active = new OutgoingCast(Device(2), OutgoingPhase.Casting).ApplyStatus("Receiver warning: decoder notice");
        var row = Assert.Single(ReceiverRows.Create([], active, false));
        Assert.Equal("Casting", row.Status);
        Assert.Equal(active.Notice, row.Notice);
        Assert.Equal("Stop", row.ActionLabel);
    }
}
