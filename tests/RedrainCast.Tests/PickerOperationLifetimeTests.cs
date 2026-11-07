using RedrainCast.App;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PickerOperationLifetimeTests
{
    [Fact]
    public async Task CancellationWaitsForNativeCompletionAndDiscardsLateSelection()
    {
        using var stop = new CancellationTokenSource();
        var native = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancels = 0;
        var closes = 0;
        var selection = PickerOperationLifetime.AwaitAsync(native.Task, () => cancels++, () =>
        {
            Assert.True(native.Task.IsCompleted);
            closes++;
        }, stop.Token);
        stop.Cancel();
        Assert.Equal(1, cancels);
        Assert.False(selection.IsCompleted);
        Assert.Equal(0, closes);
        native.SetResult("Late selected source");
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(stop.Token, failure.CancellationToken);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task AlreadyCancelledTokenStillOwnsOperationUntilItTerminates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var native = new TaskCompletionSource<string>();
        var cancels = 0;
        var closed = false;
        var selection = PickerOperationLifetime.AwaitAsync(native.Task, () => cancels++, () => closed = true, stop.Token);
        Assert.Equal(1, cancels);
        Assert.False(selection.IsCompleted);
        Assert.False(closed);
        native.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.True(closed);
    }

    [Fact]
    public async Task NativeCancelFailureDoesNotEscapeTokenCallbackOrCloseEarly()
    {
        using var stop = new CancellationTokenSource();
        var native = new TaskCompletionSource<string>();
        var closed = false;
        var denied = new InvalidOperationException("Native cancellation request failed.");
        var selection = PickerOperationLifetime.AwaitAsync(native.Task, () => throw denied, () => closed = true, stop.Token);
        stop.Cancel();
        Assert.False(selection.IsCompleted);
        Assert.False(closed);
        native.SetResult("Ignored");
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Same(denied, failure.InnerException);
        Assert.True(closed);
    }

    [Fact]
    public async Task SuccessfulResultClosesOnceAndUnregistersCancellation()
    {
        using var stop = new CancellationTokenSource();
        var cancels = 0;
        var closes = 0;
        Assert.Equal("Selected", await PickerOperationLifetime.AwaitAsync(Task.FromResult("Selected"),
            () => cancels++, () => closes++, stop.Token));
        stop.Cancel();
        Assert.Equal(0, cancels);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task NativeFailureRemainsObservableAndStillCloses()
    {
        var failure = new IOException("Native failure");
        var closed = false;
        var actual = await Assert.ThrowsAsync<IOException>(() => PickerOperationLifetime.AwaitAsync(
            Task.FromException<string>(failure), () => { }, () => closed = true, default));
        Assert.Same(failure, actual);
        Assert.True(closed);
    }

    [Fact]
    public async Task LateNativeFailureCannotMaskRequestedCancellation()
    {
        using var stop = new CancellationTokenSource();
        var native = new TaskCompletionSource<string>();
        var closed = false;
        var selection = PickerOperationLifetime.AwaitAsync(native.Task, () => { }, () => closed = true, stop.Token);
        stop.Cancel();
        var nativeFailure = new IOException("Native operation failed after cancellation.");
        native.SetException(nativeFailure);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Same(nativeFailure, actual.InnerException);
        Assert.Equal(stop.Token, actual.CancellationToken);
        Assert.True(closed);
    }
}
