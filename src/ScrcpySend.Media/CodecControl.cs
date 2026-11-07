using System.Runtime.InteropServices;
using ScrcpySend.Core;
using Vortice.MediaFoundation;

namespace ScrcpySend.Media;

internal sealed unsafe class CodecControl : IDisposable
{
    private nint pointer;
    private static readonly Guid InterfaceId = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public short Boolean;
    }

    public CodecControl(IMFTransform transform)
    {
        var iid = InterfaceId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(transform.NativePointer, in iid, out pointer));
    }

    public void Configure(uint vendorId)
    {
        Set(LowLatency, new Variant { Type = 11, Boolean = -1 });
        foreach (var setting in EncoderCompatibility.ForVendor(vendorId))
            Set(setting.Property, new Variant { Type = 19, UInt32 = setting.Value });
    }

    public void RequestKeyFrame() => Set(ForceKeyFrame, new Variant { Type = 19, UInt32 = 1 });
    private void Set(Guid key, Variant value)
    {
        // ICodecAPI::SetValue is slot 9; VARIANT is 24 bytes on ARM64 and x64.
        var set = (delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)(*(nint**)pointer)[9];
        var result = set(pointer, &key, &value);
        if (result < 0) throw new COMException($"Hardware codec property {key} rejected variant type {value.Type}, " +
            $"value={(value.Type == 11 ? value.Boolean.ToString() : value.UInt32.ToString())} (0x{result:X8}).", result);
    }

    public void Dispose()
    {
        if (pointer == 0) return;
        Marshal.Release(pointer);
        pointer = 0;
    }
}
