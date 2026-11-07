using ScrcpyReceive.Core;

namespace RedrainCast.App;

internal static class ReceiverPresentation
{
    public static string ProblemKey(ReceiverUiState state) => state.ProblemCode switch
    {
        "Start failed" => "ReceiverStartFailed",
        "Listener failed" or "Cleanup failed" => "ReceiverUnsafe",
        "Discovery warning" => "ReceiverDiscoveryWarning",
        "Discovery failed" => state.Addresses.Count == 0 ? "ReceiverAddressLookupStopped" : "ReceiverDiscoveryStopped",
        "Window layout failed" => "ReceiverWindowLayoutFailed",
        "UNSUPPORTED_CONFIG" => "ReceiverUnsupportedConfig",
        "DECODER_FAILED" => "ReceiverDecoderFailed",
        "AUDIO_FAILED" => "ReceiverAudioFailed",
        "CONGESTION" => "ReceiverCongestion",
        "TIMEOUT" => "ReceiverTimeout",
        "VERSION" => "ReceiverVersion",
        "Sender error" => "ReceiverSenderError",
        _ => "ReceiverUnexpected"
    };

    public static string TitleKey(ReceiverUiState state) => state.IsWarning ? "OperationalNotice" :
        state.Phase == ReceiverUiPhase.Unavailable ? "ReceiverUnavailable" : "PlaybackStopped";
}
