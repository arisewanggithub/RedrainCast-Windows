using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class VolumeChangeTests
{
    private static AudioOutputState State(bool muted, float volume) => new(muted, volume, [volume, volume]);

    [Fact]
    public void OrdinaryVolumeAdjustmentsKeepMuteOwnershipAndDoNotRequestFailureOrRemuting()
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        Assert.False(policy.Observe(State(true, 0.8f), Guid.NewGuid()));
        Assert.False(policy.TakeRemuteRequest());
        Assert.True(policy.ShouldRestore(true));
        Assert.False(policy.ShouldRestore(false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VolumeKeysThatAlsoUnmuteRequestLocalSuppressionWithoutStoppingTheCast(bool combinedNotification)
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        if (!combinedNotification) Assert.False(policy.Observe(State(true, 0.8f), Guid.NewGuid()));
        Assert.True(policy.Observe(State(false, 0.8f), Guid.NewGuid()));
        Assert.True(policy.TakeRemuteRequest());
        Assert.False(policy.TakeRemuteRequest());
        policy.MarkAppMute();
        Assert.True(policy.ShouldRestore(true));
    }

    [Fact]
    public void InitiallyMutedOutputStaysMutedUnlessTheUserRequestsUnmute()
    {
        var policy = new MuteOwnership(State(true, 0.3f));
        policy.MarkAppMute();
        Assert.False(policy.Observe(State(true, 0.8f), Guid.NewGuid()));
        Assert.False(policy.ShouldRestore(true));
        Assert.True(policy.Observe(State(false, 0.8f), Guid.NewGuid()));
        policy.TakeRemuteRequest();
        policy.MarkAppMute();
        Assert.True(policy.ShouldRestore(true));
    }

    [Fact]
    public void AUserMuteBeforeQueuedRemutingCancelsTheRequestAndIsPreservedOnStop()
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        Assert.True(policy.Observe(State(false, 0.8f), Guid.NewGuid()));
        Assert.False(policy.Observe(State(true, 0.8f), Guid.NewGuid()));
        Assert.False(policy.TakeRemuteRequest());
        Assert.False(policy.ShouldRestore(true));
    }

    [Fact]
    public void AppAcknowledgementsDoNotOverwriteLaterVolumeChangesAndAnExplicitMutedStateIsPreserved()
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        policy.Observe(State(true, 0.8f), Guid.NewGuid());
        Assert.False(policy.Observe(State(true, 0.3f), policy.Context));
        Assert.False(policy.Observe(State(true, 0.8f), Guid.NewGuid()));
        Assert.False(policy.ShouldRestore(true));
    }

    [Fact]
    public void PerChannelVolumeChangesAreNotMisclassifiedAsMuteChanges()
    {
        var policy = new MuteOwnership(State(false, 0.5f));
        policy.MarkAppMute();
        Assert.False(policy.Observe(new(true, 0.5f, [0.3f, 0.8f]), Guid.NewGuid()));
        Assert.True(policy.ShouldRestore(true));
    }

    [Fact]
    public void PendingUnmuteAtStopIsNotOverwrittenAndNotificationDataIsSnapshotted()
    {
        float[] channels = [0.3f, 0.3f];
        var policy = new MuteOwnership(new AudioOutputState(false, 0.3f, channels));
        policy.MarkAppMute();
        channels[0] = 0.9f;
        Assert.True(policy.Observe(State(false, 0.8f), Guid.NewGuid()));
        Assert.False(policy.ShouldRestore(false));
        policy.ConfirmLocalMute(policy.ObservationRevision);
        Assert.True(policy.ShouldRestore(true));
    }

    [Fact]
    public void InvalidNativeVolumeDataFailsExplicitly()
    {
        Assert.Throws<ArgumentException>(() => new MuteOwnership(State(false, float.NaN)));
        var policy = new MuteOwnership(State(false, 0.3f));
        Assert.Throws<ArgumentException>(() => policy.Observe(new(true, 0.3f, [1.2f]), Guid.NewGuid()));
    }

    [Fact]
    public void ExternalMuteDuringSubscribedInitialReadCannotBeLostToAStaleSnapshot()
    {
        var policy = new MuteOwnership();
        policy.Initialize(() =>
        {
            policy.Observe(State(true, 0.3f), Guid.NewGuid());
            return State(false, 0.3f);
        });
        policy.MarkAppMute();
        policy.Observe(State(true, 0.3f), policy.Context);
        Assert.False(policy.ShouldRestore(true));
    }

    [Fact]
    public void CallbackBeforeInitialReadAndAStablePostSubscriptionSnapshotAreBothSupported()
    {
        var policy = new MuteOwnership();
        policy.Observe(State(true, 0.3f), Guid.NewGuid());
        policy.Initialize(() => State(true, 0.3f));
        policy.MarkAppMute();
        Assert.False(policy.ShouldRestore(true));
        Assert.Throws<InvalidOperationException>(() => policy.Initialize(() => State(false, 0.3f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalMuteRacingWithAppRemutingIsPreservedRegardlessOfAcknowledgementOrder(bool appAcknowledgementFirst)
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        policy.Observe(State(true, 0.3f), policy.Context);
        policy.Observe(State(false, 0.3f), Guid.NewGuid());
        Assert.True(policy.TakeRemuteRequest());
        policy.MarkAppMute();
        if (appAcknowledgementFirst) policy.Observe(State(true, 0.3f), policy.Context);
        policy.Observe(State(true, 0.3f), Guid.NewGuid());
        if (!appAcknowledgementFirst) policy.Observe(State(true, 0.3f), policy.Context);
        policy.ConfirmLocalMute(policy.ObservationRevision);
        Assert.False(policy.ShouldRestore(true));
    }

    [Fact]
    public void AStaleAppAcknowledgementDoesNotHideANewerVolumeAdjustment()
    {
        var policy = new MuteOwnership(State(false, 0.3f));
        policy.MarkAppMute();
        policy.Observe(State(true, 0.8f), Guid.NewGuid());
        policy.Observe(State(true, 0.3f), policy.Context);
        Assert.True(policy.ShouldRestore(true));
        Assert.True(policy.Observe(State(false, 0.8f), Guid.NewGuid()));
        policy.MarkAppMute();
        policy.Observe(State(true, 0.8f), policy.Context);
        Assert.True(policy.ShouldRestore(true));
    }

    [Fact]
    public void NativeMuteCannotBeClaimedBeforeSubscribedInitialization()
    {
        var policy = new MuteOwnership();
        Assert.Throws<InvalidOperationException>(policy.MarkAppMute);
    }

    [Fact]
    public void UnmuteBetweenNativeVerificationAndConfirmationCannotBeErased()
    {
        var policy = new MuteOwnership(State(true, 0.3f));
        policy.MarkAppMute();
        var revision = policy.ObservationRevision;
        policy.Observe(State(false, 0.3f), Guid.NewGuid());
        policy.ConfirmLocalMute(revision);
        Assert.True(policy.TakeRemuteRequest());
        policy.MarkAppMute();
        policy.Observe(State(true, 0.3f), policy.Context);
        Assert.True(policy.ShouldRestore(true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulInitialSuppressionOwnsARacingUnmuteEvenWithoutAnotherAppWrite(bool acknowledgement)
    {
        var policy = new MuteOwnership(State(true, 0.3f));
        policy.MarkAppMute();
        policy.Observe(State(false, 0.3f), Guid.NewGuid());
        if (acknowledgement) policy.Observe(State(true, 0.3f), policy.Context);
        policy.ConfirmLocalMute(policy.ObservationRevision);
        Assert.True(policy.ShouldRestore(true));
        Assert.True(policy.TakeRemuteRequest());
        policy.ConfirmLocalMute(policy.ObservationRevision);
        Assert.True(policy.ShouldRestore(true));
        policy.Observe(State(true, 0.3f), Guid.NewGuid());
        Assert.False(policy.ShouldRestore(true));
    }
}
