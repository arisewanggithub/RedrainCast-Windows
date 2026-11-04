using ScrcpySend.Core;
using System.Xml.Linq;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class WindowSizingTests
{
    [Theory]
    [InlineData(96, 16, 39, 916, 839, 656, 519)]
    [InlineData(120, 20, 50, 1145, 1050, 820, 650)]
    [InlineData(144, 24, 58, 1374, 1258, 984, 778)]
    [InlineData(192, 32, 78, 1832, 1678, 1312, 1038)]
    public void LogicalLaunchAndMinimumSizesScaleToPhysicalPixels(uint dpi, int frameWidth, int frameHeight,
        int width, int height, int minimumWidth, int minimumHeight)
    {
        var placement = WindowSizingPolicy.Create(dpi, 0, 0, 4000, 3000, frameWidth, frameHeight);
        Assert.Equal(width, placement.Width);
        Assert.Equal(height, placement.Height);
        Assert.Equal(minimumWidth, placement.MinimumWidth);
        Assert.Equal(minimumHeight, placement.MinimumHeight);
        Assert.Equal((4000 - width) / 2, placement.X);
        Assert.Equal((3000 - height) / 2, placement.Y);
    }

    [Theory]
    [InlineData(96, 1024, 768)]
    [InlineData(144, 1920, 1080)]
    [InlineData(192, 1024, 768)]
    [InlineData(288, 1920, 1080)]
    [InlineData(384, 1280, 720)]
    public void HighDpiAndSmallWorkAreasNeverForceAnOversizedMinimum(uint dpi, int workWidth, int workHeight)
    {
        var placement = WindowSizingPolicy.Create(dpi, -workWidth, 60, workWidth, workHeight, 32, 78);
        Assert.InRange(placement.Width, 1, (int)(workWidth * 0.9));
        Assert.InRange(placement.Height, 1, (int)(workHeight * 0.9));
        Assert.InRange(placement.MinimumWidth, 1, placement.Width);
        Assert.InRange(placement.MinimumHeight, 1, placement.Height);
        Assert.True(placement.X >= -workWidth && placement.X + placement.Width <= 0);
        Assert.True(placement.Y >= 60 && placement.Y + placement.Height <= 60 + workHeight);
    }

    [Fact]
    public void InvalidMonitorMetricsAreReportedRatherThanSilentlyDefaulted()
    {
        Assert.Throws<ArgumentException>(() => WindowSizingPolicy.Create(0, 0, 0, 1920, 1080, 0, 0));
        Assert.Throws<ArgumentException>(() => WindowSizingPolicy.Create(96, 0, 0, 0, 1080, 0, 0));
        Assert.Throws<ArgumentException>(() => WindowSizingPolicy.Create(96, 0, 0, 1920, 1080, -1, 0));
    }

    [Fact]
    public void PathologicalScalingAndTinyWorkAreasStillProduceUsablePositiveBounds()
    {
        var tiny = WindowSizingPolicy.Create(uint.MaxValue, 0, 0, 1, 1, 99, 99);
        Assert.Equal(new WindowPlacement(0, 0, 1, 1, 1, 1), tiny);
    }

    [Fact]
    public void SessionControlsAndDetailsLiveOnlyInTheReceiverRow()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.DoesNotContain(document.Descendants(), element => element.Attribute(x + "Name")?.Value is
            "SendingBanner" or "StopSharing" or "SendingStatus" or "EncoderStatus");
        var receivers = Named("ReceiverList");
        var action = Assert.Single(receivers.Descendants(), element => element.Attribute("Click")?.Value == "DeviceAction_Click");
        Assert.Equal("{x:Bind ActionLabel}", action.Attribute("Content")?.Value);
        Assert.Equal("{x:Bind CanAct}", action.Attribute("IsEnabled")?.Value);
        foreach (var property in new[] { "Status", "Parameters", "Encoder" })
            Assert.Contains(receivers.Descendants(), element => element.Attribute("Text")?.Value == $"{{x:Bind {property}}}");
    }
}
