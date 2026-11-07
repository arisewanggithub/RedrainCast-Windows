namespace RedrainCast.Core;

public readonly record struct SourceIdentity(nint Handle, bool IsWindow);

public static class SourcePickerPolicy
{
    public const int MaximumVisiblePreviews = 6;

    public static bool CanSelect(bool cancelled, bool closing, CastRole role) =>
        !cancelled && !closing && role == CastRole.Idle;

    public static IEnumerable<T> Group<T>(IEnumerable<T> sources, Func<T, bool> isWindow, bool windows) =>
        sources.Where(source => isWindow(source) == windows);

    public static T[] VisiblePreviews<T>(IEnumerable<T> sources, Func<T, bool> visible, Func<T, bool> unavailable) =>
        sources.Where(source => visible(source) && !unavailable(source)).Take(MaximumVisiblePreviews).ToArray();
}
