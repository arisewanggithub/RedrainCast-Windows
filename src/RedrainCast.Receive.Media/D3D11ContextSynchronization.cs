using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace RedrainCast.Receive.Media;

internal static unsafe class D3D11ContextSynchronization
{
    // These callbacks outlive any decoder: AVFrames can retain the hardware device context.
    private static readonly AVD3D11VADeviceContext_lock EnterCallback = Enter;
    private static readonly AVD3D11VADeviceContext_unlock LeaveCallback = Leave;
    private static readonly AVHWDeviceContext_free FreeCallback = Free;

    internal static void Attach(AVHWDeviceContext* hardware, ID3D11Device device)
    {
        var multithread = device.ImmediateContext.QueryInterface<ID3D11Multithread>();
        GCHandle handle;
        try { handle = GCHandle.Alloc(multithread); }
        catch { multithread.Dispose(); throw; }
        hardware->user_opaque = (void*)GCHandle.ToIntPtr(handle);
        hardware->free = FreeCallback;
        var d3d = (AVD3D11VADeviceContext*)hardware->hwctx;
        d3d->lock_ctx = hardware->user_opaque;
        d3d->@lock = EnterCallback;
        d3d->unlock = LeaveCallback;
    }

    private static ID3D11Multithread Target(void* value) =>
        (ID3D11Multithread)GCHandle.FromIntPtr((nint)value).Target!;
    private static void Enter(void* value) => Target(value).Enter();
    private static void Leave(void* value) => Target(value).Leave();
    private static void Free(AVHWDeviceContext* hardware)
    {
        var handle = GCHandle.FromIntPtr((nint)hardware->user_opaque);
        ((ID3D11Multithread)handle.Target!).Dispose();
        handle.Free();
        hardware->user_opaque = null;
    }
}
