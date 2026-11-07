using System.Runtime.InteropServices;
using RedrainCast.Send.Core;

namespace RedrainCast.Send.Media;

[ComVisible(true), Guid("657804fa-d6ad-4496-8a60-352752af4f89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRenderVolumeCallback
{
    [PreserveSig] int OnNotify(nint data);
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioPropertyKey { public Guid Format; public uint Id; }

[ComVisible(true), Guid("7991eec9-7e89-4d85-8390-6c703cec60c0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRenderDeviceCallback
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, AudioPropertyKey key);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RenderVolumeCallback(Action<AudioOutputState, Guid> changed, Action<Exception> fail) : IRenderVolumeCallback, IAudioAgileCallback
{
    public int OnNotify(nint data)
    {
        try
        {
            if (data == 0) throw new IOException("Windows supplied an empty output-volume notification.");
            var count = Marshal.ReadInt32(data, 24);
            if (count is < 1 or > 64) throw new IOException("Invalid output-volume channel count.");
            var channels = new float[count];
            for (var index = 0; index < count; index++) channels[index] = Marshal.PtrToStructure<float>(data + 28 + index * 4);
            changed(new(Marshal.ReadInt32(data, 16) != 0, Marshal.PtrToStructure<float>(data + 20), channels),
                Marshal.PtrToStructure<Guid>(data));
            return 0;
        }
        catch (Exception error) { fail(error); return unchecked((int)0x80004005); }
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RenderDeviceCallback(Action changed) : IRenderDeviceCallback, IAudioAgileCallback
{
    public int OnDeviceStateChanged(string id, uint state) { changed(); return 0; }
    public int OnDeviceAdded(string id) { changed(); return 0; }
    public int OnDeviceRemoved(string id) { changed(); return 0; }
    public int OnDefaultDeviceChanged(int flow, int role, string? id) { if (flow == 0) changed(); return 0; }
    public int OnPropertyValueChanged(string id, AudioPropertyKey key) => 0;
}

internal sealed unsafe class RenderMuteLease : IDisposable
{
    private sealed record Output(AudioCom Volume, MuteOwnership Ownership, RenderVolumeCallback Callback, nint CallbackPointer);
    private readonly AudioCom enumerator;
    private readonly AudioInterop.MtaLease mta;
    private readonly List<Output> outputs = [];
    private readonly Action<Exception> fail;
    private readonly RenderDeviceCallback topology;
    private nint topologyPointer;
    private bool disposed;
    private bool stopping;
    private readonly object lifecycleGate = new();
    private readonly AutoResetEvent changes = new(false);
    public WaitHandle Changes => changes;
    private int topologyChanged;

    public RenderMuteLease(Action<Exception> fail) : this(fail, true) { }

    private RenderMuteLease(Action<Exception> fail, bool muteOutputs)
    {
        using var apartment = new AudioInterop.AudioApartment();
        this.fail = fail;
        mta = new();
        var clsid = new Guid("bcde0395-e52f-467c-8e3d-c4579291692e");
        var iid = new Guid("a95664d2-9614-4f35-a746-de8db63617e6");
        var creation = AudioInterop.CoCreateInstance(in clsid, 0, 23, in iid, out var pointer);
        if (creation < 0) { changes.Dispose(); mta.Dispose(); Marshal.ThrowExceptionForHR(creation); }
        enumerator = new(pointer);
        topology = new(() =>
        {
            Interlocked.Exchange(ref topologyChanged, 1);
            fail(new IOException("AUDIO_FAILED: audio devices/routing changed. Stop and explicitly start a new cast."));
        });
        try
        {
            topologyPointer = Marshal.GetComInterfaceForObject(topology, typeof(IRenderDeviceCallback));
            Register(enumerator, 6, topologyPointer);
            nint collectionPointer;
            var enumerate = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)enumerator.Method(3);
            Marshal.ThrowExceptionForHR(enumerate(enumerator.Pointer, 0, 1, &collectionPointer));
            using var collection = new AudioCom(collectionPointer);
            uint count;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)collection.Method(3))(collection.Pointer, &count));
            if (count > 64) throw new NotSupportedException("Too many active audio outputs to own safely.");
            for (uint index = 0; index < count; index++)
            {
                nint devicePointer;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)collection.Method(4))(collection.Pointer, index, &devicePointer));
                using var device = new AudioCom(devicePointer);
                var volumeId = new Guid("5cdf2c82-841e-4546-9722-0cf74078229a");
                nint volumePointer;
                var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)device.Method(3);
                Marshal.ThrowExceptionForHR(activate(device.Pointer, &volumeId, 23, 0, &volumePointer));
                var volume = new AudioCom(volumePointer);
                nint callbackPointer = 0;
                var registered = false;
                try
                {
                    var ownership = new MuteOwnership();
                    var callback = new RenderVolumeCallback((state, context) =>
                    {
                        lock (lifecycleGate)
                        {
                            if (disposed) return;
                            if (ownership.Observe(state, context) && !stopping) changes.Set();
                        }
                    }, fail);
                    callbackPointer = Marshal.GetComInterfaceForObject(callback, typeof(IRenderVolumeCallback));
                    Register(volume, 3, callbackPointer);
                    registered = true;
                    ownership.Initialize(() => GetState(volume));
                    outputs.Add(new(volume, ownership, callback, callbackPointer));
                }
                catch (Exception setupError)
                {
                    try { if (registered) Register(volume, 4, callbackPointer); }
                    catch (Exception unsubscribeError) { throw new AggregateException("Audio snapshot setup and callback cleanup failed.", setupError, unsubscribeError); }
                    finally { if (callbackPointer != 0) Marshal.Release(callbackPointer); volume.Dispose(); }
                    throw;
                }
            }
            if (muteOutputs) foreach (var output in outputs)
            {
                if (Volatile.Read(ref topologyChanged) != 0) throw new IOException("Audio routing changed during setup.");
                output.Ownership.MarkAppMute();
                SetMute(output.Volume, true, output.Ownership.Context);
                var revision = output.Ownership.ObservationRevision;
                if (!GetMute(output.Volume)) throw new IOException("Windows did not silence an audio output.");
                output.Ownership.ConfirmLocalMute(revision);
            }
            if (Volatile.Read(ref topologyChanged) != 0) throw new IOException("Audio routing changed during setup.");
        }
        catch (Exception setupError)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException("Audio mute setup and restoration failed.", setupError, cleanupError); }
            throw;
        }
    }

    private static void Register(AudioCom owner, int method, nint callback) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)owner.Method(method))(owner.Pointer, callback));

    public static int ProbeBindings()
    {
        ProbeNotifications();
        Exception? changed = null;
        using var lease = new RenderMuteLease(error => Interlocked.CompareExchange(ref changed, error, null), false);
        if (changed != null) throw changed;
        return lease.outputs.Count;
    }

    private static void ProbeNotifications()
    {
        var policy = new MuteOwnership(new AudioOutputState(false, 0.3f, [0.3f, 0.3f]));
        policy.MarkAppMute();
        policy.ConfirmLocalMute(policy.ObservationRevision);
        Exception? error = null;
        var needsRemute = false;
        var callback = new RenderVolumeCallback((state, context) => needsRemute = policy.Observe(state, context),
            failed => error = failed);
        var data = Marshal.AllocCoTaskMem(36);
        try
        {
            Marshal.StructureToPtr(Guid.NewGuid(), data, false);
            Marshal.WriteInt32(data, 16, 1);
            Marshal.StructureToPtr(0.8f, data + 20, false);
            Marshal.WriteInt32(data, 24, 2);
            Marshal.StructureToPtr(0.8f, data + 28, false);
            Marshal.StructureToPtr(0.8f, data + 32, false);
            if (callback.OnNotify(data) != 0 || error != null || needsRemute || !policy.ShouldRestore(true))
                throw new IOException("Volume-only notification regression.", error);
            Marshal.WriteInt32(data, 16, 0);
            if (callback.OnNotify(data) != 0 || error != null || !needsRemute || !policy.TakeRemuteRequest())
                throw new IOException("Volume-triggered unmute notification regression.", error);
            policy.MarkAppMute();
            if (!policy.ShouldRestore(true)) throw new IOException("Latest mute restoration preference was lost.");
        }
        finally { Marshal.FreeCoTaskMem(data); }
    }

    private static bool GetMute(AudioCom volume)
    {
        int muted;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)volume.Method(15))(volume.Pointer, &muted));
        return muted != 0;
    }

    private static AudioOutputState GetState(AudioCom volume)
    {
        uint count;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)volume.Method(5))(volume.Pointer, &count));
        if (count is 0 or > 64) throw new IOException("Invalid Windows output channel count.");
        float master;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, float*, int>)volume.Method(9))(volume.Pointer, &master));
        var channels = new float[count];
        for (uint index = 0; index < count; index++)
        {
            float value;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, float*, int>)volume.Method(13))(volume.Pointer, index, &value));
            channels[index] = value;
        }
        return new(GetMute(volume), master, channels);
    }

    public void ApplyPendingMutes()
    {
        lock (lifecycleGate) { if (stopping || disposed) return; }
        var pending = outputs.Where(output => output.Ownership.TakeRemuteRequest()).ToArray();
        if (pending.Length == 0) return;
        using var apartment = new AudioInterop.AudioApartment();
        foreach (var output in pending)
        {
            var revision = output.Ownership.ObservationRevision;
            if (!GetMute(output.Volume))
            {
                output.Ownership.MarkAppMute();
                SetMute(output.Volume, true, output.Ownership.Context);
                revision = output.Ownership.ObservationRevision;
                if (!GetMute(output.Volume)) throw new IOException("AUDIO_FAILED: Windows did not retain receiver-only playback.");
                output.Ownership.ConfirmLocalMute(revision);
            }
            else output.Ownership.ConfirmLocalMute(revision);
        }
    }

    private static void SetMute(AudioCom volume, bool muted, Guid context) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)volume.Method(14))(volume.Pointer, muted ? 1 : 0, &context));

    public void Dispose()
    {
        lock (lifecycleGate) { if (stopping || disposed) return; stopping = true; }
        using var apartment = new AudioInterop.AudioApartment();
        var failures = new List<Exception>();
        if (topologyPointer != 0)
        {
            try { Register(enumerator, 7, topologyPointer); }
            catch (Exception error) { failures.Add(error); }
            Marshal.Release(topologyPointer);
            topologyPointer = 0;
        }
        foreach (var output in outputs.AsEnumerable().Reverse())
        {
            try
            {
                if (output.Ownership.ShouldRestore(GetMute(output.Volume)))
                    SetMute(output.Volume, false, output.Ownership.Context);
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                try { Register(output.Volume, 4, output.CallbackPointer); }
                catch (Exception error) { failures.Add(error); }
                Marshal.Release(output.CallbackPointer);
                output.Volume.Dispose();
                GC.KeepAlive(output.Callback);
            }
        }
        enumerator.Dispose();
        lock (lifecycleGate) { disposed = true; changes.Dispose(); }
        mta.Dispose();
        GC.KeepAlive(topology);
        if (failures.Count > 0) throw new AggregateException("Audio restoration failed; check Windows output mute settings.", failures);
    }
}
