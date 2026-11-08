using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace RedrainCast.Receive.Media;

public sealed partial record HdrDisplayPolicy
{
    public static HdrDisplayPolicy Query(nint hwnd) => ReadMetadata(() =>
    {
        try { return QueryMonitor(hwnd); }
        catch (SharpGenException error) when (IsMetadataQueryFailure(error.ResultCode.Code))
        {
            return MetadataUnavailable(error.Message);
        }
    });

    private static HdrDisplayPolicy QueryMonitor(nint hwnd)
    {
        if (hwnd == 0) throw new HdrDisplayMetadataException("The UI-owned display window is not available.");
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == 0) throw new HdrDisplayMetadataException("The window's current monitor could not be identified.");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; ; a++)
        {
            var adapterResult = factory.EnumAdapters1(a, out var adapter);
            if (adapterResult == ResultCode.NotFound) break;
            adapterResult.CheckError();
            using (adapter)
            {
                for (uint o = 0; ; o++)
                {
                    var outputResult = adapter.EnumOutputs(o, out var output);
                    if (outputResult == ResultCode.NotFound) break;
                    outputResult.CheckError();
                    using (output)
                    {
                        if (output.Description.Monitor != monitor) continue;
                        using var advanced = output.QueryInterface<IDXGIOutput6>();
                        var description = advanced.Description1;
                        if (description.ColorSpace == ColorSpaceType.RgbFullG22NoneP709) return Sdr;
                        if (description.ColorSpace != ColorSpaceType.RgbFullG2084NoneP2020)
                            throw new HdrDisplayMetadataException($"Unrecognized desktop output color space {description.ColorSpace}.");
                        return FromMonitor(true, description.MaxLuminance);
                    }
                }
            }
        }
        throw new HdrDisplayMetadataException("The window's current DXGI display could not be identified.");
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
}
