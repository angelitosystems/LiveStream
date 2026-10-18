using System.Globalization;
using System.Text;
using StreamPilot.Domain.Animation;

namespace StreamPilot.Native.Engine;

/// <summary>Formato compacto de clip que entiende sp_anim_play (ver streampilot_bridge.h).</summary>
public static class AnimationClipJson
{
    public static string Serialize(AnimationClip clip)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"delay\":").Append(Num(clip.Delay))
          .Append(",\"duration\":").Append(Num(clip.Duration))
          .Append(",\"tracks\":[");
        for (var i = 0; i < clip.Tracks.Count; i++)
        {
            var track = clip.Tracks[i];
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"p\":\"").Append(Property(track.Property)).Append("\",\"k\":[");
            for (var j = 0; j < track.Keys.Count; j++)
            {
                var key = track.Keys[j];
                if (j > 0)
                    sb.Append(',');
                sb.Append("{\"t\":").Append(Num(key.Time))
                  .Append(",\"v\":").Append(Num(key.Value))
                  .Append(",\"e\":\"").Append(Ease(key.Easing)).Append("\"}");
            }
            sb.Append("]}");
        }
        return sb.Append("]}").ToString();
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Property(AnimatedProperty property) => property switch
    {
        AnimatedProperty.X => "x",
        AnimatedProperty.Y => "y",
        AnimatedProperty.ScaleX => "scale_x",
        AnimatedProperty.ScaleY => "scale_y",
        AnimatedProperty.Rotation => "rotation",
        AnimatedProperty.Opacity => "opacity",
        AnimatedProperty.CropLeft => "crop_left",
        AnimatedProperty.CropTop => "crop_top",
        AnimatedProperty.CropRight => "crop_right",
        AnimatedProperty.CropBottom => "crop_bottom",
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };

    public static string Ease(Easing easing) => easing switch
    {
        Easing.Linear => "linear",
        Easing.InCubic => "in_cubic",
        Easing.OutCubic => "out_cubic",
        Easing.InOutCubic => "in_out_cubic",
        Easing.OutBack => "out_back",
        Easing.OutExpo => "out_expo",
        Easing.Hold => "hold",
        _ => throw new ArgumentOutOfRangeException(nameof(easing)),
    };
}
