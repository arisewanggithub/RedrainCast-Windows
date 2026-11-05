using System.Globalization;
using System.Net;
using System.Text.Json;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class SettingsAndSelectionTests
{
    private static ReceiverEndpoint Receiver(int number, bool busy = false, string name = "TV") =>
        new(IPAddress.Parse($"192.168.1.{number}"), 27184, name, busy);

    [Fact]
    public void FirstAvailableReceiverIsSelectedAndManualEntryIsAlwaysLast()
    {
        var selection = new ReceiverSelection();
        selection.Refresh([]);
        Assert.Null(selection.Selected);
        Assert.Same(ReceiverChoice.Manual, Assert.Single(selection.Choices));
        selection.Refresh([Receiver(1, true), Receiver(2), Receiver(3)]);
        Assert.Equal(Receiver(2).Key, selection.Selected!.Endpoint!.Key);
        Assert.Same(ReceiverChoice.Manual, selection.Choices.Last());
    }

    [Fact]
    public void BusyReceiversDoNotPreventLaterInitialSelection()
    {
        var selection = new ReceiverSelection();
        selection.Refresh([Receiver(1, true)]);
        Assert.Null(selection.Selected);
        selection.Refresh([Receiver(1)]);
        Assert.Equal(Receiver(1).Key, selection.Selected!.Endpoint!.Key);
    }

    [Fact]
    public void ManualChoiceSurvivesDiscoveryAndDiscoveredChoiceIgnoresHiddenManualInput()
    {
        var selection = new ReceiverSelection();
        selection.Select(ReceiverChoice.Manual);
        selection.Refresh([Receiver(1)]);
        Assert.True(selection.Selected!.IsManual);
        Assert.Equal(IPAddress.Loopback, selection.Resolve("127.0.0.1", "27184").Address);
        selection.Select(selection.Choices[0]);
        Assert.Equal(Receiver(1), selection.Resolve("invalid hidden IP", "invalid hidden port"));
    }

    [Fact]
    public void SelectionTracksEndpointNotOrderOrNameAndDoesNotRedirectOnExpiry()
    {
        var selection = new ReceiverSelection();
        selection.Refresh([Receiver(1), Receiver(2)]);
        selection.Select(selection.Choices[1]);
        selection.Refresh([Receiver(3, name: "TV"), Receiver(2, name: "Renamed")]);
        Assert.Equal(Receiver(2).Key, selection.Selected!.Endpoint!.Key);
        Assert.Equal("Renamed", selection.Selected.Endpoint.Name);
        selection.Refresh([Receiver(3, name: "Renamed")]);
        Assert.False(selection.Selected.IsCurrent);
        Assert.Equal(Receiver(2).Key, selection.Selected.Endpoint!.Key);
        Assert.Throws<ArgumentException>(() => selection.Resolve("", ""));
        Assert.Same(ReceiverChoice.Manual, selection.Choices.Last());
        selection.Refresh([Receiver(2)]);
        Assert.True(selection.Selected.IsCurrent);
    }

    [Fact]
    public void BusySelectionIsPreservedButCannotEvictAnExistingCast()
    {
        var selection = new ReceiverSelection();
        selection.Refresh([Receiver(1)]);
        selection.Refresh([Receiver(1, true), Receiver(2)]);
        Assert.Equal(Receiver(1).Key, selection.Selected!.Endpoint!.Key);
        Assert.Throws<ArgumentException>(() => selection.Resolve("", ""));
    }

    [Theory]
    [InlineData("", "27184")]
    [InlineData("TV", "27184")]
    [InlineData("127.0.0.1", "0")]
    [InlineData("127.0.0.1", "65536")]
    [InlineData("127.0.0.1", "27184.5")]
    public void InvalidManualValuesFailExplicitly(string ip, string port)
    {
        var selection = new ReceiverSelection();
        selection.Select(ReceiverChoice.Manual);
        Assert.Throws<ArgumentException>(() => selection.Resolve(ip, port));
    }

    [Fact]
    public void UntouchedEmptyManualAddressHasNoRedFeedbackButRemainsInvalid()
    {
        var input = ManualReceiverInput.Parse("", "27184");
        Assert.Null(input.Endpoint);
        Assert.Equal("Enter the receiver's IP address.", input.AddressError);
        Assert.Null(input.PortError);
        Assert.Equal((null, (string?)null), input.Feedback(false, false));
        Assert.Equal(("Enter the receiver's IP address.", (string?)null), input.Feedback(true, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("65536")]
    [InlineData("0")]
    [InlineData("27184.5")]
    public void PortFeedbackOnlyAppearsAfterPortInteraction(string port)
    {
        var input = ManualReceiverInput.Parse("127.0.0.1", port);
        Assert.Null(input.Endpoint);
        Assert.Null(input.AddressError);
        Assert.Null(input.Feedback(true, false).Port);
        Assert.Equal("Enter a whole-number TCP port from 1 to 65535.", input.Feedback(false, true).Port);
        Assert.Null(input.Feedback(false, true).Address);
    }

    [Fact]
    public void InvalidAddressDoesNotBlameAValidPortAndCorrectionsClearFeedback()
    {
        var invalid = ManualReceiverInput.Parse("not-an-ip", "27184");
        Assert.Equal("Enter a numeric IPv4 or IPv6 address.", invalid.Feedback(true, true).Address);
        Assert.Null(invalid.Feedback(true, true).Port);
        var corrected = ManualReceiverInput.Parse("127.0.0.1", "27184");
        Assert.Equal(IPAddress.Loopback, corrected.Endpoint!.Address);
        Assert.Equal((null, (string?)null), corrected.Feedback(true, true));
        Assert.Equal("Enter the receiver's IP address.",
            Assert.Throws<ArgumentException>(() => ReceiverEndpoint.Manual("", 27184)).Message);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void ValidManualAddressesAcceptTheDefaultPort(string address)
    {
        var input = ManualReceiverInput.Parse(address, "27184");
        Assert.NotNull(input.Endpoint);
        Assert.Null(input.AddressError);
        Assert.Null(input.PortError);
        Assert.Equal(27184, input.Endpoint.Port);
    }

    [Fact]
    public void NewSettingsDefaultTo60AndSavedCustomOrExistingRatesArePreserved()
    {
        Assert.Equal(60, new CaptureSettings().MaxFps);
        Assert.Equal(new[] { 15, 24, 25, 30, 48, 50, 60, 90, 120 }, CaptureSettings.FpsPresets);
        foreach (var fps in new[] { 27, 30, 48, 60, 90, 120 })
        {
            var settings = new CaptureSettings(MaxFps: fps);
            settings.Validate();
            Assert.Equal(settings, JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(settings)));
            var request = JsonSerializer.SerializeToElement(settings.Request("Hardware"));
            Assert.Equal(fps, request.GetProperty("video").GetProperty("maxFps").GetInt32());
        }
        Assert.Throws<ArgumentException>(() => new CaptureSettings(MaxFps: 121).Validate());
        Assert.Throws<ArgumentException>(() => new CaptureSettings(MaxFps: 0).Validate());
    }

    [Theory]
    [InlineData("", "8", true, false)]
    [InlineData("720.5", "8", true, false)]
    [InlineData("63", "8", true, false)]
    [InlineData("3841", "8", true, false)]
    [InlineData("720", "", false, true)]
    [InlineData("720", "NaN", false, true)]
    [InlineData("720", "Infinity", false, true)]
    [InlineData("720", "8e1", false, true)]
    [InlineData("720", "0.09", false, true)]
    [InlineData("720", "100.1", false, true)]
    [InlineData("720", "8.0000001", false, true)]
    [InlineData("720", "1,5", false, true)]
    public void InvalidTextIsReportedWithoutClampingOrSubstitution(string size, string mbps,
        bool dimensionError, bool bitrateError)
    {
        var result = CaptureSettings.ParseInput(size, mbps, 60, "auto", CultureInfo.InvariantCulture);
        Assert.Null(result.Settings);
        Assert.Equal(dimensionError, result.DimensionError != null);
        Assert.Equal(bitrateError, result.BitrateError != null);
    }

    [Theory]
    [InlineData("en-US", "8.5")]
    [InlineData("fr-FR", "8,5")]
    public void DecimalBitrateUsesTheUiCultureAndRetainsExactBits(string culture, string mbps)
    {
        var input = CaptureSettings.ParseInput("720", mbps, 120, "auto", CultureInfo.GetCultureInfo(culture));
        Assert.Equal(new CaptureSettings(720, 120, 8_500_000), input.Settings);
        Assert.Null(input.DimensionError);
        Assert.Null(input.BitrateError);
        Assert.Null(input.FpsError);
    }

    [Theory]
    [InlineData("64", "0.1", 15)]
    [InlineData("3840", "100", 120)]
    [InlineData("1920", "8.123456", 90)]
    public void ValidBoundariesAndFractionalMbpsAreAccepted(string size, string mbps, int fps)
    {
        Assert.NotNull(CaptureSettings.ParseInput(size, mbps, fps, "auto", CultureInfo.InvariantCulture).Settings);
    }
}
