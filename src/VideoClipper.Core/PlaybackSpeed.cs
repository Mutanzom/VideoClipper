using System.Globalization;

namespace VideoClipper.Core;

/// <summary>Speed factors (0.15, 1.8…): parsing, display and the ffmpeg filters that apply them.</summary>
public static class PlaybackSpeed
{
    public const double Min = 0.05;
    public const double Max = 16;

    public static bool IsNormal(double speed) => Math.Abs(speed - 1) < 1e-9;

    /// <summary>Accepts "0.15", "0,15", "1.8x", "2×". Culture-invariant (see <see cref="TimeFormat"/>).</summary>
    public static bool TryParse(string? text, out double speed)
    {
        speed = 1;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimEnd('x', 'X', '×').Trim().Replace(',', '.');
        return double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out speed) && speed > 0;
    }

    /// <summary>Null when usable, otherwise why not.</summary>
    public static string? Validate(double speed) =>
        speed is >= Min and <= Max ? null : $"Speed must be between {Format(Min)} and {Format(Max)}.";

    /// <summary>"0.15", "1", "2.3" — no trailing zeros, always a '.' decimal mark.</summary>
    public static string Format(double speed) => speed.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Video filter: retimes frames; the encoder keeps the source frame rate by duplicating or dropping.</summary>
    public static string VideoFilter(double speed) => $"setpts=PTS/{Format(speed)}";

    /// <summary>
    /// Audio filter: atempo changes tempo without changing pitch but only accepts 0.5–2 per
    /// instance (older builds), so larger changes are a chain whose product is the speed.
    /// </summary>
    public static string AudioFilter(double speed)
    {
        var parts = new List<double>();
        double rest = speed;
        while (rest < 0.5) { parts.Add(0.5); rest /= 0.5; }
        while (rest > 2) { parts.Add(2); rest /= 2; }
        parts.Add(rest);
        return string.Join(",", parts.Select(p => "atempo=" + p.ToString("0.######", CultureInfo.InvariantCulture)));
    }
}
