using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal sealed record LocalWebDesktopHotkeys(
    IReadOnlyDictionary<string, string> Bindings, int SkipBackwardSeconds, int SkipForwardSeconds)
{
    internal static LocalWebDesktopHotkeys Capture(HotkeySettings settings,
        Func<string, string, string>? effectiveGesture = null)
    {
        string Binding(string value, string fallback) => effectiveGesture?.Invoke(value, fallback) ?? value;

        return new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["back"] = Binding(settings.GoBack, HotkeySettings.DefaultGoBack),
            ["dismiss"] = Binding(settings.DismissFullscreenOrAutoScroll, HotkeySettings.DefaultDismissFullscreenOrAutoScroll),
            ["nextTab"] = Binding(settings.NextTab, HotkeySettings.DefaultNextTab),
            ["previousTab"] = Binding(settings.PreviousTab, HotkeySettings.DefaultPreviousTab),
            ["grid"] = Binding(settings.ToggleMultiStream, HotkeySettings.DefaultToggleMultiStream),
            ["toggleSeekBar"] = Binding(settings.ToggleReplaySeekBar, HotkeySettings.DefaultToggleReplaySeekBar),
            ["togglePause"] = Binding(settings.TogglePause, HotkeySettings.DefaultTogglePause),
            ["skipBackward"] = Binding(settings.SkipBackward, HotkeySettings.DefaultSkipBackward),
            ["skipForward"] = Binding(settings.SkipForward, HotkeySettings.DefaultSkipForward),
            ["volumeUp"] = Binding(settings.VolumeUp, HotkeySettings.DefaultVolumeUp),
            ["volumeDown"] = Binding(settings.VolumeDown, HotkeySettings.DefaultVolumeDown)
        }, settings.SkipBackwardSeconds, settings.SkipForwardSeconds);
    }
}
