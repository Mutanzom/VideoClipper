using System.Globalization;
using System.Text.Json;

namespace VideoClipper.Core;

public sealed record MediaInfo(TimeSpan Duration, double Fps, string? VideoCodec, bool HasAudio)
{
    /// <summary>Parses <c>ffprobe -show_format -show_streams -of json</c> output.</summary>
    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double seconds = 0;
        if (root.TryGetProperty("format", out var format)) seconds = ReadDouble(format, "duration");

        double fps = 0;
        string? codec = null;
        bool hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "audio") hasAudio = true;
                if (type != "video" || codec != null) continue;
                // Cover art is stored as a one-frame "video" stream; it is not the picture we want.
                if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue;

                codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                fps = ParseRate(s.TryGetProperty("avg_frame_rate", out var avg) ? avg.GetString() : null);
                if (fps <= 0) fps = ParseRate(s.TryGetProperty("r_frame_rate", out var r) ? r.GetString() : null);
                if (seconds <= 0) seconds = ReadDouble(s, "duration");
            }
        }

        return new MediaInfo(TimeSpan.FromSeconds(seconds), fps, codec, hasAudio);
    }

    /// <summary>"30000/1001" → 29.97; "0/0" or junk → 0.</summary>
    public static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return 0;
        if (parts.Length == 1) return num;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) || den == 0) return 0;
        return num / den;
    }

    private static double ReadDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
}
