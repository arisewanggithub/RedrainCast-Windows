using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace ScrcpySend.Media;

internal sealed class GpuSurfacePool : IAsyncDisposable
{
    private sealed class Slot
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ReturnedCallback Callback { get; init; }
        public Lease? Active { get; set; }
    }

    private sealed class ReturnedCallback(Action returned, Action<Exception> failed) : CallbackBase, IMFAsyncCallback
    {
        public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
        {
            flags = 0;
            queue = 0;
            return new(unchecked((int)0x80004001));
        }

        public Result Invoke(IMFAsyncResult result)
        {
            try { returned(); return Result.Ok; }
            catch (Exception error) { failed(error); return new(unchecked((int)0x80004005)); }
        }
    }

    internal sealed class Lease(GpuSurfacePool owner, int index, ID3D11Texture2D texture) : IDisposable
    {
        private bool tracked;
        public ID3D11Texture2D Texture { get; } = texture;

        public void Track(IMFTrackedSample sample)
        {
            sample.SetAllocator(owner.slots[index].Callback, null!);
            tracked = true;
        }

        public void Dispose()
        {
            if (!tracked) owner.Return(index, this);
        }
    }

    private readonly object gate = new();
    private readonly ID3D11Device device;
    private readonly int width, height, capacity;
    private readonly List<Slot> slots = [];
    private readonly Queue<int> available = new();
    private readonly Action<string> diagnostic;
    private TaskCompletionSource allReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? failure;
    private bool disposed;
    private int inUse, maximumInUse;
    private long recycled;
    public long RecycledSamples { get { lock (gate) return recycled; } }
    private Task? shutdown;
    public string Diagnostics
    {
        get { lock (gate) return $"GPU surface pool allocated={slots.Count}/{capacity}, inUse={inUse}, peakInUse={maximumInUse}, recycled={recycled}, nominalBytesPerSurface={checked((long)width * height * 3 / 2)}"; }
    }

    public GpuSurfacePool(ID3D11Device device, int width, int height, Action<string> diagnostic)
    {
        this.device = device;
        this.width = width;
        this.height = height;
        this.diagnostic = diagnostic;
        capacity = (int)Math.Clamp(128L * 1024 * 1024 / checked((long)width * height * 3 / 2), 1, 32);
        allReturned.SetResult();
    }

    public Lease? TryRent()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null) throw new IOException("GPU surface recycling failed.", failure);
            int index;
            if (available.TryDequeue(out var ready)) index = ready;
            else
            {
                if (slots.Count >= capacity) return null;
                index = slots.Count;
                var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height,
                    1, 1, BindFlags.RenderTarget));
                try
                {
                    var slotIndex = index;
                    slots.Add(new Slot
                    {
                        Texture = texture,
                        Callback = new ReturnedCallback(() => ReturnTracked(slotIndex), error =>
                        {
                            lock (gate) failure ??= error;
                            diagnostic($"GPU surface recycling failed: {error}");
                        })
                    });
                }
                catch { texture.Dispose(); throw; }
            }
            if (inUse++ == 0) allReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            maximumInUse = Math.Max(maximumInUse, inUse);
            return slots[index].Active = new Lease(this, index, slots[index].Texture);
        }
    }

    private void ReturnTracked(int index)
    {
        lock (gate)
        {
            var active = slots[index].Active ?? throw new InvalidOperationException("GPU surface callback has no active input.");
            Return(index, active);
            recycled++;
        }
    }

    private void Return(int index, Lease lease)
    {
        lock (gate)
        {
            if (!ReferenceEquals(slots[index].Active, lease)) return;
            slots[index].Active = null;
            if (!disposed) available.Enqueue(index);
            if (--inUse == 0) allReturned.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(shutdown ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        Task returned;
        lock (gate) { disposed = true; returned = allReturned.Task; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await returned.WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            diagnostic($"GPU encoder retained inputs after shutdown: {Diagnostics}. Worker termination must release remaining native resources.");
        }
        lock (gate)
        {
            diagnostic($"GPU surface pool stopped: {Diagnostics}");
            foreach (var slot in slots)
            {
                if (slot.Active is null) slot.Callback.Dispose();
                slot.Texture.Dispose();
            }
        }
    }
}
