using System.Xml.Linq;
using Xunit;

namespace RedrainCast.Tests;

public sealed class HomeLayoutTests
{
    [Fact]
    public void WholeHomeScrollsWithinFiniteViewportWithoutScrollingLiveVideo()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var home = Named("HomePage");
        var scroll = Named("HomeScroll");
        Assert.Equal("Grid", home.Name.LocalName);
        Assert.Same(home, scroll.Parent);
        Assert.DoesNotContain(home.Elements(), element => element.Name.LocalName == "Grid.RowDefinitions");
        foreach (var name in new[] { "SettingsButton", "Problem", "Cards", "SendCard", "ReceiveCard", "VersionText", "ConnectionHelp" })
            Assert.Contains(scroll, Named(name).Ancestors());
        Assert.DoesNotContain(scroll, Named("PlayerInput").Ancestors());
        Assert.DoesNotContain(scroll, Named("VideoSurface").Ancestors());
        Assert.DoesNotContain(scroll.Descendants(), element => element.Name.LocalName == "ScrollViewer");
        Assert.All(scroll.Descendants().Where(element => element.Name.LocalName == "RowDefinition"),
            row => Assert.Equal("Auto", (string?)row.Attribute("Height")));
        Assert.Equal("Stretch", (string?)scroll.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("False", (string?)scroll.Attribute("IsTabStop"));
        Assert.Null(Named("Problem").Attribute("MaxHeight"));
    }
}
