using HailMary.Models;

namespace HailMary.Services;

/// <summary>How an overlay looks at one point in time (opacity, slide offset, pop scale).</summary>
public readonly record struct OverlayAnimationState(bool Visible, double Opacity, double OffsetY, double Scale)
{
    public static readonly OverlayAnimationState Hidden = new(false, 0, 0, 1);

    public static readonly OverlayAnimationState Static = new(true, 1, 0, 1);
}

/// <summary>
/// Mirrors the FFmpeg filters in overlay_core._sprite_overlay_parts so the preview animates
/// exactly like the export: linear alpha fades, cubic ease-out slide/pop on the way in.
/// </summary>
public static class TextOverlayAnimation
{
    public static OverlayAnimationState Evaluate(TextOverlaySegment seg, double t)
    {
        var hasFrom = TimeFieldHelper.TryParseSeconds(seg.From, out var from);
        var hasTo = TimeFieldHelper.TryParseSeconds(seg.To, out var to);
        if (hasFrom && hasTo && to < from)
        {
            (from, to) = (to, from);
        }

        if ((hasFrom && t < from) || (hasTo && t > to))
        {
            return OverlayAnimationState.Hidden;
        }

        var anim = seg.Animation;
        if ((!hasFrom && !hasTo) || anim is not ("fade" or "slide" or "pop"))
        {
            return OverlayAnimationState.Static;
        }

        var d = Math.Clamp(seg.AnimDuration, 0.05, 5.0);
        if (hasFrom && hasTo)
        {
            d = Math.Min(d, Math.Max(0.05, (to - from) / 2));
        }

        var opacity = 1.0;
        var pIn = 1.0;
        if (hasFrom)
        {
            pIn = Math.Clamp((t - from) / d, 0, 1);
            opacity = Math.Min(opacity, pIn);
        }

        if (hasTo)
        {
            opacity = Math.Min(opacity, Math.Clamp((to - t) / d, 0, 1));
        }

        var easeOut = 1 - Math.Pow(1 - pIn, 3);
        var offsetY = 0.0;
        var scale = 1.0;
        if (anim == "slide" && hasFrom)
        {
            var dist = Math.Max(8, (int)Math.Round(seg.Fontsize * 0.6, MidpointRounding.ToEven));
            offsetY = dist * Math.Pow(1 - pIn, 3);
        }
        else if (anim == "pop" && hasFrom)
        {
            scale = 0.6 + 0.4 * easeOut;
        }

        return new OverlayAnimationState(true, opacity, offsetY, scale);
    }
}
