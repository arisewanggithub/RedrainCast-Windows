using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoReadDiagnosticTests
{
    [Fact]
    public void ProductionHookIsVideoOnlyAndAdmissionFinallyCoversFilteringPressureAndCancellation()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ReceiverServer.cs.txt"));
        Assert.Contains("channel.mediaTrack == 1 ? playbackForMedia?.VideoReadDiagnostics : null", source);
        Assert.Contains("Interlocked.Increment(ref nextVideoReadId)", source);
        Assert.Contains("channel.ReadAsync(linked.Token, diagnostic, currentReadId)", source);
        Assert.Contains("admission?.Record(\"admission_begin\")", source);
        Assert.Contains("admissionOutcome = \"recovery_filtered\"", source);
        Assert.Contains("admissionOutcome = \"recovery_pressure\"", source);
        Assert.Contains("else await queue.AddAsync(packet, linked.Token);", source);
        Assert.Contains("admissionOutcome = \"cancelled\"", source);
        Assert.Contains("admission?.Record(\"admission_end\", outcome: admissionOutcome)", source);
        Assert.True(source.IndexOf("admission?.Record(\"admission_begin\")", StringComparison.Ordinal) <
            source.IndexOf("else await queue.AddAsync(packet, linked.Token);", StringComparison.Ordinal));
    }

    private static async Task<byte[]> Bytes()
    {
        using var output = new MemoryStream();
        await Wire.WriteAsync(output, new(PacketType.VideoFrame, 123, 1, 7, 90001, 0, [1, 2, 3, 4]));
        return output.ToArray();
    }

    [Fact]
    public async Task FragmentedSynchronousBacklogHasBoundedStagesAndHeaderIdentity()
    {
        using var stream = new Fragmented(await Bytes());
        var observations = new List<VideoReadObservation>();
        var packet = await Wire.ReadAsync(stream, diagnostic: observations.Add, readId: 42);
        Assert.Equal(90001, packet.PtsUs);
        Assert.Equal(["read_begin", "header_begin", "header_end", "payload_begin", "payload_end", "read_end"],
            observations.Select(x => x.Stage));
        Assert.All(observations, x => Assert.Equal(42, x.ReadId));
        Assert.Equal(-1, observations[0].PtsUs);
        Assert.All(observations.Skip(2), x => { Assert.Equal(7u, x.Generation); Assert.Equal(90001, x.PtsUs); });
        Assert.Equal(3, observations[2].SyncReads);
        Assert.Equal(0, observations[2].AsyncReads);
        Assert.Equal(2, observations[4].SyncReads);
        Assert.Equal("complete", observations[^1].Outcome);
        Assert.True(observations.Zip(observations.Skip(1)).All(pair => pair.First.Qpc <= pair.Second.Qpc));
    }

    [Theory]
    [InlineData(0, "header")]
    [InlineData(40, "payload")]
    public async Task ControlledAsynchronousReadRecordsAwaitWithoutPerChunkEvents(int pauseAt, string phase)
    {
        using var stream = new Fragmented(await Bytes(), pauseAt);
        var observations = new List<VideoReadObservation>();
        var read = Wire.ReadAsync(stream, diagnostic: observations.Add, readId: 1).AsTask();
        Assert.False(read.IsCompleted);
        stream.Release();
        await read;
        var end = Assert.Single(observations, x => x.Stage == phase + "_end");
        Assert.Equal(1, end.AsyncReads);
        Assert.Equal(6, observations.Count);
    }

    [Theory]
    [InlineData(0, "header:eof")]
    [InlineData(41, "payload:eof")]
    public async Task EofHasFailureNotFakeCompletion(int length, string outcome)
    {
        var bytes = await Bytes();
        using var stream = new MemoryStream(bytes[..length]);
        var observations = new List<VideoReadObservation>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Wire.ReadAsync(stream, diagnostic: observations.Add).AsTask());
        Assert.Equal(outcome, observations[^1].Outcome);
        Assert.DoesNotContain(observations, x => x.Outcome == "complete");
    }

    [Fact]
    public async Task InvalidHeaderRetainsProtocolErrorAndNoPayloadStages()
    {
        var bytes = await Bytes();
        bytes[0] = 0;
        using var stream = new MemoryStream(bytes);
        var observations = new List<VideoReadObservation>();
        await Assert.ThrowsAsync<ProtocolException>(() =>
            Wire.ReadAsync(stream, diagnostic: observations.Add).AsTask());
        Assert.Equal("header:invalid_or_io", observations[^1].Outcome);
        Assert.DoesNotContain(observations, x => x.Stage == "payload_begin");
    }

    [Fact]
    public async Task CancellationPreservesCancellationAndNoFakeComplete()
    {
        using var stream = new Fragmented(await Bytes(), 40);
        using var cancellation = new CancellationTokenSource();
        var observations = new List<VideoReadObservation>();
        var read = Wire.ReadAsync(stream, cancellation.Token, diagnostic: observations.Add).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal("payload:cancelled", observations[^1].Outcome);
    }

    [Fact]
    public async Task DisabledHookPreservesPayloadProgressAndWireData()
    {
        using var stream = new Fragmented(await Bytes());
        var progress = new List<int>();
        var packet = await Wire.ReadAsync(stream, payloadProgress: (_, count, _) => progress.Add(count));
        Assert.Equal([0, 2, 4], progress);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, packet.Payload);
    }

    [Fact]
    public async Task AdmissionScopeCanBracketBlockedWorkWithoutPrematureSuccess()
    {
        var observations = new List<VideoReadObservation>();
        var diagnostic = new VideoReadDiagnostic(8, observations.Add);
        diagnostic.Header(new(PacketType.VideoFrame, 123, 1, 9, 222, 0, []));
        diagnostic.Record("admission_begin");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Admission()
        {
            await release.Task;
            diagnostic.Record("admission_end", outcome: "recovery_pressure");
        }
        var admission = Admission();
        Assert.False(admission.IsCompleted);
        Assert.Single(observations);
        release.SetResult();
        await admission;
        Assert.Equal("recovery_pressure", observations[^1].Outcome);
        Assert.Equal(222, observations[^1].PtsUs);
    }

    private sealed class Fragmented(byte[] bytes, int pauseAt = -1) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool paused;
        public void Release() => release.SetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!paused && Position == pauseAt)
            {
                paused = true;
                return AwaitRead(buffer, cancellationToken);
            }
            return base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
        }
        private async ValueTask<int> AwaitRead(Memory<byte> buffer, CancellationToken token)
        {
            await release.Task.WaitAsync(token);
            return await base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], token);
        }
    }
}
