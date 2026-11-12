namespace RedrainCast.Receive.Core;

public static class TrackThread
{
    internal static bool IsCurrent => RedrainCast.Media.MediaLaneThread.IsCurrent;
    public static Task Run(Func<Task> action, Func<IDisposable?>? register = null) =>
        RedrainCast.Media.MediaLaneThread.Run(action, register);
}
