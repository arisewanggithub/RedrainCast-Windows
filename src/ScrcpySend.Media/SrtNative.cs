using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

internal sealed partial class SrtNative : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListenCallback(nint opaque, int socket, int version, nint peer, nint streamId);
    private const string Library = "srt";
    [LibraryImport(Library)] private static partial int srt_startup();
    [LibraryImport(Library)] private static partial int srt_cleanup();
    [LibraryImport(Library)] private static partial int srt_create_socket();
    [LibraryImport(Library)] private static partial int srt_close(int socket);
    [LibraryImport(Library)] private static partial int srt_setsockflag(int socket, int option, byte[] value, int size);
    [LibraryImport(Library)] private static partial int srt_bind(int socket, byte[] address, int length);
    [LibraryImport(Library)] private static partial int srt_getsockname(int socket, byte[] address, ref int length);
    [LibraryImport(Library)] private static partial int srt_listen(int socket, int backlog);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int srt_listen_callback(int socket, ListenCallback callback, nint opaque);
    [LibraryImport(Library)] private static partial int srt_accept(int socket, nint address, nint length);
    [LibraryImport(Library)] private static partial int srt_sendmsg(int socket, byte[] bytes, int length, int ttl, int inOrder);
    [LibraryImport(Library)] private static partial int srt_connect(int socket, byte[] address, int length);
    [LibraryImport(Library)] private static partial int srt_recvmsg(int socket, byte[] bytes, int length);
    [LibraryImport(Library)] private static partial nint srt_getlasterror_str();
    [LibraryImport(Library)] private static partial int srt_getlasterror(nint errno);
    private static readonly object StartupGate = new();
    private static bool initialized;
    private int listener = -1, peerSocket = -1;
    private int disposed;
    private readonly ListenCallback callback;
    private readonly IPAddress peer;
    private readonly string streamId;

    private static void Initialize()
    {
        lock (StartupGate)
        {
            if (initialized) return;
            NativeLibrary.SetDllImportResolver(typeof(SrtNative).Assembly, (name, _, _) =>
                name == Library ? NativeLibrary.Load(Path.Combine(NativeArchitecture.LibraryDirectory, "srt.dll")) : 0);
            Check(srt_startup(), "Initialize libsrt");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => srt_cleanup();
            initialized = true;
        }
    }

    public SrtNative(IPAddress local, IPAddress peer, long session)
    {
        Initialize();
        this.peer = peer;
        streamId = $"sc-{session:x16}";
        callback = ValidatePeer;
        try
        {
            listener = srt_create_socket();
            Check(listener, "Create SRT listener");
            Set(50, 0); // SRTT_LIVE
            Set(1, 0); Set(2, 0); // cancellable nonblocking I/O
            Set(23, 100); Set(31, 0); Set(49, 1316);
            Set(5, 1024 * 1024);
            Check(srt_setsockflag(listener, 7, new byte[4], 4), "Disable Windows SRT close linger");
            var address = SocketAddress(local);
            Check(srt_bind(listener, address, address.Length), "Bind interface-owned SRT listener");
            Check(srt_listen_callback(listener, callback, 0), "Pin SRT peer/session");
            Check(srt_listen(listener, 1), "Listen for SRT receiver");
            var size = address.Length;
            Check(srt_getsockname(listener, address, ref size), "Query SRT listener port");
            var port = address[2] << 8 | address[3];
            Descriptor = new($"srt://{(local.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{local}]" : local)}:{port}", streamId);
        }
        catch { Dispose(); throw; }
    }

    public SrtDescriptor Descriptor { get; }

    private void Set(int option, int value) => Check(srt_setsockflag(listener, option, BitConverter.GetBytes(value), 4), $"Set SRT option {option}");

    private static byte[] SocketAddress(IPAddress ip)
    {
        var address = new IPEndPoint(ip, 0).Serialize();
        var bytes = new byte[address.Size];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = address[index];
        return bytes;
    }

    private int ValidatePeer(nint opaque, int socket, int version, nint address, nint id)
    {
        if (address == 0 || id == 0) return -1;
        var family = Marshal.ReadInt16(address);
        var size = family == (short)AddressFamily.InterNetwork ? 16 :
            family == (short)AddressFamily.InterNetworkV6 ? 28 : 0;
        if (size == 0) return -1;
        var bytes = new byte[size];
        Marshal.Copy(address, bytes, 0, size);
        var supplied = family == (short)AddressFamily.InterNetwork
            ? new IPAddress(bytes.AsSpan(4, 4))
            : new IPAddress(bytes.AsSpan(8, 16), BitConverter.ToUInt32(bytes, 24));
        var suppliedId = Marshal.PtrToStringUTF8(id);
        return supplied.Equals(peer) && suppliedId == streamId ? 0 : -1;
    }

    public async Task AcceptAsync(CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var socket = srt_accept(Volatile.Read(ref listener), 0, 0);
            if (socket >= 0)
            {
                if (Interlocked.CompareExchange(ref peerSocket, socket, -1) != -1)
                {
                    srt_close(socket);
                    throw new InvalidOperationException("SRT peer already accepted.");
                }
                if (Volatile.Read(ref disposed) != 0)
                {
                    socket = Interlocked.Exchange(ref peerSocket, -1);
                    if (socket >= 0) srt_close(socket);
                    token.ThrowIfCancellationRequested();
                    throw new ObjectDisposedException(nameof(SrtNative));
                }
                return;
            }
            var error = srt_getlasterror(0);
            if (error != 6002) Check(-1, "Accept SRT peer");
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10)) throw new TimeoutException("SRT receiver did not connect in ten seconds.");
            await Task.Delay(10, token);
        }
    }

    public void Write(byte[] data, CancellationToken token)
    {
        var progress = Stopwatch.GetTimestamp();
        for (var offset = 0; offset < data.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(1316, data.Length - offset);
            var chunk = data.AsSpan(offset, count).ToArray();
            var sent = srt_sendmsg(Volatile.Read(ref peerSocket), chunk, chunk.Length, -1, 1);
            if (sent > 0)
            {
                if (sent != count) throw new IOException("SRT message write was incomplete.");
                offset += sent;
                progress = Stopwatch.GetTimestamp();
            }
            else
            {
                if (srt_getlasterror(0) != 6001) Check(-1, "Write SRT media");
                if (Stopwatch.GetElapsedTime(progress) > TimeSpan.FromSeconds(1))
                    throw new TimeoutException("SRT media made no write progress for one second.");
                token.WaitHandle.WaitOne(2);
            }
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation}: {Marshal.PtrToStringUTF8(srt_getlasterror_str())}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var socket = Interlocked.Exchange(ref peerSocket, -1);
        if (socket >= 0) srt_close(socket);
        socket = Interlocked.Exchange(ref listener, -1);
        if (socket >= 0) srt_close(socket);
        GC.KeepAlive(callback);
    }

    internal static async Task ReceiveProbeAsync(SrtDescriptor descriptor, Action<byte[]> receive, CancellationToken token)
    {
        Initialize();
        var url = new Uri(descriptor.Url);
        if (!IPAddress.TryParse(url.Host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("SRT diagnostics may only connect to localhost.");
        var socket = srt_create_socket();
        Check(socket, "Create diagnostic SRT caller");
        try
        {
            void Set(int option, int value) => Check(srt_setsockflag(socket, option, BitConverter.GetBytes(value), 4), "Set caller option");
            Set(50, 0); Set(23, 100); Set(31, 0); Set(49, 1316);
            var id = System.Text.Encoding.UTF8.GetBytes(descriptor.StreamId);
            Check(srt_setsockflag(socket, 46, id, id.Length), "Set SRT stream routing");
            var serialized = new IPEndPoint(address, url.Port).Serialize();
            var bytes = new byte[serialized.Size];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = serialized[index];
            Set(36, 1000); // bounded connect timeout
            Check(srt_connect(socket, bytes, bytes.Length), "Connect SRT caller");
            Set(2, 0);
            var buffer = new byte[1316];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var size = srt_recvmsg(socket, buffer, buffer.Length);
                if (size > 0) receive(buffer.AsSpan(0, size).ToArray());
                else
                {
                    if (srt_getlasterror(0) != 6002) Check(-1, "Receive SRT probe");
                    await Task.Delay(2, token);
                }
            }
        }
        finally { srt_close(socket); }
    }

    internal static async Task CheckAcceptCancellationAsync(CancellationToken token)
    {
        using var listener = new SrtNative(IPAddress.Loopback, IPAddress.Loopback, 9);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(50);
        var started = Stopwatch.GetTimestamp();
        try { await listener.AcceptAsync(stop.Token); throw new IOException("SRT accepted without a caller."); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(1))
            throw new IOException("Native SRT accept did not cancel within one second.");
    }
}
