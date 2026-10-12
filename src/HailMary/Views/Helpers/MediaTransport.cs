using HailMary.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

namespace HailMary.Views.Helpers;

/// <summary>
/// Shared player keyboard handling and play/pause button state for the video tool previews.
/// Space: play/pause · ←/→: one frame (paused) · Shift+←/→: 1 s · Ctrl+←/→: 5 s · Home/End.
/// </summary>
public static class MediaTransport
{
    /// <summary>Clicking anywhere on the preview focuses the host so its shortcuts work right away.</summary>
    public static void FocusOnPointer(Control host, UIElement surface) =>
        surface.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => host.Focus(FocusState.Programmatic)),
            handledEventsToo: true);

    /// <summary>Handle the common transport keys. Returns true when the key was consumed.</summary>
    public static bool HandleKey(
        VirtualKey key,
        MediaPlayer? player,
        double duration,
        Action togglePlay,
        Action<double> seekTo)
    {
        if (player is null)
        {
            return false;
        }

        var position = player.PlaybackSession.Position.TotalSeconds;
        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);
        switch (key)
        {
            case VirtualKey.Space:
                togglePlay();
                return true;
            case VirtualKey.Home:
                seekTo(0);
                return true;
            case VirtualKey.End:
                seekTo(Math.Max(0, duration - 0.05));
                return true;
            case VirtualKey.Left or VirtualKey.Right:
                var dir = key == VirtualKey.Right ? 1 : -1;
                if (!shift && !ctrl && player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                {
                    // Frame-exact stepping; the host's seek keeps slider and display in sync.
                    if (dir > 0)
                    {
                        player.StepForwardOneFrame();
                    }
                    else
                    {
                        player.StepBackwardOneFrame();
                    }

                    var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    var timer = dispatcher.CreateTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(60);
                    timer.IsRepeating = false;
                    timer.Tick += (_, _) => seekTo(player.PlaybackSession.Position.TotalSeconds);
                    timer.Start();
                    return true;
                }

                var step = ctrl ? 5.0 : shift ? 1.0 : 1.0 / 30.0;
                seekTo(Math.Clamp(position + dir * step, 0, Math.Max(0, duration)));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Swap the play/pause glyph, tooltip and accessible name of a toggle button.</summary>
    public static void ApplyPlayState(Button button, FontIcon icon, bool playing)
    {
        icon.Glyph = playing ? "" : "";
        var label = Loc.T(playing ? "common.pause" : "common.play");
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
