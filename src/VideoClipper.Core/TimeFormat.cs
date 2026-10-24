using System.Globalization;

namespace VideoClipper.Core;

/// <summary>
/// Time display and parsing. Everything is culture-invariant: the machine locale is Turkish,
/// where 1.5 formats as "1,5" and ffmpeg would reject it.
/// </summary>
public static class TimeFormat
{
    /// <summary>"01:02.500", or "1:01:02.500" once the video is an hour long.</summary>
    public static string Display(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Seconds with millisecond precision, the way ffmpeg's -ss/-t expect them.</summary>
    public static string Ffmpeg(TimeSpan t) =>
        Math.Max(0, t.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Accepts "75", "75.5", "1:15", "1:15.5", "0:01:15.250"; either '.' or ',' as decimal mark.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v)) return false;
            // Only the seconds field may carry a fraction, and minutes/seconds after the first field stay below 60.
            if (!last && v != Math.Floor(v)) return false;
            if (i > 0 && v >= 60) return false;
            total = total * 60 + v;
        }

        result = TimeSpan.FromMilliseconds(Math.Round(total * 1000));
        return true;
    }
}
