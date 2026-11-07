using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RedrainCast.Send.Media;

internal sealed class GpuMemoryDiagnostics : IDisposable
{
    private readonly IDXGIAdapter3? adapter;
    private readonly string adapterStatus;
    private readonly Process process = Process.GetCurrentProcess();
    public uint VendorId { get; }

    public GpuMemoryDiagnostics(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var source = dxgi.GetAdapter();
            VendorId = source.Description.VendorId;
            adapter = source.QueryInterface<IDXGIAdapter3>();
            adapterStatus = $"adapter={source.Description.Description}";
        }
        catch (SharpGenException error)
        {
            adapterStatus = $"GPU budget query unavailable: HRESULT=0x{unchecked((uint)error.ResultCode.Code):X8}";
        }
    }

    public void CheckBudget()
    {
        if (adapter is null) throw new NotSupportedException($"Capture requires GPU memory budget monitoring. {adapterStatus}");
        ulong total = 0;
        foreach (var segment in new[] { MemorySegmentGroup.Local, MemorySegmentGroup.NonLocal })
        {
            var memory = adapter.QueryVideoMemoryInfo(0, segment);
            total = checked(total + memory.CurrentUsage);
            if (memory.CurrentUsage > 0 && memory.CurrentUsage > memory.Budget - memory.Budget / 5)
                throw new IOException($"Capture exceeded 80% of its {segment} GPU budget: usage={memory.CurrentUsage}, budget={memory.Budget} bytes.");
        }
        if (total > 1024UL * 1024 * 1024)
            throw new IOException($"Capture exceeded its 1 GiB GPU-memory guard: {total} bytes.");
    }

    public string Snapshot()
    {
        string cpu;
        try
        {
            process.Refresh();
            cpu = $"processPrivateBytes={process.PrivateMemorySize64}, processWorkingSetBytes={process.WorkingSet64}, " +
                $"managedHeapBytes={GC.GetTotalMemory(false)}, gcCommittedBytes={GC.GetGCMemoryInfo().TotalCommittedBytes}, " +
                $"gcCollections={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cpu = $"Process memory query failed: {error.GetType().Name}: {error.Message}";
        }
        return $"{cpu}; {adapterStatus}; local=[{Read(MemorySegmentGroup.Local)}]; nonlocal=[{Read(MemorySegmentGroup.NonLocal)}]";
    }

    private string Read(MemorySegmentGroup segment)
    {
        if (adapter is null) return "unavailable";
        try
        {
            var memory = adapter.QueryVideoMemoryInfo(0, segment);
            return $"usageBytes={memory.CurrentUsage}, budgetBytes={memory.Budget}, " +
                $"reservationBytes={memory.CurrentReservation}, availableReservationBytes={memory.AvailableForReservation}";
        }
        catch (SharpGenException error)
        {
            return $"GPU memory query failed: HRESULT=0x{unchecked((uint)error.ResultCode.Code):X8}";
        }
    }

    public void Dispose()
    {
        adapter?.Dispose();
        process.Dispose();
    }
}
