namespace RedrainCast.Send.Core;

public sealed record WindowPlacement(int X, int Y, int Width, int Height, int MinimumWidth, int MinimumHeight);

public static class WindowSizingPolicy
{
    public static WindowPlacement Create(uint dpi, int workX, int workY, int workWidth, int workHeight,
        int frameWidth, int frameHeight)
    {
        if (dpi == 0 || workWidth <= 0 || workHeight <= 0 || frameWidth < 0 || frameHeight < 0)
            throw new ArgumentException("Invalid monitor DPI, work area or window frame dimensions.");
        var availableWidth = Math.Max(1, (int)(workWidth * 0.9));
        var availableHeight = Math.Max(1, (int)(workHeight * 0.9));
        int Width(int dips) => (int)Math.Min(availableWidth, Math.Ceiling(dips * (double)dpi / 96) + frameWidth);
        int Height(int dips) => (int)Math.Min(availableHeight, Math.Ceiling(dips * (double)dpi / 96) + frameHeight);
        var minimumWidth = Width(640);
        var minimumHeight = Height(480);
        var width = Math.Max(minimumWidth, Width(900));
        var height = Math.Max(minimumHeight, Height(800));
        return new(checked(workX + (workWidth - width) / 2), checked(workY + (workHeight - height) / 2),
            width, height, minimumWidth, minimumHeight);
    }
}
