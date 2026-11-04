using System.Runtime.InteropServices;

namespace ScrcpySend.Core;

public static class NativeArchitecture
{
    public static string RuntimeIdentifier(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X64 => "win-x64",
        _ => throw new PlatformNotSupportedException("RedrainCast supports native ARM64 and x64 processes only.")
    };

    public static string RuntimeIdentifierForProcess => RuntimeIdentifier(RuntimeInformation.ProcessArchitecture);
    public static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "native", RuntimeIdentifierForProcess);
}
