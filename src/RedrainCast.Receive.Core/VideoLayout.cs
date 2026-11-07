namespace RedrainCast.Receive.Core;

public static class VideoLayout
{
    public static (int X, int Y, int Width, int Height) Fit(int width, int height, int rotation,
        int viewportWidth, int viewportHeight)
    {
        if (width <= 0 || height <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Video and viewport dimensions must be positive.");
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (rotation is 90 or 270) (width, height) = (height, width);
        var scale = Math.Min((double)viewportWidth / width, (double)viewportHeight / height);
        var fittedWidth = Math.Max(1, (int)(width * scale));
        var fittedHeight = Math.Max(1, (int)(height * scale));
        return ((viewportWidth - fittedWidth) / 2, (viewportHeight - fittedHeight) / 2, fittedWidth, fittedHeight);
    }
}
