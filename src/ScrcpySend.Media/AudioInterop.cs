using System.Runtime.InteropServices;

namespace ScrcpySend.Media;

[ComVisible(true), Guid("41d949ab-9862-444a-80f6-c261334da5eb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioActivationCompletion
{
    [PreserveSig] int ActivateCompleted(nint operation);
}

[ComVisible(true), Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioAgileCallback { }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class AudioActivationCompletion : IAudioActivationCompletion, IAudioAgileCallback
{
    internal TaskCompletionSource<nint> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public unsafe int ActivateCompleted(nint operation)
    {
        nint pointer = 0;
        try
        {
            int activationResult;
            var get = (delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)(*(nint**)operation)[3];
            Marshal.ThrowExceptionForHR(get(operation, &activationResult, &pointer));
            Marshal.ThrowExceptionForHR(activationResult);
            Completion.SetResult(pointer);
            pointer = 0;
        }
        catch (Exception error) { Completion.TrySetException(error); }
        finally { if (pointer != 0) Marshal.Release(pointer); }
        return 0;
    }
}

internal sealed unsafe class AudioCom(nint pointer) : IDisposable
{
    public nint Pointer { get; private set; } = pointer;
    public nint Method(int index)
    {
        ObjectDisposedException.ThrowIf(Pointer == 0, this);
        return (*(nint**)Pointer)[index];
    }
    public void Dispose()
    {
        if (Pointer == 0) return;
        Marshal.Release(Pointer);
        Pointer = 0;
    }
}

internal static partial class AudioInterop
{
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoIncrementMTAUsage(out nint cookie);

    [LibraryImport("ole32.dll")]
    private static partial int CoDecrementMTAUsage(nint cookie);

    internal sealed class MtaLease : IDisposable
    {
        private nint cookie;
        public MtaLease() => Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out cookie));
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref cookie, 0);
            if (owned != 0) Marshal.ThrowExceptionForHR(CoDecrementMTAUsage(owned));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParameters { public int Type; public uint ProcessId; public int Mode; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public nint Data;
    }

    internal sealed class AudioApartment : IDisposable
    {
        private readonly bool initialized;
        public AudioApartment()
        {
            var result = AudioInterop.CoInitializeEx(0, 0);
            if (result != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(result);
            initialized = result >= 0;
        }
        public void Dispose() { if (initialized) AudioInterop.CoUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort Format, Channels;
        public uint SampleRate, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateAudioInterfaceAsync(string path, in Guid iid, in PropVariant parameters,
        nint completion, out nint operation);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    public static async Task<AudioCom> ActivateLoopbackAsync(CancellationToken token)
    {
        var activation = new ActivationParameters { Type = 1, ProcessId = (uint)Environment.ProcessId, Mode = 1 };
        var memory = Marshal.AllocCoTaskMem(Marshal.SizeOf<ActivationParameters>());
        var callback = new AudioActivationCompletion();
        var callbackPointer = Marshal.GetComInterfaceForObject(callback, typeof(IAudioActivationCompletion));
        nint operation = 0;
        var deferredCleanup = false;
        try
        {
            Marshal.StructureToPtr(activation, memory, false);
            var parameters = new PropVariant { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = memory };
            var iid = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in iid,
                in parameters, callbackPointer, out operation));
            try { return new AudioCom(await callback.Completion.Task.WaitAsync(token)); }
            catch (OperationCanceledException)
            {
                deferredCleanup = true;
                _ = callback.Completion.Task.ContinueWith(completed =>
                {
                    try
                    {
                        if (completed.IsCompletedSuccessfully) Marshal.Release(completed.Result);
                        else _ = completed.Exception;
                    }
                    finally
                    {
                        if (operation != 0) Marshal.Release(operation);
                        Marshal.Release(callbackPointer);
                        Marshal.FreeCoTaskMem(memory);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
        }
        finally
        {
            if (!deferredCleanup)
            {
                if (operation != 0) Marshal.Release(operation);
                Marshal.Release(callbackPointer);
                Marshal.FreeCoTaskMem(memory);
            }
            GC.KeepAlive(callback);
        }
    }

    public static unsafe AudioCom InitializeCapture(AudioCom client, EventWaitHandle ready)
    {
        var format = new WaveFormat { Format = 1, Channels = 2, SampleRate = 48000, BytesPerSecond = 192000, BlockAlign = 4, BitsPerSample = 16 };
        var initialize = (delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, nint, int>)client.Method(3);
        Marshal.ThrowExceptionForHR(initialize(client.Pointer, 0, 0x80060000, 0, 0, &format, 0));
        var setEvent = (delegate* unmanaged[Stdcall]<nint, nint, int>)client.Method(13);
        Marshal.ThrowExceptionForHR(setEvent(client.Pointer, ready.SafeWaitHandle.DangerousGetHandle()));
        var iid = new Guid("c8adbd64-e71e-48a0-a4de-185c395cd317");
        nint capture;
        var service = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)client.Method(14);
        Marshal.ThrowExceptionForHR(service(client.Pointer, &iid, &capture));
        return new AudioCom(capture);
    }

    public static unsafe void Start(AudioCom client) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)client.Method(10))(client.Pointer));

    public static unsafe void Stop(AudioCom client) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)client.Method(11))(client.Pointer));

    public static unsafe uint PacketSize(AudioCom capture)
    {
        uint count;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)capture.Method(5))(capture.Pointer, &count));
        return count;
    }

    public static unsafe ScrcpySend.Core.AudioPacket ReadPacket(AudioCom capture, bool first)
    {
        nint data;
        uint frames, flags;
        ulong position, qpc;
        var get = (delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, ulong*, ulong*, int>)capture.Method(3);
        Marshal.ThrowExceptionForHR(get(capture.Pointer, &data, &frames, &flags, &position, &qpc));
        try
        {
            if (frames is 0 or > 65536 || qpc > long.MaxValue || (flags & 4) != 0)
                throw new IOException("AUDIO_FAILED: invalid WASAPI packet or capture timestamp.");
            if (!first && (flags & 1) != 0) throw new IOException("AUDIO_FAILED: WASAPI capture discontinuity.");
            var bytes = new byte[frames * 4];
            if ((flags & 2) == 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            return new(bytes, (long)(qpc / 10));
        }
        finally
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)capture.Method(4))(capture.Pointer, frames));
        }
    }
}
