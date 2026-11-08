using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class NativeSourcePickerTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void ClosedSelectionCannotBecomeAvailableAgainOrInvalidateAnotherSelection()
    {
        using var selection = new CaptureSelectionLifetime();
        using var other = new CaptureSelectionLifetime();
        selection.EnsureAvailable(true);
        selection.Invalidate();
        Assert.True(selection.Closed.IsCancellationRequested);
        Assert.Throws<IOException>(() => selection.EnsureAvailable(true));
        other.EnsureAvailable(true);
        Assert.False(other.IsClosed);
    }

    [Fact]
    public void MissingSourceIsLatchedAndCancelsPreparation()
    {
        using var selection = new CaptureSelectionLifetime();
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(selection.Closed);
        Assert.Throws<IOException>(() => selection.EnsureAvailable(false));
        Assert.True(preparation.IsCancellationRequested);
        Assert.Throws<IOException>(() => selection.EnsureAvailable(true));
    }

    [Fact]
    public void CleanupCancelsSelectionAndIsIdempotent()
    {
        var selection = new CaptureSelectionLifetime();
        var token = selection.Closed;
        selection.Dispose();
        selection.Dispose();
        selection.Invalidate();
        Assert.True(token.IsCancellationRequested);
        Assert.Throws<IOException>(() => selection.EnsureAvailable(true));
    }

    [Fact]
    public void SourceClosureAndOwnerCleanupMayRace()
    {
        for (var i = 0; i < 100; i++)
        {
            var selection = new CaptureSelectionLifetime();
            Parallel.Invoke(selection.Invalidate, selection.Dispose);
            Assert.True(selection.Closed.IsCancellationRequested);
        }
    }

    [Fact]
    public void NativePickerUsesOwnedWindowAndTheExactReturnedItemWithoutEnumeration()
    {
        var picker = Fixture("NativeSourcePicker");
        Assert.Contains("new GraphicsCapturePicker()", picker);
        Assert.Contains("InitializeWithWindow.Initialize(picker, owner)", picker);
        Assert.Contains("PickerOperationLifetime.AwaitAsync(operation.AsTask()", picker);
        Assert.DoesNotContain("operation.AsTask(cancellation)", picker);
        Assert.Contains("cancellation.ThrowIfCancellationRequested()", picker);
        Assert.Contains("CaptureSource.FromPickedItem(item)", picker);
        Assert.Contains("operation.Cancel, operation.Close, cancellation", picker);
        Assert.DoesNotContain("Enumerate", picker);
        Assert.DoesNotContain("DisplayName", picker);
        Assert.DoesNotContain("Size", picker);
        Assert.DoesNotContain("StartSending", picker);
        Assert.DoesNotContain("CaptureThumbnail", picker);
    }

    [Fact]
    public void PublicSdkGettersAreRuntimeQueriedAndNeverMatchTitlesOrSizes()
    {
        var interop = Fixture("CaptureInterop");
        Assert.Contains("38E4C48B-94E6-4C44-9CFA-968193316C0C", interop);
        Assert.Contains("33274D14-A076-4048-8416-747E9B04DB7B", interop);
        Assert.Contains("MarshalInspectable<Windows.Graphics.Capture.GraphicsCaptureItem>.FromManaged(item)", interop);
        Assert.Contains("Marshal.QueryInterface", interop);
        Assert.Contains("Marshal.Release(inspectable)", interop);
        Assert.Contains("if (result.HResult == unchecked((int)0x80004002)) return false", interop);
        var selection = interop[interop.IndexOf("internal static (nint Handle, bool IsWindow) ItemTarget", StringComparison.Ordinal)..];
        Assert.DoesNotContain("DisplayName", selection);
        Assert.DoesNotContain("Size.Width", selection);
        Assert.Contains("throw new NotSupportedException", selection);
        var source = Fixture("CaptureSource");
        Assert.Contains("new CaptureSource(item, target.Handle, target.IsWindow)", source);
        Assert.Contains("item.Closed += sourceClosed", source);
        Assert.Contains("Item.Closed -= sourceClosed", source);
        Assert.Contains("thread == target.ThreadId", source);
    }

    [Fact]
    public void WorkerCannotStartAfterOriginalSelectionCloses()
    {
        var worker = Fixture("WorkerVideoSource");
        Assert.Contains("SourceClosed => source.Closed", worker);
        Assert.Contains("protected override void ValidateSource()", worker);
        Assert.Contains("source.EnsureAvailable();", worker);
        Assert.Contains("source.WindowProcessId", worker);
        Assert.Contains("source.WindowThreadId", worker);
        Assert.Contains("var color = source.ReadColor(diagnostic);", worker);
        Assert.Contains("EncoderCompatibility.ValidateCapture(0, color.Hdr, settings.VideoCodec)", worker);
        var process = Fixture("ProcessVideoSource");
        Assert.Contains("CreateLinkedTokenSource(token, lifetime.Token, SourceClosed)", process);
        var ready = process.IndexOf("await readinessRead.WaitAsync", StringComparison.Ordinal);
        Assert.True(process.IndexOf("ValidateSource();", ready, StringComparison.Ordinal) > ready);
        var command = process.IndexOf("WriteLineAsync(\"start\"", StringComparison.Ordinal);
        Assert.Contains("ValidateSource();", process[(command - 100)..command]);
        var main = Fixture("MainWindow.xaml");
        Assert.Contains("pickerCancellation?.Cancel()", main);
        Assert.Contains("source?.Dispose()", main);
    }
}
